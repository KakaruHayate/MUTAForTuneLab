#Requires -Version 5.1
<#
.SYNOPSIS
  Build the native libraries into the package folder.

.DESCRIPTION
  Today this builds muta_hts (the HTS engine plus our wrapper). worldline and the
  Sinsy front end join it as their phases land.

  The vendored HTS tree is never compiled in place: it is copied to a scratch
  directory and the patches in third_party/hts_engine_API/patches are applied there,
  so the checked-in sources stay byte-identical to upstream and the local changes
  stay limited to a reviewable diff.

  ASCII-only on purpose: Windows PowerShell 5.1 reads BOM-less .ps1 files using the
  ANSI code page.

.PARAMETER Configuration
  Release (default) or Debug.

.PARAMETER KeepScratch
  Do not delete build\native before preparing sources.

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
$vendored = Join-Path $repo "third_party\hts_engine_API"
$scratch = Join-Path $repo "build\native"
$source = Join-Path $scratch "hts_src"
$buildDir = Join-Path $scratch "hts_build"
$package = Join-Path $repo "artifacts\package"

if (-not $KeepScratch -and (Test-Path $scratch)) {
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $scratch, $package | Out-Null

# --- 1. sources: copy upstream, then patch the copy ---
Write-Host "preparing patched HTS sources..."
Copy-Item -LiteralPath $vendored -Destination $source -Recurse -Force
Get-ChildItem (Join-Path $vendored "patches") -Filter *.patch | Sort-Object Name | ForEach-Object {
    Write-Host ("  applying {0}" -f $_.Name)
    & git -C $source apply -p1 $_.FullName
    if ($LASTEXITCODE -ne 0) { throw ("git apply failed on {0}" -f $_.Name) }
}

# --- 2. configure ---
Write-Host "configuring..."
$generator = $null
foreach ($candidate in @("Visual Studio 18 2026", "Visual Studio 17 2022", "Ninja")) {
    $known = (& cmake --help 2>&1 | Select-String -SimpleMatch $candidate)
    if ($known) { $generator = $candidate; break }
}
if (-not $generator) { throw "no usable CMake generator found" }
Write-Host ("  generator: {0}" -f $generator)

$configureArgs = @(
    "-S", (Join-Path $repo "src\native\hts"),
    "-B", $buildDir,
    "-G", $generator,
    ("-DHTS_SRC=" + $source)
)
if ($generator -like "Visual Studio*") { $configureArgs += @("-A", "x64") }

& cmake @configureArgs
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }

# --- 3. build ---
Write-Host "building..."
& cmake --build $buildDir --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "cmake build failed" }

# --- 4. copy the artifacts next to the managed plugin ---
$built = Join-Path $buildDir $Configuration
if (-not (Test-Path $built)) { $built = $buildDir }
$dll = Get-ChildItem $built -Filter "muta_hts.dll" -Recurse | Select-Object -First 1
if (-not $dll) { throw "muta_hts.dll not found under $built" }
Copy-Item -LiteralPath $dll.FullName -Destination (Join-Path $package "muta_hts.dll") -Force

Write-Host ""
Write-Host ("wrote {0} ({1:N0} bytes)" -f (Join-Path $package "muta_hts.dll"), $dll.Length)

# --- 5. worldline: no patching needed, the vendored trees are already final ---
Write-Host ""
Write-Host "building worldline..."
$wlBuild = Join-Path $scratch "worldline_build"
$wlArgs = @(
    "-S", (Join-Path $repo "src\native\worldline"),
    "-B", $wlBuild,
    "-G", $generator,
    ("-DWORLDLINE_SRC=" + (Join-Path $repo "third_party\worldline")),
    ("-DWORLD_SRC=" + (Join-Path $repo "third_party\world"))
)
if ($generator -like "Visual Studio*") { $wlArgs += @("-A", "x64") }

& cmake @wlArgs
if ($LASTEXITCODE -ne 0) { throw "worldline configure failed" }
& cmake --build $wlBuild --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "worldline build failed" }

$wlOut = Join-Path $wlBuild $Configuration
if (-not (Test-Path $wlOut)) { $wlOut = $wlBuild }
$wlDll = Get-ChildItem $wlOut -Filter "worldline.dll" -Recurse | Select-Object -First 1
if (-not $wlDll) { throw "worldline.dll not found under $wlOut" }
Copy-Item -LiteralPath $wlDll.FullName -Destination (Join-Path $package "worldline.dll") -Force
Write-Host ("wrote {0} ({1:N0} bytes)" -f (Join-Path $package "worldline.dll"), $wlDll.Length)
# --- 6. muta_sinsy: the Japanese label builder ---
Write-Host ""
Write-Host "building muta_sinsy..."
$sinsyBuild = Join-Path $scratch "sinsy_build"
$sinsyArgs = @(
    "-S", (Join-Path $repo "src\native\sinsy"),
    "-B", $sinsyBuild,
    "-G", $generator,
    ("-DSINSY_SRC=" + (Join-Path $repo "third_party\sinsy"))
)
if ($generator -like "Visual Studio*") { $sinsyArgs += @("-A", "x64") }

& cmake @sinsyArgs
if ($LASTEXITCODE -ne 0) { throw "muta_sinsy configure failed" }
& cmake --build $sinsyBuild --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "muta_sinsy build failed" }

$sinsyOut = Join-Path $sinsyBuild $Configuration
if (-not (Test-Path $sinsyOut)) { $sinsyOut = $sinsyBuild }
$sinsyDll = Get-ChildItem $sinsyOut -Filter "muta_sinsy.dll" -Recurse | Select-Object -First 1
if (-not $sinsyDll) { throw "muta_sinsy.dll not found under $sinsyOut" }
Copy-Item -LiteralPath $sinsyDll.FullName -Destination (Join-Path $package "muta_sinsy.dll") -Force
Write-Host ("wrote {0} ({1:N0} bytes)" -f (Join-Path $package "muta_sinsy.dll"), $sinsyDll.Length)