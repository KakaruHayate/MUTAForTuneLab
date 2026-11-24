#Requires -Version 5.1
<#
.SYNOPSIS
  Repack a MUTA 1.0 song voice into a voice folder TuneLab can load, and zip it.

.DESCRIPTION
  MUTA 1.0 keeps a song voice in two files with unhelpful names:

    yanxi.inf     the unit index, revision 100 (9 fields per row)
    yanxi.voice   the recorded sample bank, a plain float file (222 MB for Yanxi)

  The file called ".voice" is NOT an HTS acoustic model, and there is no ".bnk".
  MUTA 2.0 keeps the same voice as <name>.voice (the model), <name>.inf (revision 200)
  and <name>.bnk (the samples). So a 1.0 voice is turned into something the plugin can
  read by:

    <id>.inf    <- the 1.0 index, copied as-is (the plugin parses revision 100)
    <id>.bnk    <- the 1.0 ".voice" sample bank, renamed
    <id>.voice  <- the model from a MUTA 2.0 installation
    <id>.chr    <- "name=<display name>" in GBK, so it can sit beside the 2.0 voice

  Nothing is modified: the 1.0 index offsets already point into the sample file, and the
  two banks use the same 50 MGC + 26 BAP float layout.

  The script writes the folder, a README, and a zip whose root holds both. The zip is the
  thing to hand out; users extract it and add the folder under
  Settings > Extensions > MUTA for TuneLab > Extra voice directories.

  ASCII-only on purpose: Windows PowerShell 5.1 reads BOM-less .ps1 files using the
  ANSI code page.

.PARAMETER Muta10Dir
  The MUTA 1.0 folder to read: the extracted 1.0.0.3 root, its VocalSources\Song\voices
  folder, or the voice folder itself. Searched recursively.

.PARAMETER ModelVoice
  The 2.0 acoustic model to pair with the 1.0 samples. Defaults to
  <Muta2Dir>\voice\song\<source>\<source>.voice, then the standard MUTA install.

.PARAMETER Muta2Dir
  A MUTA 2.0 installation. Defaults to %ProgramFiles(x86)%\Muta.

.PARAMETER Out
  Folder to write. Defaults to artifacts\voice\<id>.

.PARAMETER Id
  Voice id (also the folder name). Defaults to "yanxi10", which keeps it distinct from the
  2.0 "yanxi" voice so both can be installed.

.PARAMETER Name
  Display name. Defaults to the 2.0 voice's own name (read from its .chr) plus "1.0".

