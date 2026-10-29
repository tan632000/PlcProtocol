# Triển khai lên server AWS (CI/CD)

Tài liệu này dành cho chủ repo (`tan632000`) và người vận hành server. Phần Cloud (Cloud API, Mosquitto, PostgreSQL,
Keycloak) chạy trên **một server Linux trên AWS**. Edge vẫn cài trên **máy Windows của khách** bằng `install-edge.ps1`,
CD không động tới Edge.

Luồng tự động:

1. Mở pull request vào `main` thì CI (`.github/workflows/ci.yml`) chạy job `test` (toàn bộ test trên Linux) và job `smoke`
   (smoke test của stack).
2. Merge vào `main` thì CD (`.github/workflows/cd.yml`) chạy lại `test`, build image Cloud, đẩy lên GHCR
   (`ghcr.io/tan632000/plcprotocol/cloud:<sha>`), rồi chờ người duyệt environment `production`.
3. Khi được duyệt, CD SSH vào server bằng một key **chỉ chạy được `remote-deploy.sh`**. Script này chỉ thay container
   Cloud sang image mới, kiểm tra `/health/ready`, và từ chối mọi trạng thái không an toàn.

CD **không bao giờ** đụng tới secret, broker (ACL), Keycloak hay `compose.yaml`. Một commit sửa các file đó sẽ bị từ chối
và phải nâng cấp thủ công (mục 7), nên một lần deploy không thể đổi ACL hay biến môi trường của server.

Nhưng code Cloud và chính `remote-deploy.sh` lấy từ commit được deploy, và chạy với quyền tương đương root. Vì vậy ranh
giới tin cậy thật sự là **nhánh `main` được bảo vệ + người duyệt environment `production`** (mục 1): chỉ duyệt deploy
những commit đã đọc qua.

> Đọc kèm `docs/deployment.md` (stack, secret, chế độ read-only). Các lệnh trên server dưới đây chạy bằng user `deploy`
> trong thư mục `/srv/plcprotocol`, trừ khi ghi khác.

## 1. GitHub: làm trước lần deploy đầu tiên

Tất cả bằng tài khoản `tan632000` (chủ repo). Không làm bước này thì **ai push được vào `main` cũng deploy được lên
server**, mà quyền đó tương đương root.

### 1.1. Bảo vệ nhánh `main`

Settings → Branches → Add rule cho `main`, hoặc dùng `gh`:

```bash
gh api -X PUT repos/tan632000/PlcProtocol/branches/main/protection --input - <<'EOF'
{
  "required_status_checks": { "strict": true, "contexts": ["test", "smoke"] },
  "enforce_admins": true,
  "required_pull_request_reviews": { "required_approving_review_count": 0 },
  "restrictions": null,
  "allow_force_pushes": false,
  "allow_deletions": false
}
EOF
```

- Bắt buộc pull request, bắt buộc hai check `test` và `smoke` xanh, áp dụng cả cho admin (không có bypass), cấm force push.
- `required_approving_review_count: 0` phù hợp khi một người làm. Khi có thêm người có quyền ghi, đổi thành `1`.
- Hai check `test`/`smoke` chỉ xuất hiện sau khi CI chạy lần đầu trên một pull request.

### 1.2. Environment `production`

Settings → Environments → New environment `production`:

- **Deployment branches**: "Selected branches and tags" → thêm đúng `main` (không chọn "Protected branches", vì tuỳ chọn
  đó cho phép mọi nhánh được bảo vệ).
- **Required reviewers**: thêm `tan632000`. Mỗi lần deploy phải bấm duyệt.

### 1.3. Secret của environment (KHÔNG đặt ở cấp repo)

Secret cấp repo thì một workflow chạy từ nhánh khác cũng đọc được. Secret của environment `production` chỉ job deploy từ
`main` đã được duyệt mới đọc được. Nhập bằng `gh` (gõ/dán vào, không hiện lên màn hình) hoặc Settings → Environments →
production → Add secret:

