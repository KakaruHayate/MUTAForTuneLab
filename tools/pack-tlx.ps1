#Requires -Version 5.1
<#
.SYNOPSIS
  Pack artifacts\package\ into a .tlx (a zip whose ROOT holds manifest.json).

.DESCRIPTION
  A TuneLab package is a zip renamed to .tlx. Entries are written by hand instead of
  with ZipFile.CreateFromDirectory for one reason: on Windows that helper emits
  backslash-separated entry names, which violates the ZIP specification (APPNOTE 4.4.17
  requires forward slashes) and breaks unzip tools that treat the name literally.
  Here every entry name is normalised to '/'.

  ASCII-only on purpose: Windows PowerShell 5.1 reads BOM-less .ps1 files using the
  ANSI code page.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\pack-tlx.ps1
  powershell -ExecutionPolicy Bypass -File tools\pack-tlx.ps1 -Output "C:\out\mutafortunelab.tlx"
#>
[CmdletBinding()]
param(
    [string]$Output
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$package = Join-Path $repo "artifacts\package"

if (-not (Test-Path (Join-Path $package "manifest.json"))) {
    throw "artifacts\package has no manifest.json - run tools\build.ps1 first."
}
if (-not $Output) {
    $Output = Join-Path $repo "artifacts\mutafortunelab.tlx"
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Output) | Out-Null
if (Test-Path $Output) { Remove-Item -LiteralPath $Output -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = (Resolve-Path $package).Path.TrimEnd('\')
$entries = 0
$zip = [System.IO.Compression.ZipFile]::Open($Output, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $package -File -Recurse | Sort-Object FullName | ForEach-Object {
        $name = $_.FullName.Substring($root.Length + 1) -replace '\\', '/'
        # Fixed timestamp keeps the package reproducible: same inputs -> same bytes.
        $entry = $zip.CreateEntry($name, [System.IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $stream = $entry.Open()
        try {
            $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
            $stream.Write($bytes, 0, $bytes.Length)
        }
        finally {
            $stream.Dispose()
        }
        $entries++
    }
}
finally {
    $zip.Dispose()
}

$size = (Get-Item $Output).Length
Write-Host ("wrote {0}  ({1:N0} entries, {2:N0} bytes, {3:N1} MB)" -f $Output, $entries, $size, ($size / 1MB))
