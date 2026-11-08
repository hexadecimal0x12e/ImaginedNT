param(
    [ValidateSet('hello', 'threads', 'fileio', 'memory', 'native', 'reloc', 'syscall', 'dllhost', 'directsyscall', 'spin', 'shell', 'failfast')]
    [string]$Sample = 'hello',
    [switch]$Syscalls,
    [switch]$Strict,
    [switch]$Verbose,
    [switch]$Job
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$hostDll = Join-Path $root 'src\ImaginedNT.Host\bin\Debug\net10.0\ImaginedNT.Host.dll'
$image = Join-Path $root "samples\bin\$Sample.exe"
if (-not (Test-Path $hostDll)) { throw 'build first: pwsh -File scripts/build.ps1' }
if (-not (Test-Path $image)) { throw "missing sample: $image; run samples/build-samples.ps1" }

$sandbox = Join-Path $root "sandbox\$Sample"
New-Item -ItemType Directory -Force $sandbox | Out-Null
if ($Sample -eq 'dllhost') {
    Copy-Item (Join-Path $root 'samples\bin\helper.dll') $sandbox -Force
}
if ($Sample -eq 'failfast') {
    Copy-Item (Join-Path $root 'samples\bin\failfastdll.dll') $sandbox -Force
}

$timeout = if ($Sample -eq 'spin') { '2' } elseif ($Sample -eq 'failfast') { '120' } else { '20' }
$args = @($hostDll, 'run', $image, '--sandbox', $sandbox, '--timeout', $timeout)
if ($Syscalls -or $Sample -in @('syscall', 'directsyscall')) { $args += '--syscalls' }
if ($Strict -or $Sample -ne 'native') { $args += '--strict' }
if ($Verbose) { $args += '--verbose' }
if ($Job) { $args += '--job' }
dotnet @args
exit $LASTEXITCODE
