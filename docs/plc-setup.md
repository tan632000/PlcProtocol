# Chuẩn bị PLC S7-1200 và máy Edge

Edge đọc/ghi PLC bằng giao thức **S7comm PUT/GET** (thư viện S7netplus 0.20.0) qua TCP cổng 102.
Tài liệu này liệt kê những gì cần cấu hình trên PLC và trên máy chạy Edge trước khi kết nối.

Lần đầu dùng TIA Portal: làm theo mục 0 từ đầu đến cuối. Mục 0 đã gồm cả các bước ở mục 1–3; mục 1–3 là
tham chiếu nhanh khi đã có sẵn project.

## 0. Tạo project mới trong TIA Portal (lần đầu)

Ví dụ dưới đây dùng PLC IP `192.168.3.20`, máy tính (PG/PC, cũng là máy chạy Edge) IP `192.168.3.100`, và một DB
mẫu khớp cấu hình tag trong `src/Edge/appsettings.json`. Tên menu có thể khác đôi chút giữa các bản TIA Portal
(V16–V20); bố cục chung giống nhau.

### 0.1. Ghi lại thông tin CPU thật

Trên mặt trước CPU (hoặc tem bên hông) ghi lại:

- **Mã đặt hàng** (Article number).
- **Phiên bản firmware**.

TIA cần đúng hai thông tin này; khai báo sai CPU hoặc firmware cao hơn CPU thật thì download sẽ bị từ chối.

CPU dùng cho dự án này (theo tem bên hông):

| Thông số | Giá trị |
|---|---|
| Model | SIMATIC S7-1200 **CPU 1214C AC/DC/RLY** |
| Mã đặt hàng | `6ES7 214-1BG40-0XB0` |
| FS (Functional State — phiên bản phần cứng, **không phải** firmware) | 13 (sản xuất 2023) |
| Firmware | **V4.5.1** (đọc qua Online & diagnostics) → khi Add new device chọn Version **V4.5** |
| Nguồn cấp | **85–264 VAC**, 47–63 Hz — cấp điện lưới vào cực L1/N, **không** cấp 24 VDC |
| Ngõ vào số (DI) | 14 × 24 VDC (`I0.0`–`I1.5`) |
| Ngõ ra số (DO) | 10 × relay, 2 A (`Q0.0`–`Q1.1`) |
| Ngõ vào analog (AI) | 2 × 0–10 VDC, 10 bit |
| IP PLC | `192.168.3.20` |
| Rack / Slot | 0 / 1 |

Tem không in firmware. Cách xem firmware (sau khi làm mục 0.2): TIA → **Online** → **Accessible devices** →
tìm thấy PLC → chọn PLC → **Show** → trong cây project mở **Online & diagnostics** → **General** → dòng
**Firmware**. Hoặc dùng cách **Detect** ở mục 0.3 để TIA tự điền đúng.

### 0.2. Đặt IP tĩnh cho máy tính

1. Cắm cáp Ethernet từ máy tính vào cổng PROFINET (X1) của CPU (trực tiếp hoặc qua switch).
2. Windows: **Settings** → **Network & Internet** → **Ethernet** → **IP assignment** → **Edit** → **Manual**, bật IPv4:
   - IP address: `192.168.3.100`
   - Subnet mask: `255.255.255.0`
   - Gateway: để trống
3. Chọn đúng card mạng đang cắm vào PLC (máy có nhiều card thì đừng đổi nhầm card Internet/Wi-Fi).

#### Máy chỉ có một cổng LAN: vừa dùng mạng văn phòng vừa vào PLC

Cách trên làm mất Internet nếu máy chỉ có một card mạng. Thay vào đó, nối chung qua một switch và gán thêm IP
thứ hai cho card, giữ nguyên IP DHCP của mạng văn phòng:

```
Ổ mạng văn phòng ──┐
PLC (cổng X1) ─────┼── Switch
PC ────────────────┘
```

PowerShell **Run as administrator** (Windows 10 2004+ / Windows 11; thay `"Ethernet"` bằng tên card trong
`Get-NetAdapter`):

