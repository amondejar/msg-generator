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
  # Without nuget.org the Blazor framework assets package can't be restored: build the CLI without it.
  dotnet build "$ROOT/src/Msg.Cli" -c Release -v q -nologo -p:RequiresAspNetWebAssets=false >/dev/null
  dotnet pack  "$ROOT/src/Msg.Ddd" -c Release -o "$PKGS" -v q -nologo >/dev/null
  dotnet pack  "$ROOT/src/Msg.ServiceDefaults" -c Release -o "$PKGS" -v q -nologo >/dev/null
fi
pass "packed: $(ls "$PKGS" | tr '\n' ' ')"
MSG=(dotnet "$ROOT/src/Msg.Cli/bin/Release/net10.0/msg.dll")
VERSION="$("${MSG[@]}" --version)"

# The global NuGet cache keeps packages by id+version: drop old Msg.* builds so we test the ones just packed.
GLOBAL="$(dotnet nuget locals global-packages --list | sed 's/^global-packages: //')"
rm -rf "$GLOBAL"/msg.servicedefaults* "$GLOBAL"/msg.ddd

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

echo "▶ msg add entity"
"${MSG[@]}" add entity Product  --path "$WORK/full" --props "Name:string, Price:decimal, Description:string?, Stock:int" >/dev/null
"${MSG[@]}" add entity Category --path "$WORK/full" --props "Title:string, Position:int?" --plural Categories >/dev/null
"${MSG[@]}" add entity Invoice  --path "$WORK/auth" --db sqlserver --props "Number:string, Total:decimal, Paid:bool" >/dev/null
pass "entities added (postgres + sqlserver)"
if "${MSG[@]}" add entity Product --path "$WORK/full" --props "Name:string" >/dev/null 2>&1; then fail "duplicate entity accepted"; fi
pass "duplicate entity rejected"
grep -q "MapProductEndpoints" "$WORK/full/src/Orders.Api/Features/FeatureRegistration.g.cs" \
  && grep -q "Npgsql.EntityFrameworkCore.PostgreSQL" "$WORK/full/Directory.Packages.props" \
  && grep -q '"Database"' "$WORK/full/src/Orders.Api/appsettings.Development.json" \
  && pass "registration, packages and connection string wired" || fail "entity wiring"
"${MSG[@]}" new --from "$WORK/full/msg.json" -o "$WORK/regen" --package-source "$PKGS" >/dev/null && pass "msg new --from regenerates the entities"

echo "▶ Clean Architecture + DDD"
"${MSG[@]}" new Catalog -o "$WORK/clean" --architecture clean-ddd --package-source "$PKGS" >/dev/null
"${MSG[@]}" add entity Product  --path "$WORK/clean" --props "Name:string, Price:decimal, Description:string?" >/dev/null
"${MSG[@]}" add entity Category --path "$WORK/clean" --props "Title:string, Position:int?" --plural Categories >/dev/null
for p in Domain Application Infrastructure Api; do
  [[ -f "$WORK/clean/src/Catalog.$p/Catalog.$p.csproj" ]] || fail "missing Catalog.$p"
done
grep -q "AggregateRoot<Guid>" "$WORK/clean/src/Catalog.Domain/Products/Product.g.cs" \
  && grep -q "ICommandHandler<CreateProduct, Guid>" "$WORK/clean/src/Catalog.Application/Products/ProductCommands.g.cs" \
  && grep -q "AddScoped<IProductRepository, ProductRepository>" "$WORK/clean/src/Catalog.Infrastructure/Persistence/RepositoryRegistration.g.cs" \
  && pass "Domain / Application / Infrastructure / Api with aggregates, use cases and repositories" || fail "clean-ddd structure"
if grep -rn "@msg:\|{{Service}}\|{{Entity}}\|__Entity__" "$WORK/clean"; then fail "unrendered markers in clean-ddd"; fi
pass "no template markers left in clean-ddd"

echo "▶ Editable entities (msg entity)"
CP="$WORK/clean/src/Catalog.Domain/Products/Product.cs"
# User code in the partial class: must survive every regeneration.
cat > "$CP" <<'CS'
using Msg.Ddd;

namespace Catalog.Domain.Products;

public sealed partial class Product
{
    partial void EnsureInvariants()
    {
        if (Price < 0)
        {
            throw new DomainException("Price can't be negative.");
        }
    }

