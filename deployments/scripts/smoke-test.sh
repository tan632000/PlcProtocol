#!/usr/bin/env bash
# End-to-end smoke test of the trial stack (task-02). It never touches the real trial stack: it uses its own
# compose project (iiot-smoke), its own temporary secrets and an Edge id that is never installed (smoke-edge),
# and refuses to start (exit 2) when that project exists or the stack's ports are in use.
#
#   bash deployments/scripts/smoke-test.sh          (Windows: run with Git Bash)
#
# Prints PASS/FAIL per check (SMOKE-00..07); exit 0 only when every check passes. Never prints a secret.
set -uo pipefail
export MSYS_NO_PATHCONV=1
# The guard checks the default ports, so the stack must use exactly those; a smoke stack never restarts itself.
unset IIOT_API_PORT IIOT_KEYCLOAK_PORT IIOT_MQTT_PORT
export IIOT_RESTART=no

HELPER_IMAGE="eclipse-mosquitto@sha256:21421af7b32bf9ce508e9090c8eb13bb81f410ca778dc205506180a6f862d0eb"
PROJECT="iiot-smoke"
# The Cloud image this stack builds: its own name, never the trial stack's or a deployed one.
export IIOT_CLOUD_IMAGE="${PROJECT}-cloud"
API="http://127.0.0.1:8080"

host_path() {
  if command -v cygpath >/dev/null; then cygpath -m "$1"; else printf '%s' "$1"; fi
}

# Native paths (C:/... on Windows): git, docker and tar here are not Git Bash programs.
root="$(host_path "$(cd "$(dirname "$0")/../.." && pwd)")"
compose_file="$root/deployments/compose.yaml"
failures=0
pass() { echo "PASS $1 $2"; }
fail() { echo "FAIL $1 $2"; failures=$((failures + 1)); }
dc() { docker compose -p "$PROJECT" -f "$(host_path "$compose_file")" "$@"; }

# --- Guard: never run next to the trial stack or a previous smoke run. ---
if [ -n "$(docker compose -p "$PROJECT" ps -a -q 2>/dev/null)" ]; then
  echo "smoke-test: compose project '$PROJECT' already exists; remove it first (docker compose -p $PROJECT down -v)" >&2
  exit 2
fi
for port in 8080 8443 8883; do
  if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then
    echo "smoke-test: port $port is in use (is the trial stack running?); stop it before the smoke test" >&2
    exit 2
  fi
done

# Content snapshot of everything changed or untracked (not ignored), file by file: a folder-level "??" line
# would hide a file written inside an untracked folder.
tree_snapshot() {
  git -C "$root" status --porcelain --untracked-files=all || return 1
  git -C "$root" ls-files -m -o --exclude-standard -z | (cd "$root" && while IFS= read -r -d '' f; do
    if [ -e "$f" ]; then sha256sum "$f"; fi    # a deleted file is already in the status lines above
  done) || return 1
}
status_before="$(tree_snapshot)" || { echo "smoke-test: git status failed" >&2; exit 1; }
work="$(host_path "$(mktemp -d)")"
secrets="$work/secrets"

cleanup() {
  dc down -v --remove-orphans >/dev/null 2>&1 || true
  docker image rm "${PROJECT}-cloud" >/dev/null 2>&1 || true
  # Service folders may belong to container uids (Linux): empty them as root; ca.key is never mounted.
  for service in mosquitto keycloak; do
    [ -d "$secrets/$service" ] && docker run --rm --user 0 --entrypoint sh -v "$secrets/$service:/work" "$HELPER_IMAGE" \
      -c 'rm -rf /work/* 2>/dev/null; true' >/dev/null 2>&1
  done
  rm -rf "$work"
}
trap cleanup EXIT

echo "Smoke test in compose project '$PROJECT' with temporary secrets"
if ! bash "$root/deployments/scripts/dev-secrets.sh" --out "$secrets" --edge smoke-edge >/dev/null; then
  echo "smoke-test: could not create the temporary secrets" >&2
  exit 1