```powershell
# Cho phép IP tĩnh tồn tại song song với IP DHCP
netsh interface ipv4 set interface "Ethernet" dhcpstaticipcoexistence=enabled
# Thêm IP cùng dải với PLC, không khai gateway để Internet vẫn đi qua mạng văn phòng
netsh interface ipv4 add address "Ethernet" 192.168.3.100 255.255.255.0
```

Kiểm tra:

```powershell
ipconfig                                       # card có cả IP DHCP và 192.168.3.100
ping 192.168.3.20                              # thấy PLC
Test-NetConnection 192.168.3.20 -Port 102      # TcpTestSucceeded : True
```

Gỡ IP thứ hai khi không cần: `netsh interface ipv4 delete address "Ethernet" 192.168.3.100`.

Lưu ý: cách này đưa PLC lên chung mạng văn phòng — ai trong mạng tự gán IP `192.168.3.x` cũng đọc/ghi được PLC
(PUT/GET không có xác thực). Chỉ dùng cho chạy thử; lâu dài nên tách mạng PLC (ví dụ thêm USB-LAN adapter riêng).

### 0.3. Tạo project và thêm CPU

1. Mở TIA Portal → **Portal view** → **Create new project**.
2. Nhập **Project name** (ví dụ `IIoT_Line1`), chọn **Path** → **Create**.
3. Chọn **Configure a device** → **Add new device** → **Controllers** → **SIMATIC S7-1200** → **CPU** →
   **CPU 1214C AC/DC/Rly** → **6ES7 214-1BG40-0XB0** → ô **Version** chọn đúng firmware đã xem ở 0.1 → **Add**.
   - Không biết mã CPU: chọn **Unspecified CPU 1200** → **Add**, sau đó trong Device view bấm dòng chữ
     **detect** trên CPU để TIA tự đọc cấu hình từ PLC thật (cần làm xong mục 0.2 trước).
4. Nếu TIA mở hộp thoại **PLC security settings** (bản TIA mới, firmware mới), với bài thử trong phòng lab:
   - **Protect confidential PLC configuration data**: có thể đặt mật khẩu hoặc bỏ qua.
   - **PG/PC and HMI communication**: để chế độ cho phép kết nối cũ (legacy) — bật chế độ "chỉ kết nối an toàn"
     có thể làm PUT/GET không chạy `[UNVERIFIED]`.
   - **Access level**: chọn **Full access (no protection)**.
   Các thiết lập này sửa lại được trong **Properties** của CPU.
5. TIA chuyển sang **Project view**, mở **Device configuration** với CPU `PLC_1`.

### 0.4. Cấu hình CPU

Bấm chọn CPU trong **Device view** → cửa sổ **Properties** (phía dưới) → tab **General**:

1. **PROFINET interface [X1]** → **Ethernet addresses**:
   - **Subnet**: bấm **Add new subnet** (tạo `PN/IE_1`).
   - **Set IP address in the project**: IP `192.168.3.20`, Subnet mask `255.255.255.0`.
   - Không tick **Use router** (không cần gateway khi máy tính và PLC cùng mạng).
2. **Protection & Security**:
   - **Access level**: **Full access (no protection)** (chỉ cho lab; production nên đặt mật khẩu).
   - **Connection mechanisms**: tick **Permit access with PUT/GET communication from remote partner**.
     Thiếu bước này Edge sẽ báo `PlcAccessDeniedException`.
3. (Tuỳ chọn) **System and clock memory**: tick **Enable the use of clock memory byte** (mặc định `MB0`) — có bit
   nhấp nháy `M0.5` (1 Hz) để thử đọc vùng `M` mà không cần viết chương trình.

### 0.5. Tạo Data Block chuẩn (không Optimized)

1. Cây project bên trái: **PLC_1** → **Program blocks** → **Add new block**.
2. Chọn **Data block (DB)**:
   - **Name**: `Line1`
   - **Type**: **Global DB**
   - **Number**: chọn **Manual**, nhập `1` (Edge dùng số DB trong địa chỉ, nên cố định số này).
   - Bấm **OK**.
