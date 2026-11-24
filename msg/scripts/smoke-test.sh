#!/usr/bin/env bash
# End-to-end check: build + test + pack Stratum, generate services that consume the local Stratum.* packages,
# build them with warnings-as-errors, run one and hit its endpoints, check "stratum upgrade",
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

echo "▶ Build, test and pack Stratum"
if [[ $ONLINE == 1 ]]; then
  dotnet build "$ROOT/Stratum.slnx" -c Release -v q -nologo >/dev/null
  dotnet test  "$ROOT/Stratum.slnx" -c Release --no-build -v q -nologo >/dev/null && pass "Stratum.ServiceDefaults.* and Stratum.Messaging tests pass"
  dotnet pack  "$ROOT/Stratum.slnx" -c Release --no-build -o "$PKGS" -v q -nologo >/dev/null
else
  echo "  – nuget.org not reachable: building/packing only the framework-only projects"
  # Without nuget.org the Blazor framework assets package can't be restored: build the CLI without it.
  dotnet build "$ROOT/src/Stratum.Cli" -c Release -v q -nologo -p:RequiresAspNetWebAssets=false >/dev/null
  dotnet pack  "$ROOT/src/Stratum.Ddd" -c Release -o "$PKGS" -v q -nologo >/dev/null
  dotnet pack  "$ROOT/src/Stratum.ServiceDefaults" -c Release -o "$PKGS" -v q -nologo >/dev/null
  dotnet pack  "$ROOT/src/Stratum.Messaging" -c Release -o "$PKGS" -v q -nologo >/dev/null
fi
pass "packed: $(ls "$PKGS" | tr '\n' ' ')"
MSG=(dotnet "$ROOT/src/Stratum.Cli/bin/Release/net10.0/stratum.dll")
VERSION="$("${MSG[@]}" --version)"
HELP_MAIN="$("${MSG[@]}" --help 2>&1)"; HELP_NEW="$("${MSG[@]}" new --help 2>&1)"
[[ "$HELP_MAIN" == *"stratum new"* && "$HELP_NEW" == *"stratum new"* && "$HELP_MAIN" != *"Exception"* ]] \
  && pass "stratum --help and stratum new --help" || fail "--help: $HELP_MAIN"

# The global NuGet cache keeps packages by id+version: drop old Stratum.* builds so we test the ones just packed.
GLOBAL="$(dotnet nuget locals global-packages --list | sed 's/^global-packages: //')"
rm -rf "$GLOBAL"/stratum.servicedefaults* "$GLOBAL"/stratum.ddd "$GLOBAL"/stratum.messaging* "$GLOBAL"/msg.servicedefaults* "$GLOBAL"/msg.ddd "$GLOBAL"/msg.messaging*

echo "▶ Generate every feature combination worth checking (Stratum $VERSION)"
"${MSG[@]}" new Orders  -o "$WORK/full" --package-source "$PKGS"        >/dev/null && pass "full (default features)"
"${MSG[@]}" new Billing -o "$WORK/auth" --package-source "$PKGS" --auth >/dev/null && pass "full + auth"
"${MSG[@]}" new Catalog -o "$WORK/core" --package-source "$PKGS" \
  --no-observability --no-openapi --no-resilience --no-tests            >/dev/null && pass "core (framework only)"
"${MSG[@]}" new --from "$WORK/auth/stratum.json" -o "$WORK/from" --package-source "$PKGS" >/dev/null && pass "--from stratum.json"

echo "▶ Template sanity"
if grep -rn "@stratum:\|{{Service}}\|{{service-kebab}}\|{{DevPort}}\|{{StratumVersion}}\|__Service__" "$WORK"/{full,auth,core,from} ; then
  fail "unrendered template markers found"
fi
pass "no template markers left"
for f in $(find "$WORK"/{full,auth,core,from} -name "*.json"); do
  python3 -m json.tool "$f" >/dev/null || fail "invalid JSON: $f"
done
pass "all JSON files are valid"
diff -r "$WORK/auth" "$WORK/from" >/dev/null && pass "--from reproduces the same service" || fail "--from differs"
grep -q "Include=\"Stratum.ServiceDefaults\" Version=\"$VERSION\"" "$WORK/core/Directory.Packages.props" \
  && pass "service pins Stratum.ServiceDefaults $VERSION" || fail "Stratum.ServiceDefaults version"

echo "▶ Build 'core' service (TreatWarningsAsErrors)"
dotnet build "$WORK/core" -c Release -v q -nologo "${AUDIT[@]}" >/dev/null || dotnet build "$WORK/core" -c Release -nologo "${AUDIT[@]}" | tail -20
pass "core builds against the local Stratum.ServiceDefaults package with zero warnings"

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

echo "▶ stratum upgrade"
cp -r "$WORK/core" "$WORK/upgraded"
"${MSG[@]}" upgrade --path "$WORK/upgraded" --to 9.9.9 >/dev/null
grep -q 'Include="Stratum.ServiceDefaults" Version="9.9.9"' "$WORK/upgraded/Directory.Packages.props" \
  && grep -q '"stratumVersion": "9.9.9"' "$WORK/upgraded/stratum.json" \
  && pass "upgrade bumps Stratum.* packages and stratum.json" || fail "upgrade"

echo "▶ stratum add entity"
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
"${MSG[@]}" new --from "$WORK/full/stratum.json" -o "$WORK/regen" --package-source "$PKGS" >/dev/null && pass "stratum new --from regenerates the entities"

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
if grep -rn "@stratum:\|{{Service}}\|{{Entity}}\|__Entity__" "$WORK/clean"; then fail "unrendered markers in clean-ddd"; fi
pass "no template markers left in clean-ddd"