fi
export IIOT_SECRETS="$secrets"

# --- SMOKE-00: static checks of what the stack is built from. ---
static_ok=1
static_fail() { echo "  $1"; static_ok=0; }
grep -v '^\s*#' "$compose_file" | grep -q 'ca\.key' && static_fail "compose.yaml mounts or names ca.key"
while IFS= read -r line; do
  # Every secrets mount is one named file, read-only: a certificate/key, or the broker's passwd.
  [[ "$line" =~ ^[[:space:]]*-[[:space:]]\$\{IIOT_SECRETS:-\./\.secrets\}/((mosquitto|keycloak)/)?(ca|server)\.(pem|key):/[^:]+:ro$ ]] \
    || [[ "$line" =~ ^[[:space:]]*-[[:space:]]\$\{IIOT_SECRETS:-\./\.secrets\}/mosquitto/passwd:/[^:]+:ro$ ]] \
    || static_fail "secrets mount is not a single read-only file: $line"
done < <(grep -v '^\s*#' "$compose_file" | grep -E '^\s*-' | grep -E 'secrets|IIOT_SECRETS')
grep -v '^\s*#' "$compose_file" | grep -qE '^\s*(source|target):' && static_fail "long-syntax mounts are not allowed (cannot be checked)"
# The one exemption: the Cloud line names the locally built image (or a digest chosen by CD).
[ "$(grep -cxF '    image: ${IIOT_CLOUD_IMAGE:-iiot-cloud:local}' "$compose_file")" = 1 ] \
  || static_fail "the Cloud image exemption must appear exactly once"
while IFS= read -r line; do
  [[ "$line" == *@sha256:* ]] || static_fail "image without digest: $line"
done < <(grep -hE '^\s*image:' "$compose_file" | grep -vxF '    image: ${IIOT_CLOUD_IMAGE:-iiot-cloud:local}'
         grep -hE '^FROM ' "$root"/deployments/cloud/Dockerfile)
while IFS= read -r line; do
  [[ "$line" == *@sha256:* ]] || static_fail "script image without digest: $line"
