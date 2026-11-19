#!/usr/bin/env bash
# End-to-end check: build + test + pack MSG, generate services that consume the local Msg.* packages,
# build them with warnings-as-errors, run one and hit its endpoints, check "msg upgrade",
# then build and test the full variants. Usage: scripts/smoke-test.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
PKGS="$WORK/packages"
PID=""
cleanup() { [[ -n "$PID" ]] && kill "$PID" 2>/dev/null || true; rm -rf "$WORK"; }
trap cleanup EXIT

pass() { echo "  ✔ $1"; }
fail() { echo "  ✘ $1"; [[ -f "$WORK/app.log" ]] && tail -20 "$WORK/app.log"; exit 1; }

# Without nuget.org (offline sandboxes) only the framework-only parts can be built; audit data can't be fetched either.
if curl -sf -m 5 https://api.nuget.org/v3/index.json >/dev/null 2>&1; then ONLINE=1; AUDIT=(); else ONLINE=0; AUDIT=(-p:NuGetAudit=false); fi

echo "▶ Build, test and pack MSG"
if [[ $ONLINE == 1 ]]; then
  dotnet build "$ROOT/Msg.slnx" -c Release -v q -nologo >/dev/null
  dotnet test  "$ROOT/Msg.slnx" -c Release --no-build -v q -nologo >/dev/null && pass "Msg.ServiceDefaults.* tests pass"
  dotnet pack  "$ROOT/Msg.slnx" -c Release --no-build -o "$PKGS" -v q -nologo >/dev/null
else
  echo "  – nuget.org not reachable: building/packing only the framework-only projects"
  dotnet build "$ROOT/src/Msg.Cli" -c Release -v q -nologo >/dev/null
  dotnet pack  "$ROOT/src/Msg.ServiceDefaults" -c Release -o "$PKGS" -v q -nologo >/dev/null
fi
pass "packed: $(ls "$PKGS" | tr '\n' ' ')"
MSG=(dotnet "$ROOT/src/Msg.Cli/bin/Release/net10.0/msg.dll")
VERSION="$("${MSG[@]}" --version)"

# The global NuGet cache keeps packages by id+version: drop old Msg.* builds so we test the ones just packed.
GLOBAL="$(dotnet nuget locals global-packages --list | sed 's/^global-packages: //')"
rm -rf "$GLOBAL"/msg.servicedefaults*

echo "▶ Generate every feature combination worth checking (MSG $VERSION)"
"${MSG[@]}" new Orders  -o "$WORK/full" --package-source "$PKGS"        >/dev/null && pass "full (default features)"
"${MSG[@]}" new Billing -o "$WORK/auth" --package-source "$PKGS" --auth >/dev/null && pass "full + auth"
"${MSG[@]}" new Catalog -o "$WORK/core" --package-source "$PKGS" \
  --no-observability --no-openapi --no-resilience --no-tests            >/dev/null && pass "core (framework only)"
"${MSG[@]}" new --from "$WORK/auth/msg.json" -o "$WORK/from" --package-source "$PKGS" >/dev/null && pass "--from msg.json"

echo "▶ Template sanity"
if grep -rn "@msg:\|{{Service}}\|{{service-kebab}}\|{{DevPort}}\|{{MsgVersion}}\|__Service__" "$WORK"/{full,auth,core,from} ; then
  fail "unrendered template markers found"
fi
pass "no template markers left"
for f in $(find "$WORK"/{full,auth,core,from} -name "*.json"); do
  python3 -m json.tool "$f" >/dev/null || fail "invalid JSON: $f"
done
pass "all JSON files are valid"
diff -r "$WORK/auth" "$WORK/from" >/dev/null && pass "--from reproduces the same service" || fail "--from differs"
grep -q "Include=\"Msg.ServiceDefaults\" Version=\"$VERSION\"" "$WORK/core/Directory.Packages.props" \
  && pass "service pins Msg.ServiceDefaults $VERSION" || fail "Msg.ServiceDefaults version"