echo "▶ Editable entities (stratum entity)"
CP="$WORK/clean/src/Catalog.Domain/Products/Product.cs"
# User code in the partial class: must survive every regeneration.
cat > "$CP" <<'CS'
using Stratum.Ddd;

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
grep -q '"from": "Name"' "$WORK/clean/stratum.json" && pass "rename recorded in stratum.json (pendingRenames)" || fail "pendingRenames"
sed -i 's/{Name} ({Price})/{Title} ({Price})/' "$CP"
"${MSG[@]}" entity update Product --path "$WORK/clean" --props "Title:string, Price:decimal, Stock:int" | grep -q "Nada que cambiar" \
  && pass "no-op update changes nothing" || fail "no-op update"
"${MSG[@]}" add entity Tag --path "$WORK/clean" --props "Label:string" >/dev/null
"${MSG[@]}" entity remove Tag --path "$WORK/clean" --delete-own-files >/dev/null
if find "$WORK/clean" -path "*Tag*" -name "*.cs" | grep -q .; then fail "entity remove left files"; fi
grep -q "Tag" "$WORK/clean/src/Catalog.Infrastructure/Persistence/CatalogDbContext.g.cs" && fail "Tag still in DbContext"
pass "entity remove deletes its files and registrations"
# References between entities and aggregate operations (built and tested below with the full services).
"${MSG[@]}" add entity Review --path "$WORK/clean" --props "Text:string, Product:Product, ReplyToId:Review?" >/dev/null
"${MSG[@]}" entity operation add Product Discontinue --params "Reason:string" --path "$WORK/clean" >/dev/null
"${MSG[@]}" entity operation add Product Restock --path "$WORK/clean" >/dev/null
CR="$WORK/clean/src"
grep -q "HasOne<global::Catalog.Domain.Products.Product>().WithMany().HasForeignKey(e => e.ProductId)" "$CR/Catalog.Infrastructure/Persistence/Reviews/ReviewConfiguration.g.cs" \
  && grep -q "MissingReferenceAsync" "$CR/Catalog.Application/Reviews/ReviewCommands.g.cs" \
  && grep -q "InUseAsync" "$CR/Catalog.Infrastructure/Persistence/Products/ProductIntegrity.g.cs" \
  && grep -q "public partial void Discontinue(string reason)" "$CR/Catalog.Domain/Products/Product.Discontinue.cs" \
  && grep -q '"/{id:guid}/discontinue"' "$CR/Catalog.Api/Features/Products/ProductOperationEndpoints.g.cs" \
  && grep -q "record ProductRestocked(Guid ProductId)" "$CR/Catalog.Domain/Products/ProductOperations.g.cs" \
  && ! "${MSG[@]}" entity remove Product --path "$WORK/clean" >/dev/null 2>&1 \
  && pass "clean-ddd: references (foreign keys, checks) and aggregate operations" || fail "clean-ddd references/operations"
"${MSG[@]}" feature on mcp --path "$WORK/clean" >/dev/null
grep -q 'app.MapMcp("/mcp")' "$CR/Catalog.Api/StratumWiring.g.cs" \
  && grep -q 'Name = "discontinue_product"' "$CR/Catalog.Api/Features/Products/ProductMcpTools.g.cs" \
  && grep -q 'Name = "create_review"' "$CR/Catalog.Api/Features/Reviews/ReviewMcpTools.g.cs" \
  && grep -q 'Include="ModelContextProtocol.AspNetCore"' "$CR/Catalog.Api/Catalog.Api.csproj" \
  && pass "feature mcp: /mcp with the entities' CRUD and operations as tools" || fail "feature mcp"
"${MSG[@]}" add entity Supplier --path "$WORK/full" --props "Name:string" >/dev/null
"${MSG[@]}" add entity Shipment --path "$WORK/full" --props "Code:string, SupplierId:Supplier, ProductId:Product?" >/dev/null
grep -q "MissingReferencesAsync" "$WORK/full/src/Orders.Api/Features/Shipments/ShipmentEndpoints.g.cs" \
  && grep -q "UsedByAsync" "$WORK/full/src/Orders.Api/Features/Suppliers/SupplierEndpoints.g.cs" \
  && grep -q "await CreateSupplierAsync(client)" "$WORK/full/tests/Orders.Api.Tests/Features/ShipmentEndpointsTests.g.cs" \
  && ! "${MSG[@]}" entity operation add Supplier Approve --path "$WORK/full" >/dev/null 2>&1 \
  && ! "${MSG[@]}" add entity Bad --path "$WORK/full" --props "X:Nope" >/dev/null 2>&1 \
  && pass "vertical slice: references with 400/409 checks and test data" || fail "vertical references"
"${MSG[@]}" enum add ShipmentState --values "pending, Sent, Delivered" --path "$WORK/full" >/dev/null
"${MSG[@]}" entity update Shipment --path "$WORK/full" --props "Code:string, SupplierId:Supplier, ProductId:Product?, State:ShipmentState, Previous:ShipmentState?" >/dev/null
"${MSG[@]}" enum add ProductLine --values "Basic, Premium" --path "$WORK/clean" >/dev/null
"${MSG[@]}" entity operation add Product Reclassify --params "Line:ProductLine" --path "$WORK/clean" >/dev/null
grep -q "public enum ShipmentState" "$WORK/full/src/Orders.Api/Features/Enums.g.cs" \
  && grep -q "HasConversion<string>()" "$WORK/full/src/Orders.Api/Features/Shipments/ShipmentConfiguration.g.cs" \
  && grep -q '<Using Include="Orders.Api.Features" />' "$WORK/full/tests/Orders.Api.Tests/Orders.Api.Tests.csproj" \
  && grep -q "public enum ProductLine" "$WORK/clean/src/Catalog.Domain/Enums.g.cs" \
  && grep -q "ProductLine line" "$WORK/clean/src/Catalog.Domain/Products/Product.Reclassify.cs" \
  && ! "${MSG[@]}" enum remove ShipmentState --path "$WORK/full" >/dev/null 2>&1 \
  && pass "enums: C# enum, stored as text, usable in properties and operations" || fail "enums"
