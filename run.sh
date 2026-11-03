#!/usr/bin/env bash
# One flow: build -> test -> start the API -> walk through every feature -> stop.
# Usage: ./run.sh            (local)
#        ./run.sh --docker   (also build the Docker image at the end)
set -euo pipefail

cd "$(dirname "$0")"
[ -x "$HOME/.dotnet/dotnet" ] && export PATH="$HOME/.dotnet:$PATH"
command -v dotnet >/dev/null || { echo "Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0"; exit 1; }

PORT="${PORT:-5080}"
BASE="http://localhost:$PORT"
LOG="$(mktemp -t capacity-router.XXXXXX)"
JSON='Content-Type: application/json'

step() { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
call() { # call METHOD PATH [BODY]
  local method="$1" path="$2" body="${3:-}"
  printf '\033[0;33m%s %s %s\033[0m\n' "$method" "$path" "$body"
  if [ -n "$body" ]; then
    curl -s -w '\n[HTTP %{http_code}]\n' -X "$method" "$BASE$path" -H "$JSON" -d "$body"
  else
    curl -s -w '\n[HTTP %{http_code}]\n' -X "$method" "$BASE$path"
  fi
}
heartbeat() { for id in "$@"; do curl -s -o /dev/null -X POST "$BASE/capacities/$id/heartbeat"; done; }

step "1/7 Build"
dotnet build -c Release --nologo -v q

step "2/7 Test"
dotnet test tests/CapacityRouter.Tests -c Release --no-build --nologo -v q

step "3/7 Start API on $BASE (heartbeat timeout shortened to 6s for the demo)"
Heartbeat__TimeoutSeconds=6 Heartbeat__CheckIntervalSeconds=1 ASPNETCORE_URLS="$BASE" \
  dotnet run --project src/CapacityRouter.Api -c Release --no-build >"$LOG" 2>&1 &
API_PID=$!
trap 'kill $API_PID 2>/dev/null || true; wait $API_PID 2>/dev/null || true' EXIT
for _ in $(seq 1 30); do curl -sf "$BASE/health" >/dev/null && break; sleep 1; done
call GET /health

step "4/7 Register capacities and route workloads"
call POST /capacities '{"id":"cap-india-1","region":"centralindia","totalUnits":10}'
call POST /capacities '{"id":"cap-eu-1","region":"westeurope","totalUnits":20}'
call POST /route '{"workloadId":"job-1","units":4,"preferredRegion":"centralindia"}'
call POST /route '{"workloadId":"job-2","units":8,"preferredRegion":"centralindia"}'   # India full -> falls back to EU
call POST /route '{"workloadId":"job-3","units":0}'                                    # invalid -> 400
call POST /route '{"workloadId":"job-4","units":999}'                                  # too big -> 503
call GET /capacities

step "5/7 Release an allocation"
ALLOC_ID=$(curl -s -X POST "$BASE/route" -H "$JSON" -d '{"workloadId":"job-5","units":2}' \
  | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')
call DELETE "/allocations/$ALLOC_ID"
call DELETE /allocations/does-not-exist                                                # -> 404

step "6/7 Failure detection: stop heartbeats for cap-india-1 only"
for _ in $(seq 1 8); do heartbeat cap-eu-1; sleep 1; done
call GET /capacities
call POST /route '{"workloadId":"job-6","units":1,"preferredRegion":"centralindia"}'   # goes to EU, India is unhealthy

step "7/7 Recovery: cap-india-1 sends a heartbeat again"
call POST /capacities/cap-india-1/heartbeat
call POST /route '{"workloadId":"job-7","units":1,"preferredRegion":"centralindia"}'   # back to India
call GET /capacities

step "API log"
cat "$LOG"

if [ "${1:-}" = "--docker" ]; then
  step "Docker image"
  docker build -t capacity-router .
  echo "Run it with: docker run -p 8080:8080 capacity-router"
fi

step "Done. Build, tests and the end-to-end demo all passed."
