param(
    [string]$Configuration = 'Debug',
    [string]$OutputDirectory = 'artifacts/method-trace',
    [string[]]$WeaverArgs = @(),
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $hostOutput = Join-Path $root "src/ImaginedNT.Host/bin/$Configuration/net10.0"
    $weaverProject = Join-Path $root 'tools/ImaginedNT.Weaver/ImaginedNT.Weaver.csproj'
    $weaverDll = Join-Path $root "tools/ImaginedNT.Weaver/bin/$Configuration/net10.0/ImaginedNT.Weaver.dll"

    if (-not $NoBuild) {
        Write-Host 'building host and managed layer...'
        & dotnet build (Join-Path $root 'src/ImaginedNT.Host/ImaginedNT.Host.csproj') -c $Configuration -nodeReuse:false -v:minimal
        if ($LASTEXITCODE -ne 0) { throw 'managed build failed' }

        Write-Host 'building method-trace weaver...'
        & dotnet build $weaverProject -c $Configuration -nodeReuse:false -v:minimal
        if ($LASTEXITCODE -ne 0) { throw 'weaver build failed' }
    }

    if (-not (Test-Path $hostOutput)) { throw "host output not found: $hostOutput (run without -NoBuild)" }
    if (-not (Test-Path $weaverDll)) { throw "weaver not found: $weaverDll (run without -NoBuild)" }

    $destination = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
        $OutputDirectory
    } else {
        Join-Path $root $OutputDirectory
    }

    if (Test-Path $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -Path (Join-Path $hostOutput '*') -Destination $destination -Recurse -Force

    & dotnet $weaverDll $destination @WeaverArgs
    if ($LASTEXITCODE -ne 0) { throw 'method instrumentation failed' }

    Write-Host ''
    Write-Host "Instrumented runtime: $destination"
    Write-Host 'Run it with method tracing enabled, for example:'
    Write-Host "  dotnet `"$destination/ImaginedNT.Host.dll`" run <image.exe> --trace-methods --trace-methods-filter `"LoadManagedLibrary,InitializeLibrary,TryResolveImport,RaiseFailFastException`""
    Write-Host ''
    Write-Host 'Or set IMAGINEDNT_TRACE_METHODS=1 (IMAGINEDNT_TRACE_METHODS_FILTER / IMAGINEDNT_TRACE_METHODS_FILE also work).'
}
finally {
    Pop-Location
}
