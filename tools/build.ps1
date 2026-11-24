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

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\build.ps1 -TuneLabDir "C:\Program Files\TuneLab" -Lite
#>
[CmdletBinding()]
param(
    [string]$TuneLabDir = $env:TUNELAB_DIR,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    # R1.1 only: no ONNX Runtime, no mel.onnx, no R2 code.
    [switch]$Lite
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

$project = Join-Path (Join-Path $repo "src") "MutaForTuneLab\MutaForTuneLab.csproj"
$package = Join-Path (Join-Path $repo "artifacts") $(if ($Lite) { "package-lite" } else { "package" })
Write-Host "TuneLabDir = $TuneLabDir"
Write-Host "project    = $project"
Write-Host "package    = $package"

$arguments = @($project, "-c", $Configuration, "-p:TuneLabDir=$TuneLabDir", "--nologo")
if ($Lite) {
    # The package root is the output folder, so the lite build needs its own.
    $arguments += "-p:MutaLite=true"
    $arguments += "-p:OutputPath=${package}$([System.IO.Path]::DirectorySeparatorChar)"
}
& dotnet build @arguments
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }

$windows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows)

# A build without a RuntimeIdentifier stages every RID's native assets under runtimes/.
# Keep the ones this machine can load, flat beside the managed assembly (that is the
# second place the WebGPU plugin EP looks), and drop the rest.
$stagedRuntimes = Join-Path $package "runtimes"
if (Test-Path $stagedRuntimes) {
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
    $arch = switch ($architecture) { 'X64' { 'x64' } 'Arm64' { 'arm64' } default { 'x86' } }
    $os = if ($windows) { 'win' } elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::OSX)) { 'osx' } else { 'linux' }
    $native = Join-Path (Join-Path $stagedRuntimes "$os-$arch") "native"
    if (Test-Path $native) {
        Get-ChildItem $native -File | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $package $_.Name) -Force
        }
    }
    Remove-Item $stagedRuntimes -Recurse -Force
}

# Symbols and ONNX Runtime's debug build are dead weight in a shipped package: 12 MB of
# the download is `.pdb` and another 2 MB is DirectML.Debug, which nothing loads.
Get-ChildItem $package -Filter '*.pdb' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force
Get-ChildItem $package -Filter 'DirectML.Debug.*' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

# The native libraries are not produced by dotnet build. Without them the package still
# loads, enumerates its parameters, and passes any check that never synthesises -- and
# then fails at the first note with "Unable to load DLL 'muta_hts'".
# tools/build-native.ps1 is the MSVC build, so only Windows can satisfy this today.
if ($windows) {
    foreach ($required in 'muta_hts.dll', 'worldline.dll') {
        if (-not (Test-Path (Join-Path $package $required))) {
            throw ("package is missing {0} - run tools\build-native.ps1, or copy the native " +
                   "libraries into artifacts\package before packing") -f $required
        }
    }
    if (-not (Test-Path (Join-Path $package 'muta_sinsy.dll'))) {
        Write-Warning "muta_sinsy.dll is missing: Japanese parts will fail to synthesise."
    }
}
else {
    Write-Warning ("native libraries are MSVC-only; this package has none, so synthesis on " +
                   "this platform needs tools\build-native.ps1 ported first")
}

Write-Host ""
Write-Host "package: $package"
Get-ChildItem $package -File | Sort-Object Name |
    ForEach-Object { "  {0,-32} {1,10:N0}" -f $_.Name, $_.Length }
