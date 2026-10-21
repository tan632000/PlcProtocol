#!/usr/bin/env bash
# Server-side Cloud deploy, called by CD over SSH (docs/server-deploy.md). It swaps only the Cloud container to an
# immutable image of a commit on main; broker, Keycloak and compose changes are manual upgrades, so CD can never flip a
# PLC write switch. It never reads, creates or prints secrets.
#
#   remote-deploy.sh <sha> <image@digest> <tip|rollback>
#   (no arguments: the same three words from SSH_ORIGINAL_COMMAND, as the CI key's forced command)
#
# Exit 0 deployed (or skipped: older than what runs), 1 failed while deploying, 2 refused before changing anything.
set -euo pipefail

refuse() { echo "refused: $*" >&2; exit 2; }
fail() { echo "failed: $*" >&2; exit 1; }

if [ $# -eq 0 ]; then
  [ -n "${SSH_ORIGINAL_COMMAND:-}" ] || refuse "usage: remote-deploy.sh <sha> <image@digest> <tip|rollback>"
  # read stops at a newline, so a second line would otherwise pass unseen.
  [[ "$SSH_ORIGINAL_COMMAND" != *$'\n'* ]] || refuse "unexpected second line"
  read -r sha image mode extra <<<"$SSH_ORIGINAL_COMMAND" || true
else
  [ $# -eq 3 ] || refuse "usage: remote-deploy.sh <sha> <image@digest> <tip|rollback>"
  sha="$1" image="$2" mode="$3" extra=""
fi

[[ "${sha:-}" =~ ^[0-9a-f]{40}$ ]] || refuse "sha must be 40 lowercase hex characters"
[[ "${image:-}" =~ ^ghcr\.io/tan632000/plcprotocol/cloud@sha256:[0-9a-f]{64}$ ]] \
  || refuse "image must be ghcr.io/tan632000/plcprotocol/cloud@sha256:<digest>"
[[ "${mode:-}" == tip || "${mode:-}" == rollback ]] || refuse "mode must be tip or rollback"
[ -z "${extra:-}" ] || refuse "unexpected input after the mode"
timeout="${IIOT_READY_TIMEOUT:-120}"
[[ "$timeout" =~ ^[1-9][0-9]{0,3}$ ]] || refuse "IIOT_READY_TIMEOUT must be 1-9999 seconds"

cd "$(dirname "${BASH_SOURCE[0]}")/../.."
compose() { IIOT_CLOUD_IMAGE="$image" docker compose -f deployments/compose.yaml "$@"; }

# --- Refusals: nothing has changed yet. ---
[ -d deployments/.secrets ] || refuse "deployments/.secrets is missing (create it once with dev-secrets.sh; CD never does)"
dirty="$(git status --porcelain --untracked-files=no)"
[ -z "$dirty" ] || refuse "tracked files are modified on the server: $(echo "$dirty" | awk '{print $2}' | tr '\n' ' ')"
git fetch --quiet origin main
git merge-base --is-ancestor "$sha" refs/remotes/origin/main 2>/dev/null || refuse "$sha is not a commit on origin/main"

head="$(git rev-parse HEAD)"
if [ "$mode" = tip ] && [ "$sha" != "$head" ] && git merge-base --is-ancestor "$sha" "$head"; then
  echo "skipped: $sha is older than the deployed $head"
  exit 0
fi

target_compose="$(git show "$sha:deployments/compose.yaml")" || refuse "$sha has no deployments/compose.yaml"
grep -qF 'IIOT_CLOUD_IMAGE' <<<"$target_compose" \
  || refuse "$sha predates the IIOT_CLOUD_IMAGE switch in deployments/compose.yaml and cannot be deployed by CD"
upgrade="$(git diff --name-only "$head" "$sha" -- deployments/mosquitto deployments/keycloak deployments/compose.yaml deployments/.secrets)"
[ -z "$upgrade" ] || refuse "manual upgrade needed, see docs/server-deploy.md; changed: $(echo "$upgrade" | tr '\n' ' ')"

# --- Deploy. ---
docker pull --quiet "$image" >/dev/null || fail "cannot pull $image"

if [ -n "$(git diff --name-only "$head" "$sha" -- src/Cloud/Data/Migrations)" ]; then
  mkdir -p deployments/.backups
  backup="deployments/.backups/$(date -u +%Y%m%dT%H%M%SZ)-${head:0:12}.sql"
  (umask 077 && compose exec -T postgres pg_dump -U iiot iiot >"$backup") || { rm -f "$backup"; fail "database backup failed"; }
  echo "database backed up to $PWD/$backup"
fi

git checkout --quiet --detach "$sha"

# Logs stay on the server (workflow logs of a public repository are public); only the file path is printed.
fail_with_logs() {
  mkdir -p deployments/.deploy-logs
  local log="deployments/.deploy-logs/$sha.log"
  rm -f "$log" # a file from an earlier run keeps its mode; a new one is created private
  (umask 077 && compose logs --no-color --tail 200 cloud >"$log" 2>&1) || true
  chmod 600 "$log"
  fail "$1; Cloud log tail on the server: $PWD/$log"
}

compose up -d --no-deps --no-build --wait cloud || fail_with_logs "the Cloud container did not start"

# The address compose published (deployments/.env may set IIOT_API_PORT; the forced command's environment does not).
api="$(compose port cloud 8080)" || fail_with_logs "the Cloud port is not published"
deadline=$((SECONDS + timeout))
until curl -fsS --max-time 5 -o /dev/null "http://$api/health/ready" 2>/dev/null; do
  [ "$SECONDS" -lt "$deadline" ] || fail_with_logs "/health/ready did not answer within $timeout s"
  sleep 2
done

# Manual compose commands on the server use the last deployed image, never a local build.
docker tag "$image" iiot-cloud:local
docker image prune -af --filter until=240h >/dev/null
echo "deployed $sha: $(compose ps --format '{{.Image}}' cloud)"
