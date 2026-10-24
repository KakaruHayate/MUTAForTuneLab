#Requires -Version 5.1
<#
.SYNOPSIS
  Build the plugin into artifacts\package\ (the package root is the output folder,
  so manifest.json lands where the host expects it).

.DESCRIPTION
  The SDK contract assemblies (TuneLab.Foundation / TuneLab.SDK) are provided by the
  host and never shipped, so we only need one copy at compile time. Point -TuneLabDir
  at any TuneLab installation; otherwise TUNELAB_DIR is used, then the standard
  install location.

  Compile against the LOWEST version you intend to support: manifest declares
  sdk-version 1.0, so referencing 2.0.0 is the safe floor (using a newer API then
  fails at compile time instead of at load time on an older host).

  NOTE: this script is intentionally ASCII-only. Windows PowerShell 5.1 reads
  BOM-less .ps1 files using the ANSI code page, which mangles non-ASCII text.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\build.ps1 -TuneLabDir "C:\Program Files\TuneLab"
#>
[CmdletBinding()]
param(
    [string]$TuneLabDir = $env:TUNELAB_DIR,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

if (-not $TuneLabDir) {
    $candidates = @()
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles "TuneLab") }
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} "TuneLab") }
    foreach ($candidate in $candidates) {
        if (Test-Path (Join-Path $candidate "TuneLab.SDK.dll")) {
            $TuneLabDir = $candidate
            break
        }
    }
}

if (-not $TuneLabDir) {
    throw "TuneLab not found. Pass -TuneLabDir <install dir> or set TUNELAB_DIR."
}
if (-not (Test-Path (Join-Path $TuneLabDir "TuneLab.SDK.dll"))) {
    throw "No TuneLab.SDK.dll under '$TuneLabDir'."
}

$project = Join-Path $repo "src\MutaForTuneLab\MutaForTuneLab.csproj"
Write-Host "TuneLabDir = $TuneLabDir"
Write-Host "project    = $project"

& dotnet build $project -c $Configuration -p:TuneLabDir="$TuneLabDir" --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }

$package = Join-Path $repo "artifacts\package"
# Only win-x64 native assets are loadable; drop whatever else the SDK staged.
$stagedRuntimes = Join-Path $package "runtimes"
if (Test-Path $stagedRuntimes) { Remove-Item $stagedRuntimes -Recurse -Force }

# Symbols and ONNX Runtime's debug build are dead weight in a shipped package: 12 MB of
# the download is `.pdb` and another 2 MB is DirectML.Debug, which nothing loads.
Get-ChildItem $package -Filter '*.pdb' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force
Get-ChildItem $package -Filter 'DirectML.Debug.*' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

Write-Host ""
Write-Host "package: $package"
Get-ChildItem $package -File | Sort-Object Name |
    ForEach-Object { "  {0,-32} {1,10:N0}" -f $_.Name, $_.Length }
