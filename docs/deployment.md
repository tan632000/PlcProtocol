# Triển khai stack chạy thử (trial stack)

Tài liệu này hướng dẫn dựng toàn bộ phía Cloud trên **một máy** (máy dev hoặc một máy chủ nhỏ trong mạng
xưởng) bằng Docker: PostgreSQL, broker MQTT Mosquitto (TLS + phân quyền theo từng danh tính), Keycloak
(cấp token) và Cloud API. Edge chạy trên máy Windows cạnh PLC và kết nối tới broker qua mạng LAN — xem mục
[Edge](#edge-tren-windows).

> **Đây là stack để chạy thử, không phải production.** Xem mục [Giới hạn bảo mật](#gioi-han-bao-mat) trước
> khi dùng.

## 1. Chuẩn bị

| Cần có | Ghi chú |
|---|---|
| Docker Desktop (Windows) hoặc Docker Engine + compose v2 (Linux) | `docker compose version` phải chạy được |
| Git for Windows | Dùng **Git Bash** (`C:\Program Files\Git\bin\bash.exe`) để chạy các script `.sh`. Lệnh `bash` gõ trong PowerShell/CMD thường là **WSL**, không phải Git Bash — đừng dùng nó. |
| IP tĩnh hoặc tên máy cố định cho máy chạy stack | Edge kiểm tra chứng chỉ của broker theo tên/IP này. IP đổi (DHCP) → Edge không kết nối được. |
| Cổng 8883 mở từ máy Edge | Docker Desktop trên Windows cần thêm rule inbound cho TCP 8883 (xem mục 5). |

Các lệnh dưới đây chạy trong **Git Bash** tại thư mục gốc của repo.

## 2. Tạo secret

```bash
bash deployments/scripts/dev-secrets.sh --san IP:192.168.1.50 --edge line1-edge01
```

- `--san` là IP (hoặc `DNS:ten-may`) mà **Edge** dùng để gọi tới broker; có thể lặp lại nhiều lần.
- `--edge` là `EdgeId` của từng Edge (mặc định `line1-edge01`). EdgeId là tên đăng nhập MQTT của Edge, nên
  phải khớp `Edge:EdgeId` và `Edge:Mqtt:Username` trên máy Edge.
- Kết quả nằm trong `deployments/.secrets/` (đã có trong `.gitignore`): CA dev, chứng chỉ, mật khẩu ngẫu nhiên,
  file mật khẩu của broker và các file `.env` cho compose. Script **không bao giờ in ra giá trị bí mật** và từ
  chối ghi đè một bộ secret đã có.
- `ca.key` chỉ nằm trong thư mục này, không bao giờ được mount vào container. Giữ kỹ: ai có nó là giả mạo được
  broker.

### Thêm một Edge sau này

```bash
bash deployments/scripts/dev-secrets.sh --add-edge line2-edge01
docker compose -f deployments/compose.yaml up -d --force-recreate mosquitto cloud
```

Dùng `--force-recreate`, **không** dùng `restart`: `restart` giữ nguyên biến môi trường cũ, nên Cloud sẽ không
thấy Edge mới trong allow-list.

Bộ secret tạo bằng phiên bản script cũ (trước khi có dòng `Cloud__Edges__N__DisplayName`) sẽ hiển thị nhãn mặc
định "Line 1" cho Edge đầu tiên. Thêm dòng `Cloud__Edges__0__DisplayName=<EdgeId>` vào
`deployments/.secrets/cloud.env` (hoặc tạo lại bộ secret) rồi `--force-recreate cloud`.

## 3. Khởi động / dừng

```bash
docker compose -f deployments/compose.yaml up -d --build --wait     # lần đầu build mất vài phút
docker compose -f deployments/compose.yaml ps
curl http://127.0.0.1:8080/health/ready                              # {"status":"ready"} khi DB + MQTT sẵn sàng
docker compose -f deployments/compose.yaml down                      # dừng, giữ dữ liệu
docker compose -f deployments/compose.yaml down -v                   # dừng và XOÁ dữ liệu (DB, hàng đợi MQTT)
```

| Dịch vụ | Địa chỉ | Ai truy cập được |
|---|---|---|
| Cloud API | `http://127.0.0.1:8080` | chỉ chính máy này (HTTP, không TLS) |
| Keycloak | `https://127.0.0.1:8443` (chỉ dùng cho token qua `get-token.sh`; trang admin chuyển hướng sang `https://keycloak:8443`, cần thêm `127.0.0.1 keycloak` vào file hosts nếu muốn mở) | chỉ chính máy này |
| Mosquitto | `mqtts://<IP máy>:8883` | cả mạng LAN (Edge cần) |
| PostgreSQL | không mở cổng ra ngoài | chỉ các container |

## 4. Lấy token và gọi API

Hai user mẫu: `viewer` (đọc) và `operator` (đọc + gửi lệnh). Mật khẩu nằm trong
`deployments/.secrets/passwords/` (`viewer.txt`, `operator.txt`; mật khẩu admin Keycloak là `keycloak-admin.txt`).

```bash
TOKEN=$(bash deployments/scripts/get-token.sh viewer)
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:8080/api/v1/edges

OP=$(bash deployments/scripts/get-token.sh operator)
curl -X POST -H "Authorization: Bearer $OP" -H "Content-Type: application/json" \
  -d '{"tagId":"Line1.Setpoint","value":1200}' \
  http://127.0.0.1:8080/api/v1/edges/line1-edge01/commands
```

`get-token.sh` gọi Keycloak từ bên trong mạng compose, nên token luôn có issuer
`https://keycloak:8443/realms/iiot` — đúng địa chỉ Cloud dùng để kiểm tra. Lấy token bằng trình duyệt hay gọi
thẳng `https://127.0.0.1:8443` sẽ ra token Cloud không chấp nhận.

> ⚠️ Lệnh POST ở trên **ghi thật xuống PLC** nếu Edge `line1-edge01` đang chạy và tag đó được khai báo ghi được.
> Lần đầu hãy cấu hình Edge chỉ đọc (xem `docs/plc-setup.md`).

## 5. Mở cổng 8883 cho Edge (Windows + Docker Desktop)

PowerShell chạy với quyền Administrator trên máy chạy stack:

```powershell
New-NetFirewallRule -DisplayName "IIoT MQTT 8883" -Direction Inbound -Protocol TCP -LocalPort 8883 -Action Allow -Profile Domain,Private
```

Kiểm tra từ máy Edge: `Test-NetConnection <IP máy stack> -Port 8883`.

## 6. Kiểm tra stack (smoke test)

```bash
bash deployments/scripts/smoke-test.sh
```

Smoke test dựng một stack **riêng** (project `iiot-smoke`, secret tạm, Edge giả `smoke-edge`), chạy 8 bước
kiểm tra `SMOKE-00`…`SMOKE-07` rồi xoá sạch. Nó **từ chối chạy** (exit 2) khi stack thật đang chạy (cổng
8080/8443/8883 bận), nên không bao giờ đụng tới dữ liệu chạy thử hay gửi lệnh tới Edge thật.

## 7. Xử lý sự cố

| Triệu chứng | Nguyên nhân thường gặp | Cách xử lý |
|---|---|---|
| `dev-secrets.sh` báo "not empty" | Đã có bộ secret | Dùng `--add-edge`, hoặc xoá `deployments/.secrets` (mất mọi mật khẩu, phải cài lại Edge). Trên Linux hai thư mục `mosquitto/` và `keycloak/` thuộc uid của container: xoá bằng `sudo rm -rf deployments/.secrets` |
| `/health/ready` trả 503 | DB hoặc MQTT chưa sẵn sàng | `docker compose -f deployments/compose.yaml logs cloud mosquitto` |
| API trả 401 với token hợp lệ | Token lấy từ địa chỉ khác `keycloak:8443` | Dùng `get-token.sh` |
| Edge báo lỗi chứng chỉ | IP/tên Edge dùng không có trong `--san` | Tạo lại secret với `--san` đúng (phải cài lại Edge) |
| Edge kết nối được nhưng Cloud không nhận dữ liệu | EdgeId chưa có trong allow-list hoặc Username ≠ EdgeId | `--add-edge` + `--force-recreate`; kiểm tra cấu hình Edge |
| Mosquitto log "not authorised" | Sai mật khẩu, sai username, hoặc publish ra topic của Edge khác | Kiểm tra `Edge__Mqtt__Password` và EdgeId |

## Giới hạn bảo mật

Stack này đánh đổi bảo mật để dựng nhanh. Chỉ dùng trên **mạng tin cậy**, **máy một người dùng**, cho **chạy thử**:

- Cloud API là **HTTP không TLS**, chỉ mở trên `127.0.0.1`. Chưa có reverse proxy TLS (ngoài phạm vi đợt này).
- Chứng chỉ do **CA dev** tự ký; Keycloak chạy **dev mode** và cho phép password grant (`iiot-cli`).
- Mật khẩu đi vào container qua `env_file`: ai thuộc nhóm `docker` / `docker-users` đều xem được bằng
  `docker inspect`.
- Docker publish cổng **vượt qua rule firewall của host trên Linux** (iptables của Docker); cổng 8883 mở cho cả
  mạng LAN. Broker vẫn bắt buộc TLS + mật khẩu + ACL theo danh tính: mỗi Edge chỉ đọc/ghi được topic của chính
  nó, chỉ Cloud được gửi lệnh.
- Trên Windows, quyền file trong `deployments/.secrets` chỉ được bảo vệ bởi quyền thư mục người dùng (NTFS); trên
  Linux, key và file mật khẩu thuộc riêng uid của container (0600).

## Edge trên Windows

Edge chạy trên máy Windows cạnh PLC (nối mạng với PLC qua cổng 102 và với máy stack qua cổng 8883). Máy Edge
**không cần** .NET SDK, mã nguồn hay Internet: gói cài đặt được tạo sẵn trên máy dev.

### 1. Đóng gói (trên máy dev)

```powershell
powershell -ExecutionPolicy Bypass -File deployments\edge\package-edge.ps1
# In ra đường dẫn file zip, ví dụ %TEMP%\iiot-edge-win-x64.zip (khoảng 30-40 MB, đã kèm runtime .NET)
```

Chép sang máy Edge: file zip đó và `deployments\.secrets\ca.pem`. Mật khẩu MQTT của Edge nằm ở
`deployments\.secrets\passwords\<EdgeId>.txt` — mở file để copy khi được hỏi, **đừng** chép file này sang máy Edge.

### 2. Chuẩn bị danh sách tag (khuyến nghị: chỉ đọc trước)

Sau khi cài, danh sách tag nằm trong `C:\Program Files\IndustrialIot\Edge\appsettings.json` (mục `Edge:Tags`).
Lần chạy đầu, để mọi tag `"Writable": false` và kiểm tra giá trị đọc được khớp với TIA Portal (chế độ Monitor)
trước khi cho phép ghi. Xem `docs/plc-setup.md` để chuẩn bị PLC (PUT/GET, tắt Optimized block access).

### 3. Cài đặt (PowerShell **Run as administrator** trên máy Edge)

```powershell
Expand-Archive C:\Temp\iiot-edge-win-x64.zip C:\Temp\iiot-edge     # chỉ để lấy script cài
cd C:\Temp\iiot-edge
powershell -ExecutionPolicy Bypass -File .\install-edge.ps1 `
  -PackagePath C:\Temp\iiot-edge-win-x64.zip -EdgeId line1-edge01 `
  -BrokerHost 192.168.1.50 -CaCertificate C:\Temp\ca.pem -PlcHost 192.168.0.10
# Script hỏi mật khẩu MQTT (gõ/dán vào, không hiện ra màn hình)
```

Script sẽ:

- giải nén vào `C:\Program Files\IndustrialIot\Edge` (chỉ Administrators/SYSTEM sửa được; service chỉ đọc);
- tạo `C:\ProgramData\IndustrialIot\Edge` cho buffer và chứng chỉ (chỉ SYSTEM, Administrators và service);
- tạo service `IndustrialIot.Edge` chạy bằng tài khoản ảo `NT SERVICE\IndustrialIot.Edge`, tự khởi động cùng
  Windows và tự khởi động lại khi lỗi (3 lần, cách nhau 5 giây);
- lưu cấu hình và mật khẩu MQTT vào biến môi trường của service (registry), khoá lại để chỉ SYSTEM,
  Administrators và chính service đọc được.

Gặp lỗi giữa chừng, script tự gỡ những gì đã tạo. `-EdgeId` phải trùng EdgeId đã tạo bằng `dev-secrets.sh`;
`-BrokerHost` phải là IP/tên đã đưa vào `--san`.

### 4. Kiểm tra và vận hành

```powershell
Get-Service IndustrialIot.Edge                         # Running
Get-WinEvent -LogName Application -ProviderName IndustrialIot.Edge -MaxEvents 20 | Format-List TimeCreated, Message
Restart-Service IndustrialIot.Edge                     # sau khi sửa appsettings.json
```

Trên máy stack: `curl -H "Authorization: Bearer $(bash deployments/scripts/get-token.sh viewer)" http://127.0.0.1:8080/api/v1/edges/line1-edge01/tags/latest`
phải trả về giá trị tag mới nhất.

#### Màn hình PLC Monitor trên máy Edge

Mở trình duyệt bất kỳ (Chrome, Microsoft Edge…) **trên chính máy chạy phần mềm Edge**: **http://localhost:5080/** (trang **PLC Monitor**). Trang tự cập nhật mỗi giây và hiển thị:

- trạng thái PLC (Đọc tốt / Có tag lỗi / Mất kết nối / Dữ liệu cũ), IP, Rack/Slot;
- thời điểm chu kỳ đọc gần nhất, trạng thái MQTT, số mẫu đang chờ gửi trong buffer;
- bảng tag: địa chỉ, kiểu, giá trị, chất lượng, thời điểm đọc, quyền ghi và lý do lỗi (nếu có).

Trang **chỉ xem**: không ghi xuống PLC, không mở thêm kết nối tới PLC (dùng kết quả của chính vòng đọc), và
chỉ nghe trên `localhost` — máy khác trong mạng không truy cập được. Cấu hình trong `appsettings.json`:

```json
"Monitor": { "Enabled": true, "Port": 5080 }
```

Cổng 5080 đã bị chương trình khác chiếm thì Edge vẫn chạy bình thường, chỉ ghi lỗi
`Monitor page disabled: port ... is not available` vào log — đổi `Port` rồi `Restart-Service IndustrialIot.Edge`.

### 5. Gỡ cài đặt

Script gỡ cũng nằm sẵn trong thư mục cài đặt. Nó tự đọc thư mục cần xoá từ service đã đăng ký (không nhận
đường dẫn từ tham số, nên không thể xoá nhầm thư mục khác):

```powershell
powershell -ExecutionPolicy Bypass -File "C:\Program Files\IndustrialIot\Edge\uninstall-edge.ps1"              # giữ dữ liệu
powershell -ExecutionPolicy Bypass -File "C:\Program Files\IndustrialIot\Edge\uninstall-edge.ps1" -RemoveData  # xoá cả buffer
```

Danh sách tag được giữ lại ở `C:\ProgramData\IndustrialIot\Edge\appsettings.backup.json`. Sau khi cài lại, chép
file này đè lên `C:\Program Files\IndustrialIot\Edge\appsettings.json` rồi `Restart-Service IndustrialIot.Edge`.

Nếu script cài báo một thư mục `IndustrialIot` "not owned by Administrators or SYSTEM", có người đã tạo sẵn thư
mục đó: kiểm tra nội dung, xoá nó (hoặc `takeown /f <thư mục> /a /r`) rồi cài lại. Script cố ý từ chối, vì chủ
sở hữu thư mục luôn tự cấp lại được quyền cho mình và có thể thay chứng chỉ CA. Báo "is a link or junction" nghĩa
là trong thư mục có liên kết trỏ ra ngoài: xem nó trỏ đi đâu (`dir /al /s <thư mục>`), xoá liên kết rồi cài lại.

Nếu lần gỡ trước dừng giữa chừng (service đã xoá nhưng thư mục còn), script gỡ sẽ báo "not installed". Khi đó gỡ
nốt bằng tay trong PowerShell admin:

```powershell
Copy-Item "C:\Program Files\IndustrialIot\Edge\appsettings.json" "C:\ProgramData\IndustrialIot\Edge\appsettings.backup.json"
cmd /c rmdir /s /q "C:\Program Files\IndustrialIot\Edge"   # rmdir gỡ liên kết chứ không đi theo nó
cmd /c rmdir /s /q "C:\ProgramData\IndustrialIot\Edge"     # chỉ khi muốn bỏ cả buffer
```

### Chạy thử không cài service

Trên máy có .NET SDK có thể chạy trực tiếp (dừng bằng Ctrl+C):

```powershell
$secure = Read-Host "MQTT password" -AsSecureString        # không hiện lên màn hình
$env:Edge__Mqtt__Password = [Net.NetworkCredential]::new('', $secure).Password   # chỉ tồn tại trong cửa sổ này
$env:Edge__EdgeId = "line1-edge01"; $env:Edge__Mqtt__Username = "line1-edge01"
$env:Edge__Mqtt__Host = "192.168.1.50"; $env:Edge__Mqtt__CaCertificatePath = "C:\Temp\ca.pem"
dotnet run --project src\Edge --launch-profile IndustrialIot.Edge   # không dùng profile "PLC test" (nó ghi đè MQTT bằng broker giả)
```

#### Chạy từ Visual Studio

1. Mở `IndustrialIot.sln`.
2. Solution Explorer → chuột phải `IndustrialIot.Edge` → **Set as Startup Project**.
3. Trên thanh công cụ, cạnh nút ▶, chọn profile:
   - **PLC test (no broker)** (mặc định): chỉ đọc PLC, chưa cần broker MQTT (MQTT báo "chưa kết nối" là bình thường);
     dữ liệu nằm tạm ở `src/Edge/data/`.
   - **IndustrialIot.Edge**: chạy thật với broker — đặt `Edge__Mqtt__*` như đoạn PowerShell ở trên, hoặc thêm vào
     profile qua **Debug → IndustrialIot.Edge Debug Properties** (không commit mật khẩu thật).
4. Bấm ▶ (**F5** có debug, **Ctrl+F5** không debug), rồi mở **http://localhost:5080/** (PLC Monitor).
5. Sửa `src/Edge/appsettings.json` (IP PLC, danh sách tag) → **dừng** (Shift+F5) rồi chạy lại: Edge chỉ đọc cấu hình
   lúc khởi động.

Không chạy hai Edge cùng lúc (Visual Studio + service đã cài): cùng chiếm cổng 5080 và thêm một kết nối tới PLC.

### Kiểm tra bộ cài (cho người phát triển)

```powershell
powershell -ExecutionPolicy Bypass -File deployments\edge\verify-edge.ps1          # không cần quyền admin
powershell -ExecutionPolicy Bypass -File deployments\edge\verify-edge.ps1 -Live    # PowerShell admin: cài thử rồi gỡ
```

`-Live` cài thật vào thư mục riêng `EdgeVerify` với EdgeId giả và broker/PLC không tồn tại, kiểm tra service,
quyền thư mục, quyền registry, rồi gỡ sạch. Nó từ chối chạy nếu máy đã có Edge thật.