done < <(grep -hE '^[A-Z_]*IMAGE=' "$root"/deployments/scripts/*.sh)
# Every container-starting docker command (continuation lines joined) must take its image from a pinned *_IMAGE
# variable. "docker run/create" counts as a command where a command can start: line start, after ; & | ( { or
# inside $( ... ) — even within a double-quoted "$( ... )". Single-quoted text (grep patterns) is removed first;
# a message such as "docker run ..." has a quote right before "docker" and is not a command position.
command_position='(^|[;&|({]|\$\()[[:space:]]*docker[[:space:]]+(container[[:space:]]+)?(run|create)([[:space:]]|$)'
for script in "$root"/deployments/scripts/*.sh; do
  while IFS= read -r command; do
    unquoted="$(sed -E "s/'[^']*'//g" <<<"$command")"
    if [[ "$unquoted" =~ $command_position ]]; then
      [[ "$command" =~ \$\{?[A-Z_]*IMAGE ]] || static_fail "docker run/create without a pinned *_IMAGE variable in $(basename "$script")"
    fi
  done < <(sed -e ':a' -e '/\\$/N; s/\\\n//; ta' "$script" | grep -vE '^[[:space:]]*#' | grep -E 'docker')
done
# Test sources: every image handed to Testcontainers or Docker, every *Image constant, and any "name:tag" literal
# (except localhost:port and IP:port endpoints) must carry a digest.
while IFS= read -r literal; do
  [[ "$literal" == *@sha256:* ]] || static_fail "test image without digest: $literal"
done < <(
  grep -rhoE '(ContainerBuilder|WithImage|DockerImage)\("[^"]*"' "$root/tests/Deployment.Tests" --include='*.cs'
  grep -rhoE 'Image[[:space:]]*=[[:space:]]*"[^"]*"' "$root/tests/Deployment.Tests" --include='*.cs'
  grep -rhoE '"[a-z0-9][a-z0-9._-]*(/[a-z0-9._-]+)*:[A-Za-z0-9._-]+(@sha256:[0-9a-f]+)?"' "$root/tests/Deployment.Tests" --include='*.cs' \
    | grep -vE '^"(localhost|[0-9]+(\.[0-9]+){3}):[0-9]+"$'
)
for entry in 'deployments/.secrets/' '**/*.key' '**/*.env'; do
  grep -qxF "$entry" "$root/.dockerignore" || static_fail ".dockerignore lacks $entry"
done
if sed -e ':a' -e '/\\$/N; s/\\\n//; ta' "$root/deployments/scripts/get-token.sh" | grep -E '^[^#]*docker run --rm' \
     | grep -qE '(^|[[:space:]])(-e|--env|--env-file)([[:space:]=]|[A-Z])'; then
  static_fail "get-token.sh passes something through -e/--env/--env-file"
fi
grep -q 'password@/run/iiot/password' "$root/deployments/scripts/get-token.sh" || static_fail "get-token.sh does not read the password from the mounted file"
[ "$static_ok" = 1 ] && pass SMOKE-00 "static: per-file secret mounts, no ca.key, digests everywhere, .dockerignore, no password on argv" \
  || fail SMOKE-00 "static checks"

# --- Start the stack. ---
echo "Starting the stack (first build may take a few minutes)..."
if ! dc up -d --build --wait --wait-timeout 300 >"$work/up.log" 2>&1; then
  tail -20 "$work/up.log" >&2
  fail SMOKE-01 "the stack did not start"
  exit 1
fi

# --- SMOKE-01: ready (database + MQTT) within 120 s. ---
ready=0
for _ in $(seq 1 120); do
  if [ "$(curl -s -o /dev/null -w '%{http_code}' "$API/health/ready")" = "200" ]; then ready=1; break; fi
  sleep 1
done
[ "$ready" = 1 ] && pass SMOKE-01 "/health/ready 200" || { fail SMOKE-01 "/health/ready not 200 within 120 s"; dc logs cloud 2>&1 | tail -20 >&2; }

# --- SMOKE-02: no token. ---
code="$(curl -s -o /dev/null -w '%{http_code}' "$API/api/v1/edges")"
[ "$code" = "401" ] && pass SMOKE-02 "no token -> 401" || fail SMOKE-02 "no token -> $code (expected 401)"

viewer="$(bash "$root/deployments/scripts/get-token.sh" viewer --secrets "$secrets" --project "$PROJECT" 2>"$work/token.err")" || viewer=""
operator="$(bash "$root/deployments/scripts/get-token.sh" operator --secrets "$secrets" --project "$PROJECT" 2>>"$work/token.err")" || operator=""
[ -n "$viewer" ] && [ -n "$operator" ] || cat "$work/token.err" >&2

# --- SMOKE-03: a Viewer reads the allow-listed edges. ---
body="$(curl -s -w '\n%{http_code}' -H "Authorization: Bearer $viewer" "$API/api/v1/edges")"
code="${body##*$'\n'}"
if [ "$code" = "200" ] && [[ "$body" == *'"edgeId":"smoke-edge","displayName":"smoke-edge"'* ]]; then
  pass SMOKE-03 "viewer token -> 200 with smoke-edge (labelled smoke-edge)"
else
  fail SMOKE-03 "viewer token -> $code"
fi

command='{"tagId":"Smoke.Never","value":1}'
# --- SMOKE-04: read-only default, the command API does not exist for a Viewer. ---
code="$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "Authorization: Bearer $viewer" -H 'Content-Type: application/json' \
  -d "$command" "$API/api/v1/edges/smoke-edge/commands")"
[ "$code" = "404" ] && pass SMOKE-04 "viewer POST -> 404 (read-only default)" || fail SMOKE-04 "viewer POST -> $code (expected 404)"

# --- SMOKE-05: read-only default, nor for an Operator (Cloud:Commands:Enabled is off). ---
code="$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "Authorization: Bearer $operator" -H 'Content-Type: application/json' \
  -d "$command" "$API/api/v1/edges/smoke-edge/commands")"
[ "$code" = "404" ] && pass SMOKE-05 "operator POST -> 404 (read-only default)" || fail SMOKE-05 "operator POST -> $code (expected 404)"

# --- SMOKE-06: no private key or password in any layer of the Cloud image. ---
image_ok=1
mkdir -p "$work/image"
if docker save -o "$work/image.tar" "${PROJECT}-cloud" && tar --force-local -xf "$work/image.tar" -C "$work/image"; then
  : > "$work/needles"
  for dir in "$secrets" "$root/deployments/.secrets"; do
    # One needle per password: the files have no trailing newline, so concatenating them would make one long needle.
    for password in "$dir"/passwords/*.txt; do
      [ -f "$password" ] && { cat "$password"; echo; } >> "$work/needles"
    done
    for key in "$dir/ca.key" "$dir/mosquitto/server.key" "$dir/keycloak/server.key"; do
      # The second line of a PEM key: unique base64 of that key (read as root if it belongs to a container uid).
      [ -e "$key" ] && { sed -n 2p "$key" 2>/dev/null || docker run --rm --user 0 --entrypoint sed -v "$(dirname "$key"):/k:ro" \
        "$HELPER_IMAGE" -n 2p "/k/$(basename "$key")"; } >> "$work/needles"
    done
  done
  sed -i '/^$/d' "$work/needles"
  # Layers of the pinned runtime base image (from Microsoft) are skipped by the generic PEM check only: Debian's
  # libgnutls ships an embedded sample key. The exact-match check below still covers every layer.
  base_image="$(grep -E '^FROM ' "$root/deployments/cloud/Dockerfile" | tail -1 | awk '{print $2}')"
  base_layers="$(docker image inspect "$base_image" --format '{{range .RootFS.Layers}}{{println .}}{{end}}' 2>/dev/null)"
  while IFS= read -r -d '' blob; do
    if [ "$(head -c2 "$blob" | od -An -tx1 | tr -d ' ')" = "1f8b" ]; then reader=(gzip -dc "$blob"); else reader=(cat "$blob"); fi
    # A real PEM private key: the header line (CRLF tolerated) followed by a base64 line of 40+ characters
    # (literal strings in libraries are not followed by key material; OpenSSH keys use 70-character lines).
    if ! grep -qx "sha256:$(basename "$blob")" <<<"$base_layers" && "${reader[@]}" | awk '{ sub(/\r$/, "") } /-----BEGIN [A-Z ]*PRIVATE KEY-----$/ { if ((getline n) > 0) { sub(/\r$/, "", n); if (n ~ /^[A-Za-z0-9+\/=]{40,}$/) found = 1 } } END { exit !found }'; then
      echo "  a PEM private key in layer $(basename "$blob")"; image_ok=0
    fi
    if [ -s "$work/needles" ] && "${reader[@]}" | grep -a -F -q -f "$work/needles"; then
      echo "  a generated password or key in layer $(basename "$blob")"; image_ok=0
    fi
  done < <(find "$work/image" -type f -size +0 -print0)
else
  echo "  could not export the image"; image_ok=0
fi
[ "$image_ok" = 1 ] && pass SMOKE-06 "no private key or generated secret in any image layer" || fail SMOKE-06 "secret material in the image"

# --- SMOKE-07: the tracked tree is unchanged (nothing rendered or written into the repository). ---
status_after="$(tree_snapshot)" || status_after="<git status failed>"
[ "$status_after" = "$status_before" ] && pass SMOKE-07 "repository tree unchanged" \
  || fail SMOKE-07 "repository tree changed during the run"

if [ "$failures" -eq 0 ]; then
  echo "Smoke test passed."
  exit 0
fi
echo "Smoke test failed: $failures check(s)."
exit 1
