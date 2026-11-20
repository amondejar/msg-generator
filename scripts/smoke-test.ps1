# End-to-end check for Windows (PowerShell 5.1 or 7+): build + test + pack MSG, generate services
# that consume the local Msg.* packages, build them with warnings-as-errors, run one and hit its
# endpoints, check "stratum upgrade", then build and test the full variants.
#
# Usage (from the repo root):
#   powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1
#   pwsh -File scripts/smoke-test.ps1

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8   # dotnet prints UTF-8; avoids garbled accents
Add-Type -AssemblyName System.Net.Http

$Root   = Split-Path -Parent $PSScriptRoot
$Work   = Join-Path ([IO.Path]::GetTempPath()) ("msg-smoke-" + [guid]::NewGuid().ToString('N'))
$MsgDll = Join-Path $Root 'src\Msg.Cli\bin\Release\net10.0\stratum.dll'
$Pkgs   = Join-Path $Work 'packages'
$Port   = 5987
$Base   = "http://127.0.0.1:$Port"
$App    = $null
$Http   = New-Object System.Net.Http.HttpClient
$Http.Timeout = [TimeSpan]::FromSeconds(5)
New-Item -ItemType Directory -Path $Work | Out-Null

function Pass([string]$msg) { Write-Host "  [OK]   $msg" -ForegroundColor Green }
function Fail([string]$msg) {
    Write-Host "  [FAIL] $msg" -ForegroundColor Red
    $log = Join-Path $Work 'app.out.log'
    if (Test-Path $log) { Get-Content $log -Tail 20 | Out-Host }
    throw "Smoke test failed: $msg"
}
function Step([string]$msg) { Write-Host "> $msg" -ForegroundColor Cyan }

# Runs dotnet, hides output unless it fails. ErrorAction is relaxed locally because
# Windows PowerShell 5.1 turns any stderr line of a native command into an error.
function Invoke-Dotnet {
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        # "$_" turns stderr ErrorRecords into plain text (no red NativeCommandError noise).
        $out = & dotnet @args 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $eap
    }
    if ($code -ne 0) {
        $out | Out-Host
        Fail "dotnet $($args -join ' ') (exit $code)"
    }
}

# Runs dotnet and returns its output as text (for commands whose output is checked). Exit code in $LASTEXITCODE.
function Get-DotnetOutput {
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & dotnet @args 2>&1 | ForEach-Object { "$_" }
    } finally {
        $ErrorActionPreference = $eap
    }
    return ($out -join "`n")
}

function Get-Http([string]$path, [string]$correlationId) {
    $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, "$Base$path")
    if ($correlationId) { [void]$req.Headers.TryAddWithoutValidation('X-Correlation-ID', $correlationId) }
    $res = $Http.SendAsync($req).GetAwaiter().GetResult()
    $correlation = $null
    $values = $null
    if ($res.Headers.TryGetValues('X-Correlation-ID', [ref]$values)) { $correlation = @($values)[0] }
    [pscustomobject]@{
        Status        = [int]$res.StatusCode
        ContentType   = if ($res.Content.Headers.ContentType) { $res.Content.Headers.ContentType.MediaType } else { $null }
        CorrelationId = $correlation
        Body          = $res.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    }
}

