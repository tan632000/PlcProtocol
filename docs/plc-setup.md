# Chuẩn bị PLC S7-1200 và máy Edge

Edge đọc/ghi PLC bằng giao thức **S7comm PUT/GET** (thư viện S7netplus 0.20.0) qua TCP cổng 102.
Tài liệu này liệt kê những gì cần cấu hình trên PLC và trên máy chạy Edge trước khi kết nối.

## 1. Cấu hình CPU trong TIA Portal

1. Mở project, chọn CPU S7-1200 → **Properties** → **Protection & Security** → **Connection mechanisms**.
2. Bật **Permit access with PUT/GET communication from remote partner**.
3. Compile và download cấu hình phần cứng xuống CPU.

Nếu bỏ qua bước này, PLC trả lỗi header S7 (error class `0x81`, code `0x04`) và Edge báo
`PlcAccessDeniedException`.

## 2. Tắt "Optimized block access" cho từng DB

PUT/GET truy cập DB theo địa chỉ byte tuyệt đối (`DB1.DBW2`), nên DB phải dùng bố cục tiêu chuẩn:

1. Chuột phải DB → **Properties** → **Attributes**.
2. Bỏ chọn **Optimized block access**.
3. Compile lại, ghi nhận offset của từng biến (cột **Offset** trong DB editor) để khai báo tag.

DB còn để Optimized sẽ trả item return code lỗi; Edge báo `PlcAccessDeniedException`.
Vùng `M`, `I`, `Q` không cần bước này.

## 3. Rack và Slot

| CPU | Rack | Slot |
|---|---|---|
| S7-1200 / S7-1200 G2 | 0 | 1 |

Khai báo trong cấu hình Edge (section `Edge:Plc`):

```json
{
  "Edge": {
    "Plc": {
      "Host": "192.168.0.10",
      "Port": 102,
      "Rack": 0,
      "Slot": 1,
      "TimeoutMs": 3000
    }
  }
}
```

| Trường | Mặc định | Giới hạn hợp lệ |
|---|---|---|
| `Host` | (bắt buộc) | không rỗng |
| `Port` | 102 | 1..65535 |
| `Rack` | 0 | 0..7 |
| `Slot` | 1 | 0..15 |
| `TimeoutMs` | 3000 | 100..60000 — tổng thời gian một thao tác (chờ lượt + kết nối + đọc/ghi) |

Giá trị ngoài giới hạn làm Edge **dừng ngay khi khởi động**, không chờ tới lần gọi PLC đầu tiên.

## 4. Mạng

- Chỉ mở TCP 102 **trong mạng LAN nhà máy**. Không bao giờ NAT/port-forward cổng 102 ra Internet:
  PUT/GET không có xác thực, ai tới được cổng này đều đọc/ghi được PLC.
- Edge kết nối ra Cloud qua MQTT/TLS; Cloud không bao giờ kết nối thẳng vào PLC.
- S7-1200 có ít connection slot. Mỗi Edge giữ **một** kết nối và xử lý tuần tự từng thao tác;
  không chạy hai Edge cùng trỏ vào một PLC.

## 5. Đồng bộ thời gian (NTP)

Máy Edge **phải** đồng bộ giờ qua NTP. Lệnh ghi từ Cloud có `issuedAtUtc`/`expiresAtUtc`; Edge từ chối lệnh
khi `issuedAtUtc` lệch hơn **2 phút** về tương lai (`clock_skew`) hoặc đã hết hạn (`expired`).

- Linux: `timedatectl status` → `System clock synchronized: yes`.
- Windows: `w32tm /query /status`.

Nếu thấy nhiều kết quả `clock_skew`, hãy sửa NTP; không nới giới hạn 2 phút.

## 6. Ý nghĩa các lỗi driver

| Exception | Ý nghĩa | Đã gửi gì tới PLC? | Tự động thử lại? |
|---|---|---|---|
| `PlcUnavailableException` | Không kết nối được / hết thời gian khi đọc | Không có lệnh ghi nào được gửi | Được |
| `PlcBusyException` | Thao tác khác đang giữ kết nối quá `TimeoutMs` | Không | Được |
| `PlcAccessDeniedException` | PLC trả lỗi (PUT/GET tắt, DB Optimized, địa chỉ sai) | PLC đã từ chối, giá trị **không** được áp dụng | Không — sửa cấu hình PLC |
| `PlcWriteOutcomeUnknownException` | Lệnh ghi có thể đã tới PLC nhưng không nhận được xác nhận | **Có thể** đã áp dụng | **Không bao giờ** — đọc lại tag trước khi gửi lại |
| `PlcValueException` | Giá trị/kiểu không khớp địa chỉ | Không | Không — sửa dữ liệu/cấu hình |

Sau mọi lỗi kết nối, lần gọi tiếp theo tự mở kết nối mới.

## 7. Giới hạn đã biết

- Driver mới được kiểm thử với PLC giả lập trên loopback (`FakeS7Server`), **chưa** thử trên phần cứng thật.
- S7-1200 **G2**: hỗ trợ PUT/GET phía server `[UNVERIFIED]` cho đến khi thử trên phần cứng.
- Khung lỗi header `0x8104` thật của S7-1200 có thể khác khung giả lập; S7netplus 0.20.0 không tự giải mã
  error class của header, nên driver nhận diện lỗi này qua "phản hồi đầy đủ nhưng quá ngắn". Cần xác nhận trên
  phần cứng.
- S7netplus 0.20.0 chỉ đóng socket khi nó còn ở trạng thái "connected"; sau lỗi connect hoặc peer reset,
  socket cũ chờ GC thu hồi. Khi PLC mất kết nối lâu, tầng gọi driver nên retry có backoff thay vì gọi dồn dập.
- Chưa hỗ trợ kiểu `String`, `DTL`, `LReal`.