# Lists: of basic types, of enums and of objects (item types, own table, replaced on update). The tests of "full"
# and "clean" (run at the end) create, read and update them against a real database.
"${MSG[@]}" type add ShipmentLine --props "Sku:string, Quantity:int, State:ShipmentState, Note:string?" --path "$WORK/full" >/dev/null
"${MSG[@]}" entity update Shipment --path "$WORK/full" --props "Code:string, SupplierId:Supplier, ProductId:Product?, State:ShipmentState, Previous:ShipmentState?, Tags:string[], States:ShipmentState[], Lines:ShipmentLine[]" >/dev/null
"${MSG[@]}" type add BundleLine --props "Sku:string, Price:decimal, Line:ProductLine" --path "$WORK/clean" >/dev/null
"${MSG[@]}" add entity Bundle --path "$WORK/clean" --props "Name:string, Codes:int[], Lines:BundleLine[]" >/dev/null
grep -q "public sealed class ShipmentLine" "$WORK/full/src/Orders.Api/Features/Types.g.cs" \
  && grep -q 'OwnsMany(e => e.Lines' "$WORK/full/src/Orders.Api/Features/Shipments/ShipmentConfiguration.g.cs" \
  && grep -q 'ToTable("shipment_lines")' "$WORK/full/src/Orders.Api/Features/Shipments/ShipmentConfiguration.g.cs" \
  && grep -q "PrimitiveCollection(e => e.States)" "$WORK/full/src/Orders.Api/Features/Shipments/ShipmentConfiguration.g.cs" \
  && grep -q "IReadOnlyList<ShipmentLineContract> Lines" "$WORK/full/src/Orders.Api/Features/Shipments/ShipmentContracts.g.cs" \
  && grep -q "public sealed class BundleLine" "$WORK/clean/src/Catalog.Domain/Types.g.cs" \
  && grep -q "Lines = \[.. lines\];" "$WORK/clean/src/Catalog.Domain/Bundles/Bundle.g.cs" \
  && ! "${MSG[@]}" type remove ShipmentLine --path "$WORK/full" >/dev/null 2>&1 \
  && ! "${MSG[@]}" add entity Bad --path "$WORK/full" --props "Items:ShipmentLine" >/dev/null 2>&1 \
  && ! "${MSG[@]}" add entity Bad --path "$WORK/full" --props "Tags:string[]?" >/dev/null 2>&1 \
  && pass "lists: string[], enum[] and item types (own table, replaced on update)" || fail "lists"
# Operations on lists (clean-ddd): one item or a list of items as parameters, with a head start for the rule.
"${MSG[@]}" entity operation add Bundle AddLine --params "Line:BundleLine" --path "$WORK/clean" >/dev/null
"${MSG[@]}" entity operation add Bundle ReplaceLines --params "Lines:BundleLine[]" --path "$WORK/clean" >/dev/null
grep -q "Lines.Add(line);" "$WORK/clean/src/Catalog.Domain/Bundles/Bundle.AddLine.cs" \
  && grep -q "Lines = \[.. lines\];" "$WORK/clean/src/Catalog.Domain/Bundles/Bundle.ReplaceLines.cs" \
  && grep -q "record BundleLineAdded(Guid BundleId, BundleLine Line)" "$WORK/clean/src/Catalog.Domain/Bundles/BundleOperations.g.cs" \
  && grep -q "\[property: Required\] BundleLineContract Line" "$WORK/clean/src/Catalog.Api/Features/Bundles/BundleOperationEndpoints.g.cs" \
  && pass "operations on lists: AddLine(Line:BundleLine) → BundleLineAdded, with the rule started" || fail "operations on lists"
# Best practices of dotnet/skills (dotnet-webapi, test-anti-patterns) and instructions for coding agents.
grep -q '.WithSummary("Create a Shipment")' "$WORK/full/src/Orders.Api/Features/Shipments/ShipmentEndpoints.g.cs" \
  && grep -q "<GenerateDocumentationFile>true</GenerateDocumentationFile>" "$WORK/full/src/Orders.Api/Orders.Api.csproj" \
  && grep -q "JsonNumberHandling.Strict" "$WORK/full/src/Orders.Api/Program.cs" \
  && grep -q "public sealed record ShipmentLineContract(" "$WORK/full/src/Orders.Api/Features/Types.g.cs" \
  && grep -q "IReadOnlyList<BundleLineContract> Lines" "$WORK/clean/src/Catalog.Api/Features/Bundles/BundleContracts.g.cs" \
  && grep -q "# @name createShipment" "$WORK/full/src/Orders.Api/Orders.Api.http" \
  && grep -q "UpdateReplacesTheData" "$WORK/full/tests/Orders.Api.Tests/Features/ShipmentEndpointsTests.g.cs" \
  && grep -q "Never edit them" "$WORK/clean/AGENTS.md" && grep -q "@AGENTS.md" "$WORK/clean/CLAUDE.md" \
  && pass "best practices: OpenAPI metadata, strict JSON, item contracts, .http, focused tests, AGENTS.md" || fail "best practices"
# An entity from Stratum < 0.10 (no "layout" in stratum.json) is read-only until converted.
python3 - "$WORK/full/stratum.json" <<'PY'
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

echo "▶ stratum import openapi"
cat > "$WORK/library.yaml" <<'YAML'
openapi: 3.0.3
info:
  title: Library API
  version: 1.0.0
