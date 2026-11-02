# Tích hợp SCADA với Cloud API

Tài liệu này dành cho dự án SCADA cần đọc dữ liệu PLC qua Cloud: lấy token, liệt kê Edge, PLC và tag, đọc giá trị
mới nhất và lịch sử, nhận giá trị realtime, và (khi bật chế độ ghi) gửi lệnh. Mọi route dưới đây là những route mà
ứng dụng mẫu [`samples/InventoryClient`](../samples/InventoryClient/Program.cs) gọi tới, hoặc liệt kê đầy đủ để dùng thêm.

## 1. Mô hình dữ liệu

```
Edge (máy tính tại nhà máy, edgeId)
└── PLC (device: S7-1200, FX5U, FX3U)
    └── Tag (tagId = <device>.<tên tag>, ví dụ Line.Speed)
```

- Mỗi Edge gửi **danh sách PLC và tag** của nó lên Cloud khi khởi động, khi trạng thái một PLC đổi và mỗi 60 giây.
- Giá trị tag là **telemetry**, gửi theo chu kỳ đọc của từng PLC (mặc định 1 giây).
- API không bao giờ trả IP/cổng của PLC, địa chỉ tag (`DB1.DBW2`, `D100`) hay nội dung lỗi gốc của driver.

## 2. Lấy token

Mọi route cần header `Authorization: Bearer <token>` với role `Viewer` (đọc) hoặc `Operator` (đọc + gửi lệnh).
Trên stack chạy thử:

```bash
TOKEN=$(bash deployments/scripts/get-token.sh viewer)
```

- Gửi token **chỉ** trong header `Authorization`, không đặt trong URL (URL nằm lại trong log và proxy).
- Token hết hạn sau vài phút (Keycloak mặc định 5 phút): với HTTP, xin lại khi nhận `401`; với realtime, xem mục 7.
- Không in token ra log hay màn hình.

## 3. Liệt kê Edge

`GET /api/v1/edges` → các Edge được cấu hình trên Cloud:

```json
[{ "edgeId": "line1-edge01", "displayName": "Line 1", "lastTelemetryUtc": "2026-11-02T08:00:01.25+00:00" }]
```

## 4. PLC của một Edge

`GET /api/v1/edges/{edgeId}/devices`

```json
{
  "edgeId": "line1-edge01",
  "reportedUtc": "2026-11-02T08:00:00.123Z",
  "stale": false,
  "commandsEnabled": false,
  "devices": [
    { "name": "Line", "model": "S7-1200", "enabled": true, "writesEnabled": false, "state": "connected",
      "lastGoodUtc": "2026-11-02T07:59:59.900Z", "lastError": null, "tagCount": 2 },
    { "name": "Press", "model": "FX5U", "enabled": true, "writesEnabled": false, "state": "unavailable",
      "lastGoodUtc": "2026-11-02T07:41:10.000Z", "lastError": "unavailable", "tagCount": 2 }
  ]
}
```

| Trường | Ý nghĩa |
|---|---|
| `reportedUtc` | Lúc Cloud nhận bản danh sách mới nhất; `null` khi Edge chưa gửi lần nào (Edge bản cũ hoặc chưa kết nối) |
| `stale` | `true` khi bản mới nhất cũ hơn **150 giây** (Edge mất kết nối hoặc tắt). Khi đó mọi `state` là `"stale"` |
| `state` | `disabled` (tắt trong cấu hình), `waiting` (chưa đọc lần nào), `connected`, `unavailable` (mất kết nối PLC), `stale` |
| `lastError` | `null`, `unavailable` (không kết nối được PLC) hoặc `tagErrors` (một số tag đọc lỗi). Chi tiết lỗi xem trên PLC Monitor của Edge |
| `commandsEnabled` | `true` chỉ khi cả Edge lẫn Cloud cho phép lệnh ghi |

Edge chưa gửi danh sách: `reportedUtc` và `commandsEnabled` là `null`, `stale` là `true`, `devices` là `[]`.

## 5. Tag của một PLC

`GET /api/v1/edges/{edgeId}/devices/{device}/tags` (tên PLC không phân biệt hoa thường)

```json
[
  { "tagId": "Line.Speed", "name": "Speed", "dataType": "Int", "length": null, "writable": true, "canWrite": false,
    "min": 0, "max": 3000, "description": "Belt speed",
    "value": 1200, "quality": "good", "timestampUtc": "2026-11-02T08:00:00.000Z" }
]
```

- Thứ tự tag là thứ tự trong cấu hình Edge.
- `value`, `quality`, `timestampUtc` là giá trị mới nhất Cloud đã lưu; cả ba `null` khi chưa có giá trị nào. `value`
  cũng là `null` khi `quality` khác `good` (ví dụ mất kết nối PLC).
