using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluxCord;

public sealed class FluxSettings
{
    public int WindowWidth { get; set; } = 1360;
    public int WindowHeight { get; set; } = 840;
    public float UiScale { get; set; } = 1.0f;
    public string Theme { get; set; } = "Obsidian";
    public bool MigratedFromDorion { get; set; }

    // Migrated from Dorion / Discord
    public bool PushToTalk { get; set; }
    public string PushToTalkKey { get; set; } = "RControl";
    public bool DesktopNotifications { get; set; }
    public bool StreamerMode { get; set; }
    public bool UnreadBadge { get; set; } = true;
    public bool HardwareAccel { get; set; } = true;
    public bool UiSoundsEnabled { get; set; } = true;
    public float UiSoundsVolume { get; set; } = 0.75f;
    public bool ShowMemberList { get; set; } = true;
    public string PresenceStatus { get; set; } = "online"; // online, idle, dnd, invisible
    public string PreferredRtcRegion { get; set; } = ""; // empty = Auto-Fastest benchmarked region
    public bool TurboLowLatency { get; set; } = true;
    public bool AutoCheckUpdates { get; set; } = false;
    public bool AutoDownloadUpdates { get; set; } = false;
    public bool CloseToTray { get; set; } = true;

    // P2P + Jetson Orin Nano Identity & Room Config
    public string P2pUserId { get; set; } = "";
    public string P2pNickname { get; set; } = "";
    public string P2pHostList { get; set; } = "192.168.100.72:37400,jetsno-orion.local:37400,185.27.71.230:37400";

    // Voice & Multi-Backend Noise Suppression
    public string CaptureDeviceId { get; set; } = "";
    public string RenderDeviceId { get; set; } = "";
    public int NoiseMode { get; set; } = (int)FluxCord.NoiseMode.AutoBest;
    public float NoiseStrength { get; set; } = 0.88f;
    public bool HpfEnabled { get; set; } = true;
    public bool AgcEnabled { get; set; } = true;
    public float GateDb { get; set; } = -46.0f;
    public float InputGain { get; set; } = 1.0f;
    public float OutputVolume { get; set; } = 1.0f;

    // 4K Zero-Copy Streaming (NVENC)
    public int StreamMonitor { get; set; } = 0;
    public int StreamWidth { get; set; } = 1920;
    public int StreamHeight { get; set; } = 1080;
    public int StreamFps { get; set; } = 60;
    public int StreamBitrateKbps { get; set; } = 16000;
    public int StreamCodec { get; set; } = (int)VideoCodec.H264;
    public bool StreamOptimizeDetail { get; set; } = true;
    public bool StreamCaptureAudio { get; set; } = true;

    // DPAPI-encrypted Discord token (base64)
    public string EncryptedTokenB64 { get; set; } = "";
    public string LastGuildId { get; set; } = "";
    public string LastChannelId { get; set; } = "";
}

public sealed class P2pPreset
{
    public string Nickname { get; set; } = "";
    public string RoomSecret { get; set; } = "";
    public string HostList { get; set; } = "";
    public bool AutoConnect { get; set; } = true;
}

[JsonSerializable(typeof(FluxSettings))]
[JsonSerializable(typeof(ResumeSession))]
[JsonSerializable(typeof(P2pPreset))]
internal partial class FluxJsonContext : JsonSerializerContext { }

public static class SettingsStore
{
    public static readonly string DataDir = ResolveDataDir();
    public static readonly string SettingsPath = Path.Combine(DataDir, "settings.json");
    public static readonly string LogPath = Path.Combine(DataDir, "fluxcord_p2p.log");

    private static string ResolveDataDir()
    {
        string? env = Environment.GetEnvironmentVariable("FLUXCORD_DATA_DIR")?.Trim();
        if (!string.IsNullOrEmpty(env)) return Path.GetFullPath(env);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluxCordP2P");
    }

    private static readonly object _saveLock = new();
    private static readonly object _logLock = new();
    private static int _logWrites;

    public static FluxSettings Current { get; private set; } = new();
    public static string MigrationSummary { get; private set; } = "";

