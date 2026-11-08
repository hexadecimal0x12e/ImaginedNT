# Build the native sample PEs with MSVC + ml64 and no CRT startup.
# Usage: pwsh -File samples/build-samples.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$vcvars = $env:IMAGINEDNT_VCVARS
if (-not $vcvars) {
    $candidates = @(
        'C:\Program Files\Microsoft Visual Studio\18\Insiders\VC\Auxiliary\Build\vcvars64.bat',
        'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat',
        'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat',
        'C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat'
    )
    $vcvars = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $vcvars) {
    $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'VC\Auxiliary\Build\vcvars64.bat' | Select-Object -First 1
        if ($found) { $vcvars = $found }
    }
}
if (-not $vcvars -or -not (Test-Path $vcvars)) {
    throw 'vcvars64.bat not found. Install Visual C++ build tools or set IMAGINEDNT_VCVARS.'
}

$bin = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null

function Build-C($name, $libraries = 'kernel32.lib') {
    $src = Join-Path $PSScriptRoot "$name\$name.c"
    $obj = Join-Path $bin "$name.obj"
    $exe = Join-Path $bin "$name.exe"
    $cmd = '"{0}" >nul 2>&1 && cl /nologo /c /O1 /GS- /guard:cf- /Zl "{1}" /Fo:"{2}" && link /nologo /NODEFAULTLIB /SUBSYSTEM:CONSOLE /ENTRY:NtEntry /OUT:"{3}" "{2}" {4}' -f $vcvars, $src, $obj, $exe, $libraries
    cmd /c $cmd
    if ($LASTEXITCODE -ne 0) { throw "failed to build $name" }
    Write-Host "built $exe"
}

function Build-Dll($name) {
    $src = Join-Path $PSScriptRoot "$name\$name.c"
    $obj = Join-Path $bin "$name.obj"
    $dll = Join-Path $bin "$name.dll"
    $cmd = '"{0}" >nul 2>&1 && cl /nologo /c /O1 /GS- /guard:cf- /Zl "{1}" /Fo:"{2}" && link /nologo /NODEFAULTLIB /DLL /ENTRY:DllMain /OUT:"{3}" "{2}" kernel32.lib' -f $vcvars, $src, $obj, $dll
    cmd /c $cmd
    if ($LASTEXITCODE -ne 0) { throw "failed to build $name.dll" }
    Write-Host "built $dll"
}

function Build-Asm($name) {
    $src = Join-Path $PSScriptRoot "$name\$name.asm"
    $obj = Join-Path $bin "$name.obj"
    $exe = Join-Path $bin "$name.exe"
    $cmd = '"{0}" >nul 2>&1 && ml64 /nologo /c /Fo "{1}" "{2}" && link /nologo /NODEFAULTLIB /SUBSYSTEM:CONSOLE /ENTRY:NtEntry /OUT:"{3}" "{1}" kernel32.lib' -f $vcvars, $obj, $src, $exe
    cmd /c $cmd
    if ($LASTEXITCODE -ne 0) { throw "failed to build $name" }
    Write-Host "built $exe"
}

Build-C 'hello'
Build-C 'threads'
Build-C 'fileio'
Build-C 'memory'
Build-C 'native'
Build-C 'reloc'
Build-C 'spin'
Build-C 'dllhost'
Build-C 'child'
Build-C 'spawn'
Build-C 'shell'
Build-C 'failfast' 'kernel32.lib ucrt.lib'
Build-Dll 'helper'
Build-Dll 'failfastdll'
Build-Asm 'syscall'

# Direct real-syscall sample: C driver + ML64 helper in one image.
$directSrc = Join-Path $PSScriptRoot 'directsyscall\directsyscall.c'
$directAsm = Join-Path $PSScriptRoot 'directsyscall\syscall_helper.asm'
$directObj = Join-Path $bin 'directsyscall.obj'
$helperObj = Join-Path $bin 'syscall_helper.obj'
$directExe = Join-Path $bin 'directsyscall.exe'
$cmd = '"{0}" >nul 2>&1 && cl /nologo /c /O1 /GS- /guard:cf- /Zl "{1}" /Fo:"{2}" && ml64 /nologo /c /Fo "{3}" "{4}" && link /nologo /NODEFAULTLIB /SUBSYSTEM:CONSOLE /ENTRY:NtEntry /OUT:"{5}" "{2}" "{3}" kernel32.lib' -f $vcvars, $directSrc, $directObj, $helperObj, $directAsm, $directExe
cmd /c $cmd
if ($LASTEXITCODE -ne 0) { throw 'failed to build directsyscall' }
Write-Host "built $directExe"
Write-Host 'sample build complete'
