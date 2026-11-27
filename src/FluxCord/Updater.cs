using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FluxCord;

public sealed class ResumeWatchItem
{
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public float Volume { get; set; } = 1.0f;
    public bool Muted { get; set; }
}

public sealed class ResumeSession
{
    public long SavedUtcTicks { get; set; }
    public string FromVersion { get; set; } = "";
    public string TargetVersion { get; set; } = "";
    public string ActiveGuildId { get; set; } = "";
    public string ActiveChannelId { get; set; } = "";
    public bool InVoiceStage { get; set; }
    public bool ShowFriendsView { get; set; }
    public string VoiceGuildId { get; set; } = "";
    public string VoiceChannelId { get; set; } = "";
    public bool Muted { get; set; }
    public bool Deafened { get; set; }
    public string? FocusedStreamUserId { get; set; }
    public List<ResumeWatchItem> WatchedStreams { get; set; } = [];
    public bool WasStreaming { get; set; }
    public int StreamMonitor { get; set; }
    public ulong StreamHwnd { get; set; }
}

/// <summary>
/// Ultra-fast GitHub Releases Auto-Updater (So2K/FluxCord) with silent background pre-download,
/// SHA-256 verification, Unicode-safe PowerShell atomic binary swap (<1s downtime), full rollback safety,
/// and 100% seamless Discord call + multi-stream + Go Live session restoration!
/// </summary>
public static class Updater
{
    public const string CurrentVersion = "2.3.1";
    public const string Repo = "So2K/FluxCordP2P";
    public static readonly string UpdatesDir = Path.Combine(SettingsStore.DataDir, "updates");
    public static readonly string ResumePath = Path.Combine(SettingsStore.DataDir, "resume_session.json");
    public static readonly string ErrorMarkerPath = Path.Combine(UpdatesDir, "update_error.txt");

    private static readonly HttpClient Http = MakeClient();
    private static volatile bool _checking, _downloading, _applying, _autoApplyQueued;

    public static bool Checking => _checking;
    public static bool Downloading => _downloading;
    public static bool Applying => _applying;
    public static double Progress { get; private set; }
    public static string? AvailableVersion { get; private set; }
    public static string? DownloadUrl { get; private set; }
    public static string? AssetName { get; private set; }
    public static string? ExpectedSha256 { get; private set; }
    public static string ReleaseNotes { get; private set; } = "";
    public static long AssetSize { get; private set; }
    public static string? StagedDir { get; private set; }
    public static string StatusText { get; private set; } = $"Версия v{CurrentVersion}";

    public static bool HasUpdate => AvailableVersion != null && (DownloadUrl != null || IsReadyToApply);
    public static bool IsReadyToApply => !string.IsNullOrEmpty(StagedDir) && File.Exists(Path.Combine(StagedDir, "FluxCordP2P.exe"));

    public static ResumeSession? PendingResume { get; private set; }