3. Chuột phải `Line1 [DB1]` → **Properties** → **Attributes** → **bỏ tick Optimized block access** → **OK**.
   Làm bước này **trước** khi thêm biến (đổi sau sẽ phải compile lại và offset có thể thay đổi).
4. Mở DB1, thêm biến:

   | Name | Data type | Start value |
   |---|---|---|
   | `Running` | `Bool` | `false` |
   | `Speed` | `Int` | `100` |
   | `Setpoint` | `Int` | `1200` |
   | `Temperature` | `Real` | `25.5` |

5. Thanh công cụ → **Compile** (`Ctrl+B`). Sau khi compile, cột **Offset** hiện ra (chỉ có ở DB không Optimized).
   Với bảng trên, kết quả mong đợi:

   | Biến | Offset | Địa chỉ cho Edge | `DataType` |
   |---|---|---|---|
   | `Running` | `0.0` | `DB1.DBX0.0` | `Bool` |
   | `Speed` | `2.0` | `DB1.DBW2` | `Int` |
   | `Setpoint` | `4.0` | `DB1.DBW4` | `Int` |
   | `Temperature` | `6.0` | `DB1.DBD6` | `Real` |

   Luôn lấy offset từ cột **Offset** của TIA, không tự tính: `Int`/`Real` luôn bắt đầu ở byte chẵn nên có thể
   có byte trống (ví dụ byte 1 ở trên).

   Quy tắc tiền tố địa chỉ: `Bool` → `DBX<byte>.<bit>`; `Byte` → `DBB`; `Word`/`Int` → `DBW`;
   `DWord`/`DInt`/`Real` → `DBD`. Chưa hỗ trợ `String`, `DTL`, `LReal`.

Không cần viết chương trình trong `Main [OB1]` để thử đọc/ghi: giá trị được đổi bằng Watch table (mục 0.8).

### 0.6. Compile và download xuống PLC

1. Chọn `PLC_1` trong cây project → **Compile** → **Hardware and software (only changes)**; sửa hết lỗi
   (cửa sổ **Info** → tab **Compile**).
2. Kiểm tra kết nối: **Online** → **Accessible devices** → **Type of the PG/PC interface**: `PN/IE` →
   **PG/PC interface**: card mạng ở mục 0.2 → **Start search**. PLC phải hiện trong danh sách.
   - PLC mới xuất xưởng có thể chưa có IP (hiện `0.0.0.0` hoặc chỉ có MAC) — không sao, bước download sẽ
     gán IP.
3. Chọn `PLC_1` → **Download to device** (`Ctrl+L`). Ở hộp thoại **Extended download to device**:
   - **Type of the PG/PC interface**: `PN/IE`
   - **PG/PC interface**: card mạng ở mục 0.2
   - **Connection to interface/subnet**: `PN/IE_1`
   - **Start search** → chọn PLC tìm thấy → **Load**.
   - TIA hỏi gán thêm IP cho máy tính cùng subnet: chọn **Yes** nếu máy tính chưa có IP `192.168.3.x`.
4. Hộp thoại **Load preview**: nếu yêu cầu, chọn **Stop all** (dừng CPU) → **Load**.
5. Hộp thoại **Load results**: chọn **Start all** → **Finish**. Đèn **RUN/STOP** trên CPU chuyển xanh.

Mỗi lần đổi DB (thêm/xoá biến) phải compile + download lại, và kiểm tra lại cột Offset.

### 0.7. Kiểm tra từ máy Edge

PowerShell:

```powershell
ping 192.168.3.20
Test-NetConnection 192.168.3.20 -Port 102    # TcpTestSucceeded : True
```

Khai báo PLC và tag trong `src/Edge/appsettings.json` (lần đầu để mọi tag `"Writable": false`):

