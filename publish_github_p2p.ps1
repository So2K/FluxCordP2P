$ErrorActionPreference = 'Stop'
$root = 'C:\Users\User\Projects\FluxCordP2P'
$ver = '2.3.0'
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
    git commit -m "Release v${ver}: Inline Markdown, OG Embeds, History Pagination, Game Overlay v3, Soundboard, Rebindable Keybinds, Themes & PiP"
}

# Obtain GitHub token via Git Credential Manager (never printed!)
$token = $null
$tmpCred = Join-Path $env:TEMP "fc_cred_in.txt"
[System.IO.File]::WriteAllText($tmpCred, "protocol=https`nhost=github.com`n`n", [System.Text.Encoding]::ASCII)
$credRaw = (cmd /c "git credential fill < `"$tmpCred`"") -join "`n"
Remove-Item $tmpCred -Force -ErrorAction SilentlyContinue
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

$hasOrigin = (git remote) -contains "origin"
if (-not $hasOrigin) {
    git remote add origin "https://github.com/$repo.git"
}
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
git push -u origin main --force 2>&1 | Out-Host
$pushCode = $LASTEXITCODE
$ErrorActionPreference = $prevEap
if ($pushCode -ne 0) { throw "git push failed" }

# Create or update GitHub Release v2.1.0
$tag = "v$ver"
$relBody = @"
## FluxCord P2P v$ver (Full Discord Feature Pack • Public & Private P2P)

### Что нового в v${ver}:
- **Полноценный Inline Markdown (DirectWrite AST)** — ``**жирный**``, ``*курсив*``, ``~~зачёркнутый~~``, `` `код` ``, кликабельные ``||спойлеры||``, ``[текст](ссылка)``, авто-ссылки и ``@упоминания``.
- **OpenGraph-превью ссылок + подгрузка истории (>80 сообщений)** — сервер на Jetson Orin автоматически подгружает заголовки/описания/картинки сайтов (SSRF-safe) и отдаёт старые страницы истории чата при прокрутке вверх.
- **Игровой оверлей (Overlay v3 style)** — прозрачное click-through `TopMost` окно поверх любых игр (0% DLL-инжекта, безопасно для любых античитов) с индикаторами говорящих, статусом стрима/мута, выбором 4 углов экрана и хоткеем `Ctrl+Shift+O`.
- **Soundboard (синхронный на всю комнату)** — 8 встроенных звуков (`Airhorn`, `Vine Boom`, `Ba-Dum-Tss`, `Bruh`, `Quack`, `Oof`, `Wow`, `GG EZ`) с кнопкой `🎺` в панели голоса, регулировкой громкости и баннером в игровом оверлее.
- **Переназначаемые глобальные хоткеи + двухцветная шкала VAD** — переназначение клавиш Мута, Дефена и Оверлея в 1 клик в Настройках + интерактивная двухцветная полоска чувствительности микрофона (как в Discord).
- **Живые темы (`Obsidian`, `Onyx OLED`, `Discord Ash`, `Midnight`), PiP-окно стрима в чате, Windows Toast-уведомления и Режим стримера**.

### Установка (публичная чистая сборка без приватных ключей):
- Скачайте ``FluxCordP2P-Setup.exe`` (установщик Windows) или ``FluxCordP2P-$ver-win-x64.zip`` (portable / автообновление).

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