    private static HttpClient MakeClient()
    {
        var h = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"FluxCord/{CurrentVersion}");
        return c;
    }

    private static bool IsSafeVersionTag(string ver)
    {
        if (string.IsNullOrWhiteSpace(ver) || ver.Length > 40) return false;
        if (ver[0] < '0' || ver[0] > '9') return false;
        if (ver.Contains("..", StringComparison.Ordinal)) return false;
        foreach (char c in ver)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '.' || c == '-';
            if (!ok) return false;
        }
        string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(UpdatesDir)) + Path.DirectorySeparatorChar;
        string candFull = Path.GetFullPath(Path.Combine(UpdatesDir, "v" + ver));
        return candFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidHexSha256(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length != 64) return false;
        foreach (char c in s)
        {
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }
        return true;
    }

    private static string? ExtractSha256FromReleaseBody(string body, string? assetName)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        // Scan release body for a 64-hex SHA-256 (preferring after assetName or "SHA-256")
        int searchStart = 0;
        if (!string.IsNullOrEmpty(assetName))
        {
            int idx = body.IndexOf(assetName, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) searchStart = idx;
        }
        if (searchStart == 0)
        {
            int idx2 = body.IndexOf("SHA-256", StringComparison.OrdinalIgnoreCase);
            if (idx2 >= 0) searchStart = idx2;
        }
        for (int pass = 0; pass < 2; pass++)
        {
            int start = pass == 0 ? searchStart : 0;
            int run = 0;
            for (int i = start; i < body.Length; i++)
            {
                char c = body[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (hex)
                {
                    run++;
                    if (run == 64)
                    {
                        bool nextHex = (i + 1 < body.Length) &&
                            ((body[i + 1] >= '0' && body[i + 1] <= '9') ||
                             (body[i + 1] >= 'a' && body[i + 1] <= 'f') ||
                             (body[i + 1] >= 'A' && body[i + 1] <= 'F'));
                        if (!nextHex) return body.Substring(i - 63, 64).ToLowerInvariant();
                    }
                }
                else run = 0;
            }
        }
        return null;
    }

    private static string? FindStagedExeDir(string stageRoot, string? expectedSha256 = null)
    {
        if (!Directory.Exists(stageRoot)) return null;
        if (!string.IsNullOrEmpty(expectedSha256))
        {
            string marker = Path.Combine(stageRoot, ".verified_sha256");
            if (!File.Exists(marker)) return null;
            string saved = "";
            try { saved = File.ReadAllText(marker).Trim(); } catch { }
            if (!string.Equals(saved, expectedSha256, StringComparison.OrdinalIgnoreCase)) return null;
        }
        if (File.Exists(Path.Combine(stageRoot, "FluxCordP2P.exe"))) return stageRoot;
        return Directory.GetDirectories(stageRoot).FirstOrDefault(d => File.Exists(Path.Combine(d, "FluxCordP2P.exe")));
    }

    public static void InitOnStartup(bool resumedArg)
    {
        // Surface any swap failure marker written by apply_update.ps1
        try
        {
            if (File.Exists(ErrorMarkerPath))
            {
                string err = File.ReadAllText(ErrorMarkerPath).Trim();
                try { File.Delete(ErrorMarkerPath); } catch { }
                SettingsStore.Log("Updater previous swap failed (rolled back): " + err);
                StatusText = "Предыдущее обновление откачено (файлы были заняты)";
            }
        }
        catch { }

        // Clean leftover hot-swap files & old cached update folders
        try
        {
            string appDir = AppContext.BaseDirectory;
            foreach (var f in Directory.GetFiles(appDir, "*.old*"))
                try { File.Delete(f); } catch { }
            foreach (var f in Directory.GetFiles(appDir, "*.bak"))
                try { File.Delete(f); } catch { }
            if (Directory.Exists(UpdatesDir))
            {
                foreach (var d in Directory.GetDirectories(UpdatesDir, "v*"))
                {
                    string verName = Path.GetFileName(d).TrimStart('v', 'V');
                    if (!IsNewer(verName, CurrentVersion))
                        try { Directory.Delete(d, true); } catch { }
                }
            }
        }
        catch { }

        // Load fresh resume_session.json (valid for 120 seconds so normal restarts never hijack voice!)
        if (File.Exists(ResumePath))
        {
            try
            {
                var json = File.ReadAllText(ResumePath);
                var sess = JsonSerializer.Deserialize(json, FluxJsonContext.Default.ResumeSession);
                try { File.Delete(ResumePath); } catch { }
                if (sess != null)
                {
                    var age = DateTime.UtcNow - new DateTime(sess.SavedUtcTicks, DateTimeKind.Utc);
                    if (age.TotalSeconds >= -5 && age.TotalSeconds <= 120)
                    {
                        PendingResume = sess;
                        SettingsStore.Log($"Updater: loaded resume snapshot (resumedArg={resumedArg}, age={age.TotalSeconds:F1}s, voice={sess.VoiceChannelId}, watches={sess.WatchedStreams.Count}, streaming={sess.WasStreaming})");
                    }
                }
            }
            catch { try { File.Delete(ResumePath); } catch { } }
        }

        // Start silent background update check loop (first check after 4s, then every 30 min)
        if (!App.BenchArg && SettingsStore.Current.AutoCheckUpdates)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(4000);
                    Check(manual: false);
                    while (true)
                    {
                        await Task.Delay(TimeSpan.FromMinutes(30));
                        if (SettingsStore.Current.AutoCheckUpdates) Check(manual: false);
                    }
                }
                catch { }
            });
        }
    }

    public static ResumeSession? ConsumePendingResume()
    {
        var r = PendingResume;
        PendingResume = null;
        return r;
    }

    public static void Check(bool manual = false)
    {
        if (_checking || _downloading || _applying) return;
        _checking = true;
        if (manual) StatusText = "Проверяю обновления на GitHub...";
        App.WakeUi();

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var resp = await Http.SendAsync(req, cts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    _checking = false;
                    StatusText = $"Версия v{CurrentVersion}";
                    if (manual) Shell.Toast($"Не удалось проверить обновления (HTTP {(int)resp.StatusCode})");
                    App.WakeUi();
                    return;
                }

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync(cts.Token));
                var root = doc.RootElement;
                string tag = (root.TryGetProperty("tag_name", out var tEl) ? tEl.GetString() ?? "" : "").TrimStart('v', 'V').Trim();
                string body = root.TryGetProperty("body", out var bEl) && bEl.ValueKind == JsonValueKind.String ? bEl.GetString() ?? "" : "";

                string? zipUrl = null, zipName = null, sha256 = null;
                long zipSize = 0;
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        string an = a.TryGetProperty("name", out var ne) ? ne.GetString() ?? "" : "";
                        string au = a.TryGetProperty("browser_download_url", out var ue) ? ue.GetString() ?? "" : "";
                        if (!an.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(au)) continue;
                        bool preferred = an.Contains("win-x64", StringComparison.OrdinalIgnoreCase) || an.Contains("FluxCordP2P", StringComparison.OrdinalIgnoreCase);
                        if (zipUrl == null || preferred)
                        {
                            zipName = Path.GetFileName(an);
                            zipUrl = au;
                            zipSize = a.TryGetProperty("size", out var se) && se.ValueKind == JsonValueKind.Number ? se.GetInt64() : 0;
                            sha256 = null;
                            if (a.TryGetProperty("digest", out var de) && de.ValueKind == JsonValueKind.String)
                            {
                                string d = de.GetString() ?? "";
                                if (d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha256 = d[7..].Trim().ToLowerInvariant();
                            }
                            if (!IsValidHexSha256(sha256))
                                sha256 = ExtractSha256FromReleaseBody(body, zipName);
                            if (preferred) break;
                        }
                    }
                }

                bool newer = IsSafeVersionTag(tag) && IsNewer(tag, CurrentVersion) && !string.IsNullOrEmpty(zipUrl) && IsValidHexSha256(sha256);
                _checking = false;

                if (newer)
                {
                    AvailableVersion = tag;
                    DownloadUrl = zipUrl;
                    AssetName = Path.GetFileName(zipName ?? $"FluxCordP2P-{tag}-win-x64.zip");
                    AssetSize = zipSize;
                    ExpectedSha256 = sha256;
                    ReleaseNotes = TruncateSafe(body, 420);

                    string? preStage = FindStagedExeDir(Path.Combine(UpdatesDir, "v" + tag, "stage"), ExpectedSha256);
                    if (preStage != null)
                    {
                        StagedDir = preStage;
                        Progress = 1.0;
                        StatusText = $"Готово к установке: v{tag} (мгновенно, вернёт в звонок)";
                        Shell.Toast($"⚡ Обновление FluxCord P2P v{tag} готово — нажми «⬆ Обновить»!", 4500);
                    }
                    else
                    {
                        StagedDir = null;
                        StatusText = $"Доступно обновление v{tag} ({FormatSize(zipSize)})";
                        if (SettingsStore.Current.AutoDownloadUpdates)
                            _ = DownloadAndStageAsync(autoApply: false);
                        else if (manual)
                            Shell.Toast($"Доступна новая версия FluxCord P2P v{tag}!", 4000);
                    }
                }
                else
                {
                    AvailableVersion = null;
                    DownloadUrl = null;
                    StagedDir = null;
                    StatusText = $"Актуальная версия v{CurrentVersion}";
                    if (manual) Shell.Toast($"Установлена последняя версия FluxCord P2P (v{CurrentVersion})");
                }
                App.WakeUi();
            }
            catch (Exception ex)
            {
                _checking = false;
                SettingsStore.Log("Updater check error: " + ex.Message);
                StatusText = $"Версия v{CurrentVersion}";
                if (manual) Shell.Toast("Ошибка проверки обновлений: " + ex.Message);
                App.WakeUi();
            }
        });
    }

    private static void UnusedDiscordCheck(bool manual = false)
    {
        if (_checking || _downloading || _applying) return;
        _checking = true;
        if (manual) StatusText = "Проверяю обновления на GitHub...";
        App.WakeUi();

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var resp = await Http.SendAsync(req, cts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    _checking = false;
                    StatusText = $"Версия v{CurrentVersion}";
                    if (manual) Shell.Toast($"Не удалось проверить обновления (HTTP {(int)resp.StatusCode})");
                    App.WakeUi();
                    return;
                }

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync(cts.Token));
                var root = doc.RootElement;
                string tag = (root.TryGetProperty("tag_name", out var tEl) ? tEl.GetString() ?? "" : "").TrimStart('v', 'V').Trim();
                string body = root.TryGetProperty("body", out var bEl) && bEl.ValueKind == JsonValueKind.String ? bEl.GetString() ?? "" : "";

                string? zipUrl = null, zipName = null, sha256 = null;
                long zipSize = 0;
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    // Prefer win-x64 FluxCord portable zip specifically
                    foreach (var a in assets.EnumerateArray())
                    {
                        string an = a.TryGetProperty("name", out var ne) ? ne.GetString() ?? "" : "";
                        string au = a.TryGetProperty("browser_download_url", out var ue) ? ue.GetString() ?? "" : "";
                        if (!an.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(au)) continue;
                        bool preferred = an.Contains("win-x64", StringComparison.OrdinalIgnoreCase) || an.Contains("FluxCord", StringComparison.OrdinalIgnoreCase);
                        if (zipUrl == null || preferred)
                        {
                            zipName = Path.GetFileName(an);
                            zipUrl = au;
                            zipSize = a.TryGetProperty("size", out var se) && se.ValueKind == JsonValueKind.Number ? se.GetInt64() : 0;
                            sha256 = null;
                            if (a.TryGetProperty("digest", out var de) && de.ValueKind == JsonValueKind.String)
                            {
                                string d = de.GetString() ?? "";
                                if (d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha256 = d[7..].Trim().ToLowerInvariant();
                            }
                            if (!IsValidHexSha256(sha256))
                                sha256 = ExtractSha256FromReleaseBody(body, zipName);
                            if (preferred) break;
                        }
                    }
                }

                // Strict security checks: valid non-traversal SemVer tag + valid 64-hex SHA-256 required!
                bool newer = IsSafeVersionTag(tag) && IsNewer(tag, CurrentVersion) && !string.IsNullOrEmpty(zipUrl) && IsValidHexSha256(sha256);
                _checking = false;

                if (newer)
                {
                    AvailableVersion = tag;
                    DownloadUrl = zipUrl;
                    AssetName = Path.GetFileName(zipName ?? $"FluxCord-{tag}-win-x64.zip");
                    AssetSize = zipSize;
                    ExpectedSha256 = sha256;
                    ReleaseNotes = TruncateSafe(body, 420);

                    string? preStage = FindStagedExeDir(Path.Combine(UpdatesDir, "v" + tag, "stage"), ExpectedSha256);
                    if (preStage != null)
                    {
                        StagedDir = preStage;
                        Progress = 1.0;
                        StatusText = $"Готово к установке: v{tag} (мгновенно, вернёт в звонок)";
                        Shell.Toast($"⚡ Обновление FluxCord v{tag} скачано и готово — нажми «⬆ Обновить»!", 4500);
                    }
                    else
                    {
                        StagedDir = null;
                        StatusText = $"Доступно обновление v{tag} ({FormatSize(zipSize)})";
                        if (SettingsStore.Current.AutoDownloadUpdates)
                        {
                            _ = DownloadAndStageAsync(autoApply: false);
                        }
                        else if (manual)
                        {
                            Shell.Toast($"Доступна новая версия FluxCord v{tag}!", 4000);
                        }
                    }
                }
                else
                {
                    AvailableVersion = null;
                    DownloadUrl = null;
                    StagedDir = null;
                    StatusText = $"Актуальная версия v{CurrentVersion}";
                    if (manual) Shell.Toast($"Установлена последняя версия FluxCord (v{CurrentVersion})");
                }
                App.WakeUi();
            }
            catch (Exception ex)
            {
                _checking = false;
                SettingsStore.Log("Updater check error: " + ex.Message);
                StatusText = $"Версия v{CurrentVersion}";
                if (manual) Shell.Toast("Ошибка проверки обновлений: " + ex.Message);
                App.WakeUi();
            }
        });
    }

    public static void StartUpdateNow()
    {
        if (_applying) return;
        if (IsReadyToApply)
        {
            ApplyStagedUpdateAndRestart();
            return;
        }
        if (_downloading)
        {
            // User clicked update while background download is already running -> auto-apply immediately on finish!
            _autoApplyQueued = true;
            Shell.Toast("Обновление докачивается — установится сразу после загрузки!", 2500);
            return;
        }
        if (!string.IsNullOrEmpty(DownloadUrl))
        {
            _autoApplyQueued = true;
            _ = DownloadAndStageAsync(autoApply: true);
        }
    }

    private static async Task DownloadAndStageAsync(bool autoApply)
    {
        if (_downloading || string.IsNullOrEmpty(DownloadUrl) || string.IsNullOrEmpty(AvailableVersion)) return;
        _downloading = true;
        if (autoApply) _autoApplyQueued = true;
        Progress = 0;
        string ver = AvailableVersion;
        if (!IsSafeVersionTag(ver))
        {
            _downloading = false;
            _autoApplyQueued = false;
            return;
        }
        string url = DownloadUrl;
        string? wantSha = ExpectedSha256;
        string verDir = Path.Combine(UpdatesDir, "v" + ver);
        string safeAsset = Path.GetFileName(AssetName ?? $"FluxCordP2P-{ver}-win-x64.zip");
        if (string.IsNullOrEmpty(safeAsset) || !safeAsset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            safeAsset = $"FluxCordP2P-{ver}-win-x64.zip";
        string zipPath = Path.Combine(verDir, safeAsset);
        string partPath = zipPath + ".part";
        string stageRoot = Path.Combine(verDir, "stage");

        StatusText = $"Скачивание v{ver}...";
        App.WakeUi();

        try
        {
            Directory.CreateDirectory(verDir);
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? AssetSize;

            await using var input = await resp.Content.ReadAsStreamAsync();
            await using (var output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                var buf = new byte[128 * 1024];
                long read = 0, lastUi = 0;
                long tStart = Environment.TickCount64;
                int n;
                while ((n = await input.ReadAsync(buf)) > 0)
                {
                    await output.WriteAsync(buf.AsMemory(0, n));
                    read += n;
                    long now = Environment.TickCount64;
                    if (now - lastUi >= 100)
                    {
                        lastUi = now;
                        double sec = Math.Max(0.1, (now - tStart) / 1000.0);
                        double mbps = (read / (1024.0 * 1024.0)) / sec;
                        Progress = total > 0 ? Math.Clamp((double)read / total, 0, 0.92) : 0.5;
                        StatusText = total > 0
                            ? $"Скачивание v{ver}: {(int)(read * 100 / total)}% ({mbps:F1} МБ/с)"
                            : $"Скачивание v{ver}: {FormatSize(read)}";
                        App.WakeUi();
                    }
                }
            }

            if (!IsValidHexSha256(wantSha))
                throw new InvalidOperationException("Отсутствует SHA-256 подпись релиза!");

            StatusText = $"Проверка SHA-256 v{ver}...";
            App.WakeUi();
            await using (var fs = File.OpenRead(partPath))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
                if (!string.Equals(hash, wantSha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Контрольная сумма SHA-256 не совпала!");
            }

            File.Move(partPath, zipPath, overwrite: true);

            StatusText = $"Распаковка v{ver}...";
            Progress = 0.95;
            App.WakeUi();

            if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, true);
            Directory.CreateDirectory(stageRoot);
            ZipFile.ExtractToDirectory(zipPath, stageRoot, overwriteFiles: true);
            File.WriteAllText(Path.Combine(stageRoot, ".verified_sha256"), wantSha!);

            string? stageDir = FindStagedExeDir(stageRoot, wantSha);
            if (stageDir == null)
                throw new InvalidOperationException("В архиве обновления не найден FluxCordP2P.exe");

            StagedDir = stageDir;
            Progress = 1.0;
            _downloading = false;
            StatusText = $"Готово к установке: v{ver} (вернёт в звонок)";
            SettingsStore.Log($"Updater: staged v{ver} at {stageDir}");
            App.WakeUi();

            if (_autoApplyQueued)
            {
                _autoApplyQueued = false;
                App.InvokeOnUiThread(ApplyStagedUpdateAndRestart);
            }
            else
            {
                Shell.Toast($"⚡ FluxCord v{ver} готов! Нажми «⬆ Обновить» сверху — звонок восстановится сам.", 5500);
            }
        }
        catch (Exception ex)
        {
            _downloading = false;
            _autoApplyQueued = false;
            Progress = 0;
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
            SettingsStore.Log("Updater download error: " + ex);
            StatusText = $"Ошибка обновления: {ex.Message}";
            Shell.Toast("Ошибка скачивания обновления: " + ex.Message, 5000);
            App.WakeUi();
        }
    }

    /// <summary>
    /// Captures exact voice/stream/channel state, spawns a Unicode-safe hidden PowerShell swapper
    /// (Wait-Process + full backup/rollback + <1s swap), and cleanly exits FluxCord!
    /// </summary>
    public static void ApplyStagedUpdateAndRestart()
    {
        if (_applying || !IsReadyToApply || string.IsNullOrEmpty(StagedDir)) return;
        _applying = true;
        string targetVer = AvailableVersion ?? "new";
        StatusText = $"Установка v{targetVer} и возврат в Discord...";
        App.WakeUi();

        try
        {
            var snap = Shell.CaptureResumeSession(targetVer);
            Directory.CreateDirectory(SettingsStore.DataDir);
            File.WriteAllText(ResumePath, JsonSerializer.Serialize(snap, FluxJsonContext.Default.ResumeSession));
            SettingsStore.Log($"Updater: saved resume_session.json (voice={snap.VoiceChannelId}, watches={snap.WatchedStreams.Count}, streaming={snap.WasStreaming})");

            int pid = Environment.ProcessId;
            string appDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            string stageDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(StagedDir));
            string backupDir = Path.Combine(UpdatesDir, "backup_prev");
            string swapPs1 = Path.Combine(UpdatesDir, "apply_update.ps1");
            string errFile = ErrorMarkerPath;
            string newExe = Path.Combine(appDir, "FluxCordP2P.exe");

            static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";
            string script = $@"
$ErrorActionPreference = 'SilentlyContinue'
$pidTarget = {pid}
$src = {PsQuote(stageDir)}
$dst = {PsQuote(appDir)}
$bak = {PsQuote(backupDir)}
$errOut = {PsQuote(errFile)}
$exe = {PsQuote(newExe)}

# Wait cleanly up to 8 seconds for FluxCord to flush SQLite/GPU/Discord Disconnect
try {{
    $p = Get-Process -Id $pidTarget -ErrorAction Stop
    if (-not $p.WaitForExit(8000)) {{
        Stop-Process -Id $pidTarget -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 300
    }}
}} catch {{}}
Start-Sleep -Milliseconds 120

# Full backup of existing app files for atomic rollback safety
if (Test-Path $bak) {{ Remove-Item $bak -Recurse -Force }}
New-Item -ItemType Directory -Force -Path $bak | Out-Null
Get-ChildItem -Path $src -Recurse -File | Where-Object {{ $_.Name -ne '.verified_sha256' }} | ForEach-Object {{
    $rel = $_.FullName.Substring($src.Length).TrimStart('\','/')
    $dstFile = Join-Path $dst $rel
    if (Test-Path $dstFile) {{
        $bakFile = Join-Path $bak $rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $bakFile) | Out-Null
        Copy-Item $dstFile $bakFile -Force
    }}
}}

# Copy staged release files with retry + automatic rollback on any failure
$ok = $true
$lastErr = ''
for ($try = 0; $try -lt 6; $try++) {{
    $ok = $true
    Get-ChildItem -Path $src -Recurse -File | Where-Object {{ $_.Name -ne '.verified_sha256' }} | ForEach-Object {{
        $srcFile = $_.FullName
        $rel = $srcFile.Substring($src.Length).TrimStart('\','/')
        $dstFile = Join-Path $dst $rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dstFile) | Out-Null
        try {{ Copy-Item $srcFile $dstFile -Force -ErrorAction Stop }} catch {{ $ok = $false; $lastErr = $_.Exception.Message }}
    }}
    if ($ok) {{ break }}
    Start-Sleep -Milliseconds 250
}}

if (-not $ok) {{
    Set-Content -Path $errOut -Value $lastErr -Encoding UTF8
    for ($rtry = 0; $rtry -lt 4; $rtry++) {{
        $rbOk = $true
        Get-ChildItem -Path $bak -Recurse -File | ForEach-Object {{
            $bakFile = $_.FullName
            $rel = $bakFile.Substring($bak.Length).TrimStart('\','/')
            $dstFile = Join-Path $dst $rel
            try {{ Copy-Item $bakFile $dstFile -Force -ErrorAction Stop }} catch {{ $rbOk = $false }}
        }}
        if ($rbOk) {{ break }}
        Start-Sleep -Milliseconds 200
    }}
}}

Start-Process -FilePath $exe -ArgumentList '--resumed-update' -WorkingDirectory $dst
Remove-Item $MyInvocation.MyCommand.Path -Force
";
            Directory.CreateDirectory(UpdatesDir);
            // UTF-8 with BOM ensures Windows PowerShell 5.1 parses Cyrillic/Unicode paths 100% faithfully!
            File.WriteAllText(swapPs1, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{swapPs1}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            App.RequestExit();
        }
        catch (Exception ex)
        {
            _applying = false;
            SettingsStore.Log("Updater apply error: " + ex);
            StatusText = "Ошибка установки: " + ex.Message;
            Shell.Toast("Не удалось применить обновление: " + ex.Message, 5000);
            App.WakeUi();
        }
    }

    public static bool IsNewer(string remote, string local)
    {
        if (string.IsNullOrWhiteSpace(remote)) return false;
        remote = remote.Trim().TrimStart('v', 'V');
        local = (local ?? "").Trim().TrimStart('v', 'V');
        string remPre = ExtractPreRelease(remote, out string remCore);
        string locPre = ExtractPreRelease(local, out string locCore);
        if (!Version.TryParse(Normalize(remCore), out var rv) || !Version.TryParse(Normalize(locCore), out var lv))
            return false;
        int cmp = CompareVersion4(rv, lv);
        if (cmp != 0) return cmp > 0;
        // SemVer 2.0: 1.0.4 > 1.0.4-rc1 (stable beats pre-release of identical numeric version!)
        if (string.IsNullOrEmpty(remPre) && !string.IsNullOrEmpty(locPre)) return true;
        if (!string.IsNullOrEmpty(remPre) && string.IsNullOrEmpty(locPre)) return false;
        return !string.IsNullOrEmpty(remPre) && string.Compare(remPre, locPre, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static int CompareVersion4(Version a, Version b)
    {
        int c = a.Major.CompareTo(b.Major); if (c != 0) return c;
        c = a.Minor.CompareTo(b.Minor); if (c != 0) return c;
        c = Math.Max(0, a.Build).CompareTo(Math.Max(0, b.Build)); if (c != 0) return c;
        return Math.Max(0, a.Revision).CompareTo(Math.Max(0, b.Revision));
    }

    private static string ExtractPreRelease(string v, out string core)
    {
        int dash = v.IndexOf('-');
        if (dash >= 0) { core = v[..dash]; return v[(dash + 1)..]; }
        core = v;
        return "";
    }

    private static string Normalize(string v)
    {
        var parts = v.Split('.');
        return parts.Length == 1 ? v + ".0" : v;
    }

    private static string TruncateSafe(string s, int maxChars)
    {
        if (s.Length <= maxChars) return s;
        int len = maxChars;
        if (char.IsHighSurrogate(s[len - 1])) len--;
        return s[..len] + "…";
    }

    private static string FormatSize(long b)
        => b >= 1024 * 1024 ? $"{b / (1024.0 * 1024.0):F1} МБ" : (b >= 1024 ? $"{b / 1024.0:F0} КБ" : $"{b} Б");
}
