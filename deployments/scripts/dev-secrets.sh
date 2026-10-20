#!/usr/bin/env bash
# Generates every secret of the trial stack (plan D-03): a dev CA, server certificates for Mosquitto and
# Keycloak, random passwords, the Mosquitto password file and the env files compose reads.
#
#   dev-secrets.sh [--out DIR] [--edge ID]... [--san DNS:name|IP:addr]...   create a new set (DIR new or empty)
#   dev-secrets.sh --add-edge ID [--out DIR]                               add one Edge to an existing set
#
# DIR defaults to deployments/.secrets (gitignored), wherever the script is run from. Prints file names
# only, never a secret value. On Windows run it with Git Bash, not the WSL "bash".
set -euo pipefail

# Git Bash would rewrite "/CN=..." and "/work" into Windows paths.
export MSYS_NO_PATHCONV=1

MOSQUITTO_IMAGE="eclipse-mosquitto@sha256:21421af7b32bf9ce508e9090c8eb13bb81f410ca778dc205506180a6f862d0eb"
MOSQUITTO_UID=1883
KEYCLOAK_UID=1000
CLOUD_ID="iiot-cloud"

script_dir="$(cd "$(dirname "$0")" && pwd)"
out="$script_dir/../.secrets"
edges=()
sans=()
add_edge=""

fail() { echo "dev-secrets: $1" >&2; exit "${2:-1}"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --out) out="${2:?--out needs a directory}"; shift 2 ;;
    --edge) edges+=("${2:?--edge needs an id}"); shift 2 ;;
    --san) sans+=("${2:?--san needs DNS:name or IP:addr}"); shift 2 ;;
    --add-edge) add_edge="${2:?--add-edge needs an id}"; shift 2 ;;
    *) fail "unknown argument '$1'" 2 ;;
  esac
done

