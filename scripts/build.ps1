param(
    [switch]$SkipSamples
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet build ImaginedNT.slnx -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'managed build failed' }
    if (-not $SkipSamples) {
        $buildSamples = Join-Path $root 'samples\build-samples.ps1'
        if (Test-Path $buildSamples) {
            Write-Host 'building native samples...'
            try {
                & pwsh -NoProfile -File $buildSamples
                if ($LASTEXITCODE -ne 0) { Write-Warning 'native sample build failed; tests that need samples will be skipped' }
            }
            catch {
                Write-Warning "native sample build failed: $($_.Exception.Message)"
            }
        }
    }
    Write-Host 'build complete'
}
finally {
    Pop-Location
}