| Secret | Giá trị |
|---|---|
| `DEPLOY_HOST` | Elastic IP của server (mục 2) |
| `DEPLOY_USER` | `deploy` |
| `DEPLOY_SSH_KEY` | private key của key CI (mục 4), cả file |
| `DEPLOY_KNOWN_HOSTS` | dòng host key của server (mục 4.3) |
| `DEPLOY_PORT` | tuỳ chọn, mặc định 22 |

```bash
gh secret set DEPLOY_HOST --env production --repo tan632000/PlcProtocol
gh secret set DEPLOY_USER --env production --repo tan632000/PlcProtocol
gh secret set DEPLOY_SSH_KEY --env production --repo tan632000/PlcProtocol < ci_deploy_key
gh secret set DEPLOY_KNOWN_HOSTS --env production --repo tan632000/PlcProtocol < known_hosts_server
```

### 1.4. Kiểm tra lại (chỉ in tên và cấu hình, không in giá trị secret)

```bash
gh api repos/tan632000/PlcProtocol/branches/main/protection --jq '.required_status_checks.contexts, .enforce_admins.enabled'
gh api repos/tan632000/PlcProtocol/environments/production --jq '.deployment_branch_policy, [.protection_rules[].type]'
gh api repos/tan632000/PlcProtocol/environments/production/secrets --jq '[.secrets[].name]'
gh secret list --repo tan632000/PlcProtocol   # phải KHÔNG có DEPLOY_* ở cấp repo
```

### 1.5. Cho image public (làm trong lần deploy đầu, mục 6)

Image Cloud không chứa secret và repo đã public, nên package `plcprotocol/cloud` để **public**. Server pull không cần
đăng nhập, nên không có token nào nằm trên server. Package chỉ xuất hiện sau lần CD đầu tiên đẩy image: GitHub → profile
`tan632000` → Packages → `plcprotocol/cloud` → Package settings → Change visibility → Public.

## 2. AWS: EC2 + Elastic IP

1. EC2 **Ubuntu Server LTS** (24.04), tối thiểu 2 vCPU / 4 GB RAM (Keycloak cần khoảng 1 GB), đĩa **gp3 ≥ 30 GB**.
2. **Elastic IP**: Allocate rồi Associate với instance. Bắt buộc dùng: chứng chỉ của broker ghi IP này (`--san`). Nếu
   IP public đổi sau khi stop/start instance, mọi Edge sẽ từ chối kết nối.
3. **Security group**, inbound chỉ hai cổng:

   | Cổng | Nguồn | Dùng cho |
   |---|---|---|
   | 22 | `0.0.0.0/0` (GitHub Actions không có IP cố định) | SSH, chỉ đăng nhập bằng key |
   | 8883 | IP public của các nhà máy khách; tạm `0.0.0.0/0` khi chưa biết | Edge → Mosquitto (TLS + mật khẩu + ACL) |

   Không mở 8080, 8443 hay 5432: Cloud API và Keycloak chỉ nghe trên `127.0.0.1` của server.
4. Kiểm tra đĩa định kỳ: `df -h` và `docker system df`. Sau mỗi lần deploy, script xoá các image không có container nào dùng và được tạo cách đây hơn 240 giờ.

## 3. Cài Docker và user `deploy`

Đăng nhập bằng user quản trị mặc định (`ubuntu`):

```bash
# Docker Engine + compose plugin, theo https://docs.docker.com/engine/install/ubuntu/
sudo apt-get update && sudo apt-get install -y ca-certificates curl git openssl
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
  | sudo tee /etc/apt/sources.list.d/docker.list >/dev/null
sudo apt-get update && sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin

# User deploy: chạy stack và nhận lệnh deploy từ CI
sudo adduser --disabled-password --gecos "" deploy
sudo usermod -aG docker deploy
sudo mkdir -p /srv/plcprotocol && sudo chown deploy:deploy /srv/plcprotocol
```

> ⚠️ Thuộc nhóm `docker` là **tương đương root** trên máy này. Vì vậy key của CI bị khoá vào đúng một lệnh (mục 4), và
> không ai đăng nhập shell bằng user `deploy` qua SSH. Người vận hành dùng user `ubuntu` rồi `sudo -iu deploy`.

