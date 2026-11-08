#Requires -Version 7.0
<#
.SYNOPSIS
    Dump the exports of the UCRT API-set forwarder DLLs.

.DESCRIPTION
    Reads the PE export directory of each api-ms-win-crt-*.dll in -Root and
    writes a CSV with Module, Name, Ordinal and Forwarder columns. This is the
    raw inventory used by docs/CRT-API-CLASSIFICATION.md; the classification
    status/reason columns in docs/crt-api-classification.csv are curated.

    The DLLs do not run; this only parses their headers and export tables.

.PARAMETER Root
    Directory containing api-ms-win-crt-*.dll. For example:
    'C:\Program Files (x86)\Windows Kits\10\Redist\10.0.26100.0\ucrt\DLLs\x64'

.PARAMETER Out
    Output CSV path.

.EXAMPLE
    pwsh -File scripts/crt-export-inventory.ps1 `
      -Root 'C:\Program Files (x86)\Windows Kits\10\Redist\10.0.26100.0\ucrt\DLLs\x64' `
      -Out docs/crt-exports-raw.csv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Root,

    [Parameter(Mandatory)]
    [string] $Out
)

$ErrorActionPreference = 'Stop'

function Get-PeExports {
    param([string] $Path)

    $fs = [System.IO.File]::OpenRead($Path)
    $br = [System.IO.BinaryReader]::new($fs)
    try {
        $fs.Position = 0x3C
        $e_lfanew = $br.ReadInt32()
        $fs.Position = $e_lfanew
        if ($br.ReadUInt32() -ne 0x00004550) { throw "not a PE image: $Path" }

        $null = $br.ReadUInt16()   # Machine
        $numberOfSections = $br.ReadUInt16()
        $null = $br.ReadUInt32()   # TimeDateStamp
        $null = $br.ReadUInt32()   # PointerToSymbolTable
        $null = $br.ReadUInt32()   # NumberOfSymbols
        $optionalHeaderSize = $br.ReadUInt16()
        $null = $br.ReadUInt16()   # Characteristics

        $optionalHeader = $fs.Position
        if ($br.ReadUInt16() -ne 0x20B) { throw "not PE32+: $Path" }

        $fs.Position = $optionalHeader + 112
        $exportRva = $br.ReadUInt32()
        $exportSize = $br.ReadUInt32()

        $sectionTable = $optionalHeader + $optionalHeaderSize
        $sections = [System.Collections.Generic.List[object]]::new()
        for ($i = 0; $i -lt $numberOfSections; $i++) {
            $fs.Position = $sectionTable + $i * 40
            $name = ([System.Text.Encoding]::ASCII.GetString($br.ReadBytes(8))).TrimEnd([char]0)
            $virtualSize = $br.ReadUInt32()
            $virtualAddress = $br.ReadUInt32()
            $rawSize = $br.ReadUInt32()
            $rawPointer = $br.ReadUInt32()
            $null = $br.ReadUInt32()
            $null = $br.ReadUInt32()
            $null = $br.ReadUInt16()
            $null = $br.ReadUInt16()
            $characteristics = $br.ReadUInt32()
            $sections.Add([pscustomobject]@{
                Name = $name
                VirtualAddress = $virtualAddress
                VirtualSize = $virtualSize
                RawSize = $rawSize
                RawPointer = $rawPointer
                Characteristics = $characteristics
            })
        }

        function Convert-RvaToOffset {
            param([uint32] $Rva)
            foreach ($section in $sections) {
                $span = [Math]::Max($section.VirtualSize, $section.RawSize)
                if ($Rva -ge $section.VirtualAddress -and $Rva -lt ($section.VirtualAddress + $span)) {
                    return [int]($section.RawPointer + ($Rva - $section.VirtualAddress))
                }
            }
            throw "RVA 0x$($Rva.ToString('X')) is outside every section of $Path"
        }

        if ($exportRva -eq 0) { return @() }

        $fs.Position = Convert-RvaToOffset $exportRva
        $null = $br.ReadUInt32()   # Characteristics
        $null = $br.ReadUInt32()   # TimeDateStamp
        $null = $br.ReadUInt16()   # MajorVersion
        $null = $br.ReadUInt16()   # MinorVersion
        $null = $br.ReadUInt32()   # Name RVA
        $ordinalBase = $br.ReadUInt32()
        $null = $br.ReadUInt32()   # NumberOfFunctions
        $numberOfNames = $br.ReadUInt32()
        $addressOfFunctions = $br.ReadUInt32()
        $addressOfNames = $br.ReadUInt32()
        $addressOfNameOrdinals = $br.ReadUInt32()

        $functionsOffset = Convert-RvaToOffset $addressOfFunctions
        $namesOffset = Convert-RvaToOffset $addressOfNames
        $ordinalsOffset = Convert-RvaToOffset $addressOfNameOrdinals

        $result = [System.Collections.Generic.List[object]]::new()
        for ($i = 0; $i -lt $numberOfNames; $i++) {
            $fs.Position = $namesOffset + $i * 4
            $nameRva = $br.ReadUInt32()
            $fs.Position = Convert-RvaToOffset $nameRva
            $name = [System.Text.StringBuilder]::new()
            while (($byte = $br.ReadByte()) -ne 0) { [void]$name.Append([char]$byte) }

            $fs.Position = $ordinalsOffset + $i * 2
            $ordinalIndex = $br.ReadUInt16()
            $fs.Position = $functionsOffset + $ordinalIndex * 4
            $functionRva = $br.ReadUInt32()

            $forwarder = $null
            if ($functionRva -ge $exportRva -and $functionRva -lt ($exportRva + $exportSize)) {
                $fs.Position = Convert-RvaToOffset $functionRva
                $forward = [System.Text.StringBuilder]::new()
                while (($byte = $br.ReadByte()) -ne 0) { [void]$forward.Append([char]$byte) }
                $forwarder = $forward.ToString()
            }

            $result.Add([pscustomobject]@{
                Module = [System.IO.Path]::GetFileName($Path)
                Name = $name.ToString()
                Ordinal = [int]($ordinalBase + $ordinalIndex)
                Forwarder = $forwarder
            })
        }
        return $result
    }
    finally {
        $br.Dispose()
        $fs.Dispose()
    }
}

$dlls = Get-ChildItem -Path (Join-Path $Root 'api-ms-win-crt-*.dll') -File | Sort-Object Name
if ($dlls.Count -eq 0) { throw "no api-ms-win-crt-*.dll under '$Root'" }

$all = [System.Collections.Generic.List[object]]::new()
foreach ($dll in $dlls) {
    $exports = Get-PeExports -Path $dll.FullName
    Write-Host ("{0}: {1}" -f $dll.Name, $exports.Count)
    foreach ($export in $exports) { $all.Add($export) }
}

$all | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $Out
Write-Host "total: $($all.Count) -> $Out"
