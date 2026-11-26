$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

Write-Host "=== [1/4] Building FluxCord.Media.dll (C++20 AVX2 + Direct P2P Hole-Punch + STUN) ===" -ForegroundColor Cyan
$vcvars = "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { $vcvars = "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" }
$bat = "@echo off`r`ncall `"$vcvars`" >nul`r`ncmake -S `"$root\native`" -B `"$root\native\build`" -G Ninja -DCMAKE_BUILD_TYPE=Release || exit /b 1`r`ncmake --build `"$root\native\build`" -j 8 || exit /b 1`r`ncopy /Y `"$root\native\third_party\libdave\bin\libdave.dll`" `"$root\native\build\libdave.dll`" >nul`r`n`"$root\native\build\FluxCord.Tests.exe`" || exit /b 1`r`n"
$tmpBat = Join-Path $env:TEMP "fc_p2p_build.cmd"
Set-Content $tmpBat -Value $bat -Encoding ASCII
cmd /c "`"$tmpBat`""
if ($LASTEXITCODE -ne 0) { throw "Native C++ build/tests failed" }

Write-Host "=== [2/4] Publishing FluxCordP2P.exe (.NET 10 NativeAOT) ===" -ForegroundColor Cyan
$outUser = Join-Path $root "dist\FluxCordP2P"
if (Test-Path $outUser) { Remove-Item $outUser -Recurse -Force }
dotnet publish (Join-Path $root "src\FluxCord\FluxCord.csproj") -c Release -r win-x64 -o $outUser
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Copy-Item (Join-Path $root "native\build\FluxCord.Media.dll") (Join-Path $outUser "FluxCord.Media.dll") -Force
Copy-Item (Join-Path $root "native\third_party\libdave\bin\libdave.dll") (Join-Path $outUser "libdave.dll") -Force
Get-ChildItem $outUser -Filter "*.pdb" | Remove-Item -Force -ErrorAction SilentlyContinue

$secretPath = Join-Path $root "room.secret"
if (-not (Test-Path $secretPath)) { throw "Missing room.secret from Jetson" }
$roomSecret = ([System.IO.File]::ReadAllText($secretPath)).Trim()

Write-Host "=== [3/4] Writing pre-configured presets for User (LAN+P2P) & Lapki (Internet P2P) ===" -ForegroundColor Cyan
$outLapki = Join-Path $root "dist\FluxCordP2P-Lapki"
if (Test-Path $outLapki) { Remove-Item $outLapki -Recurse -Force }
Copy-Item $outUser $outLapki -Recurse -Force

$userPreset = @{
    Nickname = "So2K"
    RoomSecret = $roomSecret
    HostList = "192.168.100.72:37400,jetsno-orion.local:37400,185.27.71.230:37400"
    AutoConnect = $true
} | ConvertTo-Json
[System.IO.File]::WriteAllText((Join-Path $outUser "p2p_preset.json"), $userPreset, $utf8NoBom)

$lapkiPreset = @{
    Nickname = "lapki >.<"
    RoomSecret = $roomSecret
    HostList = "185.27.71.230:37400,192.168.100.72:37400"
    AutoConnect = $true
} | ConvertTo-Json
[System.IO.File]::WriteAllText((Join-Path $outLapki "p2p_preset.json"), $lapkiPreset, $utf8NoBom)

Write-Host "=== [4/4] Packing portable FluxCordP2P-Lapki.zip + Start Menu Shortcut ===" -ForegroundColor Cyan
$zipLapki = Join-Path $root "dist\FluxCordP2P-Lapki.zip"
if (Test-Path $zipLapki) { Remove-Item $zipLapki -Force }
Compress-Archive -Path "$outLapki\*" -DestinationPath $zipLapki -CompressionLevel Optimal

$wsShell = New-Object -ComObject WScript.Shell
$lnkPath = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\FluxCord P2P.lnk"
$sc = $wsShell.CreateShortcut($lnkPath)
$sc.TargetPath = Join-Path $outUser "FluxCordP2P.exe"
$sc.WorkingDirectory = $outUser
$sc.IconLocation = Join-Path $outUser "Assets\fluxcord.ico"
$sc.Description = "FluxCord P2P • Jetson Orin Nano"
$sc.Save()

Write-Host "DONE! User Exe: $outUser\FluxCordP2P.exe" -ForegroundColor Green
Write-Host "DONE! Lapki Zip: $zipLapki ($([math]::Round((Get-Item $zipLapki).Length / 1MB, 2)) MB)" -ForegroundColor Green