echo "▶ Build 'core' service (TreatWarningsAsErrors)"
dotnet build "$WORK/core" -c Release -v q -nologo "${AUDIT[@]}" >/dev/null || dotnet build "$WORK/core" -c Release -nologo "${AUDIT[@]}" | tail -20
pass "core builds against the local Msg.ServiceDefaults package with zero warnings"

echo "▶ Run 'core' service and test the contract"
PORT=5987
APP_DIR="$WORK/core/src/Catalog.Api/bin/Release/net10.0"   # content root = cwd, where appsettings.json lives
(cd "$APP_DIR" && ASPNETCORE_URLS="http://127.0.0.1:$PORT" ASPNETCORE_ENVIRONMENT=Production \
  exec dotnet Catalog.Api.dll) >"$WORK/app.log" 2>&1 &
PID=$!
for _ in $(seq 1 50); do curl -sf "http://127.0.0.1:$PORT/health/live" >/dev/null 2>&1 && break; sleep 0.2; done

BASE="http://127.0.0.1:$PORT"
[[ "$(curl -s -o /dev/null -w '%{http_code}' $BASE/health/live)"  == 200 ]] && pass "/health/live 200"  || fail "/health/live"
[[ "$(curl -s -o /dev/null -w '%{http_code}' $BASE/health/ready)" == 200 ]] && pass "/health/ready 200" || fail "/health/ready"

PING="$(curl -s $BASE/api/v1/ping)"
echo "$PING" | grep -q '"service":"catalog"' && pass "/api/v1/ping → $PING" || fail "ping: $PING"

HDRS="$(curl -s -D - -o "$WORK/404.json" -H 'X-Correlation-ID: smoke-42' $BASE/nope)"
echo "$HDRS" | grep -qi '^content-type: application/problem+json' && pass "404 is application/problem+json" || fail "404 content-type"
echo "$HDRS" | grep -qi '^x-correlation-id: smoke-42'          && pass "X-Correlation-ID echoed"        || fail "correlation header"
grep -q '"correlationId":"smoke-42"' "$WORK/404.json"           && pass "ProblemDetails has correlationId" || fail "problem body"

BAD="$(curl -s -D - -o /dev/null -H 'X-Correlation-ID: <script>' $BASE/health/live | grep -i x-correlation-id || true)"
[[ "$BAD" != *"<script>"* ]] && pass "unsafe correlation id rejected" || fail "unsafe correlation id accepted"

head -c 1 "$WORK/app.log" | grep -q '{' && pass "logs are structured JSON in Production" || fail "logs not JSON"

kill "$PID"; wait "$PID" 2>/dev/null || true; PID=""

echo "▶ Startup fails fast on invalid configuration"
if (cd "$APP_DIR" && Service__Version=banana timeout 10 dotnet Catalog.Api.dll \
     --urls "http://127.0.0.1:$((PORT+1))") >"$WORK/bad.log" 2>&1; then
  fail "service started with invalid config"
fi
grep -q "SemVer" "$WORK/bad.log" && pass "invalid Service:Version stops startup with a clear message" || fail "no validation message"

echo "▶ msg upgrade"
cp -r "$WORK/core" "$WORK/upgraded"
"${MSG[@]}" upgrade --path "$WORK/upgraded" --to 9.9.9 >/dev/null
grep -q 'Include="Msg.ServiceDefaults" Version="9.9.9"' "$WORK/upgraded/Directory.Packages.props" \
  && grep -q '"msgVersion": "9.9.9"' "$WORK/upgraded/msg.json" \
  && pass "upgrade bumps Msg.* packages and msg.json" || fail "upgrade"

echo "▶ Full services (need nuget.org)"
if [[ $ONLINE == 1 ]]; then
  for s in full auth; do
    dotnet build "$WORK/$s" -c Release -v q -nologo >/dev/null && pass "$s builds"
    dotnet test  "$WORK/$s" -c Release --no-build -v q -nologo >/dev/null && pass "$s tests pass"
  done
else
  echo "  – nuget.org not reachable: skipped (run this script on your machine)"
fi

echo "✅ Smoke test OK"
