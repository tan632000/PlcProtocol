# Checklist triển khai tại nhà máy khách hàng

Trình tự cài đặt từ đầu đến cuối. Mỗi bước có ô `[ ]` để đánh dấu khi làm xong; chi tiết từng bước nằm ở
[plc-setup.md](plc-setup.md) (PLC, mạng) và [deployment.md](deployment.md) (server, Edge).

> ⚠️ Stack hiện tại là **bản chạy thử** (API HTTP không TLS, CA tự ký, Keycloak dev mode). Đọc mục
> [Giới hạn bảo mật](deployment.md#gioi-han-bao-mat) và thống nhất với khách hàng trước khi dùng lâu dài.

## Mô hình

```
[PLC S7-1200] ←S7 / TCP 102→ [Máy Edge] ←MQTT/TLS 8883→ [Máy Server] ←HTTP API→ người dùng
  mạng máy                     Windows, cạnh PLC          Docker: Mosquitto, PostgreSQL,
                               service IndustrialIot.Edge  Keycloak, Cloud API
```

Máy Edge và máy Server **có thể là cùng một máy** (bản nhỏ). Không bao giờ mở cổng 102 ra ngoài mạng nhà máy.

## Ghi lại thông tin trước khi đi

| Thông tin | Ví dụ (lab) | Tại khách hàng |
|---|---|---|
| Model CPU, mã đặt hàng | CPU 1214C AC/DC/RLY, `6ES7 214-1BG40-0XB0` | |
| Firmware CPU | V4.5.1 | |
| IP PLC | `192.168.3.20` | |
| IP máy Edge (dải PLC) | `192.168.3.100` | |
| IP máy Server (LAN) | `172.16.2.62` | |
| EdgeId (mỗi Edge một mã) | `line1-edge01` | |
| Danh sách tag (tên, địa chỉ, kiểu, có ghi không) | xem `src/Edge/appsettings.json` | |

## Giai đoạn 0 — Chuẩn bị (tại văn phòng)

- [ ] **Máy dev**: .NET SDK 10, Visual Studio (hoặc chỉ SDK), PowerShell. Dùng để đóng gói Edge.
- [ ] **TIA Portal** đúng hoặc mới hơn phiên bản project của khách, có **license STEP 7 thật** (trial chỉ 21 ngày, mỗi máy
      một lần).
- [ ] **Máy Server**: Windows/Linux có Docker (Docker Desktop hoặc Engine + compose v2), Git for Windows (Git Bash) nếu là
      Windows, **IP tĩnh**.
- [ ] **Máy Edge**: Windows 10/11 x64, không cần .NET SDK hay Internet; đồng bộ giờ NTP (`w32tm /query /status`).
- [ ] Chạy test trên máy dev: `dotnet test IndustrialIot.sln` → tất cả pass.
- [ ] (Nếu được) chạy smoke test stack trên máy Server **trước khi** dựng stack thật:
      `bash deployments/scripts/smoke-test.sh` — nó từ chối chạy khi cổng 8080/8443/8883 đang bận.

## Giai đoạn 1 — PLC (TIA Portal) → [plc-setup.md mục 0](plc-setup.md)

- [ ] Nếu PLC đang chạy chương trình của khách: **Upload device as new station** để sao lưu trước, hỏi khách được phép
      download hay không. **Không** download đè chương trình đang điều khiển máy khi chưa có đồng ý.
- [ ] CPU → Protection & Security → **Permit access with PUT/GET** bật.
- [ ] Mọi DB Edge cần đọc/ghi: **tắt Optimized block access**, compile, ghi lại cột **Offset**.
- [ ] Compile + download (hoặc chỉ download phần thay đổi), CPU ở **RUN**.
- [ ] Lập bảng tag: địa chỉ không có dấu `%` (`DB1.DBW2`, `I0.0`, `IW64`, `M10.0`…), kiểu dữ liệu, mặc định **chỉ đọc**.

## Giai đoạn 2 — Mạng → [plc-setup.md mục 0.2, mục 4](plc-setup.md)

- [ ] Máy Edge thấy PLC: `Test-NetConnection <IP PLC> -Port 102` → `TcpTestSucceeded : True`.
      Máy Edge chỉ có một cổng LAN: thêm IP thứ hai bằng `netsh` (plc-setup.md mục 0.2).
- [ ] Máy Edge thấy Server: `Test-NetConnection <IP Server> -Port 8883`.
- [ ] Máy Server (Windows + Docker Desktop) mở firewall inbound TCP 8883 (deployment.md mục 5).

## Giai đoạn 3 — Máy Server → [deployment.md mục 2–4](deployment.md)

Chạy trong **Git Bash** tại thư mục repo trên máy Server.

- [ ] Tạo secret (một `--edge` cho mỗi Edge, `--san` là IP/tên mà **Edge** dùng để gọi broker):
      ```bash
      bash deployments/scripts/dev-secrets.sh --edge line1-edge01 --san IP:<IP Server> --san DNS:localhost --san IP:127.0.0.1
      ```
- [ ] **Sao lưu** thư mục `deployments/.secrets/` vào nơi an toàn của dự án (có `ca.key` và mọi mật khẩu; mất là phải
      cài lại toàn bộ Edge). Không gửi qua chat/email.
- [ ] Kiểm tra cổng 8080 còn trống (`netstat -ano | findstr :8080`). Bận thì đặt cổng khác cho API — phải đặt
      **ở mọi lệnh compose** sau này:
      ```bash
      export IIOT_API_PORT=8090      # chỉ khi 8080 bận
      docker compose -f deployments/compose.yaml up -d --build --wait
      ```
- [ ] `curl http://127.0.0.1:<port API>/health/ready` → `{"status":"ready"}`.
- [ ] Lấy token thử: `bash deployments/scripts/get-token.sh viewer` ra một chuỗi dài.

## Giai đoạn 4 — Đóng gói Edge (máy dev)

- [ ] Sửa `src/Edge/appsettings.json`: danh sách `Tags` của khách (mọi tag `"Writable": false` lần đầu),
      `Poll:IntervalMs`. IP PLC/broker/EdgeId **không** cần sửa ở đây — script cài đặt ghi chúng.
- [ ] Đóng gói:
      ```powershell
      powershell -ExecutionPolicy Bypass -File deployments\edge\package-edge.ps1
      ```
- [ ] Chép sang máy Edge: file zip vừa tạo + `deployments\.secrets\ca.pem`.
      **Không** chép file mật khẩu; mở `deployments\.secrets\passwords\<EdgeId>.txt` trên máy Server để copy khi cài.

## Giai đoạn 5 — Cài Edge (máy Edge) → [deployment.md mục Edge trên Windows](deployment.md)

PowerShell **Run as administrator**:

- [ ] ```powershell
      Expand-Archive C:\Temp\iiot-edge-win-x64.zip C:\Temp\iiot-edge
      cd C:\Temp\iiot-edge
      powershell -ExecutionPolicy Bypass -File .\install-edge.ps1 `
        -PackagePath C:\Temp\iiot-edge-win-x64.zip -EdgeId line1-edge01 `
        -BrokerHost <IP Server> -CaCertificate C:\Temp\ca.pem -PlcHost <IP PLC>
      ```
      Script hỏi mật khẩu MQTT → dán từ `passwords\<EdgeId>.txt`.
- [ ] `Get-Service IndustrialIot.Edge` → `Running`.
- [ ] Xoá `C:\Temp\iiot-edge*` và `ca.pem` bản chép tạm sau khi cài.

## Giai đoạn 6 — Nghiệm thu đọc

- [ ] Trên máy Edge mở **http://localhost:5080/** (PLC Monitor):
  - PLC **Đọc tốt**, mọi tag chất lượng **Tốt**;
  - MQTT **Đã kết nối**; **Buffer chờ gửi** về gần 0.
- [ ] So từng tag với **Watch table** trong TIA (đổi vài giá trị bằng Modify, xem PLC Monitor đổi theo trong ~1 giây).
- [ ] Trên máy Server:
      ```bash
      TOKEN=$(bash deployments/scripts/get-token.sh viewer)
      curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:<port API>/api/v1/edges/<EdgeId>/tags/latest
      ```
      ra đúng giá trị như PLC Monitor.
- [ ] Rút dây mạng Edge ↔ Server 1–2 phút rồi cắm lại: Buffer tăng rồi tự giảm về 0, API không mất dữ liệu.

## Giai đoạn 7 — Bật ghi (chỉ khi khách yêu cầu)

- [ ] Thống nhất với khách tag nào được ghi, khoảng giá trị (`Min`/`Max`). Ưu tiên ghi vào **DB/M**, để chương trình
      PLC quyết định xuất ra **Q**. Vùng **I** không bao giờ ghi được.
- [ ] Sửa `C:\Program Files\IndustrialIot\Edge\appsettings.json` → `"Writable": true` cho đúng tag →
      `Restart-Service IndustrialIot.Edge`.
- [ ] Gửi một lệnh thử bằng user `operator` (deployment.md mục 4), kiểm tra giá trị trong Watch table, rồi trả về giá
      trị cũ.

## Giai đoạn 8 — Bàn giao

- [ ] Ghi vào biên bản: IP các máy, EdgeId, danh sách tag, cổng API, phiên bản phần mềm (commit git).
- [ ] Bàn giao tài khoản `viewer`/`operator` cho đúng người; mật khẩu admin Keycloak giữ phía dự án.
- [ ] Nhắc khách: không mở cổng 102 ra ngoài, không chạy hai Edge cùng trỏ vào một PLC, máy Edge giữ NTP.
- [ ] Sao lưu: `deployments/.secrets/`, `C:\Program Files\IndustrialIot\Edge\appsettings.json` (danh sách tag), project
      TIA đã download.

## Gỡ / cài lại

- Gỡ Edge: `powershell -ExecutionPolicy Bypass -File "C:\Program Files\IndustrialIot\Edge\uninstall-edge.ps1"`
  (thêm `-RemoveData` để xoá buffer). Danh sách tag được giữ ở `C:\ProgramData\IndustrialIot\Edge\appsettings.backup.json`.
- Dừng stack: `docker compose -f deployments/compose.yaml down` (giữ dữ liệu); `down -v` **xoá** dữ liệu.