```json
"Plc": { "Host": "192.168.3.20", "Port": 102, "Rack": 0, "Slot": 1, "TimeoutMs": 3000 },
"Tags": [
  { "TagId": "Line1.Running",     "Address": "DB1.DBX0.0", "DataType": "Bool", "Writable": false },
  { "TagId": "Line1.Speed",       "Address": "DB1.DBW2",   "DataType": "Int",  "Writable": false },
  { "TagId": "Line1.Setpoint",    "Address": "DB1.DBW4",   "DataType": "Int",  "Writable": false },
  { "TagId": "Line1.Temperature", "Address": "DB1.DBD6",   "DataType": "Real", "Writable": false }
]
```

### 0.8. Đổi giá trị trên PLC để kiểm tra Edge đọc đúng

1. Cây project → **PLC_1** → **Watch and force tables** → **Add new watch table**.
2. Cột **Name** nhập: `"Line1".Running`, `"Line1".Speed`, `"Line1".Setpoint`, `"Line1".Temperature`.
3. Bấm **Monitor all** (biểu tượng kính đeo) → cột **Monitor value** hiện giá trị đang chạy trên PLC.
4. Nhập giá trị mới ở cột **Modify value** (ví dụ Speed = `250`) → chuột phải → **Modify** → **Modify now**.
5. Trên máy Edge mở trình duyệt vào **http://localhost:5080/** (trang **PLC Monitor** của phần mềm Edge, xem
   `docs/deployment.md`) và so cột **Giá trị** với cột **Monitor value** của TIA. Phía Cloud: API `/tags/latest`.

Mặc định hệ thống chỉ đọc. Chỉ khi khách cần ghi, và sau khi đã đọc đúng, mới bật đủ bốn công tắc (trong đó có
`"Writable": true` cho tag cần ghi, ví dụ `Line1.Setpoint`) theo `docs/deployment.md`, mục
"Chế độ read-only và cách bật ghi"; giá trị ghi xuống phải hiện trong watch table.

Lưu ý: TIA đang **online** với PLC cũng chiếm một connection slot. Edge vẫn kết nối được song song, nhưng
nên đóng các kết nối online thừa (**Go offline**) khi không cần.

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
| `CpuType` | `S71200` | `S71200` hoặc `S71500` |
| `TimeoutMs` | 3000 | 100..60000 — tổng thời gian một thao tác (chờ lượt + kết nối + đọc/ghi) |

S7-1200 là mặc định. Với S7-1500 đặt `"CpuType": "S71500"` (S7-1500 dùng Rack 0 / Slot 1 trong cấu hình Edge)
`[UNVERIFIED — chưa có máy S7-1500 để thử]`.

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

## 8. Mitsubishi FX5U (SLMP)

Với PLC Mitsubishi FX5U, đặt `"Protocol": "Slmp"` trong `Edge:Plc`: Edge đọc qua giao thức **SLMP** (khung 3E,
dữ liệu nhị phân, TCP). Giao thức mặc định vẫn là `S7`. Edge **không bao giờ ghi** vào PLC qua SLMP; cấu hình
`Edge:Commands:Enabled = true` cùng `Protocol = Slmp` làm Edge dừng ngay khi khởi động. PLC Monitor
(`http://localhost:5080/`) hiện giao thức, IP và cổng (Rack/Slot chỉ hiện với S7).

### 8.1. Cấu hình trong GX Works3

> [UNVERIFIED — tên và vị trí các mục menu bên dưới (Module Parameter → Ethernet Port, External Device Configuration,
> Alive Check, Communication Data Code, việc phải reset sau khi ghi parameter) chưa được đối chiếu trên GX Works3 thật;
> sẽ kiểm khi chạy thử với FX5U (task-07 của specs/plc-multi-protocol) và sửa lại nếu khác.]

Vào **Navigation** → **Parameter** → **FX5UCPU** → **Module Parameter** → **Ethernet Port**:

1. **Basic Settings** → **Own Node Settings**: đặt IP của PLC (ví dụ `192.168.3.250`) và subnet mask.
2. **External Device Configuration** → **Detailed Settings** → kéo **SLMP Connection Module** vào bảng, rồi đặt:
   - **Protocol**: `TCP`.
   - **Port No.** (cổng của PLC): ví dụ `5000`. Không dùng `102` (cổng S7; Edge từ chối `Port` 102 cho Slmp);
     `Port` trong cấu hình Edge phải đúng bằng giá trị này.
   - **Partner IP / Destination IP**: IP của máy Edge (ví dụ `192.168.3.100`).
   - **Alive Check**: bật (**Enabled**) để PLC giải phóng kết nối cũ khi Edge chết giữa chừng.