if [ -n "$add_edge" ] && { [ ${#edges[@]} -gt 0 ] || [ ${#sans[@]} -gt 0 ]; }; then
  fail "--add-edge cannot be combined with --edge or --san" 2
fi

command -v openssl >/dev/null || fail "openssl not found (Git for Windows ships it in Git Bash)"
command -v docker >/dev/null || fail "docker not found"

# Edge ids become MQTT usernames and ACL patterns: no wildcards, no separators, never the Cloud's id.
valid_edge() {
  [[ "$1" =~ ^[A-Za-z0-9_-]{1,64}$ ]] || fail "edge id '$1' must be 1..64 characters [A-Za-z0-9_-]" 2
  [ "$1" != "$CLOUD_ID" ] || fail "edge id '$CLOUD_ID' is reserved for the Cloud" 2
}

valid_san() {
  [[ "$1" =~ ^(DNS:[A-Za-z0-9.-]{1,253}|IP:[0-9A-Fa-f:.]{2,45})$ ]] || fail "--san '$1' must be DNS:name or IP:addr" 2
}

# Docker Desktop and Git's openssl are native Windows programs: with path conversion off they need C:/... paths.
host_path() {
  if command -v cygpath >/dev/null; then cygpath -m "$1"; else printf '%s' "$1"; fi
}

# Runs a command quietly, showing its own error output only when it fails.
quiet() {
  local log
  log="$(mktemp)"
  if ! "$@" 2>"$log" >/dev/null; then
    cat "$log" >&2
    rm -f "$log"
    return 1
  fi
  rm -f "$log"
}

new_password() {
  # 32 characters from [A-Za-z0-9]: safe in env files, connection strings and URLs.
  local value=""
  while [ ${#value} -lt 32 ]; do
    value+="$(openssl rand -base64 48 | tr -dc 'A-Za-z0-9')"
  done
  printf '%s' "${value:0:32}"
}

# Runs a command as root in the Mosquitto image. Only the service folders and a staging folder are mounted:
# ca.key, the passwords and the env files never enter a container.
in_helper() {
  docker run --rm --user 0 --entrypoint sh \
    -v "$(host_path "$out/mosquitto"):/work/mosquitto" \
    -v "$(host_path "$out/keycloak"):/work/keycloak" \
    -v "$(host_path "$out/.staging"):/stage" \
    "$MOSQUITTO_IMAGE" -c "$1"
}

# Linux only: each service's private files belong to the uid its container runs as and are readable by nobody
# else; folders and certificates stay readable so the operator (and --add-edge) can still use them.
# (Docker Desktop on Windows shows bind-mounted files to every uid, and chown there has no effect.)
fix_ownership() {
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) return 0 ;;
  esac
  in_helper "chown -R $MOSQUITTO_UID:$MOSQUITTO_UID /work/mosquitto && chown -R $KEYCLOAK_UID:$KEYCLOAK_UID /work/keycloak \
    && chmod 755 /work/mosquitto /work/keycloak && chmod 644 /work/mosquitto/*.pem /work/keycloak/*.pem \
    && chmod 600 /work/mosquitto/server.key /work/mosquitto/passwd /work/keycloak/server.key"
}

# Hashes "user:password" lines from the staged plain file (mounted, never on a command line) and appends them.
append_passwd() {
  in_helper "mosquitto_passwd -U /stage/passwd.plain 2>/dev/null && cat /stage/passwd.plain >> /work/mosquitto/passwd \
    && rm -f /stage/passwd.plain"
}

leaf_certificate() {
  local dir="$1" cn="$2" san="$3" tmp
  tmp="$(host_path "$(mktemp -d)")"
  quiet openssl req -newkey rsa:2048 -nodes -keyout "$dir/server.key" -out "$tmp/server.csr" -subj "/CN=$cn"
  cat > "$tmp/ext.cnf" <<EOF
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=$san
subjectKeyIdentifier=hash
authorityKeyIdentifier=keyid,issuer
EOF
  quiet openssl x509 -req -in "$tmp/server.csr" -CA "$out/ca.pem" -CAkey "$out/ca.key" \
    -set_serial "0x$(openssl rand -hex 16)" -days 365 -sha256 -extfile "$tmp/ext.cnf" -out "$dir/server.pem"
  rm -rf "$tmp"
}

write_password() {
  new_password > "$out/passwords/$1.txt"
}

stage_plain() {
  # "user:password" per identity, staged for hashing; the folder is private and the file is removed afterwards.
  : > "$out/.staging/passwd.plain"
  for name in "$@"; do
    printf '%s:%s\n' "$name" "$(cat "$out/passwords/$name.txt")" >> "$out/.staging/passwd.plain"
  done
}

umask 077

if [ -n "$add_edge" ]; then
  # Add one Edge to an existing set: new password, broker identity and Cloud allow-list entry; the CA stays.
  [ -d "$out" ] || fail "no secrets in '$out' to add to (create them first)" 2
  out="$(host_path "$(cd "$out" && pwd)")"
  for required in ca.pem cloud.env mosquitto/passwd passwords; do
    [ -e "$out/$required" ] || fail "no complete set of secrets in '$out' to add to (missing $required)" 2
  done
  valid_edge "$add_edge"
  [ ! -e "$out/passwords/$add_edge.txt" ] || fail "edge '$add_edge' already exists" 2

  # On failure, undo whatever was done so the same command can simply be run again.
  rollback_add() {
    if [ $? -ne 0 ]; then
      echo "dev-secrets: failed; undoing the partial addition of '$add_edge'" >&2
      in_helper "sed -i '/^$add_edge:/d' /work/mosquitto/passwd; rm -f /stage/passwd.plain" >/dev/null 2>&1 || true
      rm -f "$out/passwords/$add_edge.txt" "$out/.staging/passwd.plain"
    fi
  }
  trap rollback_add EXIT

  mkdir -p "$out/.staging"
  write_password "$add_edge"
  stage_plain "$add_edge"
  append_passwd
  index="$(grep -c '^Cloud__Edges__[0-9]*__EdgeId=' "$out/cloud.env" || true)"
  # The display name is the id: appsettings.json's default label ("Line 1") must never name another Edge.
  printf 'Cloud__Edges__%s__EdgeId=%s\nCloud__Edges__%s__DisplayName=%s\n' "$index" "$add_edge" "$index" "$add_edge" >> "$out/cloud.env"
  fix_ownership
  trap - EXIT
  echo "Added edge '$add_edge' to $out:"
  echo "  passwords/$add_edge.txt, mosquitto/passwd, cloud.env (Cloud__Edges__${index}__EdgeId)"
  echo "Recreate the broker and the Cloud to load it: docker compose -f deployments/compose.yaml up -d --force-recreate mosquitto cloud"
  exit 0
fi

[ ${#edges[@]} -gt 0 ] || edges=("line1-edge01")
seen=" "
for edge in "${edges[@]}"; do
  valid_edge "$edge"
  [[ "$seen" != *" $edge "* ]] || fail "edge id '$edge' is given twice" 2
  seen+="$edge "
done
for san in "${sans[@]+"${sans[@]}"}"; do valid_san "$san"; done

# Only a new or empty folder: never write keys next to other files, and never delete anything that was there.
if [ -e "$out" ] && [ -n "$(ls -A "$out" 2>/dev/null)" ]; then
  fail "'$out' is not empty; refusing to overwrite or mix secrets (use --add-edge, or choose another --out)" 2
fi
mkdir -p "$out"
out="$(host_path "$(cd "$out" && pwd)")"
mkdir -p "$out/mosquitto" "$out/keycloak" "$out/passwords" "$out/.staging"

# A half-written set would be refused on the next run: remove it if anything below fails.
cleanup_on_failure() {
  if [ $? -ne 0 ]; then
    echo "dev-secrets: failed; removing the incomplete set in '$out'" >&2
    in_helper "rm -rf /work/mosquitto/* /work/keycloak/* /stage/*" >/dev/null 2>&1 || true
    rm -rf "${out:?}"/* "${out:?}"/.staging
  fi
}
trap cleanup_on_failure EXIT
echo "Writing secrets to $out:"

# Dev CA: may sign server certificates only (pathlen 0), for two years.
quiet openssl req -x509 -newkey rsa:3072 -nodes -keyout "$out/ca.key" -out "$out/ca.pem" -days 730 -sha256 \
  -subj "/CN=IIoT Trial Dev CA" \
  -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
  -addext "keyUsage=critical,keyCertSign,cRLSign" \
  -addext "subjectKeyIdentifier=hash"
echo "  ca.pem, ca.key (ca.key stays here: it is never mounted into a container)"

mosquitto_san="DNS:mosquitto,DNS:localhost,IP:127.0.0.1"
for san in "${sans[@]+"${sans[@]}"}"; do mosquitto_san+=",$san"; done
leaf_certificate "$out/mosquitto" "mosquitto" "$mosquitto_san"
cp "$out/ca.pem" "$out/mosquitto/ca.pem"
leaf_certificate "$out/keycloak" "keycloak" "DNS:keycloak,DNS:localhost"
echo "  mosquitto/server.pem, mosquitto/server.key, keycloak/server.pem, keycloak/server.key"

for name in "$CLOUD_ID" "${edges[@]}" viewer operator keycloak-admin postgres; do
  write_password "$name"
done
echo "  passwords/*.txt"

: > "$out/mosquitto/passwd"
stage_plain "$CLOUD_ID" "${edges[@]}"
append_passwd
echo "  mosquitto/passwd (hashed)"

{
  printf 'Cloud__Mqtt__Password=%s\n' "$(cat "$out/passwords/$CLOUD_ID.txt")"
  printf 'Cloud__Database__ConnectionString=Host=postgres;Port=5432;Database=iiot;Username=iiot;Password=%s\n' \
    "$(cat "$out/passwords/postgres.txt")"
  i=0
  for edge in "${edges[@]}"; do
    printf 'Cloud__Edges__%s__EdgeId=%s\nCloud__Edges__%s__DisplayName=%s\n' "$i" "$edge" "$i" "$edge"
    i=$((i + 1))
  done
} > "$out/cloud.env"
{
  printf 'POSTGRES_USER=iiot\nPOSTGRES_DB=iiot\n'
  printf 'POSTGRES_PASSWORD=%s\n' "$(cat "$out/passwords/postgres.txt")"
} > "$out/postgres.env"
{
  printf 'KC_BOOTSTRAP_ADMIN_USERNAME=admin\n'
  printf 'KC_BOOTSTRAP_ADMIN_PASSWORD=%s\n' "$(cat "$out/passwords/keycloak-admin.txt")"
  printf 'VIEWER_PASSWORD=%s\n' "$(cat "$out/passwords/viewer.txt")"
  printf 'OPERATOR_PASSWORD=%s\n' "$(cat "$out/passwords/operator.txt")"
} > "$out/keycloak.env"
echo "  cloud.env, postgres.env, keycloak.env"

fix_ownership
trap - EXIT
echo "Done. Edges: ${edges[*]}"
