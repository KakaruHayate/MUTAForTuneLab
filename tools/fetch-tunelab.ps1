<#
.SYNOPSIS
  Fetches a TuneLab installation (or just its SDK assemblies) for CI.

.DESCRIPTION
  The plugin compiles against TuneLab.SDK.dll / TuneLab.Foundation.dll, which the host
  ships and the repository does not carry. CI takes them from a TuneLab release archive:

      TUNELAB_SDK_URL = https://.../TuneLab-<version>-<rid>.zip

  The archive is unpacked into ./tunelab and the folder that actually holds
  TuneLab.SDK.dll is printed, so the build step can point TuneLabDir at it.
#>
[CmdletBinding()]
param(
    [string]$Url = $env:TUNELAB_SDK_URL,
    [string]$Destination
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path (Split-Path -Parent $PSScriptRoot) "tunelab"
}

if ([string]::IsNullOrWhiteSpace($Url)) {
    throw "TUNELAB_SDK_URL is empty. Point it at a TuneLab release archive that contains TuneLab.SDK.dll."
}

if (Test-Path $Destination) {
    Remove-Item -LiteralPath $Destination -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$zip = Join-Path ([IO.Path]::GetTempPath()) ("tunelab-" + [guid]::NewGuid().ToString("N") + ".zip")
Write-Host "downloading $Url"
Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing
Expand-Archive -LiteralPath $zip -DestinationPath $Destination -Force
Remove-Item -LiteralPath $zip -Force

$sdk = Get-ChildItem -LiteralPath $Destination -Recurse -Filter "TuneLab.SDK.dll" |
    Select-Object -First 1
if ($null -eq $sdk) {
    throw "TuneLab.SDK.dll was not found in the archive."
}

$sdk.DirectoryName