paths:
  /authors:
    get:
      responses:
        '200':
          description: All
          content:
            application/json:
              schema:
                type: array
                items:
                  $ref: '#/components/schemas/Author'
    post:
      requestBody:
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/Author'
      responses:
        '201':
          description: Created
  /authors/{id}:
    get:
      responses:
        '200':
          description: One
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/Author'
  /books:
    post:
      requestBody:
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/Book'
      responses:
        '201':
          description: Created
  /books/{id}/lend:
    post:
      requestBody:
        content:
          application/json:
            schema:
              type: object
              required: [member]
              properties:
                member:
                  type: string
      responses:
        '204':
          description: Lent
  /stats:
    get:
      responses:
        '200':
          description: Stats
components:
  schemas:
    Author:
      type: object
      required: [full_name]
      properties:
        id:
          type: string
          format: uuid
        full_name:
          type: string
        born:
          type: string
          format: date
    Book:
      type: object
      required: [title, author_id]
      properties:
        title:
          type: string
        author_id:
          type: string
          format: uuid
        pages:
          type: integer
        tags:
          type: array
          items:
            type: string
YAML
OUT=$("${MSG[@]}" import openapi "$WORK/library.yaml" -a clean-ddd -o "$WORK/imported" --package-source "$PKGS")
echo "$OUT" | grep -q "Author (/api/v1/authors): FullName:string, Born:date?" \
  && echo "$OUT" | grep -q "Book (/api/v1/books): Title:string, AuthorId:Author, Pages:int?" \
  && echo "$OUT" | grep -q "operación Lend(Member:string) → BookLent" \
  && echo "$OUT" | grep -q "Pages:int?, Tags:string\[\]" && echo "$OUT" | grep -q "GET /stats" \
  && [[ -f "$WORK/imported/src/LibraryAPI.Domain/Books/Book.Lend.cs" ]] \
  && pass "import openapi: entities, references, operations and a report of the rest" || { echo "$OUT"; fail "import openapi"; }

echo "▶ stratum upgrade: services from before 0.22 (Msg.*, msg.json)"
OLD="$WORK/oldnames"
"${MSG[@]}" new Shipping -o "$OLD" --package-source "$PKGS" >/dev/null
"${MSG[@]}" add entity Parcel --path "$OLD" --props "Code:string" >/dev/null
"${MSG[@]}" messaging on --broker inMemory --path "$OLD" >/dev/null
"${MSG[@]}" events on Parcel --path "$OLD" >/dev/null
python3 - "$OLD" <<'PY'
import os, re, sys
root = sys.argv[1]
for d, dirs, files in os.walk(root):
    dirs[:] = [x for x in dirs if x not in ("bin", "obj")]
    for n in files:
        p = os.path.join(d, n)
        try: s = open(p, encoding="utf-8").read()
        except UnicodeDecodeError: continue
        t = re.sub(r"\bStratum\.(?=(ServiceDefaults|Ddd|Messaging)\b)", "Msg.", s)
        t = re.sub(r"\b(Add|Use|Map)Stratum(?=[A-Z])", r"\1Msg", t)
        t = t.replace("StratumWiring", "MsgWiring").replace('"stratumVersion"', '"msgVersion"').replace('pattern="Stratum.*"', 'pattern="Msg.*"')
        if t != s: open(p, "w", encoding="utf-8").write(t)
os.rename(os.path.join(root, "stratum.json"), os.path.join(root, "msg.json"))
w = os.path.join(root, "src", "Shipping.Api")
os.rename(os.path.join(w, "StratumWiring.g.cs"), os.path.join(w, "MsgWiring.g.cs"))
PY
BLOCKED=$("${MSG[@]}" add entity Box --path "$OLD" --props "Size:int" 2>&1 || true)
PLAN=$("${MSG[@]}" upgrade --path "$OLD" --dry-run)
echo "$BLOCKED" | grep -q "stratum upgrade" && echo "$PLAN" | grep -q "msg.json → stratum.json" \
  && "${MSG[@]}" upgrade --path "$OLD" >/dev/null \
  && [[ -f "$OLD/stratum.json" && ! -f "$OLD/msg.json" && -f "$OLD/src/Shipping.Api/StratumWiring.g.cs" && ! -f "$OLD/src/Shipping.Api/MsgWiring.g.cs" ]] \
  && grep -q "AddStratumWiring()" "$OLD/src/Shipping.Api/Program.cs" \
  && ! grep -rqE "\bMsg\.(ServiceDefaults|Ddd|Messaging)|AddMsg|UseMsg" "$OLD" --include=*.cs --include=*.csproj --include=*.props \
  && pass "upgrade migrates Msg.* / msg.json / MsgWiring to Stratum (user code included)" || fail "legacy names migration"

echo "▶ Features on/off in an existing service (stratum feature)"
FS="$WORK/features"
"${MSG[@]}" new Shop -o "$FS" --package-source "$PKGS" >/dev/null
"${MSG[@]}" add entity Product --path "$FS" --props "Name:string" >/dev/null
"${MSG[@]}" feature off docker openApi --path "$FS" >/dev/null
[[ ! -f "$FS/Dockerfile" ]] && ! grep -q "Scalar.AspNetCore" "$FS/src/Shop.Api/Shop.Api.csproj" "$FS/Directory.Packages.props" "$FS/src/Shop.Api/StratumWiring.g.cs" \
  && grep -q "AddStratumWiring()" "$FS/src/Shop.Api/Program.cs" \
  && pass "feature off removes its files, packages and wiring (Program.cs untouched)" || fail "feature off"
"${MSG[@]}" feature on docker openApi --path "$FS" >/dev/null
[[ -f "$FS/Dockerfile" ]] && grep -q 'Include="Scalar.AspNetCore"' "$FS/src/Shop.Api/Shop.Api.csproj" && grep -q "MapScalarApiReference" "$FS/src/Shop.Api/StratumWiring.g.cs" \
  && pass "feature on brings them back" || fail "feature on"