3. **Communication Data Code**: **Binary**. Nếu chọn ASCII, Edge nhận khung ASCII và báo lỗi subheader.
4. **IP Filter Settings**: chỉ cho phép IP của máy Edge. SLMP không có xác thực, ai tới được cổng này đều đọc được
   PLC.
5. **Online Change**: chọn **Disable All (SLMP)** để SLMP không thể ghi/đổi dữ liệu từ xa.
6. **Write** parameter xuống PLC rồi **reset hoặc cấp nguồn lại** (thông số Ethernet chỉ có hiệu lực sau đó).

Tác dụng chính xác của từng lớp bảo vệ (IP Filter chặn kết nối từ IP khác ở mức nào, **Disable All (SLMP)** chặn
những lệnh nào, Alive Check đóng kết nối sau bao lâu) `[UNVERIFIED — kiểm trên PLC bench theo FX5 communication
manual]`. Edge chỉ gửi lệnh đọc nên cấu hình trên không ảnh hưởng việc đọc.

### 8.2. Kết nối và địa chỉ

- **Một Edge cho mỗi kết nối SLMP.** Khi thử từ laptop, tắt các client khác (GX Works3 monitor, Edge khác) và service
  `IndustrialIot.Edge` đã cài, nếu không các kết nối tranh nhau slot của PLC.
- Kiểm tra mạng: `Test-NetConnection 192.168.3.250 -Port 5000` → `TcpTestSucceeded : True`.
- `TimeoutMs` hợp lệ: 1000..60000 (mặc định 3000). `Port` là bắt buộc.

| Địa chỉ | Ý nghĩa | `DataType` |
|---|---|---|
| `D<n>` | Thanh ghi dữ liệu, `n` thập phân (ví dụ `D0`, `D100`) | `Int`, `Word`, `DInt`, `DWord`, `Real` |
| `M<n>` | Relay nội bộ, `n` thập phân (ví dụ `M10`) | `Bool` |
| `X<n>` | Ngõ vào, `n` **bát phân** đúng như GX Works3 hiện | `Bool` |
| `Y<n>` | Ngõ ra, `n` **bát phân** đúng như GX Works3 hiện | `Bool` |

- Kiểu 2 word (`DInt`, `DWord`, `Real`) chiếm `D<n>` và `D<n+1>`, **word thấp trước** (little-endian): ví dụ `Real`
  tại `D10` dùng `D10` (word thấp) và `D11` (word cao).
- `X`/`Y` theo bát phân: `X17` là ngõ vào thứ 16 (`X0`–`X7`, `X10`–`X17`); `X8` và `X9` **không tồn tại**.
- Tag SLMP luôn chỉ đọc; không dùng `"Writable": true`.

### 8.3. Cấu hình Edge

```json
{
  "Edge": {
    "Plc": { "Protocol": "Slmp", "Host": "192.168.3.250", "Port": 5000, "TimeoutMs": 3000 },
    "Commands": { "Enabled": false },
    "Tags": [
      { "TagId": "Line1.Speed",       "Address": "D0",   "DataType": "Int",  "Writable": false },
      { "TagId": "Line1.Temperature", "Address": "D10",  "DataType": "Real", "Writable": false },
      { "TagId": "Line1.Running",     "Address": "M0",   "DataType": "Bool", "Writable": false },
      { "TagId": "Line1.Start",       "Address": "X17",  "DataType": "Bool", "Writable": false },
      { "TagId": "Line1.Motor",       "Address": "Y10",  "DataType": "Bool", "Writable": false }
    ]
  }
}
```

Giới hạn: driver SLMP mới được kiểm thử với PLC giả lập trên loopback (`FakeSlmpServer`), **chưa** thử trên FX5U
thật `[UNVERIFIED — kiểm trên PLC bench]`.