.PARAMETER Zip
  Package to write. Defaults to artifacts\dist\MUTAForTuneLab-voice-<id>.zip.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\repack-muta10.ps1 `
      -Muta10Dir "D:\muta1\1.0.0.3" -ModelVoice "C:\Program Files (x86)\Muta\voice\song\yanxi\yanxi.voice"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Muta10Dir,
    [string]$ModelVoice,
    [string]$Muta2Dir,
    [string]$Out,
    [string]$Id = "yanxi10",
    [string]$Name,
    [string]$Zip
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $repo "artifacts\voice\$Id" }
if (-not $Zip) { $Zip = Join-Path $repo "artifacts\dist\MUTAForTuneLab-voice-$Id.zip" }
if (-not $Muta2Dir) { $Muta2Dir = Join-Path ${env:ProgramFiles(x86)} "Muta" }

# ---- locate the 1.0 pair ---------------------------------------------------
function Find-Muta10Voice {
    param([string]$Root)

    if (-not (Test-Path $Root)) { throw "MUTA 1.0 folder not found: $Root" }
    $root = (Resolve-Path $Root).Path
    $candidates = @($root) + @(Get-ChildItem $root -Directory -Recurse -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty FullName)

    foreach ($dir in $candidates) {
        $inf = Get-ChildItem $dir -File -Filter *.inf -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $inf) { continue }
        $bank = Get-ChildItem $dir -File -Filter *.voice -ErrorAction SilentlyContinue |
            Where-Object { $_.Length -gt 50MB } | Select-Object -First 1
        if (-not $bank) { continue }
        return [pscustomobject]@{
            Directory = $dir
            Inf       = $inf.FullName
            Bank      = $bank.FullName
            Source    = [System.IO.Path]::GetFileNameWithoutExtension($bank.Name)
        }
    }
    throw "no MUTA 1.0 voice found under $Root (need an .inf beside a >50 MB .voice)"
}

$found = Find-Muta10Voice -Root $Muta10Dir

# A revision-100 index starts with int32 100 = 0x64, and that byte is one of the values the
# byte transform leaves alone, so the raw first byte is enough to tell it from 2.0's 0xC8.
$firstByte = [System.IO.File]::ReadAllBytes($found.Inf)[0]
if ($firstByte -ne 0x64) {
    throw ("{0} is not a MUTA 1.0 index (first byte 0x{1:X2}, expected 0x64)" -f $found.Inf, $firstByte)
}

Write-Host ("1.0 index  = {0}  ({1:N0} bytes)" -f $found.Inf, (Get-Item $found.Inf).Length)
Write-Host ("1.0 bank   = {0}  ({1:N1} MB)" -f $found.Bank, ((Get-Item $found.Bank).Length / 1MB))

# ---- locate the 2.0 model --------------------------------------------------
function Test-HtsContainer {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $false }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 1024) { return $false }
    if ($bytes[0] -eq 0x5B) { return $true }   # '[' : plain text header
    $head = [System.Text.Encoding]::ASCII.GetString($bytes, 0, [Math]::Min(1048576, $bytes.Length))
    return $head.Contains("KTQDQM")           # encoded "[DATA]\n" marker
}

function Find-Model {
    param([string]$Explicit, [string]$Root, [string]$Source)

    if ($Explicit) {
        if (-not (Test-HtsContainer $Explicit)) { throw "not an HTS voice container: $Explicit" }
        return (Resolve-Path $Explicit).Path
    }

    $tries = @()
    foreach ($base in @($Root, (Join-Path ${env:ProgramFiles(x86)} "Muta"), (Join-Path $env:ProgramFiles "Muta"))) {
        if (-not $base) { continue }
        if ($Source) { $tries += (Join-Path $base "voice\song\$Source\$Source.voice") }
        $tries += (Join-Path $base "voice\song\$Id\$Id.voice")
    }
    foreach ($candidate in $tries) {
        if (Test-HtsContainer $candidate) { return (Resolve-Path $candidate).Path }
    }
    throw "no MUTA 2.0 model found. Pass -ModelVoice <path to a 2.0 <name>.voice>."
}

$model = Find-Model -Explicit $ModelVoice -Root $Muta2Dir -Source $found.Source
$modelSize = (Get-Item $model).Length
if ($modelSize -gt 50MB) {
    throw ("{0} is {1:N1} MB - that is a sample bank, not a model" -f $model, ($modelSize / 1MB))
}
Write-Host ("2.0 model  = {0}  ({1:N1} MB)" -f $model, ($modelSize / 1MB))

# ---- display name ----------------------------------------------------------
# The name lives in a .chr next to the 2.0 model as "name=<GBK>". Reuse it and append the
# revision, so the pack shows the voice's own name + 1.0 without this script hard-coding
# any CJK text.
if (-not $Name) {
    $baseName = $found.Source
    $chr = Join-Path (Split-Path -Parent $model) "$($found.Source).chr"
    if (Test-Path $chr) {
        $gbk = [System.Text.Encoding]::GetEncoding(936)
        $text = $gbk.GetString([System.IO.File]::ReadAllBytes($chr))
        if ($text.StartsWith("name=")) { $text = $text.Substring(5) }
        $text = $text.Trim()
        if ($text) { $baseName = $text }
    }
    $Name = "$baseName" + "1.0"
}
Write-Host ("display    = {0}" -f $Name)

# ---- write the folder ------------------------------------------------------
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$targetInf = Join-Path $Out "$Id.inf"
$targetBank = Join-Path $Out "$Id.bnk"
$targetVoice = Join-Path $Out "$Id.voice"
$targetChr = Join-Path $Out "$Id.chr"

Copy-Item -LiteralPath $found.Inf -Destination $targetInf -Force
Copy-Item -LiteralPath $found.Bank -Destination $targetBank -Force
Copy-Item -LiteralPath $model -Destination $targetVoice -Force

$gbk = [System.Text.Encoding]::GetEncoding(936)
[System.IO.File]::WriteAllBytes($targetChr, $gbk.GetBytes("name=$Name"))

$template = Join-Path $PSScriptRoot "voice-pack-readme.txt"
$readme = Join-Path (Split-Path -Parent $Out) "README-$Id.txt"
if (Test-Path $template) {
    $text = [System.IO.File]::ReadAllText($template, [System.Text.Encoding]::UTF8)
    $text = $text.Replace("{ID}", $Id).Replace("{NAME}", $Name)
    [System.IO.File]::WriteAllText($readme, $text, [System.Text.UTF8Encoding]::new($true))
}
else {
    $readme = $null
}

Write-Host ("folder     = {0}" -f $Out)
Get-ChildItem $Out -File | Sort-Object Name | ForEach-Object {
    Write-Host ("             {0,-14} {1,12:N0} bytes" -f $_.Name, $_.Length)
}

# ---- zip it ----------------------------------------------------------------
# Entries are written by hand: ZipFile.CreateFromDirectory emits backslashes on Windows,
# which the ZIP specification does not allow. Everything under <id>\ plus the README, so
# extracting yields one folder and one text file.
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Zip) | Out-Null
if (Test-Path $Zip) { Remove-Item -LiteralPath $Zip -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = (Resolve-Path $Out).Path.TrimEnd('\')
$parent = Split-Path -Parent $root
$entries = 0
$zipStream = [System.IO.Compression.ZipFile]::Open($Zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $files = @(Get-ChildItem $Out -File -Recurse | Sort-Object FullName)
    if ($readme) { $files += Get-Item $readme }
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($parent.Length + 1) -replace '\\', '/'
        $entry = $zipStream.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $stream = $entry.Open()
        try {
            $input = [System.IO.File]::OpenRead($file.FullName)
            try { $input.CopyTo($stream) }
            finally { $input.Dispose() }
        }
        finally {
            $stream.Dispose()
        }
        $entries++
    }
}
finally {
    $zipStream.Dispose()
}

$zipSize = (Get-Item $Zip).Length
Write-Host ("zip        = {0}  ({1:N0} entries, {2:N1} MB)" -f $Zip, $entries, ($zipSize / 1MB))
Write-Host ""
Write-Host "Hand this zip out as the voice pack. Users extract it and add the folder that"
Write-Host "contains $Id under Settings > Extensions > MUTA for TuneLab > Extra voice directories."