sed -i 's/builder.AddFeatures();/builder.AddFeatures(); \/\/ tweak/' "$FS/src/Shop.Api/StratumWiring.g.cs"
"${MSG[@]}" feature on auth --path "$FS" --dry-run | grep -q "Has editado a mano" \
  && pass "a hand-edited .g.cs is detected before overwriting it" || fail "hand-edit detection"
"${MSG[@]}" feature on auth --path "$FS" >/dev/null   # restores the generated wiring (and keeps auth for the build below)
LP="$WORK/legacy-program"; cp -r "$FS" "$LP"
cat > "$LP/src/Shop.Api/Program.cs" <<'CS'
using Shop.Api;
using Shop.Api.Endpoints;
using Shop.Api.Features;
using Stratum.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddStratumServiceDefaults();
builder.Services.AddValidatedOptions<ServiceOptions>(ServiceOptions.SectionName);
builder.Services.AddValidation();
builder.AddFeatures();
var app = builder.Build();
app.UseStratumServiceDefaults();
app.MapStratumDefaultEndpoints();
var v1 = app.MapGroup("/api/v1");
v1.MapPingEndpoints();
v1.MapFeatures();
await app.RunAsync().ConfigureAwait(false);
CS
if "${MSG[@]}" feature off docker --path "$LP" >/dev/null 2>&1; then fail "features changed with a Program.cs from before 0.12"; fi
"${MSG[@]}" feature convert --path "$LP" >/dev/null && grep -q "AddStratumWiring()" "$LP/src/Shop.Api/Program.cs" \
  && "${MSG[@]}" feature off docker --path "$LP" >/dev/null \
  && pass "Program.cs from before 0.12: blocked, converted, then editable" || fail "feature convert"

echo "▶ Messaging (stratum messaging / stratum events)"
# Messaging follows usage: publishing turns it on (no order to remember).
OUT="$("${MSG[@]}" events on Product --path "$FS")"
[[ "$OUT" == *"Mensajería activada en Shop con kafka"* ]] && pass "events on without messaging turns messaging on" || fail "events on: $OUT"
"${MSG[@]}" messaging on --broker kafka --path "$FS" >/dev/null
grep -q "UseKafka(builder.Configuration)" "$FS/src/Shop.Api/StratumWiring.g.cs" \
  && grep -q "UseEntityFramework<ShopDbContext>" "$FS/src/Shop.Api/StratumWiring.g.cs" \
  && grep -q 'Include="Stratum.Messaging.Kafka"' "$FS/src/Shop.Api/Shop.Api.csproj" \
  && grep -q '"kafka": "localhost:9092"' "$FS/src/Shop.Api/appsettings.Development.json" \
  && grep -q "MessagingModel.ConfigureOutbox" "$FS/src/Shop.Api/Data/MessagingConfiguration.g.cs" \
  && pass "messaging on: Kafka, outbox/inbox tables, packages and connection string" || fail "messaging on"
grep -q 'IntegrationEvent("shop.product-created")' "$FS/src/Shop.Api/IntegrationEvents/ProductIntegrationEvents.g.cs" \
  && grep -q "PublishAsync(new ProductCreated" "$FS/src/Shop.Api/Features/Products/ProductEndpoints.g.cs" \
  && pass "vertical slice: Product events published from the endpoints (outbox)" || fail "events on (vertical)"
"${MSG[@]}" messaging on --broker kafka --path "$WORK/clean" >/dev/null
"${MSG[@]}" events on Product Bundle --path "$WORK/clean" >/dev/null
grep -q "ProductIntegrationEventsMapper : IIntegrationEventMapper" "$WORK/clean/src/Catalog.Infrastructure/Persistence/Products/ProductIntegrationEventsMapper.g.cs" \
  && grep -q 'IntegrationEvent("catalog.product-created")' "$WORK/clean/src/Catalog.Application/IntegrationEvents/ProductIntegrationEvents.g.cs" \
  && pass "clean + DDD: domain events mapped to integration events" || fail "events on (clean)"
python3 -m json.tool "$FS/src/Shop.Api/appsettings.Development.json" >/dev/null || fail "invalid appsettings.Development.json with Kafka"
# Each one listens to the other: contracts copied, handlers created, names that collide on purpose (ProductCreated).
"${MSG[@]}" subscribe "$FS" Product --path "$WORK/clean" >/dev/null
"${MSG[@]}" subscribe "$WORK/clean" Product.Created --path "$FS" >/dev/null
"${MSG[@]}" subscribe "$WORK/clean" Bundle.Updated --path "$FS" >/dev/null
# A published operation: its own integration event (ProductReclassified) besides ProductUpdated.
"${MSG[@]}" entity operation publish Product Reclassify --path "$WORK/clean" >/dev/null
"${MSG[@]}" subscribe "$WORK/clean" Product.Reclassified --path "$FS" >/dev/null
grep -q 'IntegrationEvent("catalog.product-reclassified")' "$WORK/clean/src/Catalog.Application/IntegrationEvents/ProductIntegrationEvents.g.cs" \
  && grep -q "(ProductReclassified operation, Product entity) => \[new Integration.ProductUpdated(" "$WORK/clean/src/Catalog.Infrastructure/Persistence/Products/ProductIntegrationEventsMapper.g.cs" \
  && grep -q "new Integration.ProductReclassified(entity.Id" "$WORK/clean/src/Catalog.Infrastructure/Persistence/Products/ProductIntegrationEventsMapper.g.cs" \
  && grep -q "string Line) : IntegrationEvent" "$FS/src/Shop.Api/IntegrationEvents/Catalog/ProductReclassified.g.cs" \
  && "${MSG[@]}" events map --path "$WORK" | grep -q "catalog.product-reclassified .*→ Shop" \
  && pass "published operation: ProductReclassified with its own topic, subscribable" || fail "published operation"
