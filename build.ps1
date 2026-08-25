#!/usr/bin/env pwsh
# Run from the Straftipelago directory.
$ErrorActionPreference = "Stop"

$SourceBundles = "C:\Users\finne\Documents\Roulette_Item_2\AssetBundles\StandaloneWindows"
$DestBundles = "AssetBundles"
$BuildOutputDir = "bin\Release\Straftapelago.Finnegan_McD.org"
$PluginsDir = "..\BepInEx\plugins"

if ((Test-Path $SourceBundles) -and (Get-ChildItem $SourceBundles -Force | Select-Object -First 1)) {
    Write-Host "Moving asset bundles into $DestBundles..."
    Get-ChildItem $SourceBundles -Force | Move-Item -Destination $DestBundles -Force
} else {
    Write-Host "No asset bundles found in $SourceBundles, skipping."
}

Write-Host "Building..."
dotnet build -c Release
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

Write-Host "Moving contents of $BuildOutputDir to $PluginsDir..."
Get-ChildItem $BuildOutputDir -Force | Move-Item -Destination $PluginsDir -Force

Write-Host "Done."
