# End-to-end check for Windows (PowerShell 5.1 or 7+): build + test + pack Stratum, generate services
# that consume the local Stratum.* packages, build them with warnings-as-errors, run one and hit its
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
$StratumDll = Join-Path $Root 'src\Stratum.Cli\bin\Release\net10.0\stratum.dll'
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
    Step 'Build, test and pack Stratum'
    $sln = Join-Path $Root 'Stratum.slnx'
    Invoke-Dotnet build $sln -c Release -v q -nologo
    Invoke-Dotnet test  $sln -c Release --no-build -v q -nologo;              Pass 'Stratum.ServiceDefaults.* and Stratum.Messaging tests pass'
    Invoke-Dotnet pack  $sln -c Release --no-build -o $Pkgs -v q -nologo
    Pass ("packed: " + ((Get-ChildItem $Pkgs -Filter *.nupkg | ForEach-Object Name) -join ' '))
    $Version = (& dotnet $StratumDll --version | Out-String).Trim()
    $helpMain = (& dotnet $StratumDll --help 2>&1 | Out-String)
    $helpNew = (& dotnet $StratumDll new --help 2>&1 | Out-String)
    if ($helpMain -like '*stratum new*' -and $helpNew -like '*stratum new*' -and $helpMain -notlike '*Exception*') { Pass 'stratum --help and stratum new --help' } else { Fail "--help: $helpMain" }

    # The global NuGet cache keeps packages by id+version: drop old Stratum.* builds so we test the ones just packed.
    $globalLine = (& dotnet nuget locals global-packages --list | Out-String).Trim()
    $global = $globalLine.Substring($globalLine.IndexOf(':') + 1).Trim()
    Get-ChildItem $global -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'stratumkit.*' -or $_.Name -like 'stratum.servicedefaults*' -or $_.Name -eq 'stratum.ddd' -or $_.Name -like 'stratum.messaging*' -or $_.Name -like 'msg.servicedefaults*' -or $_.Name -eq 'msg.ddd' -or $_.Name -like 'msg.messaging*' } |
        Remove-Item -Recurse -Force

    Step "Generate every feature combination worth checking (Stratum $Version)"
    Invoke-Dotnet $StratumDll new Orders  -o (Join-Path $Work 'full') --package-source $Pkgs;          Pass 'full (default features)'
    Invoke-Dotnet $StratumDll new Billing -o (Join-Path $Work 'auth') --package-source $Pkgs --auth;   Pass 'full + auth'
    Invoke-Dotnet $StratumDll new Catalog -o (Join-Path $Work 'core') --package-source $Pkgs `
        --no-observability --no-openapi --no-resilience --no-tests;                                Pass 'core (framework only)'
    Invoke-Dotnet $StratumDll new --from (Join-Path $Work 'auth\stratum.json') -o (Join-Path $Work 'from') --package-source $Pkgs
    Pass '--from stratum.json'

    Step 'Template sanity'
    $files = 'full', 'auth', 'core', 'from' | ForEach-Object { Get-ChildItem -Path (Join-Path $Work $_) -Recurse -File }
    $leftovers = $files | Select-String -Pattern '@stratum:|\{\{Service\}\}|\{\{service-kebab\}\}|\{\{DevPort\}\}|\{\{StratumVersion\}\}|__Service__'
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
    if ($props -like "*Include=""StratumKit.ServiceDefaults"" Version=""$Version""*") { Pass "service pins Stratum.ServiceDefaults $Version" } else { Fail 'Stratum.ServiceDefaults version' }

    Step "Build 'core' service (TreatWarningsAsErrors)"
    Invoke-Dotnet build (Join-Path $Work 'core') -c Release -v q -nologo
    Pass 'core builds against the local Stratum.ServiceDefaults package with zero warnings'

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
    Invoke-Dotnet $StratumDll upgrade --path $upgraded --to 9.9.9
    $upProps = Get-Content (Join-Path $upgraded 'Directory.Packages.props') -Raw
    $upJson  = Get-Content (Join-Path $upgraded 'stratum.json') -Raw
    if ($upProps -like '*Include="StratumKit.ServiceDefaults" Version="9.9.9"*' -and $upJson -like '*"stratumVersion": "9.9.9"*') {
        Pass 'upgrade bumps Stratum.* packages and stratum.json'
    } else { Fail 'upgrade' }
    Invoke-Dotnet $StratumDll enum add Tier --values "Basic, Gold" --path $upgraded
    $alignedProps = Get-Content (Join-Path $upgraded 'Directory.Packages.props') -Raw
    if ($alignedProps -like "*Include=""StratumKit.ServiceDefaults"" Version=""$Version""*" -and $alignedProps -notlike '*Version="9.9.9"*') {
        Pass "a change made with this Stratum puts every Stratum.* package at $Version"
    } else { Fail 'packages not aligned' }

    Step 'stratum add entity'
    $full = Join-Path $Work 'full'
    Invoke-Dotnet $StratumDll add entity Product  --path $full --props 'Name:string, Price:decimal, Description:string?, Stock:int'
    Invoke-Dotnet $StratumDll add entity Category --path $full --props 'Title:string, Position:int?' --plural Categories
    Invoke-Dotnet $StratumDll add entity Invoice  --path (Join-Path $Work 'auth') --db sqlserver --props 'Number:string, Total:decimal, Paid:bool'
    Pass 'entities added (postgres + sqlserver)'

    $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & dotnet $StratumDll add entity Product --path $full --props 'Name:string' 2>&1 | Out-Null
    $dupCode = $LASTEXITCODE; $ErrorActionPreference = $eap
    if ($dupCode -ne 0) { Pass 'duplicate entity rejected' } else { Fail 'duplicate entity accepted' }

    $reg = Get-Content (Join-Path $full 'src\Orders.Api\Features\FeatureRegistration.g.cs') -Raw
    $pkg = Get-Content (Join-Path $full 'Directory.Packages.props') -Raw
    $dev = Get-Content (Join-Path $full 'src\Orders.Api\appsettings.Development.json') -Raw
    if ($reg -like '*MapProductEndpoints*' -and $pkg -like '*Npgsql.EntityFrameworkCore.PostgreSQL*' -and $dev -like '*"Database"*') {
        Pass 'registration, packages and connection string wired'
    } else { Fail 'entity wiring' }

    Invoke-Dotnet $StratumDll new --from (Join-Path $full 'stratum.json') -o (Join-Path $Work 'regen') --package-source $Pkgs
    Pass 'stratum new --from regenerates the entities'

    Step 'Clean Architecture + DDD'
    $clean = Join-Path $Work 'clean'
    Invoke-Dotnet $StratumDll new Catalog -o $clean --architecture clean-ddd --package-source $Pkgs
    Invoke-Dotnet $StratumDll add entity Product  --path $clean --props 'Name:string, Price:decimal, Description:string?'
    Invoke-Dotnet $StratumDll add entity Category --path $clean --props 'Title:string, Position:int?' --plural Categories
    foreach ($p in 'Domain', 'Application', 'Infrastructure', 'Api') {
        if (-not (Test-Path (Join-Path $clean "src\Catalog.$p\Catalog.$p.csproj"))) { Fail "missing Catalog.$p" }
    }
    $agg  = Get-Content (Join-Path $clean 'src\Catalog.Domain\Products\Product.g.cs') -Raw
    $cmd  = Get-Content (Join-Path $clean 'src\Catalog.Application\Products\ProductCommands.g.cs') -Raw
    $repo = Get-Content (Join-Path $clean 'src\Catalog.Infrastructure\Persistence\RepositoryRegistration.g.cs') -Raw
    if ($agg -like '*AggregateRoot<Guid>*' -and $cmd -like '*ICommandHandler<CreateProduct, Guid>*' -and $repo -like '*AddScoped<IProductRepository, ProductRepository>*') {
        Pass 'Domain / Application / Infrastructure / Api with aggregates, use cases and repositories'
    } else { Fail 'clean-ddd structure' }
    $cleanLeft = Get-ChildItem $clean -Recurse -File | Select-String -Pattern '@stratum:|\{\{Service\}\}|\{\{Entity\}\}|__Entity__'
    if ($cleanLeft) { $cleanLeft | Out-Host; Fail 'unrendered markers in clean-ddd' }
    Pass 'no template markers left in clean-ddd'

    Step 'Editable entities (stratum entity)'
    $cp = Join-Path $clean 'src\Catalog.Domain\Products\Product.cs'
    $gp = Join-Path $clean 'src\Catalog.Domain\Products\Product.g.cs'
    # User code in the partial class: must survive every regeneration.
    $userCode = @'
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
'@
    Set-Content -Path $cp -Value $userCode -Encoding UTF8
    $out = Get-DotnetOutput $StratumDll entity update Product --path $clean --props 'Title:string, Price:decimal, Stock:int' --rename Name=Title
    if ($LASTEXITCODE -ne 0) { $out | Out-Host; Fail 'entity update' }
    if ($out -like '*Product.Name se renombra a Title*' -and $out -like '*Product.Description se elimina*') {
        Pass 'update warns about renamed/removed properties used in user code'
    } else { $out | Out-Host; Fail 'entity update warnings' }
    $g = Get-Content $gp -Raw
    if ((Get-Content $cp -Raw) -like '*Label => *' -and $g -like '*public string Title*' -and $g -notlike '*Description*') {
        Pass 'properties regenerated in .g.cs, user partial untouched'
    } else { Fail 'entity update' }
    if ((Get-Content (Join-Path $clean 'stratum.json') -Raw) -like '*"from": "Name"*') { Pass 'rename recorded in stratum.json (pendingRenames)' } else { Fail 'pendingRenames' }
    Set-Content -Path $cp -Value ($userCode.Replace('{Name} ({Price})', '{Title} ({Price})')) -Encoding UTF8
    $out = Get-DotnetOutput $StratumDll entity update Product --path $clean --props 'Title:string, Price:decimal, Stock:int'
    if ($out -like '*Nada que cambiar*') { Pass 'no-op update changes nothing' } else { $out | Out-Host; Fail 'no-op update' }
    Invoke-Dotnet $StratumDll add entity Tag --path $clean --props 'Label:string'
    Invoke-Dotnet $StratumDll entity remove Tag --path $clean --delete-own-files
    if (Get-ChildItem $clean -Recurse -File -Filter 'Tag*.cs') { Fail 'entity remove left files' }

    # References between entities and aggregate operations (built and tested below with the full services).
    Invoke-Dotnet $StratumDll add entity Review --path $clean --props 'Text:string, Product:Product, ReplyToId:Review?'
    Invoke-Dotnet $StratumDll entity operation add Product Discontinue --params 'Reason:string' --path $clean
    Invoke-Dotnet $StratumDll entity operation add Product Restock --path $clean
    $cr = Join-Path $clean 'src'
    $refOk = (Get-Content (Join-Path $cr 'Catalog.Infrastructure\Persistence\Reviews\ReviewConfiguration.g.cs') -Raw) -like '*HasOne<global::Catalog.Domain.Products.Product>().WithMany().HasForeignKey(e => e.ProductId)*' `
        -and (Get-Content (Join-Path $cr 'Catalog.Application\Reviews\ReviewCommands.g.cs') -Raw) -like '*MissingReferenceAsync*' `
        -and (Get-Content (Join-Path $cr 'Catalog.Infrastructure\Persistence\Products\ProductIntegrity.g.cs') -Raw) -like '*InUseAsync*' `
        -and (Get-Content (Join-Path $cr 'Catalog.Domain\Products\Product.Discontinue.cs') -Raw) -like '*public partial void Discontinue(string reason)*' `
        -and (Get-Content (Join-Path $cr 'Catalog.Api\Features\Products\ProductOperationEndpoints.g.cs') -Raw) -like '*"/{id:guid}/discontinue"*' `
        -and (Get-Content (Join-Path $cr 'Catalog.Domain\Products\ProductOperations.g.cs') -Raw) -like '*record ProductRestocked(Guid ProductId)*'
    $null = Get-DotnetOutput $StratumDll entity remove Product --path $clean   # expected to fail: checked through $LASTEXITCODE
    if ($refOk -and $LASTEXITCODE -ne 0) { Pass 'clean-ddd: references (foreign keys, checks) and aggregate operations' } else { Fail 'clean-ddd references/operations' }
    # Specification and user stories (on a copy: docs/ doesn't change how the service builds).
    $cleanDocs = Join-Path $Work 'clean-docs'
    Copy-Item $clean $cleanDocs -Recurse
    $docs = Join-Path $cleanDocs 'docs'
    Invoke-Dotnet $StratumDll spec --path $cleanDocs
    $story = Get-ChildItem (Join-Path $docs 'historias') -Filter 'CAT-*-product-discontinue.md' -ErrorAction SilentlyContinue | Select-Object -First 1
    $again = Get-DotnetOutput $StratumDll spec --path $cleanDocs
    if ((Test-Path (Join-Path $docs 'especificacion.g.md')) -and (Test-Path (Join-Path $docs 'contexto.md')) -and $story `
        -and (Get-Content (Join-Path $docs 'especificacion.g.md') -Raw) -like '*/api/v1/products/{id}/discontinue*' `
        -and (Get-Content $story.FullName -Raw) -like '*Discontinue de Product*' -and ($again -join "`n") -like '*al d*a*') {
        Pass 'spec: especificacion, contexto e historias con criterios en Gherkin'
    } else { Fail 'spec' }
    Add-Content $story.FullName 'mine'
    Invoke-Dotnet $StratumDll entity operation add Product Archive --path $cleanDocs
    $archived = Get-ChildItem (Join-Path $docs 'historias') -Filter 'CAT-*-product-archive.md' -ErrorAction SilentlyContinue
    if ((Get-Content (Join-Path $docs 'especificacion.g.md') -Raw) -like '*/api/v1/products/{id}/archive*' -and $archived `
        -and (Get-Content $story.FullName -Raw) -like '*mine*') {
        Pass 'spec: a model change updates the specification and adds the new story (the user''s stay)'
    } else { Fail 'spec not kept up to date' }
    Invoke-Dotnet $StratumDll feature on mcp --path $clean
    if ((Get-Content (Join-Path $cr 'Catalog.Api\StratumWiring.g.cs') -Raw) -like '*app.MapMcp("/mcp")*' `
        -and (Get-Content (Join-Path $cr 'Catalog.Api\Features\Products\ProductMcpTools.g.cs') -Raw) -like '*Name = "discontinue_product"*' `
        -and (Get-Content (Join-Path $cr 'Catalog.Api\Features\Reviews\ReviewMcpTools.g.cs') -Raw) -like '*Name = "create_review"*' `
        -and (Get-Content (Join-Path $cr 'Catalog.Api\Catalog.Api.csproj') -Raw) -like '*Include="ModelContextProtocol.AspNetCore"*') {
        Pass 'feature mcp: /mcp with the entities'' CRUD and operations as tools'
    } else { Fail 'feature mcp' }
    $full = Join-Path $Work 'full'
    Invoke-Dotnet $StratumDll add entity Supplier --path $full --props 'Name:string'
    Invoke-Dotnet $StratumDll add entity Shipment --path $full --props 'Code:string, SupplierId:Supplier, ProductId:Product?'
    $vOk = (Get-Content (Join-Path $full 'src\Orders.Api\Features\Shipments\ShipmentEndpoints.g.cs') -Raw) -like '*MissingReferencesAsync*' `
        -and (Get-Content (Join-Path $full 'src\Orders.Api\Features\Suppliers\SupplierEndpoints.g.cs') -Raw) -like '*UsedByAsync*' `
        -and (Get-Content (Join-Path $full 'tests\Orders.Api.Tests\Features\ShipmentEndpointsTests.g.cs') -Raw) -like '*await CreateSupplierAsync(client)*'
    $null = Get-DotnetOutput $StratumDll entity operation add Supplier Approve --path $full   # expected to fail: checked through $LASTEXITCODE
    $opRejected = $LASTEXITCODE -ne 0
    $null = Get-DotnetOutput $StratumDll add entity Bad --path $full --props 'X:Nope'   # expected to fail: checked through $LASTEXITCODE
    if ($vOk -and $opRejected -and $LASTEXITCODE -ne 0) { Pass 'vertical slice: references with 400/409 checks and test data' } else { Fail 'vertical references' }
    Invoke-Dotnet $StratumDll enum add ShipmentState --values 'pending, Sent, Delivered' --path $full
    Invoke-Dotnet $StratumDll entity update Shipment --path $full --props 'Code:string, SupplierId:Supplier, ProductId:Product?, State:ShipmentState, Previous:ShipmentState?'
    Invoke-Dotnet $StratumDll enum add ProductLine --values 'Basic, Premium' --path $clean
    Invoke-Dotnet $StratumDll entity operation add Product Reclassify --params 'Line:ProductLine' --path $clean
    $enumOk = (Get-Content (Join-Path $full 'src\Orders.Api\Features\Enums.g.cs') -Raw) -like '*public enum ShipmentState*' `
        -and (Get-Content (Join-Path $full 'src\Orders.Api\Features\Shipments\ShipmentConfiguration.g.cs') -Raw) -like '*HasConversion<string>()*' `
        -and (Get-Content (Join-Path $full 'tests\Orders.Api.Tests\Orders.Api.Tests.csproj') -Raw) -like '*<Using Include="Orders.Api.Features" />*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Domain\Enums.g.cs') -Raw) -like '*public enum ProductLine*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Domain\Products\Product.Reclassify.cs') -Raw) -like '*ProductLine line*'
    $null = Get-DotnetOutput $StratumDll enum remove ShipmentState --path $full   # expected to fail: in use
    if ($enumOk -and $LASTEXITCODE -ne 0) { Pass 'enums: C# enum, stored as text, usable in properties and operations' } else { Fail 'enums' }
    # Lists: of basic types, of enums and of objects (item types, own table, replaced on update). The tests of "full"
    # and "clean" (run at the end) create, read and update them against a real database.
    Invoke-Dotnet $StratumDll type add ShipmentLine --props 'Sku:string, Quantity:int, State:ShipmentState, Note:string?' --path $full
    Invoke-Dotnet $StratumDll entity update Shipment --path $full --props 'Code:string, SupplierId:Supplier, ProductId:Product?, State:ShipmentState, Previous:ShipmentState?, Tags:string[], States:ShipmentState[], Lines:ShipmentLine[]'
    Invoke-Dotnet $StratumDll type add BundleLine --props 'Sku:string, Price:decimal, Line:ProductLine' --path $clean
    Invoke-Dotnet $StratumDll add entity Bundle --path $clean --props 'Name:string, Codes:int[], Lines:BundleLine[]'
    $shipConfig = Get-Content (Join-Path $full 'src\Orders.Api\Features\Shipments\ShipmentConfiguration.g.cs') -Raw
    $listsOk = (Get-Content (Join-Path $full 'src\Orders.Api\Features\Types.g.cs') -Raw) -like '*public sealed class ShipmentLine*' `
        -and $shipConfig -like '*OwnsMany(e => e.Lines*' -and $shipConfig -like '*ToTable("shipment_lines")*' -and $shipConfig -like '*PrimitiveCollection(e => e.States)*' `
        -and (Get-Content (Join-Path $full 'src\Orders.Api\Features\Shipments\ShipmentContracts.g.cs') -Raw) -like '*IReadOnlyList<ShipmentLineContract> Lines*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Domain\Types.g.cs') -Raw) -like '*public sealed class BundleLine*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Domain\Bundles\Bundle.g.cs') -Raw) -like '*Lines = `[.. lines`];*'
    $null = Get-DotnetOutput $StratumDll type remove ShipmentLine --path $full   # expected to fail: in use
    $removeFailed = $LASTEXITCODE -ne 0
    $null = Get-DotnetOutput $StratumDll add entity Bad --path $full --props 'Tags:string[]?'   # expected to fail: optional list
    if ($listsOk -and $removeFailed -and $LASTEXITCODE -ne 0) { Pass 'lists: string[], enum[] and item types (own table, replaced on update)' } else { Fail 'lists' }
    # Relations between entities (1-N, 1-1, what deleting does) and compositions (one item inside the entity).
    Invoke-Dotnet $StratumDll type add PostalAddress --props 'Street:string, City:string, Zip:string?' --path $full
    Invoke-Dotnet $StratumDll add entity Profile --path $full --props 'Bio:string?'
    Invoke-Dotnet $StratumDll relation add Profile Supplier --one-to-one --on-delete cascade --path $full
    Invoke-Dotnet $StratumDll relation add Shipment.CarrierId Supplier --optional --on-delete set-null --path $full
    Invoke-Dotnet $StratumDll relation compose Supplier.Address PostalAddress --path $full
    Invoke-Dotnet $StratumDll relation compose Supplier.Billing PostalAddress --optional --path $full
    Invoke-Dotnet $StratumDll type add Dimensions --props 'Width:decimal, Height:decimal' --path $clean
    Invoke-Dotnet $StratumDll add entity Manual --path $clean --props 'Title:string'
    Invoke-Dotnet $StratumDll relation add Manual Product --one-to-one --on-delete cascade --path $clean
    Invoke-Dotnet $StratumDll relation compose Product.Size Dimensions --optional --path $clean
    $rf = Join-Path $full 'src\Orders.Api\Features'
    $supplierConfig = Get-Content (Join-Path $rf 'Suppliers\SupplierConfiguration.g.cs') -Raw
    $relOk = (Get-Content (Join-Path $rf 'Profiles\ProfileConfiguration.g.cs') -Raw) -like '*WithOne().HasForeignKey<global::Orders.Api.Features.Profiles.Profile>(e => e.SupplierId).OnDelete(DeleteBehavior.Cascade)*' `
        -and (Get-Content (Join-Path $rf 'Shipments\ShipmentConfiguration.g.cs') -Raw) -like '*HasForeignKey(e => e.CarrierId).OnDelete(DeleteBehavior.SetNull)*' `
        -and $supplierConfig -like '*OwnsOne(e => e.Address*' -and $supplierConfig -like '*Navigation(e => e.Address).IsRequired()*' `
        -and (Get-Content (Join-Path $rf 'Profiles\ProfileEndpoints.g.cs') -Raw) -like '*DuplicateReferencesAsync*' `
        -and (Get-Content (Join-Path $rf 'Shipments\ShipmentEndpoints.g.cs') -Raw) -like '*Guid? carrierId*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Infrastructure\Persistence\Manuals\ManualIntegrity.g.cs') -Raw) -like '*DuplicateReferenceAsync*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Infrastructure\Persistence\Products\ProductConfiguration.g.cs') -Raw) -like '*OwnsOne(e => e.Size*' `
        -and ((Get-DotnetOutput $StratumDll relation list --path $full) -join "`n") -like '*Profile.SupplierId*Supplier*1*1*'
    $null = Get-DotnetOutput $StratumDll relation add Shipment.OtherId Supplier --on-delete set-null --path $full   # expected to fail: set-null needs optional
    if ($relOk -and $LASTEXITCODE -ne 0) { Pass 'relations: 1-1 (unique), on delete cascade / set null, ?supplierId= filter, compositions (OwnsOne)' } else { Fail 'relations' }
    # Validation rules (stratum rule): length, format, pattern and range, in the API (400), the domain, the column and the tests.
    Invoke-Dotnet $StratumDll rule set Supplier.Name --min-length 2 --max-length 80 --path $full
    Invoke-Dotnet $StratumDll rule set Product.Stock --min 0 --max 10000 --path $full
    Invoke-Dotnet $StratumDll rule set PostalAddress.Zip --pattern '^[0-9]{5}$' --example 28001 --path $full
    Invoke-Dotnet $StratumDll rule set Category.Title --max-length 60 --format email --path $clean
    Invoke-Dotnet $StratumDll rule set Product.Price --min 0.01 --max 99999.99 --path $clean
    $rulesOk = (Get-Content (Join-Path $rf 'Suppliers\SupplierContracts.g.cs') -Raw) -like '*`[property: Required, MinLength(2), MaxLength(80)`] string Name*' `
        -and (Get-Content (Join-Path $rf 'Suppliers\SupplierConfiguration.g.cs') -Raw) -like '*HasMaxLength(80)*' `
        -and (Get-Content (Join-Path $rf 'Products\ProductContracts.g.cs') -Raw) -like '*Range(typeof(int), "0", "10000"*' `
        -and (Get-Content (Join-Path $full 'src\Orders.Api\Features\Types.g.cs') -Raw) -like '*RegularExpression(@"^`[0-9`]{5}$")*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Infrastructure\Persistence\Categories\CategoryConfiguration.g.cs') -Raw) -like '*HasMaxLength(60)*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Domain\Categories\Category.g.cs') -Raw) -like '*EmailAddressAttribute().IsValid*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Domain\Products\Product.g.cs') -Raw) -like '*is < 0.01m or > 99999.99m*' `
        -and ((Get-DotnetOutput $StratumDll rule list --path $full) -join "`n") -like '*Supplier.Name*2*80*'
    $null = Get-DotnetOutput $StratumDll rule set Supplier.Name --pattern '^[A-Z]+$' --path $full   # expected to fail: a pattern needs an example
    $patternFailed = $LASTEXITCODE -ne 0
    $null = Get-DotnetOutput $StratumDll rule set Product.Stock --min-length 3 --path $full   # expected to fail: length on a number
    if ($rulesOk -and $patternFailed -and $LASTEXITCODE -ne 0) { Pass 'validation rules: attributes (400), domain guards, column length, samples; bad rules rejected' } else { Fail 'validation rules' }
    # Lists: ?sort=field|-field and ?search=text (vertical: ListOptions; clean-ddd: the Lister of Infrastructure).
    $listOptions = Get-Content (Join-Path $rf 'Suppliers\SupplierListOptions.g.cs') -Raw
    $listOk = $listOptions -like '*SortFields { get; } = `["name", "createdAt", "updatedAt"`]*' -and $listOptions -like '*rows.Where(e => e.Name.ToLower().Contains(term))*' `
        -and (Get-Content (Join-Path $rf 'Suppliers\SupplierEndpoints.g.cs') -Raw) -like '*SupplierListOptions.Problems(sort, search)*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Infrastructure\Persistence\Products\ProductLister.g.cs') -Raw) -like '*"-price" => rows.OrderByDescending(e => e.Price)*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Infrastructure\Persistence\RepositoryRegistration.g.cs') -Raw) -like '*IProductLister, global::Catalog.Infrastructure.Persistence.Products.ProductLister*' `
        -and (Get-Content (Join-Path $full 'tests\Orders.Api.Tests\Features\SupplierEndpointsTests.g.cs') -Raw) -like '*ListSearchesAndSorts*' `
        -and (Get-Content (Join-Path $full 'src\Orders.Api\Orders.Api.http') -Raw) -like '*search=sample&sort=name*'
    if ($listOk) { Pass 'lists: sort (-field descending) and search in text fields, 400 for an unknown field' } else { Fail 'lists: sort and search' }
    # Operations on lists (clean-ddd): one item or a list of items as parameters, with a head start for the rule.
    Invoke-Dotnet $StratumDll entity operation add Bundle AddLine --params 'Line:BundleLine' --path $clean
    Invoke-Dotnet $StratumDll entity operation add Bundle ReplaceLines --params 'Lines:BundleLine[]' --path $clean
    $opsOk = (Get-Content (Join-Path $clean 'src\Catalog.Domain\Bundles\Bundle.AddLine.cs') -Raw) -like '*Lines.Add(line);*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Domain\Bundles\Bundle.ReplaceLines.cs') -Raw) -like '*Lines = `[.. lines`];*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Domain\Bundles\BundleOperations.g.cs') -Raw) -like '*record BundleLineAdded(Guid BundleId, BundleLine Line)*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Api\Features\Bundles\BundleOperationEndpoints.g.cs') -Raw) -like '*`[property: Required`] BundleLineContract Line*'
    if ($opsOk) { Pass 'operations on lists: AddLine(Line:BundleLine) -> BundleLineAdded, with the rule started' } else { Fail 'operations on lists' }
    # Best practices of dotnet/skills (dotnet-webapi, test-anti-patterns) and instructions for coding agents.
    $bpOk = (Get-Content (Join-Path $full 'src\Orders.Api\Features\Shipments\ShipmentEndpoints.g.cs') -Raw) -like '*.WithSummary("Create a Shipment")*' `
        -and (Get-Content (Join-Path $full 'src\Orders.Api\Orders.Api.csproj') -Raw) -like '*<GenerateDocumentationFile>true</GenerateDocumentationFile>*' `
        -and (Get-Content (Join-Path $full 'src\Orders.Api\Program.cs') -Raw) -like '*JsonNumberHandling.Strict*' `
        -and (Get-Content (Join-Path $full 'src\Orders.Api\Features\Types.g.cs') -Raw) -like '*public sealed record ShipmentLineContract(*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Api\Features\Bundles\BundleContracts.g.cs') -Raw) -like '*IReadOnlyList<BundleLineContract> Lines*' `
        -and (Get-Content (Join-Path $full 'src\Orders.Api\Orders.Api.http') -Raw) -like '*# @name createShipment*' `
        -and (Get-Content (Join-Path $full 'tests\Orders.Api.Tests\Features\ShipmentEndpointsTests.g.cs') -Raw) -like '*UpdateReplacesTheData*' `
        -and (Get-Content (Join-Path $clean 'AGENTS.md') -Raw) -like '*Never edit them*' -and (Get-Content (Join-Path $clean 'CLAUDE.md') -Raw) -like '*@AGENTS.md*'
    if ($bpOk) { Pass 'best practices: OpenAPI metadata, strict JSON, item contracts, .http, focused tests, AGENTS.md' } else { Fail 'best practices' }
    if ((Get-Content (Join-Path $clean 'src\Catalog.Infrastructure\Persistence\CatalogDbContext.g.cs') -Raw) -like '*Tag*') { Fail 'Tag still in DbContext' }
    Pass 'entity remove deletes its files and registrations'
    # An entity from Stratum < 0.10 (no "layout" in stratum.json) is read-only until converted.
    $defPath = Join-Path $full 'stratum.json'
    $def = Get-Content $defPath -Raw | ConvertFrom-Json
    foreach ($e in $def.entities) { if ($e.name -eq 'Category') { $e.PSObject.Properties.Remove('layout') } }
    [System.IO.File]::WriteAllText($defPath, ($def | ConvertTo-Json -Depth 10))
    $out = Get-DotnetOutput $StratumDll entity update Category --path $full --props 'Title:string'
    if ($LASTEXITCODE -eq 0) { Fail 'legacy entity edited without converting' }
    $out = Get-DotnetOutput $StratumDll entity convert Category --path $full --dry-run
    if ($out -notlike '*(archivo tuyo)*') { $out | Out-Host; Fail 'entity convert --dry-run' }
    Invoke-Dotnet $StratumDll entity convert Category --path $full
    Invoke-Dotnet $StratumDll entity update Category --path $full --props 'Title:string, Position:int?, Color:string?'
    Pass 'legacy entity: blocked, converted (shows user files), then editable'

    Step 'Features on/off in an existing service (stratum feature)'
    $fs = Join-Path $Work 'features'
    Invoke-Dotnet $StratumDll new Shop -o $fs --package-source $Pkgs
    Invoke-Dotnet $StratumDll add entity Product --path $fs --props 'Name:string'
    Invoke-Dotnet $StratumDll feature off docker openApi --path $fs
    $apiProj = Join-Path $fs 'src\Shop.Api\Shop.Api.csproj'
    $wiring = Join-Path $fs 'src\Shop.Api\StratumWiring.g.cs'
    if (-not (Test-Path (Join-Path $fs 'Dockerfile')) -and (Get-Content $apiProj -Raw) -notlike '*Scalar.AspNetCore*' -and (Get-Content $wiring -Raw) -notlike '*Scalar.AspNetCore*' -and (Get-Content (Join-Path $fs 'src\Shop.Api\Program.cs') -Raw) -like '*AddStratumWiring()*') {
        Pass 'feature off removes its files, packages and wiring (Program.cs untouched)'
    } else { Fail 'feature off' }
    Invoke-Dotnet $StratumDll feature on docker openApi --path $fs
    if ((Test-Path (Join-Path $fs 'Dockerfile')) -and (Get-Content $apiProj -Raw) -like '*Include="Scalar.AspNetCore"*' -and (Get-Content $wiring -Raw) -like '*MapScalarApiReference*') {
        Pass 'feature on brings them back'
    } else { Fail 'feature on' }
    $w = Get-Content $wiring -Raw
    [System.IO.File]::WriteAllText($wiring, $w.Replace('builder.AddFeatures();', 'builder.AddFeatures(); // tweak'))
    $out = Get-DotnetOutput $StratumDll feature on auth --path $fs --dry-run
    if ($out -like '*Has editado a mano*') { Pass 'a hand-edited .g.cs is detected before overwriting it' } else { $out | Out-Host; Fail 'hand-edit detection' }
    Invoke-Dotnet $StratumDll feature on auth --path $fs
    $lp = Join-Path $Work 'legacy-program'
    Copy-Item -Recurse $fs $lp
    $legacyProgram = @'
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
'@
    Set-Content -Path (Join-Path $lp 'src\Shop.Api\Program.cs') -Value $legacyProgram -Encoding UTF8
    $out = Get-DotnetOutput $StratumDll feature off docker --path $lp
    if ($LASTEXITCODE -eq 0) { Fail 'features changed with a Program.cs from before 0.12' }
    Invoke-Dotnet $StratumDll feature convert --path $lp
    if ((Get-Content (Join-Path $lp 'src\Shop.Api\Program.cs') -Raw) -notlike '*AddStratumWiring()*') { Fail 'feature convert' }
    Invoke-Dotnet $StratumDll feature off docker --path $lp
    Pass 'Program.cs from before 0.12: blocked, converted, then editable'

    Step 'Messaging (stratum messaging / stratum events)'
    # Messaging follows usage: publishing turns it on (no order to remember).
    $out = Get-DotnetOutput $StratumDll events on Product --path $fs
    if ($LASTEXITCODE -eq 0 -and $out -like '*Shop con kafka*') { Pass 'events on without messaging turns messaging on' } else { Fail "events on: $out" }
    Invoke-Dotnet $StratumDll messaging on --broker kafka --path $fs
    $wiringCode = Get-Content $wiring -Raw
    $devSettings = Join-Path $fs 'src\Shop.Api\appsettings.Development.json'
    if ($wiringCode -like '*UseKafka(builder.Configuration)*' -and $wiringCode -like '*UseEntityFramework<ShopDbContext>*' -and (Get-Content $apiProj -Raw) -like '*Include="StratumKit.Messaging.Kafka"*' -and (Get-Content $devSettings -Raw) -like '*"kafka": "localhost:9092"*' -and (Get-Content (Join-Path $fs 'src\Shop.Api\Data\MessagingConfiguration.g.cs') -Raw) -like '*MessagingModel.ConfigureOutbox*') {
        Pass 'messaging on: Kafka, outbox/inbox tables, packages and connection string'
        if ((Get-Content (Join-Path $fs 'src\Shop.Api\Data\MessagingConfiguration.g.cs') -Raw) -like '*MessagingModel.ConfigureReceived*' -and (Get-Content $wiring -Raw) -like '*app.MapStratumMessagingDiagnostics()*') {
            Pass 'event traces: msg_received journal and GET /_stratum/messaging (Development)'
        } else { Fail 'event traces wiring' }
    } else { Fail 'messaging on' }
    Get-Content $devSettings -Raw | ConvertFrom-Json | Out-Null
    if ((Get-Content (Join-Path $fs 'src\Shop.Api\IntegrationEvents\ProductIntegrationEvents.g.cs') -Raw) -like '*IntegrationEvent("shop.product-created")*' -and (Get-Content (Join-Path $fs 'src\Shop.Api\Features\Products\ProductEndpoints.g.cs') -Raw) -like '*PublishAsync(new ProductCreated*') {
        Pass 'vertical slice: Product events published from the endpoints (outbox)'
    } else { Fail 'events on (vertical)' }
    Invoke-Dotnet $StratumDll messaging on --broker kafka --path $clean
    Invoke-Dotnet $StratumDll events on Product Bundle --path $clean
    if ((Get-Content (Join-Path $clean 'src\Catalog.Infrastructure\Persistence\Products\ProductIntegrationEventsMapper.g.cs') -Raw) -like '*ProductIntegrationEventsMapper : IIntegrationEventMapper*' -and (Get-Content (Join-Path $clean 'src\Catalog.Application\IntegrationEvents\ProductIntegrationEvents.g.cs') -Raw) -like '*IntegrationEvent("catalog.product-created")*') {
        Pass 'clean + DDD: domain events mapped to integration events'
    } else { Fail 'events on (clean)' }

    Invoke-Dotnet $StratumDll subscribe $fs Product --path $clean
    Invoke-Dotnet $StratumDll subscribe $clean Product.Created --path $fs
    Invoke-Dotnet $StratumDll subscribe $clean Bundle.Updated --path $fs
    # A published operation: its own integration event (ProductReclassified) besides ProductUpdated.
    Invoke-Dotnet $StratumDll entity operation publish Product Reclassify --path $clean
    Invoke-Dotnet $StratumDll subscribe $clean Product.Reclassified --path $fs
    $mapper = Get-Content (Join-Path $clean 'src\Catalog.Infrastructure\Persistence\Products\ProductIntegrationEventsMapper.g.cs') -Raw
    $mapOut = Get-DotnetOutput $StratumDll events map --path $Work
    if ((Get-Content (Join-Path $clean 'src\Catalog.Application\IntegrationEvents\ProductIntegrationEvents.g.cs') -Raw) -like '*IntegrationEvent("catalog.product-reclassified")*' `
        -and $mapper -like '*(ProductReclassified operation, Product entity) => `[new Integration.ProductUpdated(*' -and $mapper -like '*new Integration.ProductReclassified(entity.Id*' `
        -and (Get-Content (Join-Path $fs 'src\Shop.Api\IntegrationEvents\Catalog\ProductReclassified.g.cs') -Raw) -like '*string Line) : IntegrationEvent*' `
        -and $mapOut -match 'catalog\.product-reclassified .*Shop') {
        Pass 'published operation: ProductReclassified with its own topic, subscribable'
    } else { $mapOut | Out-Host; Fail 'published operation' }
    $bundleContract = Get-Content (Join-Path $fs 'src\Shop.Api\IntegrationEvents\Catalog\BundleUpdated.g.cs') -Raw
    if ($bundleContract -like '*IReadOnlyList<BundleUpdated.BundleLine> Lines*' -and $bundleContract -like '*public sealed record BundleLine(string Sku, decimal Price, string Line);*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Infrastructure\Persistence\Bundles\BundleIntegrationEventsMapper.g.cs') -Raw) -like '*new Integration.BundleUpdated.BundleLine(i.Sku, i.Price, i.Line.ToString())*') {
        Pass 'events with lists: the item records travel inside the contract'
    } else { Fail 'events with lists' }
    if ((Get-Content (Join-Path $clean 'src\Catalog.Application\IntegrationEvents\Shop\ProductDeleted.g.cs') -Raw) -like '*IntegrationEvent("shop.product-deleted")*' -and (Get-Content (Join-Path $clean 'src\Catalog.Application\Events\Shop\ProductCreatedHandler.cs') -Raw) -like '*IIntegrationEventHandler<ProductCreated>*' -and (Get-Content (Join-Path $fs 'src\Shop.Api\IntegrationEvents\Catalog\ProductCreated.g.cs') -Raw) -like '*IntegrationEvent("catalog.product-created")*' -and (Get-Content (Join-Path $fs 'src\Shop.Api\Events\Catalog\ProductCreatedHandler.cs') -Raw) -like '*IIntegrationEventHandler<ProductCreated>*') {
        Pass 'subscribe: contract copied and handler created (both architectures)'
    } else { Fail 'subscribe' }
    $out = Get-DotnetOutput $StratumDll events map --path $Work
    if ($out -match 'shop\.product-created .*-> Catalog' -or $out -match 'shop\.product-created .*Catalog') { Pass 'events map shows who publishes and who listens' } else { $out | Out-Host; Fail 'events map' }
    Invoke-Dotnet $StratumDll entity update Product --path $fs --props 'Name:string, Sku:string?'
    $out = Get-DotnetOutput $StratumDll events map --path $Work
    $sync = Get-DotnetOutput $StratumDll subscribe --sync --path $clean
    if ($out -like '*ha cambiado los datos de ProductCreated*' -and $sync -like '*IntegrationEvents/Shop/ProductCreated.g.cs*' -and (Get-Content (Join-Path $clean 'src\Catalog.Application\IntegrationEvents\Shop\ProductCreated.g.cs') -Raw) -like '*string? Sku*') {
        Pass 'a changed contract is detected and --sync copies it again'
    } else { Fail 'subscribe --sync' }
    Invoke-Dotnet $StratumDll events off Product --path $fs
    Invoke-Dotnet $StratumDll subscribe --sync --prune --path $clean
    if (-not (Test-Path (Join-Path $clean 'src\Catalog.Application\IntegrationEvents\Shop')) -and (Get-Content (Join-Path $clean 'stratum.json') -Raw) -notlike '*"service": "Shop"*') {
        Pass '--prune drops subscriptions to events no longer published'
    } else { Fail 'subscribe --prune' }
    Invoke-Dotnet $StratumDll events on Product --path $fs
    # Replicas: a local copy of another service's entity, kept up to date with its Created/Updated/Deleted events.
    Invoke-Dotnet $StratumDll replica add $fs Product --fields 'Name' --path $clean
    Invoke-Dotnet $StratumDll replica add $clean Product --path $fs
    $store = Get-Content (Join-Path $clean 'src\Catalog.Infrastructure\Persistence\Replicas\Shop\ProductReplicaStore.g.cs') -Raw
    $replicaOk = $store -like '*IIntegrationEventHandler<ProductDeleted>*' -and $store -like '*builder.ToTable("replica_products")*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Application\Replicas\Shop\ProductReplica.g.cs') -Raw) -like '*public interface IProductReplicas*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Infrastructure\Persistence\RepositoryRegistration.g.cs') -Raw) -like '*IProductReplicas, global::Catalog.Infrastructure.Persistence.Replicas.Shop.ProductReplicaStore*' `
        -and (Get-Content (Join-Path $fs 'src\Shop.Api\Replicas\Catalog\ProductReplicaHandler.g.cs') -Raw) -like '*replica.LastEventAt > occurredAt*' `
        -and (Get-Content (Join-Path $fs 'src\Shop.Api\Data\ShopDbContext.g.cs') -Raw) -like '*DbSet<global::Shop.Api.Replicas.Catalog.ProductReplica> ProductReplicas*' `
        -and -not (Test-Path (Join-Path $fs 'src\Shop.Api\Events\Catalog\ProductUpdatedHandler.cs')) -and (Test-Path (Join-Path $fs 'src\Shop.Api\Events\Catalog\ProductCreatedHandler.cs')) `
        -and ((Get-DotnetOutput $StratumDll replica list --path $fs) -join "`n") -like '*Catalog.Product*'
    $null = Get-DotnetOutput $StratumDll replica add $fs Product --fields 'Nope' --path $full   # expected to fail
    $badFieldFailed = $LASTEXITCODE -ne 0
    Invoke-Dotnet $StratumDll unsubscribe Catalog Product.Created --path $fs
    $keptForReplica = (Get-Content (Join-Path $fs 'stratum.json') -Raw) -like '*"handler": false*'
    Invoke-Dotnet $StratumDll subscribe $clean Product.Created --path $fs
    if ($replicaOk -and $badFieldFailed -and $keptForReplica) { Pass 'replicas: table, handler of Created/Updated/Deleted, reader in clean-ddd; your handlers stay apart' } else { Fail 'replicas' }
    $loaderOk = (Get-Content (Join-Path $fs 'src\Shop.Api\Replicas\Catalog\ProductReplicaLoader.g.cs') -Raw) -like '*protected override string Route => "products";*' `
        -and (Get-Content (Join-Path $fs 'src\Shop.Api\Replicas\ReplicaLoading.g.cs') -Raw) -like '*services.AddHostedService(sp => sp.GetRequiredService<global::Shop.Api.Replicas.Catalog.ProductReplicaLoader>());*' `
        -and (Get-Content (Join-Path $fs 'src\Shop.Api\StratumWiring.g.cs') -Raw) -like '*builder.Services.AddReplicaLoading();*' `
        -and (Get-Content (Join-Path $fs 'src\Shop.Api\StratumWiring.g.cs') -Raw) -like '*app.MapReplicaLoading()*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Infrastructure\Persistence\Replicas\Shop\ProductReplicaLoader.g.cs') -Raw) -like '*class ProductReplicaLoader*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Infrastructure\Persistence\Replicas\ReplicaLoading.g.cs') -Raw) -like '*global::Catalog.Infrastructure.Persistence.Replicas.Shop.ProductReplicaLoader*'
    if ($loaderOk) { Pass "replicas: initial load from the publisher's API (hosted service + Development endpoint)" } else { Fail 'replica initial load' }
    # Contract changes: the publisher is warned about the services that listen (field retyped, event no longer published).
    $contract = (Get-DotnetOutput $StratumDll entity update Product --props 'Title:string, Price:string, Stock:int, Size:Dimensions?' --path $clean --dry-run) -join "`n"
    $contractOff = (Get-DotnetOutput $StratumDll events off Product --path $clean --dry-run) -join "`n"
    $compatible = (Get-DotnetOutput $StratumDll entity update Product --props 'Title:string, Price:decimal, Stock:int, Size:Dimensions?, Color:string?' --path $clean --dry-run) -join "`n"
    if ($contract -like '*Shop*ProductCreated.Price*decimal*string*' -and $contractOff -like '*ProductUpdated*Shop*' -and $contractOff -like '*subscribe --sync --prune*Shop*' -and $compatible -like '*Shop*') {
        Pass 'contract changes: the publisher is warned about what breaks in the services that listen'
    } else { Fail 'contract changes' }

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
    $out = Get-DotnetOutput $StratumDll migration review --path $mig --dry-run
    if ($LASTEXITCODE -eq 0 -and $out -like '*RenameColumn*' -and $out -like '*Borra la tabla tags*' -and (Get-Content $mf -Raw) -notlike '*RenameColumn*') {
        Pass 'review --dry-run reports the rename fix and the dropped table without writing'
    } else { $out | Out-Host; Fail 'migration review --dry-run' }
    Invoke-Dotnet $StratumDll migration review --path $mig
    $fixed = Get-Content $mf -Raw
    $upPart = $fixed.Substring(0, $fixed.IndexOf('void Down'))
    if ($fixed -like '*migrationBuilder.RenameColumn*' -and $upPart -notlike '*AddColumn*' -and (Get-Content (Join-Path $mig 'stratum.json') -Raw) -notlike '*pendingRenames*') {
        Pass 'review turns drop + add into RenameColumn and clears pendingRenames'
    } else { $fixed | Out-Host; Fail 'migration review' }

    Step 'N-M relations'
    Invoke-Dotnet $StratumDll relation add Supplier Product --many --on-delete cascade --path $full
    Invoke-Dotnet $StratumDll relation add Product Category --many --path $clean
    $supplierEndpoints = Get-Content (Join-Path $rf 'Suppliers\SupplierEndpoints.g.cs') -Raw
    $manyOk = (Get-Content (Join-Path $full 'stratum.json') -Raw) -like '*"name": "ProductIds"*' `
        -and $supplierEndpoints -like '*e.ProductIds.Contains(productId.Value)*' `
        -and $supplierEndpoints -like '*var foundProductIds = await db.Products.Where(e => request.ProductIds.Contains(e.Id))*' `
        -and (Get-Content (Join-Path $rf 'Products\ProductEndpoints.g.cs') -Raw) -like '*row.ProductIds.Remove(id);*' `
        -and (Get-Content (Join-Path $full 'tests\Orders.Api.Tests\Features\SupplierEndpointsTests.g.cs') -Raw) -like '*UnknownIdInAManyToManyListIsRejected*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Infrastructure\Persistence\Categories\CategoryIntegrity.g.cs') -Raw) -like '*e.CategoryIds.Contains(id)*' `
        -and (Get-Content (Join-Path $clean 'src\Catalog.Infrastructure\Persistence\Products\ProductLister.g.cs') -Raw) -like '*e.CategoryIds.Contains(query.CategoryId.Value)*'
    $null = Get-DotnetOutput $StratumDll relation add Product Category --many --optional --path $clean   # expected to fail: N-M is never optional
    if ($manyOk -and $LASTEXITCODE -ne 0) { Pass 'N-M relations: list of ids checked to exist, ?productId= filter, restrict (409) or leave the list on delete' } else { Fail 'N-M relations' }

    Step 'Team templates (.stratum)'
    $team = Join-Path $Work 'team'
    New-Item -ItemType Directory -Path (Join-Path $team 'services') | Out-Null
    Invoke-Dotnet $StratumDll templates eject service/src/__Service__.Api/Program.cs --path $team
    $teamProgram = Join-Path $team '.stratum\templates\service\src\__Service__.Api\Program.cs'
    Set-Content -Path $teamProgram -Value (@('// {{Company}} conventions') + (Get-Content $teamProgram)) -Encoding utf8
    Set-Content -Path (Join-Path $team '.stratum\config.json') -Value '{ "tokens": { "Company": "Acme" } }' -Encoding utf8
    Set-Content -Path (Join-Path $team '.stratum\templates\service\CODEOWNERS') -Value '* @{{Company}}/backend' -Encoding utf8
    New-Item -ItemType File -Path (Join-Path $team '.stratum\templates\service\Dockerfile') -Force | Out-Null
    $teamOrders = Join-Path $team 'services\orders'
    Invoke-Dotnet $StratumDll new Orders -o $teamOrders --package-source $Pkgs
    Invoke-Dotnet $StratumDll add entity Product --path $teamOrders --props 'Name:string'
    $firstLine = Get-Content (Join-Path $teamOrders 'src\Orders.Api\Program.cs') -TotalCount 1
    $owners = (Get-Content (Join-Path $teamOrders 'CODEOWNERS') -Raw).Trim()
    if ($firstLine -like '*Acme conventions*' -and $owners -eq '* @Acme/backend' -and -not (Test-Path (Join-Path $teamOrders 'Dockerfile'))) {
        Pass 'team templates: replace, add, remove and tokens (found walking up)'
    } else { Fail "team templates (first line: $firstLine, CODEOWNERS: $owners)" }

    Step 'stratum aspire'
    $sys = Join-Path $Work 'system'
    New-Item -ItemType Directory -Path $sys | Out-Null
    Invoke-Dotnet $StratumDll new Orders  -o (Join-Path $sys 'orders')  --package-source $Pkgs
    Invoke-Dotnet $StratumDll add entity Order --path (Join-Path $sys 'orders') --props 'Number:string, Total:decimal'
    Invoke-Dotnet $StratumDll new Billing -o (Join-Path $sys 'billing') --architecture clean-ddd --package-source $Pkgs
    Invoke-Dotnet $StratumDll add entity Invoice --path (Join-Path $sys 'billing') --db sqlserver --props 'Number:string'
    Invoke-Dotnet $StratumDll messaging on --broker kafka --path (Join-Path $sys 'orders')
    Invoke-Dotnet $StratumDll events on Order --path (Join-Path $sys 'orders')
    Invoke-Dotnet $StratumDll aspire --path $sys --name Shop
    foreach ($f in 'Shop.AppHost\Shop.AppHost.csproj', 'Shop.AppHost\AppHost.cs', 'Shop.AppHost\StratumServices.g.cs', 'Shop.slnx', '.aspire\settings.json') {
        if (-not (Test-Path (Join-Path $sys $f))) { Fail "missing $f" }
    }
    $hostCode = Get-Content (Join-Path $sys 'Shop.AppHost\StratumServices.g.cs') -Raw
    if ($hostCode -like '*AddKafka("kafka")*' -and $hostCode -like '*WithKafkaUI*' -and (Get-Content (Join-Path $sys 'Shop.AppHost\Shop.AppHost.csproj') -Raw) -like '*Aspire.Hosting.Kafka*') {
        Pass 'AppHost: Kafka + Kafka UI for the services with messaging'
    } else { Fail 'AppHost Kafka' }
    if ($hostCode -like '*AddProject<Projects.Orders_Api>("orders")*' -and $hostCode -like '*connectionName: "Database"*' -and $hostCode -like '*AddPostgres*' -and $hostCode -like '*AddSqlServer*') {
        Pass 'AppHost with both services, their databases and health checks'
    } else { Fail 'AppHost content' }
    $again = (& dotnet $StratumDll aspire --path $sys --name Shop | Out-String)
    if ($again -notmatch '(?m)^  [+~]') { Pass 'stratum aspire is idempotent' } else { Fail 'stratum aspire rewrote files' }
    $elsewhere = Join-Path $Work 'elsewhere\payments'
    Invoke-Dotnet $StratumDll new Payments -o $elsewhere --package-source $Pkgs
    Invoke-Dotnet $StratumDll workspace add $elsewhere --path $sys
    Invoke-Dotnet $StratumDll aspire --path $sys --name Shop
    $out = Get-DotnetOutput $StratumDll workspace list --path $sys
    if ((Get-Content (Join-Path $sys 'stratum.workspace.json') -Raw) -like '*../elsewhere/payments*' -and (Get-Content (Join-Path $sys 'Shop.AppHost\Shop.AppHost.csproj') -Raw) -like '*elsewhere\payments\src\Payments.Api\Payments.Api.csproj*' -and (Get-Content (Join-Path $sys 'Shop.AppHost\StratumServices.g.cs') -Raw) -like '*AddProject<Projects.Payments_Api>("payments")*' -and $out -like '*stratum.workspace.json*') {
        Pass 'a service in another folder joins the system (stratum.workspace.json -> AppHost)'
    } else { Fail 'workspace add' }
    Invoke-Dotnet $StratumDll new Notifications -o (Join-Path $sys 'notifications') --package-source $Pkgs
    Invoke-Dotnet $StratumDll aspire --path $sys --name Shop
    $notifications = Join-Path $sys 'notifications'
    Invoke-Dotnet $StratumDll messaging on --broker azureServiceBus --path $notifications
    Invoke-Dotnet $StratumDll subscribe (Join-Path $sys 'orders') Order.Created --path $notifications
    Invoke-Dotnet $StratumDll aspire --path $sys --name Shop
    $hostCode = Get-Content (Join-Path $sys 'Shop.AppHost\StratumServices.g.cs') -Raw
    $map = Get-DotnetOutput $StratumDll events map --path $sys
    if ($hostCode -like '*AddAzureServiceBus("servicebus")*' -and $hostCode -like '*AddServiceBusSubscription("notifications-orders-order-created", "notifications")*' -and (Get-Content (Join-Path $sys 'Shop.AppHost\Shop.AppHost.csproj') -Raw) -like '*Aspire.Hosting.Azure.ServiceBus*' -and (Get-Content (Join-Path $notifications 'src\Notifications.Api\StratumWiring.g.cs') -Raw) -like '*UseAzureServiceBus*' -and $map -like '*no le llegar*') {
        Pass 'Azure Service Bus: emulator, topics and subscriptions in the AppHost; broker mismatch reported'
    } else { $map | Out-Host; Fail 'Azure Service Bus' }
    Invoke-Dotnet $StratumDll add entity Notice --path $notifications --props 'Text:string'
    Invoke-Dotnet $StratumDll replica add (Join-Path $sys 'orders') Order --fields 'Number' --path $notifications
    Invoke-Dotnet $StratumDll aspire --path $sys --name Shop
    if ((Get-Content (Join-Path $sys 'Shop.AppHost\StratumServices.g.cs') -Raw) -like '*notifications.WithReference(orders);   // initial load of its replicas*' `
        -and (Get-Content (Join-Path $notifications 'stratum.json') -Raw) -like '*"route": "orders"*') {
        Pass "AppHost: a service with replicas gets the publisher's address (initial load)"
    } else { Fail 'AppHost replica reference' }
    # Deployment (stratum deploy): optional targets in the AppHost, for "aspire deploy".
    $dsys = Join-Path $Work 'system-docker'
    New-Item -ItemType Directory -Path $dsys -Force | Out-Null
    Copy-Item (Join-Path $sys 'orders'), (Join-Path $sys 'billing') -Destination $dsys -Recurse
    Invoke-Dotnet $StratumDll aspire --path $dsys --name Store
    Invoke-Dotnet $StratumDll deploy add docker --path $dsys
    Invoke-Dotnet $StratumDll deploy add azure --path $sys
    $storeProj = Get-Content (Join-Path $dsys 'Store.AppHost\Store.AppHost.csproj') -Raw
    $shopProj = Get-Content (Join-Path $sys 'Shop.AppHost\Shop.AppHost.csproj') -Raw
    $shopHost = Get-Content (Join-Path $sys 'Shop.AppHost\StratumServices.g.cs') -Raw
    $deployOk = $storeProj -like '*<StratumDeploy>docker</StratumDeploy>*' -and $storeProj -like '*Aspire.Hosting.Docker*' `
        -and (Get-Content (Join-Path $dsys 'Store.AppHost\StratumServices.g.cs') -Raw) -like '*AddDockerComposeEnvironment("compose")*' `
        -and $shopProj -like '*<StratumDeploy>azure</StratumDeploy>*' -and $shopProj -like '*Aspire.Hosting.Azure.AppContainers*' -and $shopProj -like '*Aspire.Hosting.Azure.Sql*' `
        -and $shopHost -like '*AddAzureContainerAppEnvironment("aca")*' -and $shopHost -like '*AddAzurePostgresFlexibleServer("postgres").WithPasswordAuthentication()*'
    $null = Get-DotnetOutput $StratumDll deploy add docker --path $sys   # expected to fail: Service Bus only deploys to Azure
    if ($deployOk -and $LASTEXITCODE -ne 0) { Pass 'deploy: Docker Compose and Azure Container Apps as optional targets of the AppHost (Service Bus only to Azure)' } else { Fail 'deploy targets' }
    Invoke-Dotnet $StratumDll events asyncapi --path $sys
    $spec = Get-Content (Join-Path $sys 'asyncapi.yaml') -Raw
    if ($spec -like '*asyncapi: "3.1.0"*' -and $spec -like '*address: orders.order-created*' -and $spec -like '*action: receive*') {
        Pass 'AsyncAPI 3.1 exported from the event map'
    } else { Fail 'AsyncAPI export' }
    $mcpIn = @(
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}',
        '{"jsonrpc":"2.0","method":"notifications/initialized"}',
        '{"jsonrpc":"2.0","id":2,"method":"tools/list"}',
        '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"add_entity","arguments":{"service":"Orders","name":"Coupon","properties":"Code:string"}}}',
        '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"list_services","arguments":{}}}',
        '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"add_relation","arguments":{"service":"Orders","entity":"Order","property":"ParentId","target":"Order","optional":true}}}'
    )
    # 'stratum mcp' logs to stderr (stdout is the protocol): Windows PowerShell turns that into an error under 'Stop'.
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $mcpOut = @($mcpIn | & dotnet $StratumDll mcp --path $sys 2>$null | ForEach-Object { "$_" }) }
    finally { $ErrorActionPreference = $eap }
    $mcpText = $mcpOut -join "`n"
    if ($mcpOut.Count -eq 5 -and $mcpText -like '*"protocolVersion":"2025-11-25"*' -and $mcpText -like '*"name":"subscribe"*' -and $mcpText -like '*"name":"add_relation"*' -and (Get-Content (Join-Path $sys 'orders\stratum.json') -Raw) -notlike '*ParentId*' -and $mcpText -like '*"structuredContent":{"root"*' -and -not (Test-Path (Join-Path $sys 'orders\src\Orders.Api\Features\Coupons'))) {
        Pass 'MCP server: handshake, tools, plan without writing'
    } else { $mcpText | Out-Host; Fail 'MCP server' }
    $legacy = Join-Path $sys 'legacy'
    Invoke-Dotnet $StratumDll new Legacy -o $legacy --package-source $Pkgs
    Invoke-Dotnet $StratumDll aspire --path $sys --name Shop
    $out = Get-DotnetOutput $StratumDll service remove $legacy --path $sys
    if ($out -notlike '*No se ha tocado nada*' -or -not (Test-Path $legacy)) { $out | Out-Host; Fail 'service remove (dry run)' }
    Invoke-Dotnet $StratumDll service remove $legacy --path $sys --yes
    $appHostFiles = (Get-Content (Join-Path $sys 'Shop.AppHost\Shop.AppHost.csproj') -Raw) + (Get-Content (Join-Path $sys 'Shop.AppHost\StratumServices.g.cs') -Raw) + (Get-Content (Join-Path $sys 'Shop.slnx') -Raw)
    if (-not (Test-Path $legacy) -and $appHostFiles -notlike '*Legacy*') { Pass 'service remove: dry run first, then folder and AppHost references gone' } else { Fail 'service remove' }
    $keepDb = Get-DotnetOutput $StratumDll service remove (Join-Path $sys 'orders') --path $sys
    $dropDb = Get-DotnetOutput $StratumDll service remove (Join-Path $sys 'orders') --path $sys --drop-database
    if ($keepDb -like '*orders-db (postgres) se conserva*' -and $dropDb -like '*base de datos orders-db*' -and (Test-Path (Join-Path $sys 'orders'))) {
        Pass 'service remove --drop-database: its database goes from the server''s container (Docker or Podman), the rest stays'
    } else { $dropDb | Out-Host; Fail 'service remove --drop-database' }
    $hostCode = Get-Content (Join-Path $sys 'Shop.AppHost\StratumServices.g.cs') -Raw
    $hostProj = Get-Content (Join-Path $sys 'Shop.AppHost\Shop.AppHost.csproj') -Raw
    if ($hostCode -like '*Notifications_Api*' -and $hostProj -like '*Notifications.Api.csproj*') { Pass 'a new service is added to the AppHost' } else { Fail 'new service not added' }

    Step 'stratum studio'
    $studioPort = 5988
    $env:STRATUM_STUDIO_LANGUAGE = 'es'   # the checks below look for Spanish texts
    $studio = Start-Process dotnet -ArgumentList $StratumDll, 'studio', '--path', $sys, '--port', $studioPort, '--no-browser' -PassThru -NoNewWindow `
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
    foreach ($p in 'services', 'events', 'spec', 'run', 'templates', 'service', 'entities', 'service/data', 'service/messaging', 'service/code', 'service/build', 'service/spec', 'service/remove', 'apply', '_framework/blazor.web.js') {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        try { $code = [int]$studioHttp.GetAsync("$studioBase/$p").GetAwaiter().GetResult().StatusCode }
        catch { $code = "no response after $([int]$watch.Elapsed.TotalSeconds) s ($($_.Exception.InnerException.Message))" }
        if ($code -ne 200) { $studioHttp.Dispose(); Stop-Process -Id $studio.Id -Force; Fail "studio: /$p returned $code" }
        if ($watch.Elapsed.TotalSeconds -gt 10) { Write-Host "  (studio /$p took $([int]$watch.Elapsed.TotalSeconds) s)" -ForegroundColor Yellow }
    }
    $eventsHtml = $studioHttp.GetStringAsync("$studioBase/events").GetAwaiter().GetResult()
    $studioHttp.Dispose()
    Pass 'studio pages render and blazor.web.js is served (interactive UI)'
    if (([regex]::Matches($eventsHtml, 'class="ev-mini')).Count -ge 3 -and $eventsHtml -like '*orders.order-created*' -and $eventsHtml -like '*href="/designer"*') { Pass 'studio event map: compact summary of services and events, link to the designer' }
    else { Stop-Process -Id $studio.Id -Force; Fail 'studio: event map' }

    $entHttp = New-Object System.Net.Http.HttpClient
    $entHttp.Timeout = [TimeSpan]::FromSeconds(60)
    $ent = $entHttp.GetStringAsync("$studioBase/entities?open=orders&dialog=property:Total").GetAwaiter().GetResult()
    $enumDialog = $entHttp.GetStringAsync("$studioBase/entities?open=orders&dialog=enum").GetAwaiter().GetResult()
    $entHttp.Dispose()
    if ($ent -like '*class="modal-backdrop"*' -and $ent -like '*Propiedad Total*' -and $ent -like '*Guardar y cerrar*' -and $enumDialog -like '*Nuevo enumerado*') { Pass 'studio entity dialogs: property and enumeration open in floating windows' }
    else { Stop-Process -Id $studio.Id -Force; Fail 'studio: entity dialogs' }

    # Events designer: its page, its model, and the batch API (preview writes nothing; save is all or nothing).
    $designer = "$studioBase/designer"
    $designerHttp = New-Object System.Net.Http.HttpClient
    $designerHttp.Timeout = [TimeSpan]::FromSeconds(60)
    function Invoke-Designer([string]$action, [string]$json) {
        $content = New-Object System.Net.Http.StringContent($json, [Text.Encoding]::UTF8, 'application/json')
        $designerHttp.PostAsync("$designer/api/$action", $content).GetAwaiter().GetResult().Content.ReadAsStringAsync().GetAwaiter().GetResult()
    }
    $page = $designerHttp.GetStringAsync($designer).GetAwaiter().GetResult()
    $model = $designerHttp.GetStringAsync("$designer/api/model").GetAwaiter().GetResult()
    if ($page -like '*ador de eventos*' -and $model -like '*"name":"Billing"*') { Pass 'events designer: page and model' }
    else { Stop-Process -Id $studio.Id -Force; Fail 'designer page/model' }
    $billingJson = Join-Path $sys 'billing\stratum.json'
    $ordersJson = Join-Path $sys 'orders\stratum.json'
    $connect = '{"changes":[{"kind":"connect","publisher":"Billing","entity":"Invoice","event":"Created","consumer":"Orders"}],"layout":{"orders":{"x":40,"y":40},"billing":{"x":480,"y":40}}}'
    $before = Get-Content $billingJson -Raw
    $preview = Invoke-Designer 'preview' $connect
    if ($preview -like '*"saved":false*' -and $preview -like '*activada en Billing*' -and (Get-Content $billingJson -Raw) -eq $before) { Pass 'designer preview: plan for both services, nothing written' }
    else { Stop-Process -Id $studio.Id -Force; Fail "designer preview: $preview" }
    $saved = Invoke-Designer 'save' $connect
    $billingEvents = Get-Content (Join-Path $sys 'billing\src\Billing.Application\IntegrationEvents\InvoiceIntegrationEvents.g.cs') -Raw
    if ($saved -like '*"saved":true*' -and (Get-Content $billingJson -Raw) -like '*"publishedEvents"*' -and $billingEvents -like '*record InvoiceCreated*' -and $billingEvents -notlike '*record InvoiceUpdated*' -and (Get-Content $ordersJson -Raw) -like '*"service": "Billing"*' -and (Test-Path (Join-Path $sys 'stratum.designer.json'))) {
        Pass 'designer save: publishes only Invoice.Created, Orders subscribes, layout kept'
    } else { Stop-Process -Id $studio.Id -Force; Fail "designer save: $saved" }
    $bad = Invoke-Designer 'save' '{"changes":[{"kind":"disconnect","publisher":"Billing","entity":"Invoice","event":"Created","consumer":"Orders"},{"kind":"connect","publisher":"Orders","entity":"Order","event":"Created","consumer":"Orders"}]}'
    if ($bad -like '*"saved":false*' -and (Get-Content $ordersJson -Raw) -like '*"service": "Billing"*') { Pass 'designer: a batch with an error writes nothing' }
    else { Stop-Process -Id $studio.Id -Force; Fail "designer error batch: $bad" }
    Invoke-Designer 'save' '{"changes":[{"kind":"disconnect","publisher":"Billing","entity":"Invoice","event":"Created","consumer":"Orders"}]}' | Out-Null
    if ((Get-Content $ordersJson -Raw) -notlike '*"service": "Billing"*') { Pass 'designer: disconnect removes the subscription' }
    else { Stop-Process -Id $studio.Id -Force; Fail 'designer disconnect' }
    Invoke-Designer 'save' '{"changes":[],"layout":{}}' | Out-Null
    if ($designerHttp.GetStringAsync("$designer/api/model").GetAwaiter().GetResult() -like '*"layout":{}*') { Pass 'designer: a canvas left empty stays empty' }
    else { Stop-Process -Id $studio.Id -Force; Fail 'designer empty canvas' }
    # Entity designer of a service: a new entity and an optional relation to it (set null), saved in one batch.
    $entityDesigner = "$studioBase/entity-designer"
    $relate = '{"service":"orders","changes":[{"kind":"entity","entity":"Customer","properties":"Name:string"},{"kind":"relation","entity":"Order","property":"CustomerId","target":"Customer","optional":true,"onDelete":"setNull"}],"layout":{"Customer":{"x":40,"y":300}}}'
    function Invoke-EntityDesigner([string]$action, [string]$json) {
        $content = New-Object System.Net.Http.StringContent($json, [Text.Encoding]::UTF8, 'application/json')
        $designerHttp.PostAsync("$entityDesigner/api/$action", $content).GetAwaiter().GetResult().Content.ReadAsStringAsync().GetAwaiter().GetResult()
    }
    $edPage = $designerHttp.GetStringAsync("$entityDesigner`?service=orders").GetAwaiter().GetResult()
    $edModel = $designerHttp.GetStringAsync("$entityDesigner/api/model?service=orders").GetAwaiter().GetResult()
    $edPreview = Invoke-EntityDesigner 'preview' $relate
    $edSaved = Invoke-EntityDesigner 'save' $relate
    $orderConfig = Get-Content (Join-Path $sys 'orders\src\Orders.Api\Features\Orders\OrderConfiguration.g.cs') -Raw
    if ($edPage -like '*ador de entidades*' -and $edModel -like '*"name":"Order"*' -and $edPreview -like '*"saved":false*' -and $edSaved -like '*"saved":true*' `
        -and $edSaved -like '*"needsMigration":true*' -and $orderConfig -like '*OnDelete(DeleteBehavior.SetNull)*') {
        Pass 'entity designer: page, model, preview and save (new entity + optional relation, set null)'
    } else { Stop-Process -Id $studio.Id -Force; Fail "entity designer: $edSaved" }
    # Event traces: the window and its report (the services aren't running here, so they show as offline).
    $tracesPage = $designerHttp.GetStringAsync("$studioBase/traces").GetAwaiter().GetResult()
    $traces = $designerHttp.GetStringAsync("$studioBase/traces/api/report?minutes=15").GetAwaiter().GetResult()
    if ($tracesPage -like '*Trazas de eventos*' -and $traces -like '*"name":"Orders"*' -and $traces -like '*"state":"offline"*' -and $traces -like '*"events":`[`]*') {
        Pass 'event traces: window and report (services not running: offline, no events)'
    } else { Stop-Process -Id $studio.Id -Force; Fail "event traces: $traces" }
    $designerHttp.Dispose()
    Stop-Process -Id $studio.Id -Force

    # The same Studio in English.
    $env:STRATUM_STUDIO_LANGUAGE = 'en'
    $studio = Start-Process dotnet -ArgumentList $StratumDll, 'studio', '--path', $sys, '--port', $studioPort, '--no-browser' -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $Work 'studio-en.out.log') -RedirectStandardError (Join-Path $Work 'studio-en.err.log')
    $enHttp = New-Object System.Net.Http.HttpClient
    $enHttp.Timeout = [TimeSpan]::FromSeconds(60)
    $enHome = $null
    for ($i = 0; $i -lt 100 -and -not $enHome; $i++) {
        try { $enHome = $enHttp.GetStringAsync("$studioBase/").GetAwaiter().GetResult() } catch { Start-Sleep -Milliseconds 300 }
    }
    $enDialog = $enHttp.GetStringAsync("$studioBase/entities?open=orders&dialog=enum").GetAwaiter().GetResult()
    $enDesigner = $enHttp.GetStringAsync("$studioBase/designer").GetAwaiter().GetResult()
    $enHttp.Dispose()
    Stop-Process -Id $studio.Id -Force
    Remove-Item Env:STRATUM_STUDIO_LANGUAGE
    if ($enHome -like '*System overview*' -and $enHome -like '*lang="en"*' -and $enDialog -like '*New enumeration*' -and $enDialog -like '*Add value*' -and $enDesigner -like '*Event designer*') {
        Pass 'studio in English (pages, dialogs and the events designer)'
    } else { Fail 'studio: English' }

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
    $out = Get-DotnetOutput $StratumDll import openapi (Join-Path $Work 'library.yaml') -a clean-ddd -o (Join-Path $Work 'imported') --package-source $Pkgs
    if ($out -like '*Author (/api/v1/authors): FullName:string, Born:date?*' -and $out -like '*Book (/api/v1/books): Title:string, AuthorId:Author, Pages:int?*' -and $out -like '*Lend(Member:string)*BookLent*' -and $out -like '*Pages:int?, Tags:string`[`]*' -and $out -like '*GET /stats*' -and (Test-Path (Join-Path $Work 'imported\src\LibraryAPI.Domain\Books\Book.Lend.cs'))) {
        Pass 'import openapi: entities, references, operations and a report of the rest'
    } else { $out | Out-Host; Fail 'import openapi' }

    Step 'stratum upgrade: services from before 0.22 (Msg.*, msg.json)'
    $old = Join-Path $Work 'oldnames'
    Invoke-Dotnet $StratumDll new Shipping -o $old --package-source $Pkgs
    Invoke-Dotnet $StratumDll add entity Parcel --path $old --props 'Code:string'
    Invoke-Dotnet $StratumDll messaging on --broker inMemory --path $old
    Invoke-Dotnet $StratumDll events on Parcel --path $old
    Get-ChildItem $old -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | ForEach-Object {
        $text = [IO.File]::ReadAllText($_.FullName)
        $new = [regex]::Replace($text, '\bStratum(?:Kit)?\.(?=(ServiceDefaults|Ddd|Messaging)\b)', 'Msg.')
        $new = [regex]::Replace($new, '\b(Add|Use|Map)Stratum(?=[A-Z])', '${1}Msg')
        $new = $new.Replace('StratumWiring', 'MsgWiring').Replace('"stratumVersion"', '"msgVersion"').Replace('pattern="StratumKit.*"', 'pattern="Msg.*"')
        if ($new -ne $text) { [IO.File]::WriteAllText($_.FullName, $new) }
    }
    Rename-Item (Join-Path $old 'stratum.json') 'msg.json'
    Rename-Item (Join-Path $old 'src\Shipping.Api\StratumWiring.g.cs') 'MsgWiring.g.cs'
    $blocked = Get-DotnetOutput $StratumDll add entity Box --path $old --props 'Size:int'
    $plan = Get-DotnetOutput $StratumDll upgrade --path $old --dry-run
    Invoke-Dotnet $StratumDll upgrade --path $old
    $leftovers = Get-ChildItem $old -Recurse -File -Include *.cs, *.csproj, *.props | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        Select-String -Pattern '\bMsg\.(ServiceDefaults|Ddd|Messaging)|AddMsg|UseMsg'
    if ($blocked -like '*stratum upgrade*' -and $plan -like '*stratum.json*' -and (Test-Path (Join-Path $old 'stratum.json')) -and -not (Test-Path (Join-Path $old 'msg.json')) `
        -and (Test-Path (Join-Path $old 'src\Shipping.Api\StratumWiring.g.cs')) -and -not (Test-Path (Join-Path $old 'src\Shipping.Api\MsgWiring.g.cs')) `
        -and (Get-Content (Join-Path $old 'src\Shipping.Api\Program.cs') -Raw) -like '*AddStratumWiring()*' -and -not $leftovers `
        -and (Get-Content (Join-Path $old 'Directory.Packages.props') -Raw) -like '*Include="StratumKit.ServiceDefaults"*') {
        Pass 'upgrade migrates Msg.* / msg.json / MsgWiring to Stratum (user code included)'
    } else { $leftovers | Out-Host; Fail 'legacy names migration' }

    Step 'Full services'
    foreach ($s in 'full', 'auth', 'regen', 'clean', 'features', 'imported', 'oldnames', 'team\services\orders') {
        Invoke-Dotnet build (Join-Path $Work $s) -c Release -v q -nologo;           Pass "$s builds"
        Invoke-Dotnet test  (Join-Path $Work $s) -c Release --no-build -v q -nologo; Pass "$s tests pass"
    }
    Invoke-Dotnet build (Join-Path $sys 'Shop.AppHost') -c Release -v q -nologo; Pass 'Aspire AppHost builds (deploys to Azure)'
    Invoke-Dotnet build (Join-Path $dsys 'Store.AppHost') -c Release -v q -nologo; Pass 'Aspire AppHost builds (deploys with Docker Compose)'

    Write-Host 'Smoke test OK' -ForegroundColor Green
}
finally {
    if ($App -and -not $App.HasExited) { Stop-Process -Id $App.Id -Force -ErrorAction SilentlyContinue }
    $Http.Dispose()
    Start-Sleep -Milliseconds 300
    Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue
}