Tắt đăng nhập SSH bằng mật khẩu. AMI Ubuntu của AWS mặc định đã tắt; kiểm tra giá trị thực tế (các file trong
`/etc/ssh/sshd_config.d/` thắng file chính): `sudo sshd -T | grep -i passwordauthentication` phải ra `no`.

## 4. Key SSH

### 4.1. Key của CI: chỉ chạy được `remote-deploy.sh`

Tạo trên máy của bạn (không phải trên server), không đặt passphrase vì GitHub Actions dùng không tương tác:

```bash
ssh-keygen -t ed25519 -N "" -C "github-actions-deploy" -f ci_deploy_key
```

Trên server, thêm **public key** vào `/home/deploy/.ssh/authorized_keys` (tạo thư mục `.ssh` mode 700, file mode 600,
chủ sở hữu `deploy`), đúng **một dòng**:

```text
restrict,command="/bin/bash /srv/plcprotocol/deployments/scripts/remote-deploy.sh" ssh-ed25519 AAAA... github-actions-deploy
```

- `restrict`: không shell, không pty, không port/agent/X11 forwarding.
- `command=`: dù client gửi lệnh gì, sshd chỉ chạy `remote-deploy.sh`. Ba từ CI gửi (`<sha> <image@digest> <mode>`) đến
  script qua biến `SSH_ORIGINAL_COMMAND` và được kiểm tra bằng regex.
- Gọi qua `/bin/bash` vì script trong repo không có quyền thực thi (mode 644).

Private key `ci_deploy_key` đưa vào secret `DEPLOY_SSH_KEY` (mục 1.3) rồi **xoá khỏi máy bạn**.

### 4.2. Key của người vận hành

Dùng key riêng của user `ubuntu` (key pair chọn khi tạo EC2). Không bao giờ dùng key CI để đăng nhập.

### 4.3. Host key cho `DEPLOY_KNOWN_HOSTS`

GitHub Actions kiểm tra host key (`StrictHostKeyChecking=yes`), nên một server giả mạo (ví dụ khi Elastic IP bị nhả rồi
cấp cho người khác) không nhận được lệnh deploy.

