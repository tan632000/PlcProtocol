#!/usr/bin/env bash
# Prints an access token for a trial user (viewer | operator) — the token only, for piping:
#   TOKEN=$(bash deployments/scripts/get-token.sh viewer)
#   curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:8080/api/v1/edges
#
# The request runs in a container on the compose network, so the issuer is always
# https://keycloak:8443/realms/iiot — the URL the Cloud validates against. The password is read from the
# mounted passwords/<user>.txt by curl itself: it never appears on a command line or in an environment.
#
# Options: --secrets DIR (default deployments/.secrets), --project NAME (default iiot).
set -euo pipefail
export MSYS_NO_PATHCONV=1

CURL_IMAGE="curlimages/curl@sha256:c1fe1679c34d9784c1b0d1e5f62ac0a79fca01fb6377cdd33e90473c6f9f9a69"

script_dir="$(cd "$(dirname "$0")" && pwd)"
secrets="$script_dir/../.secrets"
project="iiot"
user=""

while [ $# -gt 0 ]; do
  case "$1" in
    --secrets) secrets="${2:?--secrets needs a directory}"; shift 2 ;;
    --project) project="${2:?--project needs a name}"; shift 2 ;;
    viewer|operator) user="$1"; shift ;;
    *) echo "get-token: unknown argument '$1' (expected viewer or operator)" >&2; exit 2 ;;
  esac
done
[ -n "$user" ] || { echo "get-token: which user? viewer or operator" >&2; exit 2; }

host_path() {
  if command -v cygpath >/dev/null; then cygpath -m "$1"; else printf '%s' "$1"; fi
}
secrets="$(host_path "$(cd "$secrets" && pwd)")"
[ -f "$secrets/passwords/$user.txt" ] || { echo "get-token: no password file for '$user' in $secrets" >&2; exit 2; }

response="$(docker run --rm --user 0 --network "${project}_default" \
  -v "$secrets/ca.pem:/run/iiot/ca.pem:ro" \
  -v "$secrets/passwords/$user.txt:/run/iiot/password:ro" \
  "$CURL_IMAGE" -sS --fail --cacert /run/iiot/ca.pem \
  -d grant_type=password -d client_id=iiot-cli -d "username=$user" -d scope=openid \
  --data-urlencode "password@/run/iiot/password" \
  https://keycloak:8443/realms/iiot/protocol/openid-connect/token)"

token="$(printf '%s' "$response" | sed -n 's/.*"access_token":"\([^"]*\)".*/\1/p')"
[ -n "$token" ] || { echo "get-token: no access token in the response" >&2; exit 1; }
printf '%s\n' "$token"