- `value`: số, `true`/`false`, hoặc chuỗi (`String`; `DTL` dạng `yyyy-MM-ddTHH:mm:ss.fff`, giờ của PLC, không có múi giờ).
- `canWrite`: `true` khi Cloud và Edge cho phép lệnh, PLC đang bật và cho ghi, và tag là `writable`. Nó lấy từ bản
  danh sách mới nhất và không xét `stale`: khi `GET .../devices` báo `stale: true`, coi như không ghi được.

## 6. Giá trị mới nhất và lịch sử theo tag

- `GET /api/v1/edges/{edgeId}/tags/latest`: giá trị mới nhất của **mọi** tag của Edge.
- `GET /api/v1/edges/{edgeId}/tags/{tagId}/history?from=2026-11-02T00:00:00Z&to=2026-11-02T08:00:00Z&limit=1000`:
  lịch sử một tag trong khoảng `[from, to)` (gồm `from`, không gồm `to`), tối đa 31 ngày và 10 000 mẫu mỗi lần.
  `truncated: true` nghĩa là còn mẫu: gọi tiếp với `from` = `timestampUtc` của mẫu cuối và bỏ các mẫu đã nhận (mẫu cuối
  được trả lại lần nữa).

## 7. Realtime qua SignalR

Kết nối tới `/hubs/live` bằng thư viện SignalR client, truyền token qua `AccessTokenProvider` (thư viện tự gửi đúng
cách), rồi gọi `JoinEdge(edgeId)`:

```csharp
var hub = new HubConnectionBuilder()
    .WithUrl(new Uri(cloud, "hubs/live"), o => o.AccessTokenProvider = () => Task.FromResult<string?>(token))
    .Build();
hub.On<JsonElement>("telemetry", sample => { /* tagId, value, quality, timestampUtc */ });
hub.On<JsonElement>("devicesUpdated", devices => { /* cùng nội dung GET .../devices */ });
await hub.StartAsync();
await hub.InvokeAsync("JoinEdge", "line1-edge01");
```

| Sự kiện | Khi nào | Nội dung |
|---|---|---|
| `telemetry` | Mỗi mẫu Cloud vừa lưu | Một mẫu: `edgeId`, `tagId`, `value`, `quality`, `timestampUtc` |
| `devicesUpdated` | Mỗi lần Cloud lưu bản danh sách PLC mới (khoảng mỗi phút, và ngay khi một PLC đổi trạng thái) | Đúng nội dung `GET /api/v1/edges/{edgeId}/devices` |
| `commandUpdated` | Mỗi lần trạng thái một lệnh đổi | Như `GET /api/v1/commands/{commandId}` |

Chuyển sang `stale` **không** được đẩy: ứng dụng tự so `reportedUtc` với giờ hiện tại, hoặc gọi lại `GET .../devices`.

**Token hết hạn thì Cloud đóng kết nối hub**, và nhóm đã `JoinEdge` không còn ở kết nối mới. Ứng dụng chạy lâu phải bắt
sự kiện `Closed`, xin token mới, `StartAsync` lại rồi gọi lại `JoinEdge` cho từng Edge (`AccessTokenProvider` được gọi
lại mỗi lần kết nối, nên cho nó trả token mới nhất). Ứng dụng mẫu chỉ báo `hub closed` và thoát với mã khác 0.

## 8. Gửi lệnh (chỉ ở chế độ ghi)

`POST /api/v1/edges/{edgeId}/commands` với role `Operator`, body `{"tagId":"Line.Speed","value":1200}`. Mặc định Cloud
ở chế độ chỉ đọc và route này trả `404`; cách bật xem [deployment.md](deployment.md#chế-độ-read-only-và-cách-bật-ghi).
Trạng thái lệnh: `GET /api/v1/commands/{commandId}` hoặc sự kiện `commandUpdated`.

## 9. Mã lỗi

| HTTP | `title` | Khi nào |
|---|---|---|
| 401 | `unauthorized` | Thiếu token, token hết hạn hoặc sai |
| 403 | `forbidden` | Token không có role `Viewer`/`Operator` |
| 404 | `edge_not_found` | Edge không có trong cấu hình Cloud |
| 404 | `device_not_found` | PLC không có trong danh sách mới nhất của Edge (hoặc Edge chưa gửi danh sách) |
| 400 | `invalid_request` | Tham số sai (ví dụ `from`/`to` của lịch sử) |

## 10. Chạy ứng dụng mẫu

```bash
export IIOT_CLOUD_URL=http://127.0.0.1:8080/
export IIOT_TOKEN=$(bash deployments/scripts/get-token.sh viewer)
export IIOT_EDGE_ID=line1-edge01
dotnet run --project samples/InventoryClient -- --seconds 30
```

Kết quả: danh sách PLC, tag kèm giá trị mới nhất, rồi các giá trị realtime trong 30 giây (bỏ `--seconds` để chạy tới
Ctrl+C, hoặc tới khi token hết hạn: khi đó in `hub closed` và thoát với mã khác 0). Sai token: in `401 Unauthorized`
và thoát với mã khác 0.
