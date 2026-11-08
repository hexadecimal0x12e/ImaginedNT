param(
    [string]$Filter
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    & pwsh -NoProfile -File (Join-Path $root 'scripts\build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }

    $testArgs = @((Join-Path $root 'tests\ImaginedNT.Tests\bin\Debug\net10.0\ImaginedNT.Tests.dll'))
    if ($Filter) { $testArgs += $Filter }
    dotnet @testArgs
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