grep -q "IReadOnlyList<BundleUpdated.BundleLine> Lines" "$FS/src/Shop.Api/IntegrationEvents/Catalog/BundleUpdated.g.cs" \
  && grep -q "public sealed record BundleLine(string Sku, decimal Price, string Line);" "$FS/src/Shop.Api/IntegrationEvents/Catalog/BundleUpdated.g.cs" \
  && grep -q "new Integration.BundleUpdated.BundleLine(i.Sku, i.Price, i.Line.ToString())" "$WORK/clean/src/Catalog.Infrastructure/Persistence/Bundles/BundleIntegrationEventsMapper.g.cs" \
  && pass "events with lists: the item records travel inside the contract" || fail "events with lists"
grep -q 'IntegrationEvent("shop.product-deleted")' "$WORK/clean/src/Catalog.Application/IntegrationEvents/Shop/ProductDeleted.g.cs" \
  && grep -q "IIntegrationEventHandler<ProductCreated>" "$WORK/clean/src/Catalog.Application/Events/Shop/ProductCreatedHandler.cs" \
  && grep -q 'IntegrationEvent("catalog.product-created")' "$FS/src/Shop.Api/IntegrationEvents/Catalog/ProductCreated.g.cs" \
  && grep -q "IIntegrationEventHandler<ProductCreated>" "$FS/src/Shop.Api/Events/Catalog/ProductCreatedHandler.cs" \
  && pass "subscribe: contract copied and handler created (both architectures)" || fail "subscribe"
"${MSG[@]}" events map --path "$WORK" | grep -q "shop.product-created .*→ Catalog" \
  && pass "events map shows who publishes and who listens" || { "${MSG[@]}" events map --path "$WORK"; fail "events map"; }
"${MSG[@]}" entity update Product --path "$FS" --props "Name:string, Sku:string?" >/dev/null
"${MSG[@]}" events map --path "$WORK" | grep -q "ha cambiado los datos de ProductCreated" \
  && "${MSG[@]}" subscribe --sync --path "$WORK/clean" | grep -q "IntegrationEvents/Shop/ProductCreated.g.cs" \
  && grep -q "string? Sku" "$WORK/clean/src/Catalog.Application/IntegrationEvents/Shop/ProductCreated.g.cs" \
  && pass "a changed contract is detected and --sync copies it again" || fail "subscribe --sync"
"${MSG[@]}" events off Product --path "$FS" >/dev/null
"${MSG[@]}" subscribe --sync --prune --path "$WORK/clean" >/dev/null
[[ ! -d "$WORK/clean/src/Catalog.Application/IntegrationEvents/Shop" ]] && ! grep -q '"service": "Shop"' "$WORK/clean/stratum.json" \
  && pass "--prune drops subscriptions to events no longer published" || fail "subscribe --prune"
"${MSG[@]}" events on Product --path "$FS" >/dev/null

echo "▶ Guided migrations (stratum migration review)"
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
  && ! grep -q pendingRenames "$MIG/stratum.json" \
  && pass "review turns drop + add into RenameColumn and clears pendingRenames" || { cat "$MF"; fail "migration review"; }

echo "▶ Team templates (.stratum)"
TEAM="$WORK/team"; mkdir -p "$TEAM/services"
"${MSG[@]}" templates eject service/src/__Service__.Api/Program.cs --path "$TEAM" >/dev/null
sed -i '1i // {{Company}} conventions' "$TEAM/.stratum/templates/service/src/__Service__.Api/Program.cs"
printf '{ "tokens": { "Company": "Acme" } }\n' > "$TEAM/.stratum/config.json"
printf '* @{{Company}}/backend\n' > "$TEAM/.stratum/templates/service/CODEOWNERS"
: > "$TEAM/.stratum/templates/service/Dockerfile"
"${MSG[@]}" new Orders -o "$TEAM/services/orders" --package-source "$PKGS" >/dev/null
"${MSG[@]}" add entity Product --path "$TEAM/services/orders" --props "Name:string" >/dev/null
head -1 "$TEAM/services/orders/src/Orders.Api/Program.cs" | grep -q "Acme conventions" \
  && [[ "$(cat "$TEAM/services/orders/CODEOWNERS")" == "* @Acme/backend" ]] \
  && [[ ! -f "$TEAM/services/orders/Dockerfile" ]] \
  && pass "team templates: replace, add, remove and tokens (found walking up)" || fail "team templates"
echo '// @stratum:if bogus' >> "$TEAM/.stratum/templates/service/CODEOWNERS"
{ "${MSG[@]}" new Broken -o "$TEAM/services/broken" 2>&1 || true; } | grep -q ".stratum/templates/service/CODEOWNERS, línea 2" \
  && pass "a broken team template is reported with file and line" || fail "broken template error"

echo "▶ stratum aspire"
SYS="$WORK/system"; mkdir -p "$SYS"
"${MSG[@]}" new Orders  -o "$SYS/orders"  --package-source "$PKGS" >/dev/null
"${MSG[@]}" add entity Order --path "$SYS/orders" --props "Number:string, Total:decimal" >/dev/null
"${MSG[@]}" new Billing -o "$SYS/billing" --architecture clean-ddd --package-source "$PKGS" >/dev/null
"${MSG[@]}" add entity Invoice --path "$SYS/billing" --db sqlserver --props "Number:string" >/dev/null
"${MSG[@]}" messaging on --broker kafka --path "$SYS/orders" >/dev/null
"${MSG[@]}" events on Order --path "$SYS/orders" >/dev/null
"${MSG[@]}" aspire --path "$SYS" --name Shop >/dev/null
for f in Shop.AppHost/Shop.AppHost.csproj Shop.AppHost/AppHost.cs Shop.AppHost/StratumServices.g.cs Shop.slnx .aspire/settings.json; do
  [[ -f "$SYS/$f" ]] || fail "missing $f"
