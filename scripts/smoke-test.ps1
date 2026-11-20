# End-to-end check for Windows (PowerShell 5.1 or 7+): build + test + pack MSG, generate services
# that consume the local Msg.* packages, build them with warnings-as-errors, run one and hit its
# endpoints, check "msg upgrade", then build and test the full variants.
#
# Usage (from the repo root):
#   powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1
#   pwsh -File scripts/smoke-test.ps1

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8   # dotnet prints UTF-8; avoids garbled accents
Add-Type -AssemblyName System.Net.Http

$Root   = Split-Path -Parent $PSScriptRoot
$Work   = Join-Path ([IO.Path]::GetTempPath()) ("msg-smoke-" + [guid]::NewGuid().ToString('N'))
$MsgDll = Join-Path $Root 'src\Msg.Cli\bin\Release\net10.0\msg.dll'
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
    Invoke-Dotnet test  $sln -c Release --no-build -v q -nologo;              Pass 'Msg.ServiceDefaults.* tests pass'
    Invoke-Dotnet pack  $sln -c Release --no-build -o $Pkgs -v q -nologo
    Pass ("packed: " + ((Get-ChildItem $Pkgs -Filter *.nupkg | ForEach-Object Name) -join ' '))
    $Version = (& dotnet $MsgDll --version | Out-String).Trim()

    # The global NuGet cache keeps packages by id+version: drop old Msg.* builds so we test the ones just packed.
    $globalLine = (& dotnet nuget locals global-packages --list | Out-String).Trim()
    $global = $globalLine.Substring($globalLine.IndexOf(':') + 1).Trim()
    Get-ChildItem $global -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'msg.servicedefaults*' -or $_.Name -eq 'msg.ddd' } |
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

    Step 'msg upgrade'
    $upgraded = Join-Path $Work 'upgraded'
    Copy-Item (Join-Path $Work 'core') $upgraded -Recurse
    Invoke-Dotnet $MsgDll upgrade --path $upgraded --to 9.9.9
    $upProps = Get-Content (Join-Path $upgraded 'Directory.Packages.props') -Raw
    $upJson  = Get-Content (Join-Path $upgraded 'msg.json') -Raw
    if ($upProps -like '*Include="Msg.ServiceDefaults" Version="9.9.9"*' -and $upJson -like '*"msgVersion": "9.9.9"*') {
        Pass 'upgrade bumps Msg.* packages and msg.json'
    } else { Fail 'upgrade' }

    Step 'msg add entity'
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
    Pass 'msg new --from regenerates the entities'

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

    Step 'Editable entities (msg entity)'
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

    Step 'Guided migrations (msg migration review)'
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

    Step 'msg aspire'
    $sys = Join-Path $Work 'system'
    New-Item -ItemType Directory -Path $sys | Out-Null
    Invoke-Dotnet $MsgDll new Orders  -o (Join-Path $sys 'orders')  --package-source $Pkgs
    Invoke-Dotnet $MsgDll add entity Order --path (Join-Path $sys 'orders') --props 'Number:string, Total:decimal'
    Invoke-Dotnet $MsgDll new Billing -o (Join-Path $sys 'billing') --architecture clean-ddd --package-source $Pkgs
    Invoke-Dotnet $MsgDll add entity Invoice --path (Join-Path $sys 'billing') --db sqlserver --props 'Number:string'
    Invoke-Dotnet $MsgDll aspire --path $sys --name Shop
    foreach ($f in 'Shop.AppHost\Shop.AppHost.csproj', 'Shop.AppHost\AppHost.cs', 'Shop.AppHost\MsgServices.g.cs', 'Shop.slnx', '.aspire\settings.json') {
        if (-not (Test-Path (Join-Path $sys $f))) { Fail "missing $f" }
    }
    $hostCode = Get-Content (Join-Path $sys 'Shop.AppHost\MsgServices.g.cs') -Raw
    if ($hostCode -like '*AddProject<Projects.Orders_Api>("orders")*' -and $hostCode -like '*connectionName: "Database"*' -and $hostCode -like '*AddPostgres*' -and $hostCode -like '*AddSqlServer*') {
        Pass 'AppHost with both services, their databases and health checks'
    } else { Fail 'AppHost content' }
    $again = (& dotnet $MsgDll aspire --path $sys --name Shop | Out-String)
    if ($again -notmatch '(?m)^  [+~]') { Pass 'msg aspire is idempotent' } else { Fail 'msg aspire rewrote files' }
    Invoke-Dotnet $MsgDll new Notifications -o (Join-Path $sys 'notifications') --package-source $Pkgs
    Invoke-Dotnet $MsgDll aspire --path $sys --name Shop
    $hostCode = Get-Content (Join-Path $sys 'Shop.AppHost\MsgServices.g.cs') -Raw
    $hostProj = Get-Content (Join-Path $sys 'Shop.AppHost\Shop.AppHost.csproj') -Raw
    if ($hostCode -like '*Notifications_Api*' -and $hostProj -like '*Notifications.Api.csproj*') { Pass 'a new service is added to the AppHost' } else { Fail 'new service not added' }

    Step 'msg studio'
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
    foreach ($p in 'service', 'entities', 'apply', '_framework/blazor.web.js') {
        $code = [int]$Http.GetAsync("$studioBase/$p").GetAwaiter().GetResult().StatusCode
        if ($code -ne 200) { Stop-Process -Id $studio.Id -Force; Fail "studio: /$p returned $code" }
    }
    Pass 'studio pages render and blazor.web.js is served (interactive UI)'
    Stop-Process -Id $studio.Id -Force

    Step 'Full services'
    foreach ($s in 'full', 'auth', 'regen', 'clean', 'team\services\orders') {
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