    public static void Log(string msg)
    {
        lock (_logLock)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                if ((++_logWrites & 127) == 0 && File.Exists(LogPath) && new FileInfo(LogPath).Length > 2 * 1024 * 1024)
                {
                    string prev = Path.Combine(DataDir, "fluxcord.prev.log");
                    File.Move(LogPath, prev, overwrite: true);
                }
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
            }
            catch { }
        }
    }

    public static void Load()
    {
        lock (_saveLock)
        {
            Directory.CreateDirectory(DataDir);
            if (File.Exists(SettingsPath))
            {
                try
                {
                    var json = File.ReadAllText(SettingsPath);
                    Current = JsonSerializer.Deserialize(json, FluxJsonContext.Default.FluxSettings) ?? new();
                    Sanitize(Current);
                }
                catch { Current = new(); }
            }

            if (string.IsNullOrEmpty(Current.P2pUserId))
            {
                Span<byte> rnd = stackalloc byte[6];
                RandomNumberGenerator.Fill(rnd);
                Current.P2pUserId = "u_" + Convert.ToHexString(rnd).ToLowerInvariant();
            }

            // Apply bundled p2p_preset.json on first launch so User & Lapki connect in 0 clicks!
            string presetPath = Path.Combine(AppContext.BaseDirectory, "p2p_preset.json");
            if (File.Exists(presetPath))
            {
                try
                {
                    var preset = JsonSerializer.Deserialize(File.ReadAllText(presetPath), FluxJsonContext.Default.P2pPreset);
                    if (preset != null)
                    {
                        if (string.IsNullOrEmpty(Current.P2pNickname) && !string.IsNullOrEmpty(preset.Nickname))
                            Current.P2pNickname = preset.Nickname;
                        if (!string.IsNullOrEmpty(preset.HostList))
                            Current.P2pHostList = preset.HostList;
                        if (string.IsNullOrEmpty(Current.EncryptedTokenB64) && !string.IsNullOrEmpty(preset.RoomSecret))
                        {
                            var plain = Encoding.UTF8.GetBytes(preset.RoomSecret.Trim());
                            var enc = W.ProtectDpapi(plain);
                            if (enc.Length > 0) Current.EncryptedTokenB64 = Convert.ToBase64String(enc);
                        }
                    }
                }
                catch { }
            }
            if (string.IsNullOrEmpty(Current.P2pNickname))
                Current.P2pNickname = Environment.UserName;
            Current.MigratedFromDorion = true;
            SaveLocked();
        }
    }

    private static void Sanitize(FluxSettings s)
    {
        if (!float.IsFinite(s.UiScale)) s.UiScale = 1.0f;
        s.UiScale = Math.Clamp(s.UiScale, 0.8f, 1.5f);
        if (!float.IsFinite(s.InputGain)) s.InputGain = 1.0f;
        s.InputGain = Math.Clamp(s.InputGain, 0f, 2f);
        if (!float.IsFinite(s.OutputVolume)) s.OutputVolume = 1.0f;
        s.OutputVolume = Math.Clamp(s.OutputVolume, 0f, 2f);
        if (!float.IsFinite(s.NoiseStrength)) s.NoiseStrength = 0.88f;
        s.NoiseStrength = Math.Clamp(s.NoiseStrength, 0f, 1f);
        if (!float.IsFinite(s.GateDb)) s.GateDb = -46f;
        s.GateDb = Math.Clamp(s.GateDb, -75f, -15f);
        if (!float.IsFinite(s.UiSoundsVolume)) s.UiSoundsVolume = 0.75f;
        s.UiSoundsVolume = Math.Clamp(s.UiSoundsVolume, 0f, 1f);
        s.WindowWidth = Math.Clamp(s.WindowWidth, 860, 3840);
        s.WindowHeight = Math.Clamp(s.WindowHeight, 560, 2160);
        s.StreamFps = Math.Clamp(s.StreamFps, 15, 120);
        s.StreamBitrateKbps = Math.Clamp(s.StreamBitrateKbps, 1000, 35000);
        s.StreamCodec = (int)VideoCodec.H264;
    }

    public static void Save()
    {
        lock (_saveLock) SaveLocked();
    }

    private static void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var json = JsonSerializer.Serialize(Current, FluxJsonContext.Default.FluxSettings);
            // Atomic temp-file write prevents settings.json / DPAPI token corruption on concurrent save or power loss
            string tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        catch { }
    }

    public static string GetToken()
    {
        lock (_saveLock)
        {
            if (string.IsNullOrEmpty(Current.EncryptedTokenB64)) return "";
            byte[]? plain = null;
            try
            {
                var enc = Convert.FromBase64String(Current.EncryptedTokenB64);
                plain = W.UnprotectDpapi(enc);
                return plain.Length > 0 ? Encoding.UTF8.GetString(plain) : "";
            }
            catch { return ""; }
            finally
            {
                if (plain is { Length: > 0 }) CryptographicOperations.ZeroMemory(plain);
            }
        }
    }

    public static void SetToken(string token)
    {
        lock (_saveLock)
        {
            token = token.Trim().Trim('"');
            if (string.IsNullOrEmpty(token))
            {
                Current.EncryptedTokenB64 = "";
            }
            else
            {
                byte[] raw = Encoding.UTF8.GetBytes(token);
                try
                {
                    var enc = W.ProtectDpapi(raw);
                    if (enc.Length == 0)
                    {
                        Log("SetToken: DPAPI ProtectData failed, preserving previous token");
                        return;
                    }
                    Current.EncryptedTokenB64 = Convert.ToBase64String(enc);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(raw);
                }
            }
            SaveLocked();
        }
    }

    public static void MigrateFromExistingClients()
    {
        var imported = new List<string>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var dorionCfg = Path.Combine(appData, "dorion", "config.json");
        if (File.Exists(dorionCfg))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(dorionCfg));
                var r = doc.RootElement;
                if (r.TryGetProperty("push_to_talk", out var ptt) && ptt.ValueKind == JsonValueKind.True) Current.PushToTalk = true;
                if (r.TryGetProperty("push_to_talk_keys", out var pttk) && pttk.ValueKind == JsonValueKind.Array && pttk.GetArrayLength() > 0)
                    Current.PushToTalkKey = pttk[0].GetString() ?? "RControl";
                if (r.TryGetProperty("desktop_notifications", out var dn)) Current.DesktopNotifications = dn.ValueKind == JsonValueKind.True;
                if (r.TryGetProperty("streamer_mode_detection", out var sm)) Current.StreamerMode = sm.ValueKind == JsonValueKind.True;
                if (r.TryGetProperty("unread_badge", out var ub)) Current.UnreadBadge = ub.ValueKind == JsonValueKind.True;
                if (r.TryGetProperty("zoom", out var zm))
                {
                    if (zm.ValueKind == JsonValueKind.Number && zm.TryGetSingle(out var zf))
                        Current.UiScale = Math.Clamp(zf, 0.8f, 1.5f);
                    else if (zm.ValueKind == JsonValueKind.String && float.TryParse(zm.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var zs))
                        Current.UiScale = Math.Clamp(zs, 0.8f, 1.5f);
                }
                imported.Add("Dorion (PTT, уведомления, масштаб, значки)");
            }
            catch { }
        }

        var discordCfg = Path.Combine(appData, "discord", "settings.json");
        if (File.Exists(discordCfg))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(discordCfg));
                var r = doc.RootElement;
                if (r.TryGetProperty("WINDOW_BOUNDS", out var wb))
                {
                    if (wb.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number) Current.WindowWidth = Math.Clamp(w.GetInt32(), 960, 2560);
                    if (wb.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number) Current.WindowHeight = Math.Clamp(h.GetInt32(), 640, 1440);
                }
                imported.Add("Discord Desktop (геометрия окна)");
            }
            catch { }
        }

        Current.MigratedFromDorion = true;
        MigrationSummary = imported.Count > 0
            ? "Перенесено: " + string.Join(", ", imported)
            : "Создан чистый профиль FluxCord";
        Log("Settings migration: " + MigrationSummary);
    }
}
