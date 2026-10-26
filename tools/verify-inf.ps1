#Requires -Version 5.1
<#
.SYNOPSIS
  Compare the C# .inf parser against the Python reference, byte for byte.

.DESCRIPTION
  The parser reads MUTA's per-voice context index (.inf): a byte-deobfuscated, line
  oriented record file. Getting the deobfuscation or the field order wrong silently
  mis-selects bank units, so this asserts the whole parse: every group, every item's
  11 integer fields, and the trailing PCM block table, folded into one SHA-256.

  The reference parser is the Python implementation used while porting and is not
  vendored here, so point -MutaTools at the directory holding muta_inf.py, or set
  MUTA_TOOLS.

  ASCII-only on purpose: Windows PowerShell 5.1 reads BOM-less .ps1 files using the
  ANSI code page.

.PARAMETER Inf
  One or more .inf files. Omit to use the voices found under the standard MUTA install.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\verify-inf.ps1
  powershell -ExecutionPolicy Bypass -File tools\verify-inf.ps1 -Inf "C:\path\to\voice\yanxi.inf"
#>
[CmdletBinding()]
param(
    [string[]]$Inf,
    [string]$MutaTools = $env:MUTA_TOOLS,
    [string]$Python = "python"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$probeProject = Join-Path $repo "tests\InfProbe\InfProbe.csproj"
$probe = Join-Path $repo "tests\InfProbe\bin\Release\net8.0\InfProbe.exe"
$digest = Join-Path $repo "tests\reference\inf_digest.py"

if (-not $Inf -or $Inf.Count -eq 0) {
    $roots = @()
    if (${env:ProgramFiles(x86)}) { $roots += (Join-Path ${env:ProgramFiles(x86)} "Muta\voice\song") }
    if ($env:ProgramFiles) { $roots += (Join-Path $env:ProgramFiles "Muta\voice\song") }
    $found = @()
    foreach ($root in $roots) {
        if (Test-Path $root) { $found += (Get-ChildItem $root -Recurse -Filter *.inf -ErrorAction SilentlyContinue) }
    }
    $Inf = $found | ForEach-Object { $_.FullName }
    if (-not $Inf) {
        throw "No .inf found. Pass -Inf <file.inf>, or install MUTA."
    }
}

if (-not $MutaTools) {
    throw "Reference parser not found. Pass -MutaTools <dir holding muta_inf.py> or set MUTA_TOOLS."
}
if (-not (Test-Path (Join-Path $MutaTools "muta_inf.py"))) {
    throw "No muta_inf.py under '$MutaTools'."
}

if (-not (Test-Path $probe)) {
    Write-Host "building InfProbe..."
    & dotnet build $probeProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "InfProbe build failed" }
}

$failures = 0
foreach ($file in $Inf) {
    Write-Host ""
    Write-Host (Split-Path -Leaf $file)
    $cs = & $probe $file 2>&1
    if ($LASTEXITCODE -ne 0) {
        $failures++
        Write-Host ("  [FAIL] C# parser: {0}" -f ($cs -join ' '))
        continue
    }
    $py = & $Python $digest --tools $MutaTools $file 2>&1
    if ($LASTEXITCODE -ne 0) {
        $failures++
        Write-Host ("  [FAIL] reference parser: {0}" -f ($py -join ' '))
        continue
    }

    $csText = ($cs | Out-String).Trim()
    $pyText = ($py | Out-String).Trim()
    if ($csText -eq $pyText) {
        $hash = ($csText -split "`n" | Where-Object { $_ -like "sha256=*" }) -replace "sha256=", ""
        Write-Host ("  [pass] {0}" -f ($csText -split "`n")[0])
        Write-Host ("         sha256 {0}" -f $hash)
    }
    else {
        $failures++
        Write-Host "  [FAIL] digest differs"
        Write-Host "    C#: $csText"
        Write-Host "    py: $pyText"
    }
}

Write-Host ""
if ($failures -eq 0) {
    Write-Host "verify-inf: all files match"
    exit 0
}
Write-Host ("verify-inf: {0} file(s) failed" -f $failures)
exit 1