```bash
# Trên máy bạn
ssh-keyscan -t ed25519 <elastic IP> > known_hosts_server
ssh-keygen -lf known_hosts_server
# Trên server: fingerprint phải giống hệt dòng trên
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

Khớp thì đưa nội dung `known_hosts_server` vào secret `DEPLOY_KNOWN_HOSTS`. Nếu SSH không ở cổng 22, dùng
`ssh-keyscan -p <cổng> …` (dòng sẽ có dạng `[ip]:cổng`). So fingerprint qua SSH vẫn là tin lần đầu; muốn chắc chắn hơn,
đối chiếu với dòng `ED25519` trong EC2 Console → Instance → Actions → Monitor and troubleshoot → Get system log.

## 5. Clone repo và tạo secret (một lần, không bao giờ do CD)

```bash
sudo -iu deploy
git clone https://github.com/tan632000/PlcProtocol.git /srv/plcprotocol
cd /srv/plcprotocol
bash deployments/scripts/dev-secrets.sh --san IP:<elastic IP> --edge line1-edge01
```

- `--san IP:<elastic IP>` là địa chỉ các Edge dùng để nối tới broker. Thêm `--san DNS:<tên miền>` khi đã có domain
  (phải tạo lại bộ secret và cài lại Edge).
- Thêm Edge sau này: mục 7.
- **`ca.key`** chỉ dùng để ký chứng chỉ server mới, không cần cho `--add-edge` hay khi chạy stack. Thư mục
  `deployments/.secrets/` có mode 700 và thuộc `deploy`, nên `scp` bằng user `ubuntu` sẽ báo "Permission denied". Chép
  qua `sudo`, **kiểm tra bản chép**, rồi mới xoá trên server:

  ```bash
  # Trên máy bạn
  ssh ubuntu@<elastic IP> sudo cat /srv/plcprotocol/deployments/.secrets/ca.key > ca.key
  openssl pkey -in ca.key -check -noout          # phải in "Key is valid"; chưa hợp lệ thì KHÔNG xoá
  # Cất ca.key vào nơi lưu trữ an toàn (password manager / két), rồi trên server:
  ssh ubuntu@<elastic IP> sudo -u deploy rm /srv/plcprotocol/deployments/.secrets/ca.key
  ```
- Ghi chế độ ghi (nếu khách cần) theo `docs/deployment.md`, mục "Chế độ read-only và cách bật ghi". `deployments/.env`
  và `deployments/.secrets/` không nằm trong git nên CD không đụng tới.

## 6. Lần deploy đầu tiên

Server chưa từng chạy stack, nên lần đầu cần một bước tay, làm **trong lúc run CD đầu tiên đang chờ duyệt**:

1. Merge pull request vào `main`. Run CD số 1 chạy `test` và `image` (khoảng 10–15 phút), rồi job `deploy` dừng ở
   "Waiting for review".
2. Trên server (user `deploy`):

   ```bash
   cd /srv/plcprotocol
   git fetch origin main
   git checkout --detach <sha của run 1>      # sha hiện trên trang run
   docker compose -f deployments/compose.yaml up -d --build --wait
   ```

3. Đặt package public (mục 1.5).
4. Bấm **Approve and deploy**. `remote-deploy.sh` thấy server đã ở đúng sha, không có thay đổi broker/Keycloak/compose,
   nên chỉ thay Cloud từ bản build tay sang image GHCR và kiểm tra `/health/ready`.

Từ lần sau chỉ cần merge rồi duyệt.

### `remote-deploy.sh` từ chối khi nào (exit 2, không thay đổi gì)

Mọi thông báo bắt đầu bằng `refused: `.

| Thông báo | Nghĩa | Xử lý |
|---|---|---|
| `usage: …`, `sha must be…`, `image must be…`, `mode must be…`, `unexpected…` | Lệnh SSH sai định dạng | Lỗi workflow; không chạy tay |
| `deployments/.secrets is missing` | Server chưa có secret | Mục 5 |
| `tracked files are modified on the server` | Ai đó sửa file của repo trên server | Đưa thay đổi vào git qua pull request; `git checkout -- <file>` trên server |
| `… is not a commit on origin/main` | Sha không thuộc `main` | Chỉ deploy commit đã merge |
| `predates the IIOT_CLOUD_IMAGE switch`, `has no deployments/compose.yaml` | Sha cũ hơn lần CD đầu tiên | Không rollback được về đó |
| `manual upgrade needed …; changed: …` | Commit sửa `deployments/mosquitto`, `deployments/keycloak`, `deployments/compose.yaml` hoặc `deployments/.secrets` | Mục 7, "Nâng cấp thủ công" |
| `IIOT_READY_TIMEOUT must be…` | Biến môi trường sai | Không đặt biến này khi chạy tay |

`skipped: … is older than the deployed …` (exit 0) nghĩa là server đã chạy commit mới hơn; CD không bao giờ lùi trừ khi
rollback (mục 9).

### Khi deploy lỗi (exit 1)

- Không pull được image hoặc backup DB lỗi: chưa có gì thay đổi.
- Cloud không khởi động hoặc `/health/ready` không trả lời trong 120 giây: container Cloud mới **đã** chạy. Log nằm trên
  server, không in ra log GitHub (repo public):

  ```bash
  less /srv/plcprotocol/deployments/.deploy-logs/<sha>.log    # mode 600
  ```

  Sửa lỗi rồi merge bản sửa, hoặc rollback (mục 9). Trong lúc đó repo trên server đã ở sha mới nhưng
  `iiot-cloud:local` vẫn là image tốt trước đó: đừng chạy lệnh tay của mục 7 cho tới khi deploy hoặc rollback xong.

## 7. Lệnh tay trên server khi đã có CD

Chạy bằng `sudo -iu deploy`, trong `/srv/plcprotocol`. **Không dùng `--build`**: image Cloud đang chạy được gắn tag
`iiot-cloud:local` sau mỗi lần deploy, nên các lệnh dưới đây dùng đúng image đó.

| Việc | Lệnh |
|---|---|
| Trạng thái, log | `docker compose -f deployments/compose.yaml ps`, `… logs --tail 100 cloud` |
| Thêm một Edge | `bash deployments/scripts/dev-secrets.sh --add-edge line2-edge01` rồi `docker compose -f deployments/compose.yaml up -d --force-recreate mosquitto cloud` |
| Bật ghi xuống PLC | Theo `docs/deployment.md`, mục "Chế độ read-only và cách bật ghi" (`cloud.env`, `deployments/.env`) |
| Lấy token | `bash deployments/scripts/get-token.sh viewer` |

### Nâng cấp thủ công (broker, Keycloak, compose)

Khi CD báo `manual upgrade needed` cho commit `<sha>`:

```bash
cd /srv/plcprotocol
git fetch origin main
# Đường này không có pg_dump tự động: nếu commit có migration thì tự backup trước khi checkout.
if [ -n "$(git diff --name-only HEAD <sha> -- src/Cloud/Data/Migrations)" ]; then
  mkdir -p deployments/.backups
  f="deployments/.backups/$(date -u +%Y%m%dT%H%M%SZ)-$(git rev-parse --short=12 HEAD).sql"
  (umask 077 && docker compose -f deployments/compose.yaml exec -T postgres pg_dump -U iiot iiot > "$f") \
    || { rm -f "$f"; echo "backup failed: STOP, do not continue"; }
