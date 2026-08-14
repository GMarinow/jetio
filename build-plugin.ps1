# Builds the Jellyfin plugin and packages it for installation.
#
# Output: artifacts/jellyfin-plugin-jetio-<version>.zip, containing the plugin dll and the
# meta.json Jellyfin reads to identify it.
#
# On Linux the equivalent is:
#   dotnet publish src/Jellyfin.Plugin.Jetio -c Release -o artifacts/plugin

[CmdletBinding()]
param(
    # Both default from version.json, the single source of truth. Override only to test a
    # build without editing that file.
    [string]$Version,
    [string]$TargetAbi
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

$versionFile = Get-Content (Join-Path $root "version.json") -Raw | ConvertFrom-Json
if (-not $Version) { $Version = "$($versionFile.version).0" }
if (-not $TargetAbi) { $TargetAbi = $versionFile.targetAbi }
$staging = Join-Path $root "artifacts/plugin"
$output = Join-Path $root "artifacts"

Write-Host "Publishing plugin $Version..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "src/Jellyfin.Plugin.Jetio/Jellyfin.Plugin.Jetio.csproj") `
    -c Release -o $staging --nologo -v minimal `
    "-p:Version=$($versionFile.version)" "-p:AssemblyVersion=$Version" "-p:FileVersion=$Version"
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# Jellyfin only needs the plugin assembly; deps.json and pdb confuse its loader.
Get-ChildItem $staging -Exclude "Jellyfin.Plugin.Jetio.dll" | Remove-Item -Recurse -Force

# Same meta.json the Docker build ships, so both paths produce an identical package.
$meta = Get-Content (Join-Path $root "src/Jellyfin.Plugin.Jetio/meta.json") -Raw | ConvertFrom-Json
$meta.version = $Version
$meta.targetAbi = $TargetAbi
$meta | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $staging "meta.json") -Encoding utf8

$zip = Join-Path $output "jellyfin-plugin-jetio-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zip

# Also drop it where jetio serves its Jellyfin repository from, so a locally run service
# offers the same package the Docker build would.
$served = Join-Path $root "src/Jetio/wwwroot/plugin"
New-Item -ItemType Directory -Force $served | Out-Null
# Clear older packages first: the manifest endpoint lists whatever is in here, and a stale
# build would keep being offered alongside the current one.
Get-ChildItem $served -Filter "jellyfin-plugin-jetio-*.zip" | Remove-Item -Force
Copy-Item $zip (Join-Path $served "jellyfin-plugin-jetio-$Version.zip") -Force

Write-Host ""
Write-Host "Packaged: $zip" -ForegroundColor Green
Write-Host "Served from: $served" -ForegroundColor Green
Get-ChildItem $staging | Select-Object Name, Length | Format-Table -AutoSize
