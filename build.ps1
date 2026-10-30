# Builds Manager Agent and copies it into the game's BepInEx\plugins folder.
#   .\build.ps1
#   .\build.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Football Manager 26"
#   .\build.ps1 -GameDir "C:\Program Files\Epic Games\Football Manager 26"
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Football Manager 26"
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "The .NET SDK isn't installed. Get the .NET 8 SDK: https://dotnet.microsoft.com/download" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path (Join-Path $GameDir "BepInEx\interop\UnityEngine.UIElementsModule.dll"))) {
    Write-Host "BepInEx interop files not found under: $GameDir" -ForegroundColor Red
    Write-Host "Install BepInEx 6 (IL2CPP) into the game folder, start FM once to the main menu, quit, then run this again." -ForegroundColor Yellow
    exit 1
}

dotnet build "$PSScriptRoot\ManagerAgent.csproj" -c Release -p:GameDir="$GameDir"
if ($LASTEXITCODE -ne 0) { Write-Host "Build failed." -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "Done. Plugin installed to: $GameDir\BepInEx\plugins\ManagerAgent\ManagerAgent.dll" -ForegroundColor Green
Write-Host "Start FM; look for the 'Agent' tab at the top right." -ForegroundColor Green