    public string Label => $"{Name} ({Price})";
}
CS
OUT=$("${MSG[@]}" entity update Product --path "$WORK/clean" --props "Title:string, Price:decimal, Stock:int" --rename Name=Title)
grep -q "Product.Name se renombra a Title" <<<"$OUT" && grep -q "Product.Description se elimina" <<<"$OUT" \
  && pass "update warns about renamed/removed properties used in user code" || { echo "$OUT"; fail "entity update warnings"; }
grep -q "Label => " "$CP" && grep -q "public string Title" "$WORK/clean/src/Catalog.Domain/Products/Product.g.cs" \
  && ! grep -q "Description" "$WORK/clean/src/Catalog.Domain/Products/Product.g.cs" \
  && pass "properties regenerated in .g.cs, user partial untouched" || fail "entity update"
grep -q '"from": "Name"' "$WORK/clean/msg.json" && pass "rename recorded in msg.json (pendingRenames)" || fail "pendingRenames"
sed -i 's/{Name} ({Price})/{Title} ({Price})/' "$CP"
"${MSG[@]}" entity update Product --path "$WORK/clean" --props "Title:string, Price:decimal, Stock:int" | grep -q "Nada que cambiar" \
  && pass "no-op update changes nothing" || fail "no-op update"
"${MSG[@]}" add entity Tag --path "$WORK/clean" --props "Label:string" >/dev/null
"${MSG[@]}" entity remove Tag --path "$WORK/clean" --delete-own-files >/dev/null
if find "$WORK/clean" -path "*Tag*" -name "*.cs" | grep -q .; then fail "entity remove left files"; fi
grep -q "Tag" "$WORK/clean/src/Catalog.Infrastructure/Persistence/CatalogDbContext.g.cs" && fail "Tag still in DbContext"
pass "entity remove deletes its files and registrations"
# An entity from MSG < 0.10 (no "layout" in msg.json) is read-only until converted.
python3 - "$WORK/full/msg.json" <<'PY'
import json, sys
p = sys.argv[1]; d = json.load(open(p))
for e in d["entities"]:
    if e["name"] == "Category": e.pop("layout", None)
json.dump(d, open(p, "w"), indent=2)
PY
if "${MSG[@]}" entity update Category --path "$WORK/full" --props "Title:string" >/dev/null 2>&1; then fail "legacy entity edited without converting"; fi
"${MSG[@]}" entity convert Category --path "$WORK/full" --dry-run | grep -q "(archivo tuyo)" \
  && "${MSG[@]}" entity convert Category --path "$WORK/full" >/dev/null \
  && "${MSG[@]}" entity update Category --path "$WORK/full" --props "Title:string, Position:int?, Color:string?" >/dev/null \
  && pass "legacy entity: blocked, converted (shows user files), then editable" || fail "entity convert"

echo "▶ Features on/off in an existing service (msg feature)"
FS="$WORK/features"
"${MSG[@]}" new Shop -o "$FS" --package-source "$PKGS" >/dev/null
"${MSG[@]}" add entity Product --path "$FS" --props "Name:string" >/dev/null
"${MSG[@]}" feature off docker openApi --path "$FS" >/dev/null
[[ ! -f "$FS/Dockerfile" ]] && ! grep -q "Scalar.AspNetCore" "$FS/src/Shop.Api/Shop.Api.csproj" "$FS/Directory.Packages.props" "$FS/src/Shop.Api/MsgWiring.g.cs" \
  && grep -q "AddMsgWiring()" "$FS/src/Shop.Api/Program.cs" \
  && pass "feature off removes its files, packages and wiring (Program.cs untouched)" || fail "feature off"
"${MSG[@]}" feature on docker openApi --path "$FS" >/dev/null
[[ -f "$FS/Dockerfile" ]] && grep -q 'Include="Scalar.AspNetCore"' "$FS/src/Shop.Api/Shop.Api.csproj" && grep -q "MapScalarApiReference" "$FS/src/Shop.Api/MsgWiring.g.cs" \
  && pass "feature on brings them back" || fail "feature on"
sed -i 's/builder.AddFeatures();/builder.AddFeatures(); \/\/ tweak/' "$FS/src/Shop.Api/MsgWiring.g.cs"
"${MSG[@]}" feature on auth --path "$FS" --dry-run | grep -q "Has editado a mano" \
  && pass "a hand-edited .g.cs is detected before overwriting it" || fail "hand-edit detection"
