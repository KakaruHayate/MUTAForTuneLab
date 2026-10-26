#Requires -Version 5.1
<#
.SYNOPSIS
  Headless acceptance for the built package: install it into a throwaway data
  directory and assert what the host reports.

.DESCRIPTION
  Requires TuneLab.Cli.exe (shipped with TuneLab 2.1.1 and newer).

  Everything runs against a sandbox data directory (TUNELAB_DATA_DIR), so the user's
  real TuneLab settings, extensions and projects are untouched. The directory is
  recreated on every run: a stale install would otherwise shadow the new package
  (the running process holds the old files open, so the host refuses to overwrite).

  Checks, in order:
    1. extension install        -> package unpacks and its entry registers
    2. extension list           -> status Loaded / Registered, no error
    3. source list              -> the MUTA engine is visible to the host
    4. source list --engine     -> at least one voice bank was found on this machine
    5. source list --source     -> the declared parameter surface matches the spec

  Exit code 0 = all passed.

.PARAMETER TlxPath
  Package to install. Defaults to artifacts\mutafortunelab.tlx.

.PARAMETER TuneLabDir
  Directory holding TuneLab.Cli.exe. Defaults to TUNELAB_DIR, then TUNELAB_CLI_DIR.

.PARAMETER Sandbox
  Data directory to use. Defaults to artifacts\sandbox.

.NOTES
  ASCII-only on purpose: Windows PowerShell 5.1 reads BOM-less .ps1 files using the
  ANSI code page.
#>
[CmdletBinding()]
param(
    [string]$TlxPath,
    [string]$TuneLabDir = $(if ($env:TUNELAB_DIR) { $env:TUNELAB_DIR } else { $env:TUNELAB_CLI_DIR }),
    [string]$Sandbox
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $TlxPath) { $TlxPath = Join-Path $repo "artifacts\mutafortunelab.tlx" }
if (-not $Sandbox) { $Sandbox = Join-Path $repo "artifacts\sandbox" }

if (-not (Test-Path $TlxPath)) {
    throw "Package not found: $TlxPath (run tools\build.ps1 and tools\pack-tlx.ps1 first)"
}

if (-not $TuneLabDir -or -not (Test-Path (Join-Path $TuneLabDir "TuneLab.Cli.exe"))) {
    # Standard install locations only. A dev checkout elsewhere is passed in explicitly
    # with -TuneLabDir; the script must not go looking around the disk.
    $candidates = @()
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles "TuneLab") }
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} "TuneLab") }
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA "TuneLab") }

    $TuneLabDir = $null
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path (Join-Path $candidate "TuneLab.Cli.exe"))) {
            $TuneLabDir = $candidate
            break
        }
    }
}

if (-not $TuneLabDir) {
    throw "TuneLab.Cli.exe not found. Pass -TuneLabDir <dir with TuneLab.Cli.exe> or set TUNELAB_DIR."
}

$cli = Join-Path $TuneLabDir "TuneLab.Cli.exe"
Write-Host "cli     = $cli"
Write-Host "package = $TlxPath"

if (Test-Path $Sandbox) { Remove-Item -LiteralPath $Sandbox -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$env:TUNELAB_DATA_DIR = $Sandbox
Write-Host "sandbox = $Sandbox"
Write-Host ""

$failures = 0

function Invoke-Tl {
    param([string[]]$Arguments)
    $raw = & $cli @Arguments 2>$null | Out-String
    try { return $raw | ConvertFrom-Json } catch { return $null }
}

function Assert-That {
    param([string]$What, [bool]$Ok, [string]$Detail)
    if ($Ok) {
        Write-Host ("  [pass] {0}" -f $What)
    }
    else {
        $script:failures++
        Write-Host ("  [FAIL] {0}" -f $What)
        if ($Detail) { Write-Host ("         {0}" -f $Detail) }
    }
}

# 1 + 2 -- install and registration
$install = Invoke-Tl @("extension", "install", "--headless", "--yes", "--json", "--path", $TlxPath)
Assert-That "package installs" ($install -and $install.outcome -eq "applied") ($install.loadError)

$list = Invoke-Tl @("extension", "list", "--headless", "--json")
$package = $null
if ($list -and $list.packages) {
    $package = $list.packages | Where-Object { $_.id -eq "com.kakaruhayate.mutafortunelab" } | Select-Object -First 1
}
Assert-That "package is loaded" ($package -and $package.status -eq "Loaded") ($package.error)

$entry = $null
if ($package -and $package.entries) {
    $entry = $package.entries | Where-Object { $_.kind -eq "voice" } | Select-Object -First 1
}
Assert-That "voice entry is registered" ($entry -and $entry.status -eq "Registered") ($entry.error)

# 3 -- the engine is visible
$engines = Invoke-Tl @("source", "list", "--headless", "--json")
$engine = $null
if ($engines -and $engines.groups) {
    foreach ($group in $engines.groups) {
        $hit = $group.engines | Where-Object { $_.type -eq "MUTA" } | Select-Object -First 1
        if ($hit) { $engine = $hit }
    }
}
Assert-That "MUTA engine is listed" ($null -ne $engine) $null

# 4 -- at least one voice bank found on this machine
$sources = Invoke-Tl @("source", "list", "--headless", "--json", "--engine", "MUTA")
$sourceCount = if ($sources) { $sources.total } else { 0 }
Assert-That ("voice banks found ({0})" -f $sourceCount) ($sourceCount -gt 0) `
    "No MUTA voice bank on this machine. Install MUTA, or add a voice folder in Settings > Extensions > MUTA."

# 5 -- declared parameter surface
if ($sourceCount -gt 0) {
    $first = $sources.sources[0].id
    $schema = Invoke-Tl @("source", "list", "--headless", "--json", "--engine", "MUTA", "--source", $first)
    $text = if ($schema) { $schema.schemaText } else { "" }

    foreach ($needle in @(
            "Part properties (7):",
            "bank_pitch_mode", "bank_pitch_offset", "bank_pitch_note", "phoneme_speed", "vocoder_mode",
            "Part automation tracks (editable) (7):",
            "Energy", "Timbre", "Husky", "Gender", "Tension", "Breathiness", "Voicing",
            "Note properties (3):",
            "pronunciation", "preutterance_ms"
        )) {
        Assert-That ("schema declares '{0}'" -f $needle) ($text -like ("*" + $needle + "*")) $null
    }
}

Write-Host ""
if ($failures -eq 0) {
    Write-Host "smoke: all checks passed"
    exit 0
}
Write-Host ("smoke: {0} check(s) failed" -f $failures)
exit 1
