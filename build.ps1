#!/usr/bin/env pwsh
# Run from the Straftipelago directory.
$ErrorActionPreference = "Stop"

$SourceBundles = "C:\Users\finne\Documents\Roulette_Item_2\AssetBundles\StandaloneWindows"
$DestBundles = "AssetBundles"
$BuildOutputDir = "bin\Release\Straftapelago.Finnegan_McD.org"
$PluginsDir = "..\BepInEx\plugins\Straftipelago"

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

# Safety net. The csproj marks the game's assemblies Private=false so they should never
# reach the build output, but if that ever regresses, copying them into BepInEx/plugins
# makes BepInEx load a second copy of Assembly-CSharp (etc.) next to the real one — two
# distinct PlayerPickup/ItemBehaviour types, Harmony patching the set the game doesn't use,
# and very confusing breakage. Anything the game already ships in its Managed folder is
# refused here rather than deployed.
$ManagedDir = "..\STRAFTAT_Data\Managed"
$gameAssemblies = @{}
if (Test-Path $ManagedDir) {
    Get-ChildItem $ManagedDir -Filter *.dll -Force | ForEach-Object { $gameAssemblies[$_.Name] = $true }
} else {
    Write-Warning "Managed dir not found at $ManagedDir - skipping the game-assembly guard."
}

# $PluginsDir is a subfolder of BepInEx/plugins, which will not exist on a fresh
# clone or a clean BepInEx install - Move-Item would fail on the very first file.
# Create it up front. BepInEx scans plugins recursively, so the subfolder is only
# for tidiness and does not change how the mod loads.
if (-not (Test-Path $PluginsDir)) {
    Write-Host "Creating $PluginsDir..."
    New-Item -ItemType Directory -Path $PluginsDir -Force | Out-Null
}

Write-Host "Deploying $BuildOutputDir to $PluginsDir..."
$deployed = 0
$skipped = @()
foreach ($item in Get-ChildItem $BuildOutputDir -Force) {
    if ($gameAssemblies.ContainsKey($item.Name)) {
        $skipped += $item.Name
        Remove-Item $item.FullName -Force
        continue
    }
    Move-Item $item.FullName -Destination $PluginsDir -Force
    $deployed++
}

Write-Host "Deployed $deployed file(s)."
if ($skipped.Count -gt 0) {
    Write-Warning ("Refused to deploy {0} game-provided assembly/assemblies (the game already has these in Managed): {1}" -f $skipped.Count, ($skipped -join ", "))
    Write-Warning "Check that the <Reference> items in the csproj still have Private=`"false`"."
}

Write-Host "Done."