done
grep -q 'AddKafka("kafka")' "$SYS/Shop.AppHost/StratumServices.g.cs" && grep -q 'WithKafkaUI' "$SYS/Shop.AppHost/StratumServices.g.cs" \
  && grep -q 'Aspire.Hosting.Kafka' "$SYS/Shop.AppHost/Shop.AppHost.csproj" \
  && pass "AppHost: Kafka + Kafka UI for the services with messaging" || fail "AppHost Kafka"
grep -q 'AddProject<Projects.Orders_Api>("orders")' "$SYS/Shop.AppHost/StratumServices.g.cs" \
  && grep -q 'connectionName: "Database"' "$SYS/Shop.AppHost/StratumServices.g.cs" \
  && grep -q 'AddPostgres' "$SYS/Shop.AppHost/StratumServices.g.cs" && grep -q 'AddSqlServer' "$SYS/Shop.AppHost/StratumServices.g.cs" \
  && pass "AppHost with both services, their databases and health checks" || fail "AppHost content"
[[ -z "$("${MSG[@]}" aspire --path "$SYS" --name Shop | grep '^  [+~]')" ]] && pass "stratum aspire is idempotent" || fail "stratum aspire rewrote files"
"${MSG[@]}" new Payments -o "$WORK/elsewhere/payments" --package-source "$PKGS" >/dev/null
"${MSG[@]}" workspace add "$WORK/elsewhere/payments" --path "$SYS" >/dev/null
"${MSG[@]}" aspire --path "$SYS" --name Shop >/dev/null
grep -q '"../elsewhere/payments"' "$SYS/stratum.workspace.json" \
  && grep -q 'elsewhere\\payments\\src\\Payments.Api\\Payments.Api.csproj' "$SYS/Shop.AppHost/Shop.AppHost.csproj" \
  && grep -q 'AddProject<Projects.Payments_Api>("payments")' "$SYS/Shop.AppHost/StratumServices.g.cs" \
  && "${MSG[@]}" workspace list --path "$SYS" | grep -q "stratum.workspace.json" \
  && pass "a service in another folder joins the system (stratum.workspace.json → AppHost)" || fail "workspace add"
"${MSG[@]}" new Notifications -o "$SYS/notifications" --package-source "$PKGS" >/dev/null
"${MSG[@]}" aspire --path "$SYS" --name Shop >/dev/null
"${MSG[@]}" messaging on --broker azureServiceBus --path "$SYS/notifications" >/dev/null
"${MSG[@]}" subscribe "$SYS/orders" Order.Created --path "$SYS/notifications" >/dev/null
"${MSG[@]}" aspire --path "$SYS" --name Shop >/dev/null
grep -q 'AddAzureServiceBus("servicebus")' "$SYS/Shop.AppHost/StratumServices.g.cs" \
  && grep -q 'AddServiceBusSubscription("notifications-orders-order-created", "notifications")' "$SYS/Shop.AppHost/StratumServices.g.cs" \
  && grep -q 'Aspire.Hosting.Azure.ServiceBus' "$SYS/Shop.AppHost/Shop.AppHost.csproj" \
  && grep -q "UseAzureServiceBus" "$SYS/notifications/src/Notifications.Api/StratumWiring.g.cs" \
  && "${MSG[@]}" events map --path "$SYS" | grep -q "no le llegarán" \
  && pass "Azure Service Bus: emulator, topics and subscriptions in the AppHost; broker mismatch reported" || fail "Azure Service Bus"
"${MSG[@]}" events asyncapi --path "$SYS" >/dev/null
grep -q 'asyncapi: "3.1.0"' "$SYS/asyncapi.yaml" && grep -q "address: orders.order-created" "$SYS/asyncapi.yaml" \
  && grep -q "action: receive" "$SYS/asyncapi.yaml" \
  && pass "AsyncAPI 3.1 exported from the event map" || fail "AsyncAPI export"
MCP_OUT=$(printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}' \
  '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' \
  '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"add_entity","arguments":{"service":"Orders","name":"Coupon","properties":"Code:string"}}}' \
  '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"list_services","arguments":{}}}' \
  | "${MSG[@]}" mcp --path "$SYS" 2>/dev/null)
[ "$(echo "$MCP_OUT" | wc -l)" -eq 4 ] && echo "$MCP_OUT" | grep -q '"protocolVersion":"2025-11-25"' \
  && echo "$MCP_OUT" | grep -q '"name":"subscribe"' && echo "$MCP_OUT" | grep -q 'no se ha escrito nada' \
  && [ ! -e "$SYS/orders/src/Orders.Api/Features/Coupons" ] && echo "$MCP_OUT" | grep -q '"structuredContent":{"root"' \
  && pass "MCP server: handshake, tools, plan without writing" || fail "MCP server"
"${MSG[@]}" new Legacy -o "$SYS/legacy" --package-source "$PKGS" >/dev/null
"${MSG[@]}" aspire --path "$SYS" --name Shop >/dev/null
"${MSG[@]}" service remove "$SYS/legacy" --path "$SYS" | grep -q "No se ha tocado nada" && [[ -d "$SYS/legacy" ]] \
  && "${MSG[@]}" service remove "$SYS/legacy" --path "$SYS" --yes >/dev/null && [[ ! -d "$SYS/legacy" ]] \
  && ! grep -q "Legacy" "$SYS/Shop.AppHost/Shop.AppHost.csproj" "$SYS/Shop.AppHost/StratumServices.g.cs" "$SYS/Shop.slnx" \
  && pass "service remove: dry run first, then folder and AppHost references gone" || fail "service remove"
grep -q 'Notifications_Api' "$SYS/Shop.AppHost/StratumServices.g.cs" && grep -q 'Notifications.Api.csproj' "$SYS/Shop.AppHost/Shop.AppHost.csproj" \
  && pass "a new service is added to the AppHost" || fail "new service not added"

