param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin'))

$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$source = Join-Path $PSScriptRoot 'conhost.c'
$obj = Join-Path $OutputDirectory 'conhost.obj'
$exe = Join-Path $OutputDirectory 'conhost.exe'
$compile = @('/nologo', '/utf-8', '/TC', '/c', '/O1', '/Oi', '/GS-', '/guard:cf-', '/Zl', $source, "/Fo:$obj")
$link = @('/nologo', '/MACHINE:X64', '/NODEFAULTLIB', '/SUBSYSTEM:WINDOWS', '/ENTRY:NtEntry',
    "/OUT:$exe", $obj, 'kernel32.lib', 'user32.lib', 'gdi32.lib')

if (Get-Command cl.exe -ErrorAction SilentlyContinue) {
    & cl.exe @compile
    if ($LASTEXITCODE -ne 0) { throw 'conhost compilation failed.' }
    & link.exe @link
    if ($LASTEXITCODE -ne 0) { throw 'conhost linking failed. Use the x64 developer shell.' }
}
else {
    $vcvars = $env:IMAGINEDNT_VCVARS
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not $vcvars -and (Test-Path -LiteralPath $vswhere)) {
        $vcvars = & $vswhere -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'VC\Auxiliary\Build\vcvars64.bat' | Select-Object -First 1
    }
    if (-not $vcvars -or -not (Test-Path -LiteralPath $vcvars)) {
        throw 'Install MSVC x64 build tools or set IMAGINEDNT_VCVARS to vcvars64.bat.'
    }
    $command = 'call "{0}" >nul && cl {1} && link {2}' -f $vcvars,
        (($compile | ForEach-Object { '"' + $_ + '"' }) -join ' '),
        (($link | ForEach-Object { '"' + $_ + '"' }) -join ' ')
    & cmd.exe /d /c $command
    if ($LASTEXITCODE -ne 0) { throw 'conhost build failed.' }
}
Write-Host "Built $exe"
