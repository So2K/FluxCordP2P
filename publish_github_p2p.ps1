$ErrorActionPreference = 'Stop'
$root = 'C:\Users\User\Projects\FluxCordP2P'
$ver = '2.1.0'
$repo = 'So2K/FluxCordP2P'
$cleanZip = "$root\dist\FluxCordP2P-$ver-win-x64.zip"
$setupExe = "$root\dist\FluxCordP2P-Setup.exe"

if (-not (Test-Path $cleanZip)) { throw "Missing $cleanZip" }
if (-not (Test-Path $setupExe)) { throw "Missing $setupExe" }

$zipHash = (Get-FileHash $cleanZip -Algorithm SHA256).Hash.ToLowerInvariant()
$setupHash = (Get-FileHash $setupExe -Algorithm SHA256).Hash.ToLowerInvariant()

Set-Location $root
if (-not (Test-Path "$root\.git")) {
    git init -b main
}
# Ensure ZERO secret files are ever tracked!
git rm --cached --ignore-unmatch room.secret p2p_preset.json 2>$null | Out-Null
git add -A
$st = git status --short
Write-Host "Git staged files count: $(($st | Measure-Object).Count)"
# Safety guard: abort if room.secret or p2p_preset.json is staged!
$leaked = git diff --cached --name-only | Where-Object { $_ -match 'room\.secret|p2p_preset\.json' }
if ($leaked) { throw "ABORT: secret file staged: $leaked" }

if ($st) {
    git commit -m "Release v${ver}: P2P Stream Decode, Dual-Screen BGRA, Avatars, Dynamic Tray & Setup.exe"
}

# Obtain GitHub token via Git Credential Manager (never printed!)
$token = $null
$psi = New-Object System.Diagnostics.ProcessStartInfo("git", "credential fill")
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$proc = [System.Diagnostics.Process]::Start($psi)
$inBytes = [System.Text.Encoding]::ASCII.GetBytes("protocol=https`nhost=github.com`n`n")
$proc.StandardInput.BaseStream.Write($inBytes, 0, $inBytes.Length)
$proc.StandardInput.BaseStream.Flush()
$proc.StandardInput.Close()
$credRaw = $proc.StandardOutput.ReadToEnd()
$proc.WaitForExit()
if ($credRaw -match "password=([^\r\n]+)") { $token = $Matches[1].Trim() }
if ([string]::IsNullOrWhiteSpace($token)) { throw "No GitHub token found in Git Credential Manager" }

$headers = @{
    Authorization = "Bearer $token"
    Accept = "application/vnd.github+json"
    "User-Agent" = "FluxCordP2P-Publisher"
    "X-GitHub-Api-Version" = "2022-11-28"
}

# Create repo So2K/FluxCordP2P if it does not exist yet
$repoExists = $true
try {
    Invoke-RestMethod -Uri "https://api.github.com/repos/$repo" -Headers $headers -Method Get | Out-Null
} catch {
    $repoExists = $false
}
if (-not $repoExists) {
    Write-Host "Creating GitHub repo $repo..." -ForegroundColor Cyan
    $body = @{
        name = "FluxCordP2P"
        description = "Native .NET 10 AOT + C++20 AVX2 P2P Voice & 4K60/Dual-Screen Client powered by NVIDIA Jetson Orin Nano"
        private = $false
        auto_init = $false
    } | ConvertTo-Json
    Invoke-RestMethod -Uri "https://api.github.com/user/repos" -Headers $headers -Method Post -Body $body -ContentType "application/json" | Out-Null
}

$remoteUrl = git remote get-url origin 2>$null
if (-not $remoteUrl) {
    git remote add origin "https://github.com/$repo.git"
}
git push -u origin main --force
if ($LASTEXITCODE -ne 0) { throw "git push failed" }

# Create or update GitHub Release v2.1.0
$tag = "v$ver"
$relBody = @"
## FluxCord P2P v$ver (Jetson Orin Edition)

### Что нового:
- **Починен декодер стримов (`dec=0`)** — стабильное аппаратное декодирование H.264/HEVC (DXVA2/NVDEC) и синхронизация ключевых кадров.
- **Починен захват «2 Экрана сразу (1 + 2)»** — новый GPU HLSL BGRA композитор без зелёного экрана (3840×1080 @ 60fps).
- **Исправлен вылет при сворачивании (`—`)** — устранён `d2d1.dll` `0xC0000005` при сворачивании/разворачивании окна.
- **Аватарки и профили** — загрузка своей аватарки (PNG/JPG/WEBP), 1-клик авто-импорт аватарки из Discord на Jetson, смена ника (`/nick`) и статуса (`/status`).
- **Индикатор голоса в трее (как в Discord)** — динамическая иконка в трее: зелёное кольцо когда говоришь, зелёная точка в голосе, красный перечёркнутый микрофон при муте, красное кольцо при дефене.
- **Speaking HUD поверх стримов** — неоново-зелёная плашка «кто говорит» прямо поверх полноэкранного стрима.
- **Автообновление с GitHub + Установщик `FluxCordP2P-Setup.exe`** — 1-клик обновление (<1с) с сохранением комнаты и звонка.

### Контрольные суммы (SHA-256):
- ``FluxCordP2P-$ver-win-x64.zip``: ``$zipHash``
- ``FluxCordP2P-Setup.exe``: ``$setupHash``
"@

$existingRel = $null
try {
    $existingRel = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/tags/$tag" -Headers $headers -Method Get
} catch { }

if ($existingRel) {
    Write-Host "Deleting previous release $tag to recreate cleanly..." -ForegroundColor Yellow
    Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/$($existingRel.id)" -Headers $headers -Method Delete | Out-Null
}

$createBody = @{
    tag_name = $tag
    target_commitish = "main"
    name = "FluxCord P2P v$ver"
    body = $relBody
    draft = $false
    prerelease = $false
} | ConvertTo-Json -Depth 4

$rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases" -Headers $headers -Method Post -Body ([System.Text.Encoding]::UTF8.GetBytes($createBody)) -ContentType "application/json; charset=utf-8"
$uploadBase = ($rel.upload_url -replace '\{\?name,label\}$', '')

foreach ($asset in @(
    @{ Path = $cleanZip; Name = "FluxCordP2P-$ver-win-x64.zip"; Type = "application/zip" },
    @{ Path = $setupExe; Name = "FluxCordP2P-Setup.exe"; Type = "application/octet-stream" }
)) {
    Write-Host "Uploading $($asset.Name)..." -ForegroundColor Cyan
    $uUrl = "${uploadBase}?name=$([Uri]::EscapeDataString($asset.Name))"
    Invoke-RestMethod -Uri $uUrl -Headers $headers -Method Post -InFile $asset.Path -ContentType $asset.Type | Out-Null
}

Write-Host "RELEASE_URL=$($rel.html_url)" -ForegroundColor Green