echo "▶ stratum studio"
STUDIO_PORT=5988
"${MSG[@]}" studio --path "$SYS" --port $STUDIO_PORT --no-browser >"$WORK/studio.log" 2>&1 &
PID=$!
for _ in $(seq 1 50); do curl -sf "http://127.0.0.1:$STUDIO_PORT/" >/dev/null 2>&1 && break; sleep 0.2; done
HOME_HTML="$(curl -s "http://127.0.0.1:$STUDIO_PORT/")"
for s in Orders Billing Notifications; do
  echo "$HOME_HTML" | grep -q ">$s<" || fail "studio: $s missing on the system page"
done
pass "studio lists the services of the system"
for p in services events run templates service entities service/data service/messaging service/code service/remove apply; do
  [[ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$STUDIO_PORT/$p")" == 200 ]] || fail "studio: /$p"
done
pass "studio pages render"
ENT="$(curl -s "http://127.0.0.1:$STUDIO_PORT/entities?open=orders&dialog=property:Total")"
ENUM_DLG="$(curl -s "http://127.0.0.1:$STUDIO_PORT/entities?open=orders&dialog=enum")"
[[ "$ENT" == *'class="modal-backdrop"'* && "$ENT" == *'Propiedad Total'* && "$ENT" == *'Guardar y cerrar'* && "$ENUM_DLG" == *'Nuevo enumerado'*'+ Añadir valor'* ]] \
  && pass "studio entity dialogs: property and enumeration open in floating windows" || fail "studio: entity dialogs"
D="http://127.0.0.1:$STUDIO_PORT/designer"
curl -s "$D" | grep -q "Diseñador de eventos" && curl -s "$D/api/model" | grep -q '"name":"Billing"' \
  && pass "events designer: page and model" || fail "designer page/model"
CONNECT='{"changes":[{"kind":"connect","publisher":"Billing","entity":"Invoice","event":"Created","consumer":"Orders"}],"layout":{"orders":{"x":40,"y":40},"billing":{"x":480,"y":40}}}'
BEFORE="$(cat "$SYS/billing/stratum.json")"
PREVIEW="$(curl -s -X POST "$D/api/preview" -H 'Content-Type: application/json' -d "$CONNECT")"
[[ "$PREVIEW" == *'"saved":false'* && "$PREVIEW" == *"Mensajería activada en Billing"* && "$(cat "$SYS/billing/stratum.json")" == "$BEFORE" ]] \
  && pass "designer preview: plan for both services, nothing written" || fail "designer preview: $PREVIEW"
SAVED="$(curl -s -X POST "$D/api/save" -H 'Content-Type: application/json' -d "$CONNECT")"
[[ "$SAVED" == *'"saved":true'* ]] && grep -q '"publishedEvents"' "$SYS/billing/stratum.json" \
  && grep -q 'record InvoiceCreated' "$SYS/billing/src/Billing.Application/IntegrationEvents/InvoiceIntegrationEvents.g.cs" \
  && ! grep -q 'record InvoiceUpdated' "$SYS/billing/src/Billing.Application/IntegrationEvents/InvoiceIntegrationEvents.g.cs" \
  && grep -q '"service": "Billing"' "$SYS/orders/stratum.json" && [[ -f "$SYS/stratum.designer.json" ]] \
  && pass "designer save: publishes only Invoice.Created, Orders subscribes, layout kept" || fail "designer save: $SAVED"
DISCONNECT='{"changes":[{"kind":"disconnect","publisher":"Billing","entity":"Invoice","event":"Created","consumer":"Orders"},{"kind":"connect","publisher":"Orders","entity":"Order","event":"Created","consumer":"Orders"}]}'
[[ "$(curl -s -X POST "$D/api/save" -H 'Content-Type: application/json' -d "$DISCONNECT")" == *'"saved":false'* ]] \
  && grep -q '"service": "Billing"' "$SYS/orders/stratum.json" \
  && pass "designer: a batch with an error writes nothing" || fail "designer error batch"
curl -s -X POST "$D/api/save" -H 'Content-Type: application/json' -d '{"changes":[{"kind":"disconnect","publisher":"Billing","entity":"Invoice","event":"Created","consumer":"Orders"}]}' >/dev/null
! grep -q '"service": "Billing"' "$SYS/orders/stratum.json" && pass "designer: disconnect removes the subscription" || fail "designer disconnect"
EVENTS_HTML="$(curl -s "http://127.0.0.1:$STUDIO_PORT/events")"
[[ "$(grep -o 'class="ev-mini' <<<"$EVENTS_HTML" | wc -l)" -ge 3 && "$EVENTS_HTML" == *'orders.order-created'* && "$EVENTS_HTML" == *'href="/designer"'* ]] \
  && pass "studio event map: compact summary of services and events, link to the designer" || fail "studio: event map"
if [[ $ONLINE == 1 ]]; then
  [[ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$STUDIO_PORT/_framework/blazor.web.js")" == 200 ]] \
    && pass "studio serves blazor.web.js (interactive UI)" || fail "studio: blazor.web.js missing"
else
  echo "  – blazor.web.js check skipped (source-built SDKs don't ship Blazor's JavaScript)"
fi
kill "$PID"; wait "$PID" 2>/dev/null || true; PID=""

echo "▶ Full services (need nuget.org)"
if [[ $ONLINE == 1 ]]; then
  for s in full auth regen clean features imported oldnames team/services/orders; do
    dotnet build "$WORK/$s" -c Release -v q -nologo >/dev/null && pass "$s builds"
    dotnet test  "$WORK/$s" -c Release --no-build -v q -nologo >/dev/null && pass "$s tests pass"
  done
  dotnet build "$SYS/Shop.AppHost" -c Release -v q -nologo >/dev/null && pass "Aspire AppHost builds"
else
  echo "  – nuget.org not reachable: skipped (run this script on your machine)"
fi

echo "✅ Smoke test OK"