"${MSG[@]}" feature on auth --path "$FS" >/dev/null   # restores the generated wiring (and keeps auth for the build below)
LP="$WORK/legacy-program"; cp -r "$FS" "$LP"
cat > "$LP/src/Shop.Api/Program.cs" <<'CS'
using Shop.Api;
using Shop.Api.Endpoints;
using Shop.Api.Features;
using Msg.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddMsgServiceDefaults();
builder.Services.AddValidatedOptions<ServiceOptions>(ServiceOptions.SectionName);
builder.Services.AddValidation();
builder.AddFeatures();
var app = builder.Build();
app.UseMsgServiceDefaults();
app.MapMsgDefaultEndpoints();
var v1 = app.MapGroup("/api/v1");
v1.MapPingEndpoints();
v1.MapFeatures();
await app.RunAsync().ConfigureAwait(false);
CS
if "${MSG[@]}" feature off docker --path "$LP" >/dev/null 2>&1; then fail "features changed with a Program.cs from before 0.12"; fi
"${MSG[@]}" feature convert --path "$LP" >/dev/null && grep -q "AddMsgWiring()" "$LP/src/Shop.Api/Program.cs" \
  && "${MSG[@]}" feature off docker --path "$LP" >/dev/null \
  && pass "Program.cs from before 0.12: blocked, converted, then editable" || fail "feature convert"

echo "▶ Guided migrations (msg migration review)"
MIG="$WORK/migration"; cp -r "$WORK/clean" "$MIG"
mkdir -p "$MIG/src/Catalog.Infrastructure/Migrations"
MF="$MIG/src/Catalog.Infrastructure/Migrations/20990101000000_RenameTitle.cs"
# What "dotnet ef migrations add" scaffolds for a renamed property: drop + add (the data would be lost).
cat > "$MF" <<'CS'
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameTitle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tags");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "products");

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "products",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Title",
                table: "products");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "products",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");
        }
    }
}
CS
OUT=$("${MSG[@]}" migration review --path "$MIG" --dry-run)
grep -q "RenameColumn" <<<"$OUT" && grep -q "BORRA DATOS: Borra la tabla tags" <<<"$OUT" && ! grep -q "RenameColumn" "$MF" \
  && pass "review --dry-run reports the rename fix and the dropped table without writing" || { echo "$OUT"; fail "migration review --dry-run"; }
"${MSG[@]}" migration review --path "$MIG" >/dev/null
grep -q 'migrationBuilder.RenameColumn' "$MF" && ! grep -q 'name: "Title",' <<<"$(sed -n '/void Up/,/void Down/p' "$MF" | grep -A1 AddColumn)" \
  && ! grep -q pendingRenames "$MIG/msg.json" \
  && pass "review turns drop + add into RenameColumn and clears pendingRenames" || { cat "$MF"; fail "migration review"; }

echo "▶ Team templates (.msg)"
TEAM="$WORK/team"; mkdir -p "$TEAM/services"
"${MSG[@]}" templates eject service/src/__Service__.Api/Program.cs --path "$TEAM" >/dev/null
sed -i '1i // {{Company}} conventions' "$TEAM/.msg/templates/service/src/__Service__.Api/Program.cs"
printf '{ "tokens": { "Company": "Acme" } }\n' > "$TEAM/.msg/config.json"
printf '* @{{Company}}/backend\n' > "$TEAM/.msg/templates/service/CODEOWNERS"
: > "$TEAM/.msg/templates/service/Dockerfile"
"${MSG[@]}" new Orders -o "$TEAM/services/orders" --package-source "$PKGS" >/dev/null
"${MSG[@]}" add entity Product --path "$TEAM/services/orders" --props "Name:string" >/dev/null
head -1 "$TEAM/services/orders/src/Orders.Api/Program.cs" | grep -q "Acme conventions" \
  && [[ "$(cat "$TEAM/services/orders/CODEOWNERS")" == "* @Acme/backend" ]] \
  && [[ ! -f "$TEAM/services/orders/Dockerfile" ]] \
  && pass "team templates: replace, add, remove and tokens (found walking up)" || fail "team templates"