fi
# Chỉ tiếp tục khi không có dòng "backup failed" ở trên.
git checkout --detach <sha>
docker compose -f deployments/compose.yaml up -d --force-recreate mosquitto    # thay bằng các service đã đổi
```

`git diff --name-only HEAD <sha> -- deployments` cho biết file nào đổi: `mosquitto/` → `mosquitto`, `keycloak/` →
`keycloak`, `compose.yaml` → đúng các service có định nghĩa bị đổi (có thể cả `postgres` hay `cloud`).

Rồi chạy lại CD cho đúng sha đó: GitHub → Actions → CD → Run workflow (nhánh `main`), ô `sha` = `<sha>`. Script thấy
server đã ở sha đó và chỉ thay Cloud. Nếu đổi `mosquitto` hoặc ACL, các Edge mất kết nối vài giây rồi tự nối lại
(session MQTT vẫn giữ).

> Keycloak chạy `start-dev` không có volume: tạo lại container sẽ mất mọi thay đổi làm tay trong trang admin (realm được
> nạp lại từ `deployments/keycloak/iiot-realm.json`). Muốn thay đổi bền thì sửa file realm trong git.

> Commit thêm trang SCADA (client `iiot-scada`, role `Operator` chứa `Viewer`) đổi `deployments/keycloak`, nên CD sẽ
> báo `manual upgrade needed` một lần: làm như trên với service `keycloak`. Trên server, trang SCADA chỉ dùng được qua
> SSH tunnel cổng 8080 và 8443 cùng dòng hosts `127.0.0.1 keycloak` trên máy mở trình duyệt, và máy đó phải tin
> `ca.pem` của server (`certutil` như `docs/deployment.md`, mục "Mở trang SCADA"), cho tới khi có domain và
> HTTPS (`redirectUris` của realm hiện chỉ có `localhost`/`127.0.0.1` với cổng 8080 và 8090). Đây là trang chẩn đoán,
> không phải sản phẩm SCADA.

## 8. Edge trên máy Windows của khách

Làm theo `docs/deployment.md`, mục "Edge trên Windows", với:

- `install-edge.ps1 -BrokerHost <elastic IP>`.
- `-CaCertificate`: file `ca.pem` của server (thư mục secret chỉ `deploy` đọc được, nên lấy qua `sudo`):
  `ssh ubuntu@<elastic IP> sudo cat /srv/plcprotocol/deployments/.secrets/ca.pem > ca.pem`.
- Mật khẩu MQTT của Edge: `ssh ubuntu@<elastic IP> sudo cat /srv/plcprotocol/deployments/.secrets/passwords/<EdgeId>.txt`
  (không lưu vào file). Chuyển cho người cài bằng kênh an toàn, không gửi qua chat/email thường.

Máy khách cần ra Internet tới `<elastic IP>:8883`: `Test-NetConnection <elastic IP> -Port 8883`.

## 9. Truy cập API và rollback

### API

API chỉ mở trên `127.0.0.1:8080` của server cho tới khi có domain và HTTPS. Mở tunnel từ máy bạn:

```bash
ssh -L 8080:127.0.0.1:8080 ubuntu@<elastic IP>
# cửa sổ khác: gọi http://127.0.0.1:8080 như trên máy chạy thử; lấy token trên server:
#   sudo -iu deploy bash /srv/plcprotocol/deployments/scripts/get-token.sh viewer
```

### Rollback

GitHub → Actions → CD → Run workflow (nhánh `main`), ô `sha` = commit cũ hơn **đã từng được CD build** (có trong lịch sử
run CD). CD bỏ qua `test`/`image`, lấy digest của `cloud:<sha>` trên GHCR, rồi deploy sau khi được duyệt.

- Không rollback được về commit trước lần CD đầu tiên, hoặc qua một thay đổi broker/Keycloak/compose (script từ chối;
  làm như mục 7).
- **Migration**: Cloud chỉ migrate tiến. Mọi migration phải tương thích với bản trước đó (thêm cột/bảng trước, xoá ở một
  bản phát hành sau). Khi commit có migration, script tự `pg_dump` trước khi deploy vào
  `deployments/.backups/<thời gian>-<sha cũ>.sql` (mode 600). Rollback qua một migration không tương thích thì phải khôi
  phục bản dump:

  ```bash
  docker compose -f deployments/compose.yaml stop cloud
  docker compose -f deployments/compose.yaml exec -T postgres psql -U iiot -d postgres -v ON_ERROR_STOP=1 \
    -c 'DROP DATABASE iiot WITH (FORCE);' -c 'CREATE DATABASE iiot OWNER iiot;'
  docker compose -f deployments/compose.yaml exec -T postgres psql -U iiot -d iiot -v ON_ERROR_STOP=1 -1 \
    < deployments/.backups/<file>.sql
  ```

  rồi chạy rollback như trên. Nếu rollback bị từ chối, Cloud vẫn đang dừng: `docker compose -f deployments/compose.yaml
  start cloud` sau khi xử lý. Dữ liệu telemetry đến sau bản dump vẫn nằm trong buffer của Edge nếu Edge chưa nhận xác
  nhận; dữ liệu đã được xác nhận sau thời điểm dump sẽ mất.

## 10. Giới hạn hiện tại

- Chưa có HTTPS và domain: API chỉ truy cập qua SSH tunnel. Keycloak chạy `start-dev` với chứng chỉ CA dev.
- Chưa có backup định kỳ: chỉ có `pg_dump` trước các lần deploy có migration. Hãy sao lưu `deployments/.secrets/` (trừ
  `ca.key` đã cất riêng) và `deployments/.backups/` ra ngoài server.
- Chưa có giám sát: kiểm tra `docker compose ps`, `df -h` và trang run CD.
- Lệnh `docker image prune` của script xoá mọi image không có container nào dùng và được tạo cách đây hơn 240 giờ,
  kể cả image postgres/keycloak nếu stack đang `down`. Chúng sẽ được pull lại khi chạy.
- **Chứng chỉ hết hạn**: chứng chỉ server của Mosquitto/Keycloak có hạn 365 ngày, CA 730 ngày. Xem ngày hết hạn:
  `sudo -u deploy openssl x509 -enddate -noout -in /srv/plcprotocol/deployments/.secrets/mosquitto/server.pem`. Hết hạn
  thì mọi Edge từ chối TLS. Hiện chỉ gia hạn được bằng cách tạo lại bộ secret và cài lại Edge; ghi lịch nhắc trước hạn
  ít nhất một tháng (gia hạn bằng `ca.key` đang cất riêng cần một spec sau).
