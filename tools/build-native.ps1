#Requires -Version 5.1
<#
.SYNOPSIS
  Build the native libraries into the package folder.

.DESCRIPTION
  Builds muta_hts (the HTS engine plus our wrapper), worldline (the vocoder) and
  muta_sinsy (the Japanese label front end). The same script runs on Windows, Linux and
  macOS: CMake picks the toolchain, and the shared libraries come out as .dll, .so or
  .dylib accordingly.

  The vendored HTS tree is never compiled in place: it is copied to a scratch
  directory and the patches in third_party/hts_engine_API/patches are applied there,
  so the checked-in sources stay byte-identical to upstream and the local changes
  stay limited to a reviewable diff.

  ASCII-only on purpose: Windows PowerShell 5.1 reads BOM-less .ps1 files using the
  ANSI code page.

.PARAMETER Configuration
  Release (default) or Debug.

.PARAMETER KeepScratch
  Do not delete build/native before preparing sources.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\build-native.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$KeepScratch
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$vendored = Join-Path (Join-Path $repo "third_party") "hts_engine_API"
$scratch = Join-Path (Join-Path $repo "build") "native"
$package = Join-Path (Join-Path $repo "artifacts") "package"

$windows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows)
$mac = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::OSX)
$suffix = if ($windows) { "dll" } elseif ($mac) { "dylib" } else { "so" }

if (-not $KeepScratch -and (Test-Path $scratch)) {
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $scratch, $package | Out-Null

# Multi-config generators (Visual Studio, Xcode) keep the configuration in a subfolder and
# take --config; single-config ones need CMAKE_BUILD_TYPE at configure time.
$multiConfig = $false
$generator = $null
if ($windows) {
    foreach ($candidate in @("Visual Studio 18 2026", "Visual Studio 17 2022", "Ninja")) {
        $known = (& cmake --help 2>&1 | Select-String -SimpleMatch $candidate)
        if ($known) { $generator = $candidate; break }
    }
}
else {
    $known = (& cmake --help 2>&1 | Select-String -SimpleMatch "Ninja")
    if ($known -and (Get-Command ninja -ErrorAction SilentlyContinue)) { $generator = "Ninja" }
    else { $generator = "Unix Makefiles" }
}
if (-not $generator) { throw "no usable CMake generator found" }
$multiConfig = $generator -like "Visual Studio*" -or $generator -eq "Xcode"
Write-Host ("generator: {0}" -f $generator)

function Invoke-CMakeBuild {
    param(
        [string]$What,
        [string]$SourceDir,
        [string]$BuildDir,
        [hashtable]$Definitions,
        [string]$Target
    )

    $arguments = @("-S", $SourceDir, "-B", $BuildDir, "-G", $generator)
    if ($generator -like "Visual Studio*") { $arguments += @("-A", "x64") }
    if (-not $multiConfig) { $arguments += ("-DCMAKE_BUILD_TYPE=" + $Configuration) }
    foreach ($key in $Definitions.Keys) { $arguments += ("-D{0}={1}" -f $key, $Definitions[$key]) }

    Write-Host "configuring $What..."
    & cmake @arguments
    if ($LASTEXITCODE -ne 0) { throw "$What configure failed" }

    Write-Host "building $What..."
    & cmake --build $BuildDir --config $Configuration --parallel
    if ($LASTEXITCODE -ne 0) { throw "$What build failed" }

    $outputDir = if ($multiConfig) { Join-Path $BuildDir $Configuration } else { $BuildDir }
    if (-not (Test-Path $outputDir)) { $outputDir = $BuildDir }
    $file = Get-ChildItem $outputDir -Filter ("{0}.{1}" -f $Target, $suffix) -Recurse |
        Select-Object -First 1
    if (-not $file) { throw ("{0}.{1} not found under {2}" -f $Target, $suffix, $outputDir) }

    $destination = Join-Path $package $file.Name
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    Write-Host ("wrote {0} ({1:N0} bytes)" -f $destination, $file.Length)
}

# --- 1. sources: copy upstream, then patch the copy ---
Write-Host "preparing patched HTS sources..."
$source = Join-Path $scratch "hts_src"
Copy-Item -LiteralPath $vendored -Destination $source -Recurse -Force
Get-ChildItem (Join-Path $vendored "patches") -Filter *.patch | Sort-Object Name | ForEach-Object {
    Write-Host ("  applying {0}" -f $_.Name)
    & git -C $source apply -p1 $_.FullName
    if ($LASTEXITCODE -ne 0) { throw ("git apply failed on {0}" -f $_.Name) }
}

# --- 2. the three libraries ---
Invoke-CMakeBuild -What "muta_hts" `
    -SourceDir (Join-Path (Join-Path $repo "src") "native\hts") `
    -BuildDir (Join-Path $scratch "hts_build") `
    -Definitions @{ HTS_SRC = $source } `
    -Target "muta_hts"

Invoke-CMakeBuild -What "worldline" `
    -SourceDir (Join-Path (Join-Path $repo "src") "native\worldline") `
    -BuildDir (Join-Path $scratch "worldline_build") `
    -Definitions @{
        WORLDLINE_SRC = (Join-Path (Join-Path $repo "third_party") "worldline")
        WORLD_SRC = (Join-Path (Join-Path $repo "third_party") "world")
    } `
    -Target "worldline"

Invoke-CMakeBuild -What "muta_sinsy" `
    -SourceDir (Join-Path (Join-Path $repo "src") "native\sinsy") `
    -BuildDir (Join-Path $scratch "sinsy_build") `
    -Definitions @{ SINSY_SRC = (Join-Path (Join-Path $repo "third_party") "sinsy") } `
    -Target "muta_sinsy"