echo '// @msg:if bogus' >> "$TEAM/.msg/templates/service/CODEOWNERS"
{ "${MSG[@]}" new Broken -o "$TEAM/services/broken" 2>&1 || true; } | grep -q ".msg/templates/service/CODEOWNERS, línea 2" \
  && pass "a broken team template is reported with file and line" || fail "broken template error"

echo "▶ msg aspire"
SYS="$WORK/system"; mkdir -p "$SYS"
"${MSG[@]}" new Orders  -o "$SYS/orders"  --package-source "$PKGS" >/dev/null
"${MSG[@]}" add entity Order --path "$SYS/orders" --props "Number:string, Total:decimal" >/dev/null
"${MSG[@]}" new Billing -o "$SYS/billing" --architecture clean-ddd --package-source "$PKGS" >/dev/null
"${MSG[@]}" add entity Invoice --path "$SYS/billing" --db sqlserver --props "Number:string" >/dev/null
"${MSG[@]}" aspire --path "$SYS" --name Shop >/dev/null
for f in Shop.AppHost/Shop.AppHost.csproj Shop.AppHost/AppHost.cs Shop.AppHost/MsgServices.g.cs Shop.slnx .aspire/settings.json; do
  [[ -f "$SYS/$f" ]] || fail "missing $f"
done
grep -q 'AddProject<Projects.Orders_Api>("orders")' "$SYS/Shop.AppHost/MsgServices.g.cs" \
  && grep -q 'connectionName: "Database"' "$SYS/Shop.AppHost/MsgServices.g.cs" \
  && grep -q 'AddPostgres' "$SYS/Shop.AppHost/MsgServices.g.cs" && grep -q 'AddSqlServer' "$SYS/Shop.AppHost/MsgServices.g.cs" \
  && pass "AppHost with both services, their databases and health checks" || fail "AppHost content"
[[ -z "$("${MSG[@]}" aspire --path "$SYS" --name Shop | grep '^  [+~]')" ]] && pass "msg aspire is idempotent" || fail "msg aspire rewrote files"
"${MSG[@]}" new Notifications -o "$SYS/notifications" --package-source "$PKGS" >/dev/null
"${MSG[@]}" aspire --path "$SYS" --name Shop >/dev/null
grep -q 'Notifications_Api' "$SYS/Shop.AppHost/MsgServices.g.cs" && grep -q 'Notifications.Api.csproj' "$SYS/Shop.AppHost/Shop.AppHost.csproj" \
  && pass "a new service is added to the AppHost" || fail "new service not added"

echo "▶ msg studio"
STUDIO_PORT=5988
"${MSG[@]}" studio --path "$SYS" --port $STUDIO_PORT --no-browser >"$WORK/studio.log" 2>&1 &
PID=$!
for _ in $(seq 1 50); do curl -sf "http://127.0.0.1:$STUDIO_PORT/" >/dev/null 2>&1 && break; sleep 0.2; done
HOME_HTML="$(curl -s "http://127.0.0.1:$STUDIO_PORT/")"
for s in Orders Billing Notifications; do
  echo "$HOME_HTML" | grep -q ">$s<" || fail "studio: $s missing on the system page"
done
pass "studio lists the services of the system"
for p in service entities apply; do
  [[ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$STUDIO_PORT/$p")" == 200 ]] || fail "studio: /$p"
done
pass "studio pages render"
if [[ $ONLINE == 1 ]]; then
  [[ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$STUDIO_PORT/_framework/blazor.web.js")" == 200 ]] \
    && pass "studio serves blazor.web.js (interactive UI)" || fail "studio: blazor.web.js missing"
else
  echo "  – blazor.web.js check skipped (source-built SDKs don't ship Blazor's JavaScript)"
fi
kill "$PID"; wait "$PID" 2>/dev/null || true; PID=""

echo "▶ Full services (need nuget.org)"
if [[ $ONLINE == 1 ]]; then
  for s in full auth regen clean features team/services/orders; do
    dotnet build "$WORK/$s" -c Release -v q -nologo >/dev/null && pass "$s builds"
    dotnet test  "$WORK/$s" -c Release --no-build -v q -nologo >/dev/null && pass "$s tests pass"
  done
  dotnet build "$SYS/Shop.AppHost" -c Release -v q -nologo >/dev/null && pass "Aspire AppHost builds"
else
  echo "  – nuget.org not reachable: skipped (run this script on your machine)"
fi

echo "✅ Smoke test OK"
