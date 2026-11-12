# Builds Grim's Ghost and its small installer.
#   powershell -ExecutionPolicy Bypass -File .\build.ps1             -> dist\GrimsGhostSetup.exe  (~200 KB, fetches deps per feature)
#   powershell -ExecutionPolicy Bypass -File .\build.ps1 -Portable   -> also dist\GrimsGhost-portable.zip (app + ffmpeg, no install)
param([switch]$Portable)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

dotnet build -c Release | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'App build failed.' }
dotnet build setup\GrimsGhostSetup.csproj -c Release | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Setup build failed.' }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item (Join-Path $root 'setup\bin\Release\net48\GrimsGhostSetup.exe') $dist -Force
Write-Host ("Installer: {0}  ({1:N0} KB)" -f (Join-Path $dist 'GrimsGhostSetup.exe'), ((Get-Item (Join-Path $dist 'GrimsGhostSetup.exe')).Length / 1KB))

if ($Portable) {
    $deps = Join-Path $root 'deps'
    if (-not (Test-Path (Join-Path $deps 'ffmpeg.exe'))) {
        Write-Host 'Fetching ffmpeg for the portable build (winget)...'
        winget install --id Gyan.FFmpeg.Essentials -e --silent --accept-source-agreements --accept-package-agreements | Out-Null
        $ff = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter ffmpeg.exe |
              Where-Object { $_.FullName -like '*Gyan.FFmpeg*' } | Select-Object -First 1
        New-Item -ItemType Directory -Force $deps | Out-Null
        Copy-Item $ff.FullName (Join-Path $deps 'ffmpeg.exe')
        Copy-Item (Join-Path $ff.Directory.Parent.FullName 'LICENSE') (Join-Path $deps 'ffmpeg-LICENSE.txt')
    }
    $stage = Join-Path $env:TEMP "GrimsGhost-portable\Grim's Ghost"
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Force $stage | Out-Null
    Copy-Item (Join-Path $root 'bin\Release\net48\GrimsGhost.exe') $stage
    Copy-Item (Join-Path $deps 'ffmpeg.exe'), (Join-Path $deps 'ffmpeg-LICENSE.txt'), (Join-Path $root 'README.md') $stage
    $zip = Join-Path $dist 'GrimsGhost-portable.zip'
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path $stage -DestinationPath $zip
    Write-Host "Portable: $zip"
}