try {
    Step 'Build, test and pack MSG'
    $sln = Join-Path $Root 'Msg.slnx'
    Invoke-Dotnet build $sln -c Release -v q -nologo
    Invoke-Dotnet test  $sln -c Release --no-build -v q -nologo;              Pass 'Msg.ServiceDefaults.* and Msg.Messaging tests pass'
    Invoke-Dotnet pack  $sln -c Release --no-build -o $Pkgs -v q -nologo
    Pass ("packed: " + ((Get-ChildItem $Pkgs -Filter *.nupkg | ForEach-Object Name) -join ' '))
    $Version = (& dotnet $MsgDll --version | Out-String).Trim()

    # The global NuGet cache keeps packages by id+version: drop old Msg.* builds so we test the ones just packed.
    $globalLine = (& dotnet nuget locals global-packages --list | Out-String).Trim()
    $global = $globalLine.Substring($globalLine.IndexOf(':') + 1).Trim()
    Get-ChildItem $global -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'msg.servicedefaults*' -or $_.Name -eq 'msg.ddd' -or $_.Name -like 'msg.messaging*' } |
        Remove-Item -Recurse -Force

    Step "Generate every feature combination worth checking (MSG $Version)"
    Invoke-Dotnet $MsgDll new Orders  -o (Join-Path $Work 'full') --package-source $Pkgs;          Pass 'full (default features)'
    Invoke-Dotnet $MsgDll new Billing -o (Join-Path $Work 'auth') --package-source $Pkgs --auth;   Pass 'full + auth'
    Invoke-Dotnet $MsgDll new Catalog -o (Join-Path $Work 'core') --package-source $Pkgs `
        --no-observability --no-openapi --no-resilience --no-tests;                                Pass 'core (framework only)'
    Invoke-Dotnet $MsgDll new --from (Join-Path $Work 'auth\msg.json') -o (Join-Path $Work 'from') --package-source $Pkgs
    Pass '--from msg.json'

    Step 'Template sanity'
    $files = 'full', 'auth', 'core', 'from' | ForEach-Object { Get-ChildItem -Path (Join-Path $Work $_) -Recurse -File }
    $leftovers = $files | Select-String -Pattern '@msg:|\{\{Service\}\}|\{\{service-kebab\}\}|\{\{DevPort\}\}|\{\{MsgVersion\}\}|__Service__'
    if ($leftovers) { $leftovers | Out-Host; Fail 'unrendered template markers found' }
    Pass 'no template markers left'

    foreach ($f in ($files | Where-Object Extension -eq '.json')) {
        try { Get-Content $f.FullName -Raw | ConvertFrom-Json | Out-Null } catch { Fail "invalid JSON: $($f.FullName)" }
    }
    Pass 'all JSON files are valid'

    $hashes = {
        param($dir)
        $prefix = (Get-Item $dir).FullName   # same path format Get-ChildItem returns (8.3 names, etc.)
        Get-ChildItem $dir -Recurse -File | ForEach-Object {
            $_.FullName.Substring($prefix.Length) + '|' + (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        } | Sort-Object
    }
    $diff = Compare-Object (& $hashes (Join-Path $Work 'auth')) (& $hashes (Join-Path $Work 'from'))
    if ($diff) { $diff | Out-Host; Fail '--from differs' }
    Pass '--from reproduces the same service'

    $props = Get-Content (Join-Path $Work 'core\Directory.Packages.props') -Raw
    if ($props -like "*Include=""Msg.ServiceDefaults"" Version=""$Version""*") { Pass "service pins Msg.ServiceDefaults $Version" } else { Fail 'Msg.ServiceDefaults version' }

    Step "Build 'core' service (TreatWarningsAsErrors)"
    Invoke-Dotnet build (Join-Path $Work 'core') -c Release -v q -nologo
    Pass 'core builds against the local Msg.ServiceDefaults package with zero warnings'

    Step "Run 'core' service and test the contract"
    # Content root = working directory, where appsettings.json is copied.
    $appDir = Join-Path $Work 'core\src\Catalog.Api\bin\Release\net10.0'
    $env:ASPNETCORE_URLS = $Base
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    $App = Start-Process dotnet -ArgumentList 'Catalog.Api.dll' -WorkingDirectory $appDir -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $Work 'app.out.log') -RedirectStandardError (Join-Path $Work 'app.err.log')
    Remove-Item Env:ASPNETCORE_URLS, Env:ASPNETCORE_ENVIRONMENT

    $up = $false
    for ($i = 0; $i -lt 50 -and -not $up; $i++) {
        try { $up = (Get-Http '/health/live').Status -eq 200 } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $up) { Fail 'service did not start' }

    if ((Get-Http '/health/live').Status  -eq 200) { Pass '/health/live 200' }  else { Fail '/health/live' }
    if ((Get-Http '/health/ready').Status -eq 200) { Pass '/health/ready 200' } else { Fail '/health/ready' }

    $ping = Get-Http '/api/v1/ping'
    if ($ping.Body -like '*"service":"catalog"*') { Pass "/api/v1/ping -> $($ping.Body)" } else { Fail "ping: $($ping.Body)" }

    $nf = Get-Http '/nope' 'smoke-42'
    if ($nf.Status -eq 404 -and $nf.ContentType -eq 'application/problem+json') { Pass '404 is application/problem+json' } else { Fail "404: $($nf.Status) $($nf.ContentType)" }
    if ($nf.CorrelationId -eq 'smoke-42') { Pass 'X-Correlation-ID echoed' } else { Fail "correlation header: $($nf.CorrelationId)" }
    if ($nf.Body -like '*"correlationId":"smoke-42"*') { Pass 'ProblemDetails has correlationId' } else { Fail "problem body: $($nf.Body)" }

    $bad = Get-Http '/health/live' '<script>'
    if ($bad.CorrelationId -ne '<script>') { Pass 'unsafe correlation id rejected' } else { Fail 'unsafe correlation id accepted' }

    Stop-Process -Id $App.Id -Force; $App.WaitForExit(); $App = $null
    $firstLine = Get-Content (Join-Path $Work 'app.out.log') -TotalCount 1
    if ($firstLine -and $firstLine.TrimStart().StartsWith('{')) { Pass 'logs are structured JSON in Production' } else { Fail "logs not JSON: $firstLine" }

    Step 'Startup fails fast on invalid configuration'
    $env:Service__Version = 'banana'
    $badApp = Start-Process dotnet -ArgumentList 'Catalog.Api.dll', '--urls', "http://127.0.0.1:$($Port + 1)" `
        -WorkingDirectory $appDir -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $Work 'bad.out.log') -RedirectStandardError (Join-Path $Work 'bad.err.log')
    Remove-Item Env:Service__Version
    $null = $badApp.Handle   # PowerShell 5.1 needs this to read ExitCode later
    if (-not $badApp.WaitForExit(15000)) { Stop-Process -Id $badApp.Id -Force; Fail 'service started with invalid config' }
    $badLog = (Get-Content (Join-Path $Work 'bad.out.log') -Raw) + (Get-Content (Join-Path $Work 'bad.err.log') -Raw)
    if ($badApp.ExitCode -ne 0 -and $badLog -like '*SemVer*') { Pass 'invalid Service:Version stops startup with a clear message' } else { Fail 'no validation message' }

    Step 'stratum upgrade'
    $upgraded = Join-Path $Work 'upgraded'
    Copy-Item (Join-Path $Work 'core') $upgraded -Recurse
    Invoke-Dotnet $MsgDll upgrade --path $upgraded --to 9.9.9
    $upProps = Get-Content (Join-Path $upgraded 'Directory.Packages.props') -Raw
    $upJson  = Get-Content (Join-Path $upgraded 'msg.json') -Raw
    if ($upProps -like '*Include="Msg.ServiceDefaults" Version="9.9.9"*' -and $upJson -like '*"msgVersion": "9.9.9"*') {
        Pass 'upgrade bumps Msg.* packages and msg.json'
    } else { Fail 'upgrade' }

    Step 'stratum add entity'
    $full = Join-Path $Work 'full'
    Invoke-Dotnet $MsgDll add entity Product  --path $full --props 'Name:string, Price:decimal, Description:string?, Stock:int'
    Invoke-Dotnet $MsgDll add entity Category --path $full --props 'Title:string, Position:int?' --plural Categories
    Invoke-Dotnet $MsgDll add entity Invoice  --path (Join-Path $Work 'auth') --db sqlserver --props 'Number:string, Total:decimal, Paid:bool'
    Pass 'entities added (postgres + sqlserver)'

    $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & dotnet $MsgDll add entity Product --path $full --props 'Name:string' 2>&1 | Out-Null
    $dupCode = $LASTEXITCODE; $ErrorActionPreference = $eap
    if ($dupCode -ne 0) { Pass 'duplicate entity rejected' } else { Fail 'duplicate entity accepted' }

    $reg = Get-Content (Join-Path $full 'src\Orders.Api\Features\FeatureRegistration.g.cs') -Raw
    $pkg = Get-Content (Join-Path $full 'Directory.Packages.props') -Raw
    $dev = Get-Content (Join-Path $full 'src\Orders.Api\appsettings.Development.json') -Raw
    if ($reg -like '*MapProductEndpoints*' -and $pkg -like '*Npgsql.EntityFrameworkCore.PostgreSQL*' -and $dev -like '*"Database"*') {
        Pass 'registration, packages and connection string wired'
    } else { Fail 'entity wiring' }

    Invoke-Dotnet $MsgDll new --from (Join-Path $full 'msg.json') -o (Join-Path $Work 'regen') --package-source $Pkgs
    Pass 'stratum new --from regenerates the entities'

    Step 'Clean Architecture + DDD'
    $clean = Join-Path $Work 'clean'
    Invoke-Dotnet $MsgDll new Catalog -o $clean --architecture clean-ddd --package-source $Pkgs
    Invoke-Dotnet $MsgDll add entity Product  --path $clean --props 'Name:string, Price:decimal, Description:string?'
    Invoke-Dotnet $MsgDll add entity Category --path $clean --props 'Title:string, Position:int?' --plural Categories
    foreach ($p in 'Domain', 'Application', 'Infrastructure', 'Api') {
        if (-not (Test-Path (Join-Path $clean "src\Catalog.$p\Catalog.$p.csproj"))) { Fail "missing Catalog.$p" }
    }
    $agg  = Get-Content (Join-Path $clean 'src\Catalog.Domain\Products\Product.g.cs') -Raw
    $cmd  = Get-Content (Join-Path $clean 'src\Catalog.Application\Products\ProductCommands.g.cs') -Raw
    $repo = Get-Content (Join-Path $clean 'src\Catalog.Infrastructure\Persistence\RepositoryRegistration.g.cs') -Raw
    if ($agg -like '*AggregateRoot<Guid>*' -and $cmd -like '*ICommandHandler<CreateProduct, Guid>*' -and $repo -like '*AddScoped<IProductRepository, ProductRepository>*') {
        Pass 'Domain / Application / Infrastructure / Api with aggregates, use cases and repositories'
    } else { Fail 'clean-ddd structure' }
    $cleanLeft = Get-ChildItem $clean -Recurse -File | Select-String -Pattern '@msg:|\{\{Service\}\}|\{\{Entity\}\}|__Entity__'
    if ($cleanLeft) { $cleanLeft | Out-Host; Fail 'unrendered markers in clean-ddd' }
    Pass 'no template markers left in clean-ddd'

    Step 'Editable entities (stratum entity)'
    $cp = Join-Path $clean 'src\Catalog.Domain\Products\Product.cs'
    $gp = Join-Path $clean 'src\Catalog.Domain\Products\Product.g.cs'
    # User code in the partial class: must survive every regeneration.
    $userCode = @'
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
'@
    Set-Content -Path $cp -Value $userCode -Encoding UTF8
    $out = Get-DotnetOutput $MsgDll entity update Product --path $clean --props 'Title:string, Price:decimal, Stock:int' --rename Name=Title
    if ($LASTEXITCODE -ne 0) { $out | Out-Host; Fail 'entity update' }
    if ($out -like '*Product.Name se renombra a Title*' -and $out -like '*Product.Description se elimina*') {
        Pass 'update warns about renamed/removed properties used in user code'
    } else { $out | Out-Host; Fail 'entity update warnings' }
    $g = Get-Content $gp -Raw
    if ((Get-Content $cp -Raw) -like '*Label => *' -and $g -like '*public string Title*' -and $g -notlike '*Description*') {
        Pass 'properties regenerated in .g.cs, user partial untouched'
    } else { Fail 'entity update' }
    if ((Get-Content (Join-Path $clean 'msg.json') -Raw) -like '*"from": "Name"*') { Pass 'rename recorded in msg.json (pendingRenames)' } else { Fail 'pendingRenames' }
    Set-Content -Path $cp -Value ($userCode.Replace('{Name} ({Price})', '{Title} ({Price})')) -Encoding UTF8
    $out = Get-DotnetOutput $MsgDll entity update Product --path $clean --props 'Title:string, Price:decimal, Stock:int'
    if ($out -like '*Nada que cambiar*') { Pass 'no-op update changes nothing' } else { $out | Out-Host; Fail 'no-op update' }
    Invoke-Dotnet $MsgDll add entity Tag --path $clean --props 'Label:string'
    Invoke-Dotnet $MsgDll entity remove Tag --path $clean --delete-own-files
    if (Get-ChildItem $clean -Recurse -File -Filter 'Tag*.cs') { Fail 'entity remove left files' }

    # References between entities and aggregate operations (built and tested below with the full services).
    Invoke-Dotnet $MsgDll add entity Review --path $clean --props 'Text:string, Product:Product, ReplyToId:Review?'
    Invoke-Dotnet $MsgDll entity operation add Product Discontinue --params 'Reason:string' --path $clean
    Invoke-Dotnet $MsgDll entity operation add Product Restock --path $clean
    $cr = Join-Path $clean 'src'
    $refOk = (Get-Content (Join-Path $cr 'Catalog.Infrastructure\Persistence\Reviews\ReviewConfiguration.g.cs') -Raw) -like '*HasOne<global::Catalog.Domain.Products.Product>().WithMany().HasForeignKey(e => e.ProductId)*' `
        -and (Get-Content (Join-Path $cr 'Catalog.Application\Reviews\ReviewCommands.g.cs') -Raw) -like '*MissingReferenceAsync*' `
        -and (Get-Content (Join-Path $cr 'Catalog.Infrastructure\Persistence\Products\ProductIntegrity.g.cs') -Raw) -like '*InUseAsync*' `
        -and (Get-Content (Join-Path $cr 'Catalog.Domain\Products\Product.Discontinue.cs') -Raw) -like '*public partial void Discontinue(string reason)*' `
        -and (Get-Content (Join-Path $cr 'Catalog.Api\Features\Products\ProductOperationEndpoints.g.cs') -Raw) -like '*"/{id:guid}/discontinue"*' `
        -and (Get-Content (Join-Path $cr 'Catalog.Domain\Products\ProductOperations.g.cs') -Raw) -like '*record ProductRestocked(Guid ProductId)*'
    $null = Get-DotnetOutput $MsgDll entity remove Product --path $clean   # expected to fail: checked through $LASTEXITCODE
    if ($refOk -and $LASTEXITCODE -ne 0) { Pass 'clean-ddd: references (foreign keys, checks) and aggregate operations' } else { Fail 'clean-ddd references/operations' }
    Invoke-Dotnet $MsgDll feature on mcp --path $clean
    if ((Get-Content (Join-Path $cr 'Catalog.Api\MsgWiring.g.cs') -Raw) -like '*app.MapMcp("/mcp")*' `
        -and (Get-Content (Join-Path $cr 'Catalog.Api\Features\Products\ProductMcpTools.g.cs') -Raw) -like '*Name = "discontinue_product"*' `
        -and (Get-Content (Join-Path $cr 'Catalog.Api\Features\Reviews\ReviewMcpTools.g.cs') -Raw) -like '*Name = "create_review"*' `
        -and (Get-Content (Join-Path $cr 'Catalog.Api\Catalog.Api.csproj') -Raw) -like '*Include="ModelContextProtocol.AspNetCore"*') {
        Pass 'feature mcp: /mcp with the entities'' CRUD and operations as tools'
    } else { Fail 'feature mcp' }
    $full = Join-Path $Work 'full'
    Invoke-Dotnet $MsgDll add entity Supplier --path $full --props 'Name:string'
    Invoke-Dotnet $MsgDll add entity Shipment --path $full --props 'Code:string, SupplierId:Supplier, ProductId:Product?'
    $vOk = (Get-Content (Join-Path $full 'src\Orders.Api\Features\Shipments\ShipmentEndpoints.g.cs') -Raw) -like '*MissingReferencesAsync*' `
        -and (Get-Content (Join-Path $full 'src\Orders.Api\Features\Suppliers\SupplierEndpoints.g.cs') -Raw) -like '*UsedByAsync*' `
        -and (Get-Content (Join-Path $full 'tests\Orders.Api.Tests\Features\ShipmentEndpointsTests.g.cs') -Raw) -like '*await CreateSupplierAsync(client)*'
    $null = Get-DotnetOutput $MsgDll entity operation add Supplier Approve --path $full   # expected to fail: checked through $LASTEXITCODE
    $opRejected = $LASTEXITCODE -ne 0
    $null = Get-DotnetOutput $MsgDll add entity Bad --path $full --props 'X:Nope'   # expected to fail: checked through $LASTEXITCODE
    if ($vOk -and $opRejected -and $LASTEXITCODE -ne 0) { Pass 'vertical slice: references with 400/409 checks and test data' } else { Fail 'vertical references' }
    if ((Get-Content (Join-Path $clean 'src\Catalog.Infrastructure\Persistence\CatalogDbContext.g.cs') -Raw) -like '*Tag*') { Fail 'Tag still in DbContext' }
    Pass 'entity remove deletes its files and registrations'
    # An entity from MSG < 0.10 (no "layout" in msg.json) is read-only until converted.
    $defPath = Join-Path $full 'msg.json'
    $def = Get-Content $defPath -Raw | ConvertFrom-Json
    foreach ($e in $def.entities) { if ($e.name -eq 'Category') { $e.PSObject.Properties.Remove('layout') } }
    [System.IO.File]::WriteAllText($defPath, ($def | ConvertTo-Json -Depth 10))
    $out = Get-DotnetOutput $MsgDll entity update Category --path $full --props 'Title:string'
    if ($LASTEXITCODE -eq 0) { Fail 'legacy entity edited without converting' }
    $out = Get-DotnetOutput $MsgDll entity convert Category --path $full --dry-run
    if ($out -notlike '*(archivo tuyo)*') { $out | Out-Host; Fail 'entity convert --dry-run' }
    Invoke-Dotnet $MsgDll entity convert Category --path $full
    Invoke-Dotnet $MsgDll entity update Category --path $full --props 'Title:string, Position:int?, Color:string?'
    Pass 'legacy entity: blocked, converted (shows user files), then editable'

    Step 'Features on/off in an existing service (stratum feature)'
    $fs = Join-Path $Work 'features'
    Invoke-Dotnet $MsgDll new Shop -o $fs --package-source $Pkgs
    Invoke-Dotnet $MsgDll add entity Product --path $fs --props 'Name:string'
    Invoke-Dotnet $MsgDll feature off docker openApi --path $fs
    $apiProj = Join-Path $fs 'src\Shop.Api\Shop.Api.csproj'
    $wiring = Join-Path $fs 'src\Shop.Api\MsgWiring.g.cs'
    if (-not (Test-Path (Join-Path $fs 'Dockerfile')) -and (Get-Content $apiProj -Raw) -notlike '*Scalar.AspNetCore*' -and (Get-Content $wiring -Raw) -notlike '*Scalar.AspNetCore*' -and (Get-Content (Join-Path $fs 'src\Shop.Api\Program.cs') -Raw) -like '*AddMsgWiring()*') {
        Pass 'feature off removes its files, packages and wiring (Program.cs untouched)'
    } else { Fail 'feature off' }
    Invoke-Dotnet $MsgDll feature on docker openApi --path $fs
    if ((Test-Path (Join-Path $fs 'Dockerfile')) -and (Get-Content $apiProj -Raw) -like '*Include="Scalar.AspNetCore"*' -and (Get-Content $wiring -Raw) -like '*MapScalarApiReference*') {
        Pass 'feature on brings them back'
    } else { Fail 'feature on' }
    $w = Get-Content $wiring -Raw
    [System.IO.File]::WriteAllText($wiring, $w.Replace('builder.AddFeatures();', 'builder.AddFeatures(); // tweak'))
    $out = Get-DotnetOutput $MsgDll feature on auth --path $fs --dry-run
    if ($out -like '*Has editado a mano*') { Pass 'a hand-edited .g.cs is detected before overwriting it' } else { $out | Out-Host; Fail 'hand-edit detection' }
    Invoke-Dotnet $MsgDll feature on auth --path $fs
    $lp = Join-Path $Work 'legacy-program'
    Copy-Item -Recurse $fs $lp
    $legacyProgram = @'
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
'@
    Set-Content -Path (Join-Path $lp 'src\Shop.Api\Program.cs') -Value $legacyProgram -Encoding UTF8
    $out = Get-DotnetOutput $MsgDll feature off docker --path $lp
    if ($LASTEXITCODE -eq 0) { Fail 'features changed with a Program.cs from before 0.12' }
    Invoke-Dotnet $MsgDll feature convert --path $lp
    if ((Get-Content (Join-Path $lp 'src\Shop.Api\Program.cs') -Raw) -notlike '*AddMsgWiring()*') { Fail 'feature convert' }
    Invoke-Dotnet $MsgDll feature off docker --path $lp
    Pass 'Program.cs from before 0.12: blocked, converted, then editable'

    Step 'Messaging (stratum messaging / stratum events)'
    $out = Get-DotnetOutput $MsgDll events on Product --path $fs
    if ($LASTEXITCODE -eq 0) { Fail 'events published without messaging' }
    Invoke-Dotnet $MsgDll messaging on --broker kafka --path $fs
    Invoke-Dotnet $MsgDll events on Product --path $fs
    $wiringCode = Get-Content $wiring -Raw
    $devSettings = Join-Path $fs 'src\Shop.Api\appsettings.Development.json'
    if ($wiringCode -like '*UseKafka(builder.Configuration)*' -and $wiringCode -like '*UseEntityFramework<ShopDbContext>*' -and (Get-Content $apiProj -Raw) -like '*Include="Msg.Messaging.Kafka"*' -and (Get-Content $devSettings -Raw) -like '*"kafka": "localhost:9092"*' -and (Get-Content (Join-Path $fs 'src\Shop.Api\Data\MessagingConfiguration.g.cs') -Raw) -like '*MessagingModel.ConfigureOutbox*') {
        Pass 'messaging on: Kafka, outbox/inbox tables, packages and connection string'
    } else { Fail 'messaging on' }
    Get-Content $devSettings -Raw | ConvertFrom-Json | Out-Null
    if ((Get-Content (Join-Path $fs 'src\Shop.Api\IntegrationEvents\ProductIntegrationEvents.g.cs') -Raw) -like '*IntegrationEvent("shop.product-created")*' -and (Get-Content (Join-Path $fs 'src\Shop.Api\Features\Products\ProductEndpoints.g.cs') -Raw) -like '*PublishAsync(new ProductCreated*') {
        Pass 'vertical slice: Product events published from the endpoints (outbox)'
    } else { Fail 'events on (vertical)' }
    Invoke-Dotnet $MsgDll messaging on --broker kafka --path $clean
    Invoke-Dotnet $MsgDll events on Product --path $clean
    if ((Get-Content (Join-Path $clean 'src\Catalog.Infrastructure\Persistence\Products\ProductIntegrationEventsMapper.g.cs') -Raw) -like '*ProductIntegrationEventsMapper : IIntegrationEventMapper*' -and (Get-Content (Join-Path $clean 'src\Catalog.Application\IntegrationEvents\ProductIntegrationEvents.g.cs') -Raw) -like '*IntegrationEvent("catalog.product-created")*') {
        Pass 'clean + DDD: domain events mapped to integration events'
    } else { Fail 'events on (clean)' }

    Invoke-Dotnet $MsgDll subscribe $fs Product --path $clean
    Invoke-Dotnet $MsgDll subscribe $clean Product.Created --path $fs
    if ((Get-Content (Join-Path $clean 'src\Catalog.Application\IntegrationEvents\Shop\ProductDeleted.g.cs') -Raw) -like '*IntegrationEvent("shop.product-deleted")*' -and (Get-Content (Join-Path $clean 'src\Catalog.Application\Events\Shop\ProductCreatedHandler.cs') -Raw) -like '*IIntegrationEventHandler<ProductCreated>*' -and (Get-Content (Join-Path $fs 'src\Shop.Api\IntegrationEvents\Catalog\ProductCreated.g.cs') -Raw) -like '*IntegrationEvent("catalog.product-created")*' -and (Get-Content (Join-Path $fs 'src\Shop.Api\Events\Catalog\ProductCreatedHandler.cs') -Raw) -like '*IIntegrationEventHandler<ProductCreated>*') {
        Pass 'subscribe: contract copied and handler created (both architectures)'
    } else { Fail 'subscribe' }
    $out = Get-DotnetOutput $MsgDll events map --path $Work
    if ($out -match 'shop\.product-created .*-> Catalog' -or $out -match 'shop\.product-created .*Catalog') { Pass 'events map shows who publishes and who listens' } else { $out | Out-Host; Fail 'events map' }
    Invoke-Dotnet $MsgDll entity update Product --path $fs --props 'Name:string, Sku:string?'
    $out = Get-DotnetOutput $MsgDll events map --path $Work
    $sync = Get-DotnetOutput $MsgDll subscribe --sync --path $clean
    if ($out -like '*ha cambiado los datos de ProductCreated*' -and $sync -like '*IntegrationEvents/Shop/ProductCreated.g.cs*' -and (Get-Content (Join-Path $clean 'src\Catalog.Application\IntegrationEvents\Shop\ProductCreated.g.cs') -Raw) -like '*string? Sku*') {
        Pass 'a changed contract is detected and --sync copies it again'
    } else { Fail 'subscribe --sync' }
    Invoke-Dotnet $MsgDll events off Product --path $fs
    Invoke-Dotnet $MsgDll subscribe --sync --prune --path $clean
    if (-not (Test-Path (Join-Path $clean 'src\Catalog.Application\IntegrationEvents\Shop')) -and (Get-Content (Join-Path $clean 'msg.json') -Raw) -notlike '*"service": "Shop"*') {
        Pass '--prune drops subscriptions to events no longer published'
    } else { Fail 'subscribe --prune' }
    Invoke-Dotnet $MsgDll events on Product --path $fs

    Step 'Guided migrations (stratum migration review)'
    $mig = Join-Path $Work 'migration'
    Copy-Item -Recurse $clean $mig
    $migDir = Join-Path $mig 'src\Catalog.Infrastructure\Migrations'
    New-Item -ItemType Directory -Path $migDir | Out-Null
    $mf = Join-Path $migDir '20990101000000_RenameTitle.cs'
    # What "dotnet ef migrations add" scaffolds for a renamed property: drop + add (the data would be lost).
    $migration = @'
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
'@
    Set-Content -Path $mf -Value $migration -Encoding UTF8
    $out = Get-DotnetOutput $MsgDll migration review --path $mig --dry-run
    if ($LASTEXITCODE -eq 0 -and $out -like '*RenameColumn*' -and $out -like '*Borra la tabla tags*' -and (Get-Content $mf -Raw) -notlike '*RenameColumn*') {
        Pass 'review --dry-run reports the rename fix and the dropped table without writing'
    } else { $out | Out-Host; Fail 'migration review --dry-run' }
    Invoke-Dotnet $MsgDll migration review --path $mig
    $fixed = Get-Content $mf -Raw
    $upPart = $fixed.Substring(0, $fixed.IndexOf('void Down'))
    if ($fixed -like '*migrationBuilder.RenameColumn*' -and $upPart -notlike '*AddColumn*' -and (Get-Content (Join-Path $mig 'msg.json') -Raw) -notlike '*pendingRenames*') {
        Pass 'review turns drop + add into RenameColumn and clears pendingRenames'
    } else { $fixed | Out-Host; Fail 'migration review' }

    Step 'Team templates (.msg)'
    $team = Join-Path $Work 'team'
    New-Item -ItemType Directory -Path (Join-Path $team 'services') | Out-Null
    Invoke-Dotnet $MsgDll templates eject service/src/__Service__.Api/Program.cs --path $team
    $teamProgram = Join-Path $team '.msg\templates\service\src\__Service__.Api\Program.cs'
    Set-Content -Path $teamProgram -Value (@('// {{Company}} conventions') + (Get-Content $teamProgram)) -Encoding utf8
    Set-Content -Path (Join-Path $team '.msg\config.json') -Value '{ "tokens": { "Company": "Acme" } }' -Encoding utf8
    Set-Content -Path (Join-Path $team '.msg\templates\service\CODEOWNERS') -Value '* @{{Company}}/backend' -Encoding utf8
    New-Item -ItemType File -Path (Join-Path $team '.msg\templates\service\Dockerfile') -Force | Out-Null
    $teamOrders = Join-Path $team 'services\orders'
    Invoke-Dotnet $MsgDll new Orders -o $teamOrders --package-source $Pkgs
    Invoke-Dotnet $MsgDll add entity Product --path $teamOrders --props 'Name:string'
    $firstLine = Get-Content (Join-Path $teamOrders 'src\Orders.Api\Program.cs') -TotalCount 1
    $owners = (Get-Content (Join-Path $teamOrders 'CODEOWNERS') -Raw).Trim()
    if ($firstLine -like '*Acme conventions*' -and $owners -eq '* @Acme/backend' -and -not (Test-Path (Join-Path $teamOrders 'Dockerfile'))) {
        Pass 'team templates: replace, add, remove and tokens (found walking up)'
    } else { Fail "team templates (first line: $firstLine, CODEOWNERS: $owners)" }

    Step 'stratum aspire'
    $sys = Join-Path $Work 'system'
    New-Item -ItemType Directory -Path $sys | Out-Null
    Invoke-Dotnet $MsgDll new Orders  -o (Join-Path $sys 'orders')  --package-source $Pkgs
    Invoke-Dotnet $MsgDll add entity Order --path (Join-Path $sys 'orders') --props 'Number:string, Total:decimal'
    Invoke-Dotnet $MsgDll new Billing -o (Join-Path $sys 'billing') --architecture clean-ddd --package-source $Pkgs
    Invoke-Dotnet $MsgDll add entity Invoice --path (Join-Path $sys 'billing') --db sqlserver --props 'Number:string'
    Invoke-Dotnet $MsgDll messaging on --broker kafka --path (Join-Path $sys 'orders')
    Invoke-Dotnet $MsgDll events on Order --path (Join-Path $sys 'orders')
    Invoke-Dotnet $MsgDll aspire --path $sys --name Shop
    foreach ($f in 'Shop.AppHost\Shop.AppHost.csproj', 'Shop.AppHost\AppHost.cs', 'Shop.AppHost\MsgServices.g.cs', 'Shop.slnx', '.aspire\settings.json') {
        if (-not (Test-Path (Join-Path $sys $f))) { Fail "missing $f" }
    }
    $hostCode = Get-Content (Join-Path $sys 'Shop.AppHost\MsgServices.g.cs') -Raw
    if ($hostCode -like '*AddKafka("kafka")*' -and $hostCode -like '*WithKafkaUI*' -and (Get-Content (Join-Path $sys 'Shop.AppHost\Shop.AppHost.csproj') -Raw) -like '*Aspire.Hosting.Kafka*') {
        Pass 'AppHost: Kafka + Kafka UI for the services with messaging'
    } else { Fail 'AppHost Kafka' }
    if ($hostCode -like '*AddProject<Projects.Orders_Api>("orders")*' -and $hostCode -like '*connectionName: "Database"*' -and $hostCode -like '*AddPostgres*' -and $hostCode -like '*AddSqlServer*') {
        Pass 'AppHost with both services, their databases and health checks'
    } else { Fail 'AppHost content' }
    $again = (& dotnet $MsgDll aspire --path $sys --name Shop | Out-String)
    if ($again -notmatch '(?m)^  [+~]') { Pass 'stratum aspire is idempotent' } else { Fail 'stratum aspire rewrote files' }
    $elsewhere = Join-Path $Work 'elsewhere\payments'
    Invoke-Dotnet $MsgDll new Payments -o $elsewhere --package-source $Pkgs
    Invoke-Dotnet $MsgDll workspace add $elsewhere --path $sys
    Invoke-Dotnet $MsgDll aspire --path $sys --name Shop
    $out = Get-DotnetOutput $MsgDll workspace list --path $sys
    if ((Get-Content (Join-Path $sys 'msg.workspace.json') -Raw) -like '*../elsewhere/payments*' -and (Get-Content (Join-Path $sys 'Shop.AppHost\Shop.AppHost.csproj') -Raw) -like '*elsewhere\payments\src\Payments.Api\Payments.Api.csproj*' -and (Get-Content (Join-Path $sys 'Shop.AppHost\MsgServices.g.cs') -Raw) -like '*AddProject<Projects.Payments_Api>("payments")*' -and $out -like '*msg.workspace.json*') {
        Pass 'a service in another folder joins the system (msg.workspace.json -> AppHost)'
    } else { Fail 'workspace add' }
    Invoke-Dotnet $MsgDll new Notifications -o (Join-Path $sys 'notifications') --package-source $Pkgs
    Invoke-Dotnet $MsgDll aspire --path $sys --name Shop
    $notifications = Join-Path $sys 'notifications'
    Invoke-Dotnet $MsgDll messaging on --broker azureServiceBus --path $notifications
    Invoke-Dotnet $MsgDll subscribe (Join-Path $sys 'orders') Order.Created --path $notifications
    Invoke-Dotnet $MsgDll aspire --path $sys --name Shop
    $hostCode = Get-Content (Join-Path $sys 'Shop.AppHost\MsgServices.g.cs') -Raw
    $map = Get-DotnetOutput $MsgDll events map --path $sys
    if ($hostCode -like '*AddAzureServiceBus("servicebus")*' -and $hostCode -like '*AddServiceBusSubscription("notifications-orders-order-created", "notifications")*' -and (Get-Content (Join-Path $sys 'Shop.AppHost\Shop.AppHost.csproj') -Raw) -like '*Aspire.Hosting.Azure.ServiceBus*' -and (Get-Content (Join-Path $notifications 'src\Notifications.Api\MsgWiring.g.cs') -Raw) -like '*UseAzureServiceBus*' -and $map -like '*no le llegar*') {
        Pass 'Azure Service Bus: emulator, topics and subscriptions in the AppHost; broker mismatch reported'
    } else { $map | Out-Host; Fail 'Azure Service Bus' }
    Invoke-Dotnet $MsgDll events asyncapi --path $sys
    $spec = Get-Content (Join-Path $sys 'asyncapi.yaml') -Raw
    if ($spec -like '*asyncapi: "3.1.0"*' -and $spec -like '*address: orders.order-created*' -and $spec -like '*action: receive*') {
        Pass 'AsyncAPI 3.1 exported from the event map'
    } else { Fail 'AsyncAPI export' }
    $mcpIn = @(
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}',
        '{"jsonrpc":"2.0","method":"notifications/initialized"}',
        '{"jsonrpc":"2.0","id":2,"method":"tools/list"}',
        '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"add_entity","arguments":{"service":"Orders","name":"Coupon","properties":"Code:string"}}}',
        '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"list_services","arguments":{}}}'
    )
    # 'stratum mcp' logs to stderr (stdout is the protocol): Windows PowerShell turns that into an error under 'Stop'.
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $mcpOut = @($mcpIn | & dotnet $MsgDll mcp --path $sys 2>$null | ForEach-Object { "$_" }) }
    finally { $ErrorActionPreference = $eap }
    $mcpText = $mcpOut -join "`n"
    if ($mcpOut.Count -eq 4 -and $mcpText -like '*"protocolVersion":"2025-11-25"*' -and $mcpText -like '*"name":"subscribe"*' -and $mcpText -like '*"structuredContent":{"root"*' -and -not (Test-Path (Join-Path $sys 'orders\src\Orders.Api\Features\Coupons'))) {
        Pass 'MCP server: handshake, tools, plan without writing'
    } else { $mcpText | Out-Host; Fail 'MCP server' }
    $legacy = Join-Path $sys 'legacy'
    Invoke-Dotnet $MsgDll new Legacy -o $legacy --package-source $Pkgs
    Invoke-Dotnet $MsgDll aspire --path $sys --name Shop
    $out = Get-DotnetOutput $MsgDll service remove $legacy --path $sys
    if ($out -notlike '*No se ha tocado nada*' -or -not (Test-Path $legacy)) { $out | Out-Host; Fail 'service remove (dry run)' }
    Invoke-Dotnet $MsgDll service remove $legacy --path $sys --yes
    $appHostFiles = (Get-Content (Join-Path $sys 'Shop.AppHost\Shop.AppHost.csproj') -Raw) + (Get-Content (Join-Path $sys 'Shop.AppHost\MsgServices.g.cs') -Raw) + (Get-Content (Join-Path $sys 'Shop.slnx') -Raw)
    if (-not (Test-Path $legacy) -and $appHostFiles -notlike '*Legacy*') { Pass 'service remove: dry run first, then folder and AppHost references gone' } else { Fail 'service remove' }
    $hostCode = Get-Content (Join-Path $sys 'Shop.AppHost\MsgServices.g.cs') -Raw
    $hostProj = Get-Content (Join-Path $sys 'Shop.AppHost\Shop.AppHost.csproj') -Raw
    if ($hostCode -like '*Notifications_Api*' -and $hostProj -like '*Notifications.Api.csproj*') { Pass 'a new service is added to the AppHost' } else { Fail 'new service not added' }

    Step 'stratum studio'
    $studioPort = 5988
    $studio = Start-Process dotnet -ArgumentList $MsgDll, 'studio', '--path', $sys, '--port', $studioPort, '--no-browser' -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $Work 'studio.out.log') -RedirectStandardError (Join-Path $Work 'studio.err.log')
    $studioBase = "http://127.0.0.1:$studioPort"
    $studioUp = $false
    for ($i = 0; $i -lt 50 -and -not $studioUp; $i++) {
        try { $studioUp = ($Http.GetAsync("$studioBase/").GetAwaiter().GetResult().StatusCode -eq 'OK') } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $studioUp) { Fail 'studio did not start' }
    $homeHtml = $Http.GetStringAsync("$studioBase/").GetAwaiter().GetResult()
    foreach ($s in 'Orders', 'Billing', 'Notifications') {
        if ($homeHtml -notlike "*>$s<*") { Fail "studio: $s missing on the system page" }
    }
    Pass 'studio lists the services of the system'
    # Own client: the first render of a page (Razor compilation, file scans) can be slow on CI runners.
    $studioHttp = New-Object System.Net.Http.HttpClient
    $studioHttp.Timeout = [TimeSpan]::FromSeconds(60)
    foreach ($p in 'services', 'events', 'run', 'templates', 'service', 'entities', 'service/data', 'service/messaging', 'service/code', 'service/remove', 'apply', '_framework/blazor.web.js') {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        try { $code = [int]$studioHttp.GetAsync("$studioBase/$p").GetAwaiter().GetResult().StatusCode }
        catch { $code = "no response after $([int]$watch.Elapsed.TotalSeconds) s ($($_.Exception.InnerException.Message))" }
        if ($code -ne 200) { $studioHttp.Dispose(); Stop-Process -Id $studio.Id -Force; Fail "studio: /$p returned $code" }
        if ($watch.Elapsed.TotalSeconds -gt 10) { Write-Host "  (studio /$p took $([int]$watch.Elapsed.TotalSeconds) s)" -ForegroundColor Yellow }
    }
    $studioHttp.Dispose()
    Pass 'studio pages render and blazor.web.js is served (interactive UI)'
    Stop-Process -Id $studio.Id -Force

    Step 'stratum import openapi'
    $librarySpec = @'
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
'@
    Set-Content -Path (Join-Path $Work 'library.yaml') -Value $librarySpec -Encoding UTF8
    $out = Get-DotnetOutput $MsgDll import openapi (Join-Path $Work 'library.yaml') -a clean-ddd -o (Join-Path $Work 'imported') --package-source $Pkgs
    if ($out -like '*Author (/api/v1/authors): FullName:string, Born:date?*' -and $out -like '*Book (/api/v1/books): Title:string, AuthorId:Author, Pages:int?*' -and $out -like '*Lend(Member:string)*BookLent*' -and $out -like '*Book.Tags: lista*' -and $out -like '*GET /stats*' -and (Test-Path (Join-Path $Work 'imported\src\LibraryAPI.Domain\Books\Book.Lend.cs'))) {
        Pass 'import openapi: entities, references, operations and a report of the rest'
    } else { $out | Out-Host; Fail 'import openapi' }

    Step 'Full services'
    foreach ($s in 'full', 'auth', 'regen', 'clean', 'features', 'imported', 'team\services\orders') {
        Invoke-Dotnet build (Join-Path $Work $s) -c Release -v q -nologo;           Pass "$s builds"
        Invoke-Dotnet test  (Join-Path $Work $s) -c Release --no-build -v q -nologo; Pass "$s tests pass"
    }
    Invoke-Dotnet build (Join-Path $sys 'Shop.AppHost') -c Release -v q -nologo; Pass 'Aspire AppHost builds'

    Write-Host 'Smoke test OK' -ForegroundColor Green
}
finally {
    if ($App -and -not $App.HasExited) { Stop-Process -Id $App.Id -Force -ErrorAction SilentlyContinue }
    $Http.Dispose()
    Start-Sleep -Milliseconds 300
    Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue
}
