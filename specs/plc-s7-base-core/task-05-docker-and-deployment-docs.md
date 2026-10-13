# Task 05 — API ships as a Linux container and Windows self-contained build, with operator docs

Status: blocked

## Outcome
`docker build` produces a non-root Linux image whose `/health/live` returns 200; docs explain PLC (TIA Portal) prerequisites, configuration, and how to reach a plant PLC from cloud safely.

## Scope
- In: multi-stage Dockerfile, `.dockerignore`, smoke script, docs for deployment and PLC setup, Windows `dotnet publish` command documented.
- Out: CI/CD pipeline, Kubernetes manifests, VPN/edge infrastructure provisioning, TLS certificates.

## Coverage
- CP-05

## Ownership
- Create: `Dockerfile` (build `mcr.microsoft.com/dotnet/sdk:10.0`, runtime `mcr.microsoft.com/dotnet/aspnet:10.0`, `USER $APP_UID`, `EXPOSE 8080`, `ASPNETCORE_URLS=http://+:8080`)
- Create: `.dockerignore` (`bin/`, `obj/`, `.git/`, `specs/`, `.claude/`, `TestResults/`)
- Create: `scripts/smoke-container.ps1`
- Create: `docs/deployment.md`, `docs/plc-setup.md`

## Contracts
- Image contains no secrets; all config through env (`Plc__Host`, `Api__Keys__0`, `Safety__WritableTags__0`, ...).
- `scripts/smoke-container.ps1`: builds image `plcprotocol-api:smoke`, runs it with `Plc__Host=127.0.0.1` and a 32-char test key, polls `http://localhost:18080/health/live` up to 30 s, asserts 200, then `docker rm -f` the container (always, via `finally`). Exit 0 on success, 1 otherwise.
- `docs/plc-setup.md`: TIA Portal steps — enable "Permit access with PUT/GET", disable "Optimized block access" per DB, rack/slot values, firewall port 102; S7-1200 G2 PUT/GET support marked `[UNVERIFIED]` until tested on hardware.
- `docs/deployment.md`: Docker run example; Windows Server `dotnet publish src/PlcProtocol.Api -c Release -r win-x64 --self-contained`; Linux systemd alternative; network warning — **never expose TCP 102 to the Internet**; use site-to-site VPN or run the container on an edge host in the plant network; put HTTPS reverse proxy in front of the API.
- Each doc ≤ 800 lines.

## Steps
1. Write Dockerfile + `.dockerignore`.
2. Write smoke script; run it.
3. Write both docs.

## Acceptance
- AC-08: smoke script exits 0 with HTTP 200 from `/health/live` in the container.

## Dependencies
- task-04-minimal-api.md

## Verification Plan
- Command: `pwsh -File scripts/smoke-container.ps1` (fallback `powershell -File scripts/smoke-container.ps1` on Windows PowerShell 5.1)
- Named probe: smoke script assertion `health/live == 200`
- Reachability: installed — requires Docker daemon running (prerequisite in plan.md).
- Oracle: exit 0 and printed line `SMOKE PASS: /health/live 200`; `docker ps -a --filter name=plcprotocol-smoke` empty afterwards.
- Counterexample: an image that runs as root or listens on a port other than 8080 must fail (script also asserts `docker exec ... id -u` ≠ 0).
- Artifacts: image `plcprotocol-api:smoke` may remain locally; container removed by script.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt
<!-- Fill only after execution; see canonical form in templates. -->
