$ErrorActionPreference = 'Stop'
$root = 'C:\Users\User\Projects\FluxCordP2P'
$ver = '2.3.1'
$vcvars = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat'

Write-Host "[1/5] Publishing FluxCordP2P v$ver (NativeAOT Release win-x64)..." -ForegroundColor Cyan
dotnet publish "$root\src\FluxCord\FluxCord.csproj" -c Release -r win-x64 --self-contained -o "$root\dist\stage_clean"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Copy freshly built native DLLs & Assets into clean stage (NO p2p_preset.json!)
Copy-Item "$root\native\build\FluxCord.Media.dll" "$root\dist\stage_clean\FluxCord.Media.dll" -Force
$libdave = "$root\native\third_party\libdave\bin\libdave.dll"
if (-not (Test-Path $libdave)) { $libdave = "$root\native\build\libdave.dll" }
if (Test-Path $libdave) { Copy-Item $libdave "$root\dist\stage_clean\libdave.dll" -Force }
if (Test-Path "$root\src\FluxCord\Assets") {
    Copy-Item "$root\src\FluxCord\Assets" "$root\dist\stage_clean\Assets" -Recurse -Force
}
# Remove any PDBs or preset from clean GitHub stage
Get-ChildItem "$root\dist\stage_clean" -Filter "*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force
Remove-Item "$root\dist\stage_clean\p2p_preset.json" -Force -ErrorAction SilentlyContinue

Write-Host "[2/5] Compiling native Dark-UI Windows Installer (setup_stub.exe)..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path "$root\installer\build" | Out-Null
cmd /c "`"$vcvars`" >nul && cd /d `"$root\installer`" && rc.exe /nologo /fo build\setup.res setup.rc && cl.exe /nologo /std:c++20 /O2 /MT /utf-8 /EHsc setup_main.cpp build\setup.res /Fe:build\setup_stub.exe /link /SUBSYSTEM:WINDOWS"
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed" }

Write-Host "[3/5] Packaging GitHub Auto-Updater ZIP + Personalized Folders..." -ForegroundColor Cyan
$cleanZip = "$root\dist\FluxCordP2P-$ver-win-x64.zip"
if (Test-Path $cleanZip) { Remove-Item $cleanZip -Force }
Compress-Archive -Path "$root\dist\stage_clean\*" -DestinationPath $cleanZip -CompressionLevel Optimal

$secret = (Get-Content "$root\room.secret" -Raw).Trim()
$hosts = "192.168.100.72:37400,jetsno-orion.local:37400,185.27.71.230:37400"

function Build-SfxInstaller([string]$stubPath, [string]$zipPath, [string]$outExe) {
    $stubBytes = [System.IO.File]::ReadAllBytes($stubPath)
    $zipBytes  = [System.IO.File]::ReadAllBytes($zipPath)
    $fs = [System.IO.File]::Create($outExe)
    try {
        $fs.Write($stubBytes, 0, $stubBytes.Length)
        $fs.Write($zipBytes, 0, $zipBytes.Length)
        $lenBytes = [System.BitConverter]::GetBytes([UInt64]$zipBytes.Length)
        $fs.Write($lenBytes, 0, 8)
        $magic = [System.Text.Encoding]::ASCII.GetBytes("FCP2PZIP")
        $fs.Write($magic, 0, 8)
    } finally { $fs.Dispose() }
}

$stubExe = "$root\installer\build\setup_stub.exe"
Build-SfxInstaller $stubExe $cleanZip "$root\dist\FluxCordP2P-Setup.exe"

# So2K personalized stage + Setup
$so2kDir = "$root\dist\stage_so2k"
if (Test-Path $so2kDir) { Remove-Item $so2kDir -Recurse -Force }
Copy-Item "$root\dist\stage_clean" $so2kDir -Recurse -Force
@{ Nickname = "So2K"; RoomSecret = $secret; HostList = $hosts; AutoConnect = $true } | ConvertTo-Json | Set-Content "$so2kDir\p2p_preset.json" -Encoding UTF8
$so2kZip = "$root\dist\stage_so2k.zip"
if (Test-Path $so2kZip) { Remove-Item $so2kZip -Force }
Compress-Archive -Path "$so2kDir\*" -DestinationPath $so2kZip -CompressionLevel Optimal
Build-SfxInstaller $stubExe $so2kZip "$root\dist\FluxCordP2P-Setup-So2K.exe"
Remove-Item $so2kZip -Force

# Lapki personalized stage + Setup
$lapkiDir = "$root\dist\stage_lapki"
if (Test-Path $lapkiDir) { Remove-Item $lapkiDir -Recurse -Force }
Copy-Item "$root\dist\stage_clean" $lapkiDir -Recurse -Force
@{ Nickname = "lapki >.<"; RoomSecret = $secret; HostList = $hosts; AutoConnect = $true } | ConvertTo-Json | Set-Content "$lapkiDir\p2p_preset.json" -Encoding UTF8
$lapkiZip = "$root\dist\stage_lapki.zip"
if (Test-Path $lapkiZip) { Remove-Item $lapkiZip -Force }
Compress-Archive -Path "$lapkiDir\*" -DestinationPath $lapkiZip -CompressionLevel Optimal
Build-SfxInstaller $stubExe $lapkiZip "$root\dist\FluxCordP2P-Setup-Lapki.exe"
Remove-Item $lapkiZip -Force

# Update live dist\FluxCordP2P folder if not locked (otherwise user can run Setup-So2K.exe)
Write-Host "[4/5] Computing SHA-256 checksums..." -ForegroundColor Cyan
$zipHash = (Get-FileHash $cleanZip -Algorithm SHA256).Hash.ToLowerInvariant()
$setupHash = (Get-FileHash "$root\dist\FluxCordP2P-Setup.exe" -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "ZIP_SHA256=$zipHash"
Write-Host "SETUP_SHA256=$setupHash"

Write-Host "[5/5] Done! Built all artifacts:" -ForegroundColor Green
Get-ChildItem "$root\dist" -File | Select-Object Name, @{N='MB';E={[math]::Round($_.Length/1MB,2)}} | Format-Table -AutoSize
