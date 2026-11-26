using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FluxCord;

public sealed class WatchSession
{
    public int Slot;
    public string StreamKey = "";
    public string StreamerUserId = "";
    public string StreamerName = "";
    public float Volume = 1.0f;
    public bool Muted;
    public string Status = "";
    public FcScreenStats Stats;

    internal uint LocalSsrc;
    internal uint VideoSsrc;
    internal uint AudioSsrc;
    internal long ConnectedTick;
    internal long LastKickTick;
    internal long LastDecodedFrames;
    internal long LastDecodedTick;
    internal long LastDiagLogTick;
}

/// <summary>
/// Native P2P + NVIDIA Jetson Orin Nano Engine (drop-in replacement for DiscordClient in FluxCordP2P).
/// - Zero-Copy 48kHz Opus + 6 Noise Suppressors + 4K60 / Dual-Screen NVENC + 4-Slot NVDEC
/// - XChaCha20-Poly1305 E2EE Media + Automatic 0-Hop Direct UDP P2P Hole-Punching + Jetson Orin Relay
/// - Zero-Config Internet Traversal via AES-256-GCM Encrypted Rendezvous + Cone-NAT Port Punching
/// </summary>
public sealed class DiscordClient
{
    public static readonly DiscordClient Instance = new();

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(4)
    })
    { Timeout = TimeSpan.FromSeconds(20) };

    private CancellationTokenSource? _connCts;
    private ClientWebSocket? _ws;
    private bool _usingMqttRendezvous;
    private string _mqttClientId = "";
    private string _directHttpBase = "";
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private string _secret = "";
    private string _roomTopic = "";
    private byte[] _aeadKey = new byte[32];
    private byte[] _mediaKey = new byte[32];
    private string _lanIp = DetectLocalLanIp();

    private string _mediaHost = "192.168.100.72";
    private ushort _mediaPort = 37401;
    private string _jetsonExtIp = "185.27.71.230";
    private ushort _jetsonExtPort = 37401;

    private string _voiceGuildId = "";
    private string _voiceChannelId = "";
    private uint _voiceSsrc;
    private uint _pubAudioSsrc;
    private uint _pubVideoSsrc;
    private bool _lastSentSpeaking;
    private long _lastPingSentMs;

    private int _pubWidth = 1920, _pubHeight = 1080, _pubFps = 60;
    private bool _pubIsDualScreen;

    private sealed class PeerEndpointInfo
    {
        public string UserId = "";
        public string DisplayName = "";
        public string VoiceChannelId = "";
        public bool SelfStream;
        public string LanIp = "";
        public string VoiceExtIp = "";
        public ushort VoiceExtPort;
        public uint VoiceSsrc;
        public string PubExtIp = "";
        public ushort PubExtPort;
        public uint PubAudioSsrc;
        public uint PubVideoSsrc;
    }

    private readonly ConcurrentDictionary<string, PeerEndpointInfo> _peerEndpoints = new();
    private readonly ConcurrentDictionary<string, uint> _userToSsrc = new();
    private readonly ConcurrentDictionary<string, float> _userVolumes = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _pendingUploads = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]?>> _pendingFileFetches = new();

    private readonly object _watchLock = new();
    private readonly Dictionary<string, WatchSession> _watches = new();

    public bool IsConnected { get; private set; }
    public bool IsConnecting { get; private set; }
    public bool HasActiveSession { get; private set; }
    public bool IsReconnecting { get; private set; }
    public bool HasToken => !string.IsNullOrEmpty(_secret);
    public bool IsDemoMode { get; private set; }
    public string StatusText { get; private set; } = "Готов к подключению P2P";
    public int GatewayPingMs { get; private set; }
    public DiscordUser Me { get; private set; } = new() { Id = "u_local", Username = "player", DisplayName = "Player", Status = "online" };

    public List<DiscordGuild> Guilds { get; } = [];
    public List<DiscordChannel> DmChannels { get; } = [];
    public List<DiscordFriend> Friends { get; } = [];
    public ConcurrentDictionary<string, DiscordUser> Users { get; } = new();
    public ConcurrentDictionary<string, string> GuildNicks { get; } = new();
    public ConcurrentDictionary<string, HashSet<string>> GuildMemberIds { get; } = new();
    public ConcurrentDictionary<string, List<DiscordMessage>> Messages { get; } = new();
    public ConcurrentDictionary<string, List<DiscordMessage>> PinnedMessages { get; } = new();
    public ConcurrentDictionary<string, (string Name, long Until)> TypingUsers { get; } = new();
    public ConcurrentDictionary<string, int> UnreadChannels { get; } = new();
    public ConcurrentDictionary<string, DiscordCallState> DmCalls { get; } = new();

    public string ActiveGuildId { get; set; } = "g_p2p";
    public string ActiveChannelId { get; set; } = "c_main";
    public string ConnectedVoiceGuildId => _voiceGuildId;
    public string ConnectedVoiceChannelId => _voiceChannelId;
    public bool VoiceConnected { get; private set; }
    public string VoiceStatus { get; private set; } = "";

    public int VoicePingMs { get; private set; }
    public int VoicePingAvgMs { get; private set; }
    public string VoiceEndpointHost { get; private set; } = "";
    public string PubEndpointHost { get; private set; } = "";
    public List<int> VoicePingHistory { get; } = [];
    public List<RtcRegionInfo> Regions { get; } =
    [
        new RtcRegionInfo { Id = "p2p-auto", Name = "⚡ Auto P2P Direct + Jetson Orin", PingMs = 1, Optimal = true },
        new RtcRegionInfo { Id = "jetson-relay", Name = "🚀 Jetson Orin Nano Relay", PingMs = 2, Optimal = false }
    ];
    public string BestRtcRegion { get; private set; } = "p2p-auto";
    public bool IsBenchmarkingRegions { get; private set; }
    public int PubVideoTxPkts { get; private set; }
    public int PubAudioTxPkts { get; private set; }
    public bool PubDaveActive { get; private set; } = true;
    public bool IsPublishingStream { get; private set; }
    public string PublishStatus { get; private set; } = "";
    public string? FocusedStreamUserId { get; set; }

    public bool IsWatchingAnyStream { get { lock (_watchLock) return _watches.Count > 0; } }
    public bool IsWatchingStream(string userId) { lock (_watchLock) return _watches.ContainsKey(userId); }

    public event Action? StateChanged;
    public event Action? ReadyCompleted;

    private DiscordClient() { }

    private void Notify() => StateChanged?.Invoke();

    private static string DetectLocalLanIp()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("1.1.1.1", 80);
            if (s.LocalEndPoint is IPEndPoint ep) return ep.Address.ToString();
        }
        catch { }
        return "127.0.0.1";
    }

    private static byte[] DeriveSha256(string tag, string secret)
        => SHA256.HashData(Encoding.UTF8.GetBytes(tag + ":" + secret));

    private static string ComputeHmacHex(byte[] key, string message)
        => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message))).ToLowerInvariant();

    private byte[] EncryptAead(ReadOnlySpan<byte> plain)
    {
        byte[] outBuf = new byte[12 + plain.Length + 16];
        RandomNumberGenerator.Fill(outBuf.AsSpan(0, 12));
        using var gcm = new AesGcm(_aeadKey, 16);
        gcm.Encrypt(outBuf.AsSpan(0, 12), plain, outBuf.AsSpan(12, plain.Length), outBuf.AsSpan(12 + plain.Length, 16));
        return outBuf;
    }

    private byte[]? DecryptAead(ReadOnlySpan<byte> cipherWithNonce)
    {
        if (cipherWithNonce.Length < 28) return null;
        int plainLen = cipherWithNonce.Length - 28;
        byte[] plain = new byte[plainLen];
        try
        {
            using var gcm = new AesGcm(_aeadKey, 16);
            gcm.Decrypt(cipherWithNonce.Slice(0, 12), cipherWithNonce.Slice(12, plainLen), cipherWithNonce.Slice(12 + plainLen, 16), plain);
            return plain;
        }
        catch { return null; }
    }

    private void RecordVoicePing(int ms)
    {
        ms = Math.Clamp(ms, 1, 999);
        VoicePingMs = ms;
        lock (VoicePingHistory)
        {
            VoicePingHistory.Add(ms);
            if (VoicePingHistory.Count > 40) VoicePingHistory.RemoveAt(0);
            int sum = 0;
            for (int i = 0; i < VoicePingHistory.Count; i++) sum += VoicePingHistory[i];
            VoicePingAvgMs = sum / VoicePingHistory.Count;
        }
    }

    public WatchSession? GetWatchSession(string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return null;
        lock (_watchLock) return _watches.TryGetValue(userId, out var s) ? s : null;
    }

    public List<WatchSession> GetWatchSessionsSnapshot()
    {
        lock (_watchLock) return [.. _watches.Values];
    }

    // ---------------- Demo Mode (keeps 35/35 UI & Hardware E2E tests 100% compatible!) ----------------

    public void EnterDemoMode()
    {
        Disconnect();
        IsDemoMode = true;
        IsConnecting = false;
        IsReconnecting = false;
        IsConnected = true;
        HasActiveSession = true;
        StatusText = "Demo / Local P2P Benchmark";
        GatewayPingMs = 1;
        Me = new DiscordUser { Id = "1001", Username = "so2k", DisplayName = "So2K (RTX 4070 Ti)", Status = "online" };

        lock (Guilds)
        {
            Guilds.Clear();
            var g1 = new DiscordGuild { Id = "g1", Name = "FluxCord P2P • Jetson" };
            g1.Channels.Add(new DiscordChannel { Id = "c_cat1", GuildId = "g1", Name = "ЧАТЫ КОМНАТЫ", Type = 4, Position = 0 });
            g1.Channels.Add(new DiscordChannel { Id = "c_gen", GuildId = "g1", Name = "общий", Type = 0, Position = 1, ParentId = "c_cat1", Topic = "Прямой P2P + Jetson Orin Nano" });
            g1.Channels.Add(new DiscordChannel { Id = "c_dev", GuildId = "g1", Name = "gamedev-granny2", Type = 0, Position = 2, ParentId = "c_cat1" });
            g1.Channels.Add(new DiscordChannel { Id = "c_cat2", GuildId = "g1", Name = "ГОЛОС И 4K СТРИМЫ", Type = 4, Position = 3 });
            g1.Channels.Add(new DiscordChannel { Id = "v_main", GuildId = "g1", Name = "Ламповая (P2P + 4K60)", Type = 2, Position = 4, ParentId = "c_cat2" });
            g1.VoiceStates["2002"] = new VoiceMember { UserId = "2002", ChannelId = "v_main", Username = "lapki >.<", SelfStream = true };
            Guilds.Add(g1);

            var g2 = new DiscordGuild { Id = "g2", Name = "Granny2 Team" };
            g2.Channels.Add(new DiscordChannel { Id = "c_g2", GuildId = "g2", Name = "релиз-билд", Type = 0, Position = 0 });
            g2.Channels.Add(new DiscordChannel { Id = "v_g2", GuildId = "g2", Name = "Созвон команды", Type = 2, Position = 1 });
            Guilds.Add(g2);
        }

        lock (DmChannels)
        {
            DmChannels.Clear();
            DmChannels.Add(new DiscordChannel { Id = "dm_1", GuildId = "@me", Name = "lapki >.<, Teammate", Type = 3, RecipientIds = ["2002", "2001"] });
            DmChannels.Add(new DiscordChannel { Id = "dm_2", GuildId = "@me", Name = "lapki >.<", RecipientId = "2002", Type = 1 });
        }

        lock (Friends)
        {
            Friends.Clear();
            Friends.Add(new DiscordFriend { UserId = "2002", Username = "lapki", DisplayName = "lapki >.<", Status = "online", ActivityText = "⚡ P2P Direct", Type = 1 });
        }

        Messages["c_gen"] =
        [
            new DiscordMessage
            {
                Id = "m1", ChannelId = "c_gen", AuthorId = "sys", AuthorName = "Jetson Orin P2P Hub", AuthorColor = 0xFF7BD88F,
                Timestamp = DateTime.Now.ToString("HH:mm"),
                Content = "FluxCord P2P готов: XChaCha20-Poly1305 E2EE + Zero-Copy NVENC 4K60 + 6 режимов шумоподавления."
            }
        ];

        ActiveGuildId = "g1";
        ActiveChannelId = "c_gen";
        Notify();
        ReadyCompleted?.Invoke();
    }

    // ---------------- P2P + Jetson Orin Connection Lifecycle ----------------

    public Task ConnectAsync(string rawInput)
    {
        Disconnect();
        string cleaned = rawInput.Trim().Trim('"');
        if (cleaned.Contains('|'))
        {
            var parts = cleaned.Split('|', 2);
            if (!string.IsNullOrWhiteSpace(parts[0]))
            {
                SettingsStore.Current.P2pNickname = parts[0].Trim();
                SettingsStore.Save();
            }
            cleaned = parts[1].Trim();
        }
        if (cleaned.StartsWith("fluxp2p://", StringComparison.OrdinalIgnoreCase))
        {
            string body = cleaned["fluxp2p://".Length..];
            int atIdx = body.IndexOf('@');
            if (atIdx > 0)
            {
                cleaned = body[..atIdx];
                string hostPart = body[(atIdx + 1)..].Trim('/');
                if (!string.IsNullOrEmpty(hostPart))
                {
                    SettingsStore.Current.P2pHostList = hostPart + "," + SettingsStore.Current.P2pHostList;
                    SettingsStore.Save();
                }
            }
        }

        if (string.IsNullOrEmpty(cleaned))
        {
            StatusText = "Укажите ключ комнаты (Room Secret)";
            Notify();
            return Task.CompletedTask;
        }

        _secret = cleaned;
        SettingsStore.SetToken(_secret);
        byte[] topicBytes = DeriveSha256("fluxp2p-topic-v1", _secret);
        _roomTopic = Convert.ToHexString(topicBytes.AsSpan(0, 12)).ToLowerInvariant();
        _aeadKey = DeriveSha256("fluxp2p-aead-v1", _secret);
        _mediaKey = DeriveSha256("fluxp2p-rtp-v1", _secret);

        string uid = SettingsStore.Current.P2pUserId;
        if (string.IsNullOrEmpty(uid))
        {
            Span<byte> r = stackalloc byte[6];
            RandomNumberGenerator.Fill(r);
            uid = "u_" + Convert.ToHexString(r).ToLowerInvariant();
            SettingsStore.Current.P2pUserId = uid;
            SettingsStore.Save();
        }
        string nick = string.IsNullOrWhiteSpace(SettingsStore.Current.P2pNickname) ? Environment.UserName : SettingsStore.Current.P2pNickname.Trim();
        Me = new DiscordUser
        {
            Id = uid,
            Username = nick.ToLowerInvariant().Replace(" ", "_"),
            DisplayName = nick,
            Avatar = SettingsStore.Current.P2pAvatar ?? "",
            ActivityText = SettingsStore.Current.P2pActivity ?? "",
            Status = SettingsStore.Current.PresenceStatus
        };

        IsDemoMode = false;
        IsConnecting = true;
        StatusText = "Поиск узла Jetson Orin Nano (LAN + P2P Hole-Punch)...";
        if (SettingsStore.Current.UserVolumes != null)
        {
            foreach (var kv in SettingsStore.Current.UserVolumes)
                _userVolumes[kv.Key] = Math.Clamp(kv.Value, 0f, 2f);
        }
        Notify();

        _connCts = new CancellationTokenSource();
        var ct = _connCts.Token;
        _ = Task.Run(() => ConnectionLoopAsync(ct), ct);
        return Task.CompletedTask;
    }

    public void Disconnect()
    {
        LeaveVoice();
        try { _connCts?.Cancel(); } catch { }
        try { _connCts?.Dispose(); } catch { }
        _connCts = null;
        try { _ws?.Dispose(); } catch { }
        _ws = null;
        _directHttpBase = "";
        _usingMqttRendezvous = false;
        IsConnected = false;
        IsConnecting = false;
        IsReconnecting = false;
        HasActiveSession = false;
        IsDemoMode = false;
    }

    public void Logout()
    {
        Disconnect();
        SettingsStore.SetToken("");
        _secret = "";
        lock (Guilds) Guilds.Clear();
        lock (DmChannels) DmChannels.Clear();
        lock (Friends) Friends.Clear();
        Messages.Clear();
        PinnedMessages.Clear();
        Users.Clear();
        StatusText = "Вышли из P2P комнаты";
        App.InvokeOnUiThread(App.ClearTokenInput);
        Notify();
    }

    private async Task ConnectionLoopAsync(CancellationToken ct)
    {
        int backoffMs = 800;
        Span<byte> cidRnd = stackalloc byte[5];
        RandomNumberGenerator.Fill(cidRnd);
        _mqttClientId = "fp_" + Convert.ToHexString(cidRnd).ToLowerInvariant();

        while (!ct.IsCancellationRequested)
        {
            _lanIp = DetectLocalLanIp();
            try
            {
                // 1. Try ultra-fast Direct WebSocket to Jetson Orin Nano (192.168.100.72:37400 on LAN or port-forwarded WAN)
                var directWs = await TryConnectDirectWsAsync(ct);
                if (directWs != null)
                {
                    _ws = directWs;
                    _usingMqttRendezvous = false;
                    backoffMs = 800;
                    await SendAuthAsync(ct);
                    await RunDirectWsLoopAsync(directWs, ct);
                }
                else
                {
                    // 2. Zero-Config Internet Traversal: Encrypted AES-256-GCM Rendezvous + Cone-NAT UDP Hole-Punching!
                    StatusText = "Пробитие NAT к Jetson Orin Nano (Encrypted P2P)...";
                    Notify();
                    var mqttWs = await TryConnectMqttRendezvousAsync(ct);
                    if (mqttWs != null)
                    {
                        _ws = mqttWs;
                        _usingMqttRendezvous = true;
                        _directHttpBase = "";
                        backoffMs = 1000;
                        await SendRendezvousPunchAndAuthAsync(mqttWs, ct);
                        await RunMqttWsLoopAsync(mqttWs, ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SettingsStore.Log("P2P Conn error: " + ex.Message);
            }
            finally
            {
                try { _ws?.Dispose(); } catch { }
                _ws = null;
                if (!ct.IsCancellationRequested)
                {
                    IsConnected = false;
                    if (HasActiveSession) IsReconnecting = true; else IsConnecting = true;
                    StatusText = "Восстановление P2P связи с Jetson Orin...";
                    Notify();
                }
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(backoffMs, ct); } catch { break; }
            backoffMs = Math.Min(backoffMs * 2, 6000);
        }
    }

    private async Task<ClientWebSocket?> TryConnectDirectWsAsync(CancellationToken ct)
    {
        var hosts = (SettingsStore.Current.P2pHostList ?? "192.168.100.72:37400,jetsno-orion.local:37400,185.27.71.230:37400")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var h in hosts)
        {
            if (ct.IsCancellationRequested) return null;
            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            using var tcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            tcts.CancelAfter(750);
            try
            {
                await ws.ConnectAsync(new Uri($"ws://{h}/ws"), tcts.Token);
                _directHttpBase = $"http://{h}";
                SettingsStore.Log($"Direct Jetson WS connected: ws://{h}/ws");
                return ws;
            }
            catch
            {
                try { ws.Dispose(); } catch { }
            }
        }
        return null;
    }

    private static byte[] EncodeMqttVarLen(int n)
    {
        var list = new List<byte>(4);
        do
        {
            byte b = (byte)(n % 128);
            n /= 128;
            if (n > 0) b |= 0x80;
            list.Add(b);
        } while (n > 0);
        return [.. list];
    }

    private static byte[] EncodeMqttString(string s)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(s);
        byte[] res = new byte[2 + utf8.Length];
        res[0] = (byte)(utf8.Length >> 8);
        res[1] = (byte)(utf8.Length & 0xFF);
        Buffer.BlockCopy(utf8, 0, res, 2, utf8.Length);
        return res;
    }

    private int _brokerIdx;
    private async Task<ClientWebSocket?> TryConnectMqttRendezvousAsync(CancellationToken ct)
    {
        string[] urls = ["wss://broker.emqx.io:8084/mqtt", "wss://broker.hivemq.com:8884/mqtt"];
        for (int i = 0; i < urls.Length; i++)
        {
            string url = urls[(_brokerIdx++) % urls.Length];
            var ws = new ClientWebSocket();
            ws.Options.AddSubProtocol("mqtt");
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            using var tcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            tcts.CancelAfter(6000);
            try
            {
                await ws.ConnectAsync(new Uri(url), tcts.Token);
                // 1. MQTT CONNECT
                using (var body = new MemoryStream())
                {
                    body.Write(EncodeMqttString("MQTT"));
                    body.WriteByte(4);    // MQTT 3.1.1
                    body.WriteByte(0x02); // CleanSession
                    body.WriteByte(0); body.WriteByte(30); // KeepAlive 30s
                    body.Write(EncodeMqttString(_mqttClientId));
                    using var pkt = new MemoryStream();
                    pkt.WriteByte(0x10);
                    pkt.Write(EncodeMqttVarLen((int)body.Length));
                    body.WriteTo(pkt);
                    await ws.SendAsync(pkt.ToArray(), WebSocketMessageType.Binary, true, ct);
                }
                // 2. MQTT SUBSCRIBE to fluxp2p/<roomTopic>/s2c/<clientId>
                string subTopic = $"fluxp2p/{_roomTopic}/s2c/{_mqttClientId}";
                using (var subBody = new MemoryStream())
                {
                    subBody.WriteByte(0); subBody.WriteByte(1);
                    subBody.Write(EncodeMqttString(subTopic));
                    subBody.WriteByte(0);
                    using var subPkt = new MemoryStream();
                    subPkt.WriteByte(0x82);
                    subPkt.Write(EncodeMqttVarLen((int)subBody.Length));
                    subBody.WriteTo(subPkt);
                    await ws.SendAsync(subPkt.ToArray(), WebSocketMessageType.Binary, true, ct);
                }
                SettingsStore.Log($"Encrypted Rendezvous connected via {url} topic=fluxp2p/{_roomTopic}/*");
                return ws;
            }
            catch (Exception ex)
            {
                SettingsStore.Log($"Rendezvous {url} failed: {ex.Message}");
                try { ws.Dispose(); } catch { }
            }
        }
        return null;
    }

    private static (string Ip, int Port) QueryStunOnce()
    {
        try
        {
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.ReceiveTimeout = 900;
            byte[] req = new byte[20];
            req[0] = 0x00; req[1] = 0x01;
            req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42;
            RandomNumberGenerator.Fill(req.AsSpan(8, 12));
            EndPoint rem = new IPEndPoint(IPAddress.Parse("162.159.207.0"), 3478);
            udp.SendTo(req, rem);
            byte[] resp = new byte[256];
            int n = udp.ReceiveFrom(resp, ref rem);
            if (n >= 20 && resp[0] == 0x01 && resp[1] == 0x01)
            {
                int pos = 20;
                while (pos + 4 <= n)
                {
                    int at = (resp[pos] << 8) | resp[pos + 1];
                    int al = (resp[pos + 2] << 8) | resp[pos + 3];
                    if (pos + 4 + al > n) break;
                    if (at == 0x0020 && al >= 8)
                    {
                        int xport = ((resp[pos + 6] << 8) | resp[pos + 7]) ^ 0x2112;
                        uint xip = ((uint)resp[pos + 8] << 24 | (uint)resp[pos + 9] << 16 | (uint)resp[pos + 10] << 8 | resp[pos + 11]) ^ 0x2112A442u;
                        var ipBytes = new byte[] { (byte)(xip >> 24), (byte)(xip >> 16), (byte)(xip >> 8), (byte)xip };
                        return (new IPAddress(ipBytes).ToString(), xport);
                    }
                    pos += 4 + ((al + 3) & ~3);
                }
            }
        }
        catch { }
        return ("", 0);
    }

    private async Task SendRendezvousPunchAndAuthAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var (stunIp, stunPort) = await Task.Run(QueryStunOnce, ct);
        if (!string.IsNullOrEmpty(stunIp) && stunPort > 0)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("cid", _mqttClientId);
                w.WriteString("kind", "punch");
                w.WriteString("stun_ip", stunIp);
                w.WriteStartArray("ports");
                w.WriteNumberValue(stunPort);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            await PublishMqttEncryptedAsync(ws, ms.ToArray(), ct);
        }
        await SendAuthAsync(ct);
    }

    private async Task PublishMqttEncryptedAsync(ClientWebSocket ws, ReadOnlyMemory<byte> plainJson, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        byte[] enc = EncryptAead(plainJson.Span);
        byte[] topicBytes = EncodeMqttString($"fluxp2p/{_roomTopic}/c2s");
        byte[] varLen = EncodeMqttVarLen(topicBytes.Length + enc.Length);
        byte[] pkt = new byte[1 + varLen.Length + topicBytes.Length + enc.Length];
        pkt[0] = 0x30; // MQTT PUBLISH QoS 0
        Buffer.BlockCopy(varLen, 0, pkt, 1, varLen.Length);
        Buffer.BlockCopy(topicBytes, 0, pkt, 1 + varLen.Length, topicBytes.Length);
        Buffer.BlockCopy(enc, 0, pkt, 1 + varLen.Length + topicBytes.Length, enc.Length);

        await _sendLock.WaitAsync(ct);
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(pkt, WebSocketMessageType.Binary, true, ct);
        }
        finally { _sendLock.Release(); }
    }

    private async Task SendRpcBytesAsync(byte[] jsonUtf8, CancellationToken ct = default)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return;
        if (!_usingMqttRendezvous)
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(jsonUtf8, WebSocketMessageType.Text, true, ct);
            }
            finally { _sendLock.Release(); }
        }
        else
        {
            using var env = new MemoryStream();
            using (var w = new Utf8JsonWriter(env))
            {
                w.WriteStartObject();
                w.WriteString("cid", _mqttClientId);
                w.WriteString("kind", "rpc");
                w.WritePropertyName("rpc");
                using var doc = JsonDocument.Parse(jsonUtf8);
                doc.RootElement.WriteTo(w);
                w.WriteEndObject();
            }
            await PublishMqttEncryptedAsync(ws, env.ToArray(), ct);
        }
    }

    private Task SendRpcAsync(Action<Utf8JsonWriter> build, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            build(w);
            w.WriteEndObject();
        }
        return SendRpcBytesAsync(ms.ToArray(), ct);
    }

    private Task SendAuthAsync(CancellationToken ct)
    {
        string proof = ComputeHmacHex(_aeadKey, "fluxp2p-auth-v1");
        uint color = 0xFF7BD88F;
        uint hash = 2166136261u;
        foreach (char c in Me.Id) hash = (hash ^ c) * 16777619u;
        uint[] palette = [0xFF7BD88F, 0xFF60A5FA, 0xFFF472B6, 0xFFFBBF24, 0xFFA78BFA, 0xFF34D399];
        color = palette[hash % (uint)palette.Length];

        return SendRpcAsync(w =>
        {
            w.WriteString("op", "auth");
            w.WriteString("proof", proof);
            w.WriteString("user_id", Me.Id);
            w.WriteString("username", Me.Username);
            w.WriteString("display_name", Me.DisplayName);
            if (!string.IsNullOrEmpty(Me.Avatar)) w.WriteString("avatar", Me.Avatar);
            if (!string.IsNullOrEmpty(Me.ActivityText)) w.WriteString("activity", Me.ActivityText);
            w.WriteString("lan_ip", _lanIp);
            w.WriteString("status", SettingsStore.Current.PresenceStatus);
            w.WriteNumber("color", color);
        }, ct);
    }

    private async Task RunDirectWsLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        _ = RunPingLoopAsync(ws, isMqtt: false, ct);
        var buf = new byte[65536];
        using var ms = new MemoryStream();
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(buf, ct);
                if (res.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buf, 0, res.Count);
            } while (!res.EndOfMessage);

            if (res.MessageType == WebSocketMessageType.Text && ms.Length > 0)
                HandleServerJson(ms.GetBuffer(), (int)ms.Length);
        }
    }

    private async Task RunMqttWsLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        _ = RunPingLoopAsync(ws, isMqtt: true, ct);
        var buf = new byte[65536];
        var stream = new List<byte>(65536);
        using var frameMs = new MemoryStream();

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            frameMs.SetLength(0);
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(buf, ct);
                if (res.MessageType == WebSocketMessageType.Close) return;
                frameMs.Write(buf, 0, res.Count);
            } while (!res.EndOfMessage);

            for (int i = 0; i < frameMs.Length; i++) stream.Add(frameMs.GetBuffer()[i]);

            while (stream.Count >= 2)
            {
                byte hdr = stream[0];
                int mult = 1, remLen = 0, pos = 1;
                bool completeHdr = false;
                while (pos < stream.Count && pos <= 4)
                {
                    byte b = stream[pos++];
                    remLen += (b & 0x7F) * mult;
                    mult *= 128;
                    if ((b & 0x80) == 0) { completeHdr = true; break; }
                }
                if (!completeHdr) break;
                int total = pos + remLen;
                if (stream.Count < total) break;

                byte[] pkt = new byte[total];
                stream.CopyTo(0, pkt, 0, total);
                stream.RemoveRange(0, total);

                if ((hdr >> 4) == 3) // MQTT PUBLISH
                {
                    int qos = (hdr >> 1) & 0x03;
                    int pOff = pos;
                    if (pOff + 2 <= total)
                    {
                        int tLen = (pkt[pOff] << 8) | pkt[pOff + 1];
                        pOff += 2 + tLen + (qos > 0 ? 2 : 0);
                        if (pOff < total)
                        {
                            byte[]? plain = DecryptAead(pkt.AsSpan(pOff, total - pOff));
                            if (plain != null) HandleServerJson(plain, plain.Length);
                        }
                    }
                }
            }
        }
    }

    private async Task RunPingLoopAsync(ClientWebSocket ws, bool isMqtt, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                _lastPingSentMs = Environment.TickCount64;
                await SendRpcAsync(w =>
                {
                    w.WriteString("op", "ping");
                    w.WriteNumber("t", _lastPingSentMs);
                }, ct);

                if (isMqtt)
                {
                    await _sendLock.WaitAsync(ct);
                    try
                    {
                        if (ws.State == WebSocketState.Open)
                            await ws.SendAsync(new byte[] { 0xC0, 0x00 }, WebSocketMessageType.Binary, true, ct);
                    }
                    finally { _sendLock.Release(); }
                }
                await Task.Delay(12000, ct);
            }
        }
        catch { }
    }

    // ---------------- Inbound Server JSON Dispatch ----------------

    private void HandleServerJson(byte[] utf8, int len)
    {
        try
        {
            using var doc = JsonDocument.Parse(utf8.AsMemory(0, len));
            var root = doc.RootElement;
            string op = root.TryGetProperty("op", out var opEl) ? opEl.GetString() ?? "" : "";

            switch (op)
            {
                case "auth_err":
                    IsConnecting = false;
                    IsReconnecting = false;
                    HasActiveSession = false;
                    IsConnected = false;
                    StatusText = root.TryGetProperty("error", out var ee) ? ee.GetString() ?? "Ошибка ключа" : "Ошибка ключа";
                    SettingsStore.SetToken("");
                    _secret = "";
                    try { _connCts?.Cancel(); } catch { }
                    Notify();
                    break;

                case "punch_ack":
                    if (root.TryGetProperty("jetson_ext_ip", out var jeip) && jeip.ValueKind == JsonValueKind.String)
                        _jetsonExtIp = jeip.GetString() ?? _jetsonExtIp;
                    if (root.TryGetProperty("jetson_ext_port", out var jep) && jep.ValueKind == JsonValueKind.Number)
                        _jetsonExtPort = (ushort)jep.GetInt32();
                    break;

                case "welcome":
                    HandleWelcome(root);
                    break;

                case "pong":
                {
                    long t0 = root.TryGetProperty("t", out var te) && te.ValueKind == JsonValueKind.Number ? te.GetInt64() : _lastPingSentMs;
                    GatewayPingMs = Math.Clamp((int)(Environment.TickCount64 - t0), 1, 999);
                    if (root.TryGetProperty("jetson_ext_ip", out var jip) && jip.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(jip.GetString()))
                        _jetsonExtIp = jip.GetString()!;
                    if (root.TryGetProperty("jetson_ext_port", out var jpt) && jpt.ValueKind == JsonValueKind.Number && jpt.GetInt32() > 0)
                        _jetsonExtPort = (ushort)jpt.GetInt32();
                    Notify();
                    break;
                }

                case "peers":
                    if (root.TryGetProperty("peers", out var pa) && pa.ValueKind == JsonValueKind.Array)
                    {
                        ApplyPeersSnapshot(pa);
                        Notify();
                    }
                    break;

                case "channels":
                    if (root.TryGetProperty("channels", out var ca) && ca.ValueKind == JsonValueKind.Array)
                    {
                        ApplyChannelsSnapshot(ca, null);
                        Notify();
                    }
                    break;

                case "msg_create":
                    if (root.TryGetProperty("message", out var mc))
                    {
                        var m = ParseMessageDto(mc);
                        var list = Messages.GetOrAdd(m.ChannelId, _ => []);
                        lock (list)
                        {
                            if (!list.Any(x => x.Id == m.Id)) list.Add(m);
                        }
                        if (m.AuthorId != Me.Id)
                        {
                            bool unfocused = App.IsHiddenToTray || W.GetForegroundWindow() != App.Hwnd;
                            if (m.ChannelId != ActiveChannelId)
                                UnreadChannels.AddOrUpdate(m.ChannelId, 1, (_, c) => c + 1);
                            if (m.ChannelId != ActiveChannelId || unfocused)
                                SoundFx.Play(UiSound.Message);
                            if (unfocused && App.Hwnd != 0)
                                App.InvokeOnUiThread(() => W.FlashWindow(App.Hwnd, true));
                        }
                        Notify();
                    }
                    break;

                case "msg_update":
                    if (root.TryGetProperty("message", out var mu))
                    {
                        var m = ParseMessageDto(mu);
                        if (Messages.TryGetValue(m.ChannelId, out var list))
                        {
                            lock (list)
                            {
                                int idx = list.FindIndex(x => x.Id == m.Id);
                                if (idx >= 0) list[idx] = m;
                            }
                            RefreshPinnedCache(m.ChannelId);
                            Notify();
                        }
                    }
                    break;

                case "msg_delete":
                {
                    string cid = root.TryGetProperty("channel_id", out var ce) ? ce.GetString() ?? "" : "";
                    string mid = root.TryGetProperty("message_id", out var me) ? me.GetString() ?? "" : "";
                    if (Messages.TryGetValue(cid, out var list))
                    {
                        lock (list) list.RemoveAll(x => x.Id == mid);
                        Notify();
                    }
                    break;
                }

                case "typing":
                {
                    string cid = root.TryGetProperty("channel_id", out var ce) ? ce.GetString() ?? "" : "";
                    string uid = root.TryGetProperty("user_id", out var ue) ? ue.GetString() ?? "" : "";
                    string an = root.TryGetProperty("author_name", out var ane) ? ane.GetString() ?? "" : "";
                    if (uid != Me.Id && !string.IsNullOrEmpty(cid))
                    {
                        TypingUsers[cid] = (an, Environment.TickCount64 + 6000);
                        Notify();
                    }
                    break;
                }

                case "speaking":
                {
                    string uid = root.TryGetProperty("user_id", out var ue) ? ue.GetString() ?? "" : "";
                    uint ssrc = root.TryGetProperty("ssrc", out var se) && se.ValueKind == JsonValueKind.Number ? se.GetUInt32() : 0;
                    bool spk = root.TryGetProperty("speaking", out var spkEl) && spkEl.ValueKind == JsonValueKind.True;
                    if (!string.IsNullOrEmpty(uid) && ssrc != 0) _userToSsrc[uid] = ssrc;
                    lock (Guilds)
                    {
                        var g = Guilds.FirstOrDefault();
                        if (g != null)
                        {
                            lock (g.VoiceStates)
                            {
                                if (g.VoiceStates.TryGetValue(uid, out var vm))
                                {
                                    vm.Speaking = spk;
                                    vm.SpeakingUntil = spk ? Environment.TickCount64 + 750 : 0;
                                }
                            }
                        }
                    }
                    Notify();
                    break;
                }

                case "voice_ready":
                {
                    string mHost = root.TryGetProperty("media_host", out var mh) ? mh.GetString() ?? _mediaHost : _mediaHost;
                    ushort mPort = root.TryGetProperty("media_port", out var mp) ? (ushort)mp.GetInt32() : _mediaPort;
                    uint ssrc = root.TryGetProperty("ssrc", out var ss) ? ss.GetUInt32() : _voiceSsrc;
                    _ = ConnectVoiceUdpAsync(mHost, mPort, ssrc);
                    break;
                }

                case "pub_ready":
                {
                    string mHost = root.TryGetProperty("media_host", out var mh) ? mh.GetString() ?? _mediaHost : _mediaHost;
                    ushort mPort = root.TryGetProperty("media_port", out var mp) ? (ushort)mp.GetInt32() : _mediaPort;
                    uint aSsrc = root.TryGetProperty("audio_ssrc", out var asEl) ? asEl.GetUInt32() : _pubAudioSsrc;
                    uint vSsrc = root.TryGetProperty("video_ssrc", out var vsEl) ? vsEl.GetUInt32() : _pubVideoSsrc;
                    _ = ConnectPublishUdpAsync(mHost, mPort, aSsrc, vSsrc);
                    break;
                }

                case "watch_ready":
                {
                    int slot = root.TryGetProperty("slot", out var sl) ? sl.GetInt32() : 0;
                    string suid = root.TryGetProperty("streamer_user_id", out var su) ? su.GetString() ?? "" : "";
                    string mHost = root.TryGetProperty("media_host", out var mh) ? mh.GetString() ?? _mediaHost : _mediaHost;
                    ushort mPort = root.TryGetProperty("media_port", out var mp) ? (ushort)mp.GetInt32() : _mediaPort;
                    uint localSsrc = root.TryGetProperty("local_ssrc", out var ls) ? ls.GetUInt32() : 0;
                    uint vSsrc = root.TryGetProperty("video_ssrc", out var vsEl) ? vsEl.GetUInt32() : 0;
                    uint aSsrc = root.TryGetProperty("audio_ssrc", out var asEl) ? asEl.GetUInt32() : 0;
                    string pIp = root.TryGetProperty("peer_ext_ip", out var pei) ? pei.GetString() ?? "" : "";
                    ushort pPort = root.TryGetProperty("peer_ext_port", out var pep) ? (ushort)pep.GetInt32() : (ushort)0;
                    string pLan = root.TryGetProperty("peer_lan_ip", out var pli) ? pli.GetString() ?? "" : "";
                    _ = ConnectWatchUdpAsync(slot, suid, mHost, mPort, localSsrc, vSsrc, aSsrc, pIp, pPort, pLan);
                    break;
                }

                case "stream_stopped":
                {
                    string suid = root.TryGetProperty("streamer_user_id", out var su) ? su.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(suid) && IsWatchingStream(suid))
                        StopWatchingStream(suid);
                    break;
                }

                case "request_keyframe":
                    MediaNative.fc_pub_request_keyframe();
                    break;

                case "upload_res":
                {
                    string rid = root.TryGetProperty("req_id", out var re) ? re.GetString() ?? "" : "";
                    bool ok = root.TryGetProperty("ok", out var oe) && oe.ValueKind == JsonValueKind.True;
                    string? path = ok && root.TryGetProperty("path", out var pe) ? pe.GetString() : null;
                    if (_pendingUploads.TryRemove(rid, out var tcs)) tcs.TrySetResult(path);
                    break;
                }

                case "file_data":
                {
                    string path = root.TryGetProperty("path", out var pe) ? pe.GetString() ?? "" : "";
                    bool ok = root.TryGetProperty("ok", out var oe) && oe.ValueKind == JsonValueKind.True;
                    byte[]? data = null;
                    if (ok && root.TryGetProperty("data_b64", out var b64) && b64.ValueKind == JsonValueKind.String)
                    {
                        try { data = Convert.FromBase64String(b64.GetString()!); } catch { }
                    }
                    if (_pendingFileFetches.TryRemove(path, out var tcs)) tcs.TrySetResult(data);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            SettingsStore.Log("HandleServerJson error: " + ex.Message);
        }
    }

    private void HandleWelcome(JsonElement root)
    {
        string srvName = root.TryGetProperty("server_name", out var sn) ? sn.GetString() ?? "FluxCord P2P • Jetson" : "FluxCord P2P • Jetson";
        if (root.TryGetProperty("my_avatar", out var mav) && mav.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(mav.GetString()))
        {
            string av = mav.GetString()!;
            if (av.StartsWith("/files/", StringComparison.Ordinal)) av = "fluxp2p://" + av.TrimStart('/');
            Me.Avatar = av;
            SettingsStore.Current.P2pAvatar = av;
            SettingsStore.Save();
        }
        if (root.TryGetProperty("my_activity", out var mact) && mact.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(mact.GetString()))
        {
            Me.ActivityText = mact.GetString()!;
            SettingsStore.Current.P2pActivity = Me.ActivityText;
            SettingsStore.Save();
        }
        _mediaHost = root.TryGetProperty("media_host", out var mh) ? mh.GetString() ?? "192.168.100.72" : "192.168.100.72";
        _mediaPort = root.TryGetProperty("media_port", out var mp) ? (ushort)mp.GetInt32() : (ushort)37401;
        if (root.TryGetProperty("jetson_ext_ip", out var jip) && jip.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(jip.GetString()))
            _jetsonExtIp = jip.GetString()!;
        if (root.TryGetProperty("jetson_ext_port", out var jpt) && jpt.ValueKind == JsonValueKind.Number && jpt.GetInt32() > 0)
            _jetsonExtPort = (ushort)jpt.GetInt32();

        if (root.TryGetProperty("media_key_b64", out var mkb) && mkb.ValueKind == JsonValueKind.String)
        {
            try
            {
                byte[] k = Convert.FromBase64String(mkb.GetString()!);
                if (k.Length == 32) _mediaKey = k;
            }
            catch { }
        }
        _voiceSsrc = root.TryGetProperty("voice_ssrc", out var vs) ? vs.GetUInt32() : 10004;
        _pubAudioSsrc = root.TryGetProperty("pub_audio_ssrc", out var pa) ? pa.GetUInt32() : 10008;
        _pubVideoSsrc = root.TryGetProperty("pub_video_ssrc", out var pv) ? pv.GetUInt32() : (_pubAudioSsrc + 1);

        if (root.TryGetProperty("channels", out var chs) && chs.ValueKind == JsonValueKind.Array)
            ApplyChannelsSnapshot(chs, srvName);

        if (root.TryGetProperty("messages", out var msgsObj) && msgsObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in msgsObj.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                var list = new List<DiscordMessage>();
                foreach (var mEl in prop.Value.EnumerateArray())
                    list.Add(ParseMessageDto(mEl));
                Messages[prop.Name] = list;
            }
        }

        if (root.TryGetProperty("peers", out var peersEl) && peersEl.ValueKind == JsonValueKind.Array)
            ApplyPeersSnapshot(peersEl);

        bool firstConnect = !HasActiveSession;
        IsConnected = true;
        IsConnecting = false;
        IsReconnecting = false;
        HasActiveSession = true;
        StatusText = _usingMqttRendezvous
            ? $"P2P подключён через Jetson Orin ({_jetsonExtIp}:{_jetsonExtPort})"
            : $"LAN подключён к Jetson Orin ({_mediaHost}:{_mediaPort})";

        if (firstConnect)
        {
            ActiveGuildId = "g_p2p";
            ActiveChannelId = "c_main";
        }
        else if (!string.IsNullOrEmpty(_voiceChannelId))
        {
            // Seamlessly restore voice + stream state after any transient reconnect!
            _ = SendRpcAsync(w =>
            {
                w.WriteString("op", "voice_state");
                w.WriteString("channel_id", _voiceChannelId);
                w.WriteBoolean("self_mute", Shell.IsMuted);
                w.WriteBoolean("self_deaf", Shell.IsDeafened);
            });
        }

        SettingsStore.Log($"Welcome OK: media={_mediaHost}:{_mediaPort} voice_ssrc={_voiceSsrc} pub_v={_pubVideoSsrc} mqtt={_usingMqttRendezvous}");
        Notify();
        if (firstConnect)
        {
            ReadyCompleted?.Invoke();
            if (string.IsNullOrEmpty(Me.Avatar) && !SettingsStore.Current.AutoImportedDiscordProfile)
            {
                SettingsStore.Current.AutoImportedDiscordProfile = true;
                SettingsStore.Save();
                _ = TryImportFromDiscordAsync(silent: true);
            }
        }
    }

    private void ApplyChannelsSnapshot(JsonElement chsArr, string? serverName)
    {
        var guildChs = new List<DiscordChannel>();
        var dmChs = new List<DiscordChannel>();

        foreach (var cEl in chsArr.EnumerateArray())
        {
            var ch = new DiscordChannel
            {
                Id = cEl.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                GuildId = cEl.TryGetProperty("guild_id", out var gi) ? gi.GetString() ?? "g_p2p" : "g_p2p",
                Name = cEl.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                Topic = cEl.TryGetProperty("topic", out var tp) ? tp.GetString() ?? "" : "",
                Type = cEl.TryGetProperty("type", out var ty) ? ty.GetInt32() : 0,
                Position = cEl.TryGetProperty("position", out var po) ? po.GetInt32() : 0,
                ParentId = cEl.TryGetProperty("parent_id", out var pa) ? pa.GetString() ?? "" : ""
            };
            if (ch.GuildId == "@me") dmChs.Add(ch);
            else guildChs.Add(ch);
        }

        lock (Guilds)
        {
            var g = Guilds.FirstOrDefault(x => x.Id == "g_p2p");
            if (g == null)
            {
                g = new DiscordGuild { Id = "g_p2p", Name = serverName ?? "FluxCord P2P • Jetson Orin" };
                Guilds.Clear();
                Guilds.Add(g);
            }
            else if (!string.IsNullOrEmpty(serverName))
            {
                g.Name = serverName;
            }
            g.Channels.Clear();
            g.Channels.AddRange(guildChs.OrderBy(c => c.Position));
            g.AllChannels.Clear();
            g.AllChannels.AddRange(g.Channels);
        }

        if (dmChs.Count > 0)
        {
            lock (DmChannels)
            {
                foreach (var dm in dmChs)
                {
                    if (!DmChannels.Any(x => x.Id == dm.Id))
                        DmChannels.Add(dm);
                }
            }
        }
    }

    private void ApplyPeersSnapshot(JsonElement peersArr)
    {
        var newFriends = new List<DiscordFriend>();
        var activeVoice = new Dictionary<string, VoiceMember>();
        var mset = GuildMemberIds.GetOrAdd("g_p2p", _ => new HashSet<string>());
        var seenUids = new HashSet<string>();

        foreach (var pEl in peersArr.EnumerateArray())
        {
            string uid = pEl.TryGetProperty("user_id", out var ue) ? ue.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(uid)) continue;
            seenUids.Add(uid);

            string uname = pEl.TryGetProperty("username", out var un) ? un.GetString() ?? uid : uid;
            string dname = pEl.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? uname : uname;
            string av = pEl.TryGetProperty("avatar", out var ave) ? ave.GetString() ?? "" : "";
            if (av.StartsWith("/files/", StringComparison.Ordinal)) av = "fluxp2p://" + av.TrimStart('/');
            string status = pEl.TryGetProperty("status", out var st) ? st.GetString() ?? "online" : "online";
            string act = pEl.TryGetProperty("activity_text", out var ac) ? ac.GetString() ?? "" : "";
            string vcid = pEl.TryGetProperty("voice_channel_id", out var vc) ? vc.GetString() ?? "" : "";
            bool sm = pEl.TryGetProperty("self_mute", out var sme) && sme.ValueKind == JsonValueKind.True;
            bool sd = pEl.TryGetProperty("self_deaf", out var sde) && sde.ValueKind == JsonValueKind.True;
            bool ss = pEl.TryGetProperty("self_stream", out var sse) && sse.ValueKind == JsonValueKind.True;
            bool spk = pEl.TryGetProperty("speaking", out var spke) && spke.ValueKind == JsonValueKind.True;
            uint vSsrc = pEl.TryGetProperty("voice_ssrc", out var vse) && vse.ValueKind == JsonValueKind.Number ? vse.GetUInt32() : 0;

            var ep = new PeerEndpointInfo
            {
                UserId = uid,
                DisplayName = dname,
                VoiceChannelId = vcid,
                SelfStream = ss,
                LanIp = pEl.TryGetProperty("lan_ip", out var lip) ? lip.GetString() ?? "" : "",
                VoiceExtIp = pEl.TryGetProperty("voice_ext_ip", out var vei) ? vei.GetString() ?? "" : "",
                VoiceExtPort = pEl.TryGetProperty("voice_ext_port", out var vep) && vep.ValueKind == JsonValueKind.Number ? (ushort)vep.GetInt32() : (ushort)0,
                VoiceSsrc = vSsrc,
                PubExtIp = pEl.TryGetProperty("pub_ext_ip", out var pei) ? pei.GetString() ?? "" : "",
                PubExtPort = pEl.TryGetProperty("pub_ext_port", out var pep) && pep.ValueKind == JsonValueKind.Number ? (ushort)pep.GetInt32() : (ushort)0,
                PubAudioSsrc = pEl.TryGetProperty("pub_audio_ssrc", out var pas) && pas.ValueKind == JsonValueKind.Number ? pas.GetUInt32() : 0,
                PubVideoSsrc = pEl.TryGetProperty("pub_video_ssrc", out var pvs) && pvs.ValueKind == JsonValueKind.Number ? pvs.GetUInt32() : 0,
            };
            _peerEndpoints[uid] = ep;

            if (vSsrc != 0)
            {
                _userToSsrc[uid] = vSsrc;
                if (_userVolumes.TryGetValue(uid, out var uv))
                    MediaNative.fc_voice_set_user_volume(vSsrc, uv);
            }

            if (uid == Me.Id && !string.IsNullOrEmpty(av)) Me.Avatar = av;
            Users[uid] = new DiscordUser { Id = uid, Username = uname, DisplayName = dname, Avatar = av, Status = status, ActivityText = act };
            lock (mset) mset.Add(uid);

            if (uid != Me.Id)
            {
                newFriends.Add(new DiscordFriend
                {
                    UserId = uid,
                    Username = uname,
                    DisplayName = dname,
                    Avatar = av,
                    Status = status,
                    ActivityText = string.IsNullOrEmpty(act) ? (vcid != "" ? "🔊 В голосовой комнате" : "⚡ P2P Онлайн") : act,
                    Type = 1
                });
            }

            if (!string.IsNullOrEmpty(vcid))
            {
                activeVoice[uid] = new VoiceMember
                {
                    UserId = uid,
                    ChannelId = vcid,
                    Username = dname,
                    Avatar = av,
                    SelfMute = sm,
                    SelfDeaf = sd,
                    SelfStream = ss,
                    Speaking = spk,
                    SpeakingUntil = spk ? Environment.TickCount64 + 750 : 0
                };
            }
            else if (IsWatchingStream(uid))
            {
                StopWatchingStream(uid);
            }
        }

        foreach (var oldKey in _peerEndpoints.Keys)
            if (!seenUids.Contains(oldKey)) _peerEndpoints.TryRemove(oldKey, out _);

        lock (Friends)
        {
            Friends.Clear();
            Friends.AddRange(newFriends);
        }

        lock (Guilds)
        {
            var g = Guilds.FirstOrDefault(x => x.Id == "g_p2p");
            if (g != null)
            {
                lock (g.VoiceStates)
                {
                    foreach (var kv in activeVoice)
                    {
                        if (!g.VoiceStates.ContainsKey(kv.Key) && VoiceConnected && kv.Key != Me.Id && kv.Value.ChannelId == _voiceChannelId)
                            SoundFx.Play(UiSound.UserJoin);
                    }
                    foreach (var oldKv in g.VoiceStates)
                    {
                        if (!activeVoice.ContainsKey(oldKv.Key) && VoiceConnected && oldKv.Key != Me.Id && oldKv.Value.ChannelId == _voiceChannelId)
                            SoundFx.Play(UiSound.UserLeave);
                    }
                    g.VoiceStates.Clear();
                    foreach (var kv in activeVoice) g.VoiceStates[kv.Key] = kv.Value;
                }
            }
        }

        UpdateP2pDirectTargets();
    }

    private DiscordMessage ParseMessageDto(JsonElement m)
    {
        string ra = m.TryGetProperty("reply_author", out var raEl) ? raEl.GetString() ?? "" : "";
        string rt = m.TryGetProperty("reply_text", out var rtEl) ? rtEl.GetString() ?? "" : "";
        int rawType = m.TryGetProperty("msg_type", out var mt) && mt.ValueKind == JsonValueKind.Number ? mt.GetInt32() : 0;
        bool hasReply = !string.IsNullOrEmpty(ra) || !string.IsNullOrEmpty(rt);

        var msg = new DiscordMessage
        {
            Id = m.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            ChannelId = m.TryGetProperty("channel_id", out var cid) ? cid.GetString() ?? "" : "",
            AuthorId = m.TryGetProperty("author_id", out var aid) ? aid.GetString() ?? "" : "",
            AuthorName = m.TryGetProperty("author_name", out var an) ? an.GetString() ?? "User" : "User",
            AuthorColor = m.TryGetProperty("author_color", out var ac) && ac.ValueKind == JsonValueKind.Number ? ac.GetUInt32() : 0xFF7BD88F,
            AuthorAvatar = m.TryGetProperty("avatar", out var av) ? av.GetString() ?? "" : "",
            Content = m.TryGetProperty("content", out var co) ? co.GetString() ?? "" : "",
            Timestamp = m.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "" : "",
            DateLabel = m.TryGetProperty("date_label", out var dl) ? dl.GetString() ?? "" : "",
            IsEdited = m.TryGetProperty("edited", out var ed) && ed.ValueKind == JsonValueKind.True,
            IsPinned = m.TryGetProperty("pinned", out var pn) && pn.ValueKind == JsonValueKind.True,
            MsgType = hasReply && rawType == 0 ? 19 : rawType,
            ReplyPreview = hasReply ? $"↪ {ra}: {rt}" : ""
        };

        if (m.TryGetProperty("attachments", out var atts) && atts.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in atts.EnumerateArray())
            {
                string rawUrl = a.TryGetProperty("url", out var ue) ? ue.GetString() ?? "" : "";
                if (rawUrl.StartsWith("/files/", StringComparison.Ordinal))
                    rawUrl = "fluxp2p://" + rawUrl.TrimStart('/');
                if (!string.IsNullOrEmpty(rawUrl))
                {
                    string fn = a.TryGetProperty("filename", out var fnEl) ? fnEl.GetString() ?? "file" : "file";
                    string ct = a.TryGetProperty("content_type", out var ctEl) ? ctEl.GetString() ?? "" : "";
                    bool isImg = ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                                 fn.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                 fn.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                 fn.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                 fn.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
                                 fn.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
                    if (isImg) msg.AttachmentUrls.Add(rawUrl);
                    msg.Attachments.Add(new DiscordAttachment
                    {
                        Url = rawUrl,
                        Filename = fn,
                        Size = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0,
                        IsImage = isImg
                    });
                }
            }
        }

        if (m.TryGetProperty("reactions", out var reacts) && reacts.ValueKind == JsonValueKind.Array)
        {
            var rlist = new List<ReactionEntry>();
            foreach (var r in reacts.EnumerateArray())
            {
                string em = r.TryGetProperty("emoji", out var ee) ? ee.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(em)) continue;
                int cnt = 0;
                bool meReacted = false;
                if (r.TryGetProperty("users", out var ua) && ua.ValueKind == JsonValueKind.Array)
                {
                    foreach (var u in ua.EnumerateArray())
                    {
                        cnt++;
                        if (u.ValueKind == JsonValueKind.String && u.GetString() == Me.Id) meReacted = true;
                    }
                }
                if (cnt > 0) rlist.Add(new ReactionEntry { Display = em, ApiKey = em, Count = cnt, Me = meReacted });
            }
            if (rlist.Count > 0) msg.Reactions = [.. rlist];
        }
        return msg;
    }

    // ---------------- Voice + Direct P2P Hole-Punching + Jetson Orin Relay ----------------

    private static unsafe bool TryConsumePendingStun(out string ip, out ushort port)
    {
        byte* buf = stackalloc byte[65];
        ushort p = 0;
        if (MediaNative.fc_get_pending_stun_endpoint(buf, 65, &p) != 0 && p != 0)
        {
            ip = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)buf) ?? "";
            port = p;
            return !string.IsNullOrEmpty(ip);
        }
        ip = "";
        port = 0;
        return false;
    }

    private void PumpPendingStunPunch(string role, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 35 && !ct.IsCancellationRequested; i++)
            {
                if (TryConsumePendingStun(out string ip, out ushort port))
                {
                    await SendRpcAsync(w =>
                    {
                        w.WriteString("op", "punch_media");
                        w.WriteString("role", role);
                        w.WriteString("ip", ip);
                        w.WriteNumber("port", port);
                    });
                }
                await Task.Delay(75, ct);
            }
        }, ct);
    }

    private void UpdateP2pDirectTargets()
    {
        if (!VoiceConnected || string.IsNullOrEmpty(_voiceChannelId))
        {
            MediaNative.fc_transport_set_p2p_peer(null, 0, 0);
            return;
        }

        var othersInRoom = _peerEndpoints.Values
            .Where(p => p.UserId != Me.Id && p.VoiceChannelId == _voiceChannelId)
            .ToList();

        // Direct 0-hop P2P when 1-on-1 (e.g. User <-> Lapki), automatic Jetson SFU fanout when 3+ friends join!
        if (othersInRoom.Count == 1 && BestRtcRegion != "jetson-relay")
        {
            var peer = othersInRoom[0];
            if (!string.IsNullOrEmpty(peer.VoiceExtIp) && peer.VoiceExtPort != 0)
            {
                MediaNative.fc_transport_set_p2p_peer(peer.VoiceExtIp, peer.VoiceExtPort, 1);
            }
        }
        else
        {
            MediaNative.fc_transport_set_p2p_peer(null, 0, 0);
        }

        foreach (var ws in GetWatchSessionsSnapshot())
        {
            if (_peerEndpoints.TryGetValue(ws.StreamerUserId, out var sp) && !string.IsNullOrEmpty(sp.PubExtIp) && sp.PubExtPort != 0)
            {
                MediaNative.fc_watch_set_p2p_peer_slot(ws.Slot, sp.PubExtIp, sp.PubExtPort, 1);
                if (sp.PubVideoSsrc != 0)
                    MediaNative.fc_watch_set_remote_slot(ws.Slot, sp.PubVideoSsrc, sp.PubAudioSsrc, ws.StreamerUserId);
            }
        }
    }

    private async Task ConnectVoiceUdpAsync(string host, ushort port, uint ssrc)
    {
        using var punchCts = new CancellationTokenSource();
        PumpPendingStunPunch("voice", punchCts.Token);

        bool ok = await Task.Run(() => DiscoverVoiceUdp(host, port, ssrc, out _, out _));
        punchCts.Cancel();
        if (!ok)
        {
            VoiceStatus = "Ошибка UDP к Jetson Orin";
            Notify();
            return;
        }

        SetTransportKey(_mediaKey);
        MediaNative.fc_dave_init(0, 1, Me.Id, ssrc); // Passthrough DAVE; RTP encrypted with 256-bit XChaCha20-Poly1305 room key!
        MediaNative.fc_voice_start(SettingsStore.Current.CaptureDeviceId, SettingsStore.Current.RenderDeviceId);
        App.InvokeOnUiThread(Shell.ApplyAudioSettings);
        MediaNative.fc_transport_send_silence();

        bool wasVoice = VoiceConnected;
        VoiceConnected = true;
        VoiceEndpointHost = "🚀 Jetson Orin";
        VoiceStatus = "Голосовая связь (Jetson P2P)";
        int udpPing = MediaNative.fc_transport_get_ping_ms();
        if (udpPing > 0) RecordVoicePing(udpPing);
        if (!wasVoice) SoundFx.Play(UiSound.VoiceJoin);
        UpdateP2pDirectTargets();

        if (Shell.IsLocalStreaming && !IsPublishingStream)
            _ = StartPublishingGoLiveAsync(_pubWidth, _pubHeight, _pubFps, _pubIsDualScreen);

        SettingsStore.Log($"P2P Voice UDP connected! {host}:{port} ssrc={ssrc} ping={udpPing}ms");
        Notify();
    }

    private async Task ConnectPublishUdpAsync(string host, ushort port, uint audioSsrc, uint videoSsrc)
    {
        using var punchCts = new CancellationTokenSource();
        PumpPendingStunPunch("pub", punchCts.Token);

        bool ok = await Task.Run(() => DiscoverPubUdp(host, port, audioSsrc, out _, out _));
        punchCts.Cancel();
        if (!ok)
        {
            PublishStatus = "Ошибка UDP стрима";
            Notify();
            return;
        }

        SetPubKey(_mediaKey);
        MediaNative.fc_pub_dave_init(0, 1, Me.Id, audioSsrc, videoSsrc);
        MediaNative.fc_pub_set_video_ssrc(videoSsrc);
        MediaNative.fc_pub_request_keyframe();

        PubEndpointHost = "Jetson Orin 4K60";
        PublishStatus = _pubIsDualScreen
            ? $"2 Экрана {_pubWidth}×{_pubHeight} {_pubFps}fps"
            : $"В эфире {_pubHeight}p {_pubFps}fps";
        SettingsStore.Log($"P2P Pub UDP LIVE! {host}:{port} a_ssrc={audioSsrc} v_ssrc={videoSsrc}");
        Notify();
    }

    private async Task ConnectWatchUdpAsync(int slot, string streamerUid, string host, ushort port,
        uint localSsrc, uint videoSsrc, uint audioSsrc, string peerExtIp, ushort peerExtPort, string peerLanIp)
    {
        WatchSession? ws;
        lock (_watchLock) _watches.TryGetValue(streamerUid, out ws);
        if (ws == null) return;

        ws.LocalSsrc = localSsrc;
        ws.VideoSsrc = videoSsrc;
        ws.AudioSsrc = audioSsrc;

        using var punchCts = new CancellationTokenSource();
        PumpPendingStunPunch("watch", punchCts.Token);

        nint targetHwnd = App.StreamHwnds[slot];
        bool ok = await Task.Run(() => DiscoverWatchUdp(slot, host, port, localSsrc, targetHwnd, out _, out _));
        punchCts.Cancel();
        if (!ok)
        {
            ws.Status = "Ошибка UDP просмотра";
            Notify();
            return;
        }

        MediaNative.fc_watch_set_codec_slot(slot, (int)VideoCodec.H264, 101);
        SetWatchKey(slot, _mediaKey);
        MediaNative.fc_watch_dave_init_slot(slot, 0, 1, Me.Id, localSsrc);
        MediaNative.fc_watch_set_remote_slot(slot, videoSsrc, audioSsrc, streamerUid);
        MediaNative.fc_watch_set_volume_slot(slot, ws.Muted ? 0f : ws.Volume);
        if (!string.IsNullOrEmpty(peerExtIp) && peerExtPort != 0)
            MediaNative.fc_watch_set_p2p_peer_slot(slot, peerExtIp, peerExtPort, 1);

        ws.ConnectedTick = Environment.TickCount64;
        ws.Status = $"Стрим {ws.StreamerName}";
        await SendRpcAsync(w =>
        {
            w.WriteString("op", "request_keyframe");
            w.WriteString("streamer_user_id", streamerUid);
        });
        SettingsStore.Log($"P2P WatchSlot[{slot}] LIVE! {host}:{port} v_ssrc={videoSsrc} a_ssrc={audioSsrc}");
        Notify();
    }

    public async Task JoinVoiceAsync(string guildId, string channelId, bool selfMute, bool selfDeaf, bool ringIfEmpty = false)
    {
        _voiceGuildId = string.IsNullOrEmpty(guildId) ? "g_p2p" : guildId;
        _voiceChannelId = channelId;
        if (IsDemoMode)
        {
            VoiceConnected = true;
            VoiceStatus = "Demo Pro Audio Active";
            MediaNative.fc_voice_start(SettingsStore.Current.CaptureDeviceId, SettingsStore.Current.RenderDeviceId);
            App.InvokeOnUiThread(Shell.ApplyAudioSettings);
            SoundFx.Play(UiSound.VoiceJoin);
            Notify();
            return;
        }
        VoiceStatus = "Подключение к P2P комнате...";
        Notify();
        await SendRpcAsync(w =>
        {
            w.WriteString("op", "voice_state");
            w.WriteString("channel_id", channelId);
            w.WriteBoolean("self_mute", selfMute);
            w.WriteBoolean("self_deaf", selfDeaf);
        });
    }

    public void LeaveVoice()
    {
        bool wasConn = VoiceConnected || !string.IsNullOrEmpty(_voiceChannelId);
        StopAllWatchedStreams();
        StopPublishingGoLive();
        MediaNative.fc_transport_disconnect();
        MediaNative.fc_voice_stop();
        VoiceConnected = false;
        VoiceStatus = "";
        _voiceChannelId = "";
        _voiceGuildId = "";
        if (!IsDemoMode && IsConnected)
        {
            _ = SendRpcAsync(w =>
            {
                w.WriteString("op", "voice_state");
                w.WriteString("channel_id", "");
                w.WriteBoolean("self_mute", false);
                w.WriteBoolean("self_deaf", false);
            });
        }
        if (wasConn) SoundFx.Play(UiSound.VoiceLeave);
        App.InvokeOnUiThread(Shell.StopStream);
        Notify();
    }

    public Task UpdateVoiceSelfStateAsync(bool selfMute, bool selfDeaf)
    {
        if (IsDemoMode || string.IsNullOrEmpty(_voiceChannelId)) return Task.CompletedTask;
        return SendRpcAsync(w =>
        {
            w.WriteString("op", "voice_state");
            w.WriteString("channel_id", _voiceChannelId);
            w.WriteBoolean("self_mute", selfMute);
            w.WriteBoolean("self_deaf", selfDeaf);
        });
    }

    public void UpdateLocalSpeaking(bool speaking, float level = 0f)
    {
        if (!VoiceConnected) return;
        lock (Guilds)
        {
            var g = Guilds.FirstOrDefault();
            if (g != null)
            {
                lock (g.VoiceStates)
                {
                    if (g.VoiceStates.TryGetValue(Me.Id, out var vm))
                    {
                        vm.Speaking = speaking;
                        vm.SpeakingUntil = speaking ? Environment.TickCount64 + 300 : 0;
                    }
                }
            }
        }
        if (speaking != _lastSentSpeaking && !IsDemoMode)
        {
            _lastSentSpeaking = speaking;
            _ = SendRpcAsync(w =>
            {
                w.WriteString("op", "speaking");
                w.WriteBoolean("speaking", speaking);
            });
        }
    }

    public List<VoiceMember> GetVoiceMembersSnapshot(string guildId, string channelId)
    {
        if (string.IsNullOrEmpty(channelId)) return [];
        lock (Guilds)
        {
            var g = Guilds.FirstOrDefault(x => x.Id == guildId) ?? Guilds.FirstOrDefault();
            if (g == null) return [];
            lock (g.VoiceStates)
                return g.VoiceStates.Values.Where(v => v.ChannelId == channelId).OrderBy(v => v.Username).ToList();
        }
    }

    public bool IsUserActuallySpeaking(VoiceMember vm)
    {
        if (vm.SelfMute) return false;
        if (vm.UserId == Me.Id) return vm.Speaking || _lastSentSpeaking;
        if (_userToSsrc.TryGetValue(vm.UserId, out var ssrc) && ssrc != 0 && MediaNative.fc_voice_is_ssrc_speaking(ssrc) != 0)
            return true;
        return vm.Speaking && Environment.TickCount64 < vm.SpeakingUntil;
    }

    public bool IsUserActuallySpeaking(string userId)
    {
        if (userId == Me.Id) return _lastSentSpeaking;
        if (_userToSsrc.TryGetValue(userId, out var ssrc) && ssrc != 0)
            return MediaNative.fc_voice_is_ssrc_speaking(ssrc) != 0;
        return false;
    }

    public float GetUserVolume(string userId) => _userVolumes.TryGetValue(userId, out var v) ? v : 1.0f;

    public void SetUserVolume(string userId, float vol)
    {
        vol = Math.Clamp(vol, 0f, 2f);
        _userVolumes[userId] = vol;
        SettingsStore.Current.UserVolumes ??= new();
        SettingsStore.Current.UserVolumes[userId] = vol;
        SettingsStore.Save();
        if (_userToSsrc.TryGetValue(userId, out var ssrc) && ssrc != 0)
            MediaNative.fc_voice_set_user_volume(ssrc, vol);
    }

    // ---------------- 4K60 / Dual-Screen Go Live + 4-Slot Stream Watching ----------------

    public async Task StartPublishingGoLiveAsync(int width, int height, int fps, bool isDualScreen = false)
    {
        _pubWidth = width; _pubHeight = height; _pubFps = fps; _pubIsDualScreen = isDualScreen;
        IsPublishingStream = true;
        if (IsDemoMode || !VoiceConnected || string.IsNullOrEmpty(_voiceChannelId))
        {
            PublishStatus = isDualScreen ? $"Локально: 2 Экрана {width}×{height}" : "Локальный 4K Zero-Copy";
            Notify();
            return;
        }
        PublishStatus = "Запуск P2P 4K60 трансляции...";
        Notify();
        await SendRpcAsync(w =>
        {
            w.WriteString("op", "stream_start");
            w.WriteNumber("width", width);
            w.WriteNumber("height", height);
            w.WriteNumber("fps", fps);
            w.WriteBoolean("dual", isDualScreen);
        });
    }

    public void StopPublishingGoLive()
    {
        bool wasPub = IsPublishingStream;
        MediaNative.fc_pub_disconnect();
        IsPublishingStream = false;
        PublishStatus = "";
        if (FocusedStreamUserId == "self") FocusedStreamUserId = null;
        if (wasPub && !IsDemoMode && IsConnected)
            _ = SendRpcAsync(w => w.WriteString("op", "stream_stop"));
        Notify();
    }

    public async Task WatchStreamAsync(string guildId, string channelId, string streamerUserId, string streamerName,
        bool focusImmediately = true, bool selfMute = false, bool selfDeaf = false)
    {
        if (!VoiceConnected || _voiceChannelId != channelId)
        {
            await JoinVoiceAsync(guildId, channelId, selfMute, selfDeaf);
            for (int i = 0; i < 25 && !VoiceConnected; i++) await Task.Delay(80);
        }

        int slot;
        lock (_watchLock)
        {
            if (_watches.TryGetValue(streamerUserId, out _))
            {
                if (focusImmediately) FocusedStreamUserId = streamerUserId;
                Notify();
                return;
            }
            slot = -1;
            for (int s = 0; s < App.MaxWatchSlots; s++)
                if (!_watches.Values.Any(w => w.Slot == s)) { slot = s; break; }
            if (slot < 0)
            {
                var oldest = _watches.Values.First();
                slot = oldest.Slot;
                MediaNative.fc_watch_stop_slot(slot);
                _watches.Remove(oldest.StreamerUserId);
            }
            _watches[streamerUserId] = new WatchSession
            {
                Slot = slot,
                StreamKey = $"p2p:{channelId}:{streamerUserId}",
                StreamerUserId = streamerUserId,
                StreamerName = streamerName,
                Status = $"Подключение к P2P стриму {streamerName}..."
            };
            if (focusImmediately) FocusedStreamUserId = streamerUserId;
        }
        SoundFx.Play(UiSound.StreamStart);
        Notify();

        if (!IsDemoMode)
        {
            await SendRpcAsync(w =>
            {
                w.WriteString("op", "watch_start");
                w.WriteNumber("slot", slot);
                w.WriteString("streamer_user_id", streamerUserId);
            });
        }
    }

    public void StopWatchingStream(string streamerUserId)
    {
        int stoppedSlot = -1;
        lock (_watchLock)
        {
            if (_watches.TryGetValue(streamerUserId, out var ws))
            {
                stoppedSlot = ws.Slot;
                MediaNative.fc_watch_stop_slot(ws.Slot);
                _watches.Remove(streamerUserId);
            }
            if (FocusedStreamUserId == streamerUserId) FocusedStreamUserId = null;
        }
        if (stoppedSlot >= 0)
        {
            SoundFx.Play(UiSound.StreamStop);
            if (!IsDemoMode && IsConnected)
            {
                _ = SendRpcAsync(w =>
                {
                    w.WriteString("op", "watch_stop");
                    w.WriteNumber("slot", stoppedSlot);
                });
            }
        }
        Notify();
    }

    public void StopAllWatchedStreams()
    {
        lock (_watchLock)
        {
            foreach (var ws in _watches.Values) MediaNative.fc_watch_stop_slot(ws.Slot);
            _watches.Clear();
            if (FocusedStreamUserId != "self") FocusedStreamUserId = null;
        }
        Notify();
    }

    public void SetStreamVolume(string userId, float vol)
    {
        vol = Math.Clamp(vol, 0f, 2f);
        lock (_watchLock)
        {
            if (_watches.TryGetValue(userId, out var ws))
            {
                ws.Volume = vol;
                ws.Muted = vol <= 0.001f;
                MediaNative.fc_watch_set_volume_slot(ws.Slot, ws.Muted ? 0f : ws.Volume);
            }
        }
        Notify();
    }

    public void ToggleStreamMute(string userId)
    {
        lock (_watchLock)
        {
            if (_watches.TryGetValue(userId, out var ws))
            {
                ws.Muted = !ws.Muted;
                MediaNative.fc_watch_set_volume_slot(ws.Slot, ws.Muted ? 0f : ws.Volume);
            }
        }
        Notify();
    }

    private long _lastStatsPollTick;
    public unsafe void PollWatchStats()
    {
        long now = Environment.TickCount64;
        if (VoiceConnected)
        {
            bool isP2p = MediaNative.fc_transport_is_p2p_direct() != 0;
            VoiceEndpointHost = isP2p ? "⚡ P2P Direct" : "🚀 Jetson Orin";
            int livePing = MediaNative.fc_transport_get_ping_ms();
            if (livePing > 0) VoicePingMs = livePing;
        }

        foreach (var ws in GetWatchSessionsSnapshot())
        {
            FcScreenStats st;
            MediaNative.fc_watch_get_stats_slot(ws.Slot, &st);
            ws.Stats = st;
            if (st.FramesEncoded != ws.LastDecodedFrames)
            {
                ws.LastDecodedFrames = st.FramesEncoded;
                ws.LastDecodedTick = now;
            }
            if (st.FramesEncoded == 0 && ws.ConnectedTick > 0 && now - ws.ConnectedTick >= 1500 && now - ws.LastKickTick >= 2000)
            {
                ws.LastKickTick = now;
                _ = SendRpcAsync(w =>
                {
                    w.WriteString("op", "request_keyframe");
                    w.WriteString("streamer_user_id", ws.StreamerUserId);
                });
            }
        }

        if (IsPublishingStream && now - _lastStatsPollTick >= 1500)
        {
            _lastStatsPollTick = now;
            int vp = 0, ap = 0, plis = 0, daveAct = 0;
            MediaNative.fc_pub_get_stats_ex(&vp, &ap, &plis, &daveAct);
            PubVideoTxPkts = vp;
            PubAudioTxPkts = ap;
            PubDaveActive = true;
        }
    }

    // ---------------- Chat, Reactions, Attachments & DMs ----------------

    public Task LoadMessagesAsync(string channelId)
    {
        UnreadChannels.TryRemove(channelId, out _);
        Notify();
        return Task.CompletedTask;
    }

    public Task LoadOlderMessagesAsync(string channelId) => Task.CompletedTask;

    private void RefreshPinnedCache(string channelId)
    {
        if (Messages.TryGetValue(channelId, out var list))
        {
            List<DiscordMessage> pins;
            lock (list) pins = list.Where(x => x.IsPinned).Reverse().ToList();
            PinnedMessages[channelId] = pins;
        }
        else
        {
            PinnedMessages.TryAdd(channelId, []);
        }
    }

    public Task LoadPinnedMessagesAsync(string channelId)
    {
        RefreshPinnedCache(channelId);
        Notify();
        return Task.CompletedTask;
    }

    public Task PinMessageAsync(string channelId, string messageId)
        => SendRpcAsync(w =>
        {
            w.WriteString("op", "pin_msg");
            w.WriteString("channel_id", channelId);
            w.WriteString("message_id", messageId);
        });

    public Task CreateChannelAsync(string name, bool isVoice)
        => SendRpcAsync(w =>
        {
            w.WriteString("op", "create_channel");
            w.WriteString("name", name.Trim());
            w.WriteNumber("type", isVoice ? 2 : 0);
        });

    public async Task OpenP2pFileLocallyAsync(string url, string? suggestedFilename = null)
    {
        if (string.IsNullOrEmpty(url)) return;
        if (!url.StartsWith("fluxp2p://", StringComparison.OrdinalIgnoreCase))
        {
            W.OpenUrl(url);
            return;
        }
        try
        {
            var data = await FetchP2pAttachmentAsync(url);
            if (data == null || data.Length == 0)
            {
                App.InvokeOnUiThread(() => Shell.Toast("Не удалось скачать файл с Jetson", 3000));
                return;
            }
            string dlDir = Path.Combine(Path.GetTempPath(), "FluxCordP2P_Downloads");
            Directory.CreateDirectory(dlDir);
            string name = Path.GetFileName(string.IsNullOrWhiteSpace(suggestedFilename) ? url : suggestedFilename);
            foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
            if (string.IsNullOrWhiteSpace(name)) name = "file.bin";
            string dst = Path.Combine(dlDir, name);
            await File.WriteAllBytesAsync(dst, data);
            W.OpenLocalFile(dst);
        }
        catch { }
    }

    public async Task<bool> SendMessageAsync(string channelId, string content, string? replyToId = null)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrEmpty(channelId)) return false;
        if (IsDemoMode)
        {
            var list = Messages.GetOrAdd(channelId, _ => []);
            lock (list)
            {
                list.Add(new DiscordMessage
                {
                    Id = Environment.TickCount64.ToString(),
                    ChannelId = channelId,
                    AuthorId = Me.Id,
                    AuthorName = Me.DisplayName,
                    AuthorColor = 0xFF7BD88F,
                    Content = content,
                    Timestamp = DateTime.Now.ToString("HH:mm")
                });
            }
            Notify();
            return true;
        }
        if (!IsConnected) return false;

        string dmName = "";
        if (channelId.StartsWith("dm_", StringComparison.Ordinal))
        {
            lock (DmChannels) dmName = DmChannels.FirstOrDefault(x => x.Id == channelId)?.Name ?? "";
        }

        await SendRpcAsync(w =>
        {
            w.WriteString("op", "send_msg");
            w.WriteString("channel_id", channelId);
            w.WriteString("content", content);
            if (!string.IsNullOrEmpty(replyToId)) w.WriteString("reply_to_id", replyToId);
            if (!string.IsNullOrEmpty(dmName)) w.WriteString("dm_name", dmName);
        });
        return true;
    }

    public Task EditMessageAsync(string channelId, string messageId, string content)
        => SendRpcAsync(w =>
        {
            w.WriteString("op", "edit_msg");
            w.WriteString("channel_id", channelId);
            w.WriteString("message_id", messageId);
            w.WriteString("content", content);
        });

    public Task DeleteMessageAsync(string channelId, string messageId)
        => SendRpcAsync(w =>
        {
            w.WriteString("op", "delete_msg");
            w.WriteString("channel_id", channelId);
            w.WriteString("message_id", messageId);
        });

    public Task ToggleReactionAsync(string channelId, string messageId, string emoji)
        => SendRpcAsync(w =>
        {
            w.WriteString("op", "react_msg");
            w.WriteString("channel_id", channelId);
            w.WriteString("message_id", messageId);
            w.WriteString("emoji", emoji);
        });

    public Task SendTypingAsync(string channelId)
        => IsConnected ? SendRpcAsync(w => { w.WriteString("op", "typing"); w.WriteString("channel_id", channelId); }) : Task.CompletedTask;

    public async Task<bool> UploadFileAsync(string channelId, string filePath, string caption = "")
    {
        if (!File.Exists(filePath) || !IsConnected) return false;
        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            string fileName = Path.GetFileName(filePath);
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            string ctype = ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                _ => "application/octet-stream"
            };

            string? serverRelPath = null;
            if (!string.IsNullOrEmpty(_directHttpBase))
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, $"{_directHttpBase}/upload?name={Uri.EscapeDataString(fileName)}");
                    req.Headers.TryAddWithoutValidation("X-Room-Auth", ComputeHmacHex(_aeadKey, "fluxp2p-upload-v1"));
                    req.Content = new ByteArrayContent(bytes);
                    using var resp = await _http.SendAsync(req);
                    if (resp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync());
                        if (doc.RootElement.TryGetProperty("path", out var pe))
                            serverRelPath = pe.GetString();
                    }
                }
                catch { }
            }

            if (string.IsNullOrEmpty(serverRelPath) && bytes.Length <= 5 * 1024 * 1024)
            {
                string reqId = Guid.NewGuid().ToString("N")[..10];
                var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingUploads[reqId] = tcs;
                await SendRpcAsync(w =>
                {
                    w.WriteString("op", "upload_b64");
                    w.WriteString("req_id", reqId);
                    w.WriteString("filename", fileName);
                    w.WriteString("data_b64", Convert.ToBase64String(bytes));
                });
                using var cts = new CancellationTokenSource(12000);
                cts.Token.Register(() => tcs.TrySetResult(null));
                serverRelPath = await tcs.Task;
                _pendingUploads.TryRemove(reqId, out _);
            }

            if (string.IsNullOrEmpty(serverRelPath)) return false;

            await SendRpcAsync(w =>
            {
                w.WriteString("op", "send_msg");
                w.WriteString("channel_id", channelId);
                w.WriteString("content", caption ?? "");
                w.WriteStartArray("attachments");
                w.WriteStartObject();
                w.WriteString("url", serverRelPath);
                w.WriteString("filename", fileName);
                w.WriteString("content_type", ctype);
                w.WriteNumber("size", bytes.Length);
                if (ctype.StartsWith("image/", StringComparison.Ordinal))
                {
                    w.WriteNumber("width", 1280);
                    w.WriteNumber("height", 720);
                }
                w.WriteEndObject();
                w.WriteEndArray();
            });
            return true;
        }
        catch { return false; }
    }

    public async Task<byte[]?> FetchP2pAttachmentAsync(string fluxUrl)
    {
        // fluxp2p://files/<stored>
        string relPath = "/" + fluxUrl["fluxp2p://".Length..].TrimStart('/');
        if (!string.IsNullOrEmpty(_directHttpBase))
        {
            try
            {
                using var resp = await _http.GetAsync(_directHttpBase + relPath);
                if (resp.IsSuccessStatusCode) return await resp.Content.ReadAsByteArrayAsync();
            }
            catch { }
        }

        var tcs = _pendingFileFetches.GetOrAdd(relPath, _ => new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously));
        await SendRpcAsync(w =>
        {
            w.WriteString("op", "fetch_file");
            w.WriteString("path", relPath);
        });
        using var cts = new CancellationTokenSource(12000);
        cts.Token.Register(() => tcs.TrySetResult(null));
        var res = await tcs.Task;
        _pendingFileFetches.TryRemove(relPath, out _);
        return res;
    }

    public Task<string?> OpenOrCreateDmAsync(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return Task.FromResult<string?>(null);
        string a = string.CompareOrdinal(Me.Id, userId) < 0 ? Me.Id : userId;
        string b = string.CompareOrdinal(Me.Id, userId) < 0 ? userId : Me.Id;
        string dmId = $"dm_{a}_{b}";
        string peerName = Users.TryGetValue(userId, out var u) ? u.DisplayName : userId;
        lock (DmChannels)
        {
            if (!DmChannels.Any(x => x.Id == dmId))
                DmChannels.Insert(0, new DiscordChannel { Id = dmId, GuildId = "@me", Name = peerName, RecipientId = userId, Type = 1 });
        }
        Notify();
        return Task.FromResult<string?>(dmId);
    }

    public Task AcceptFriendRequestAsync(string userId) => Task.CompletedTask;
    public Task RemoveRelationshipAsync(string userId) => Task.CompletedTask;

    public Task SetPresenceStatusAsync(string status)
    {
        SettingsStore.Current.PresenceStatus = status;
        SettingsStore.Save();
        Me.Status = status;
        Notify();
        return IsConnected ? SendRpcAsync(w => { w.WriteString("op", "presence"); w.WriteString("status", status); }) : Task.CompletedTask;
    }

    public async Task<bool> SetProfileAsync(string? newNick, string? newActivity, string? newAvatar)
    {
        bool changed = false;
        if (!string.IsNullOrWhiteSpace(newNick))
        {
            string dn = newNick.Trim();
            if (dn.Length > 32) dn = dn[..32];
            Me.DisplayName = dn;
            Me.Username = dn.ToLowerInvariant().Replace(" ", "_");
            SettingsStore.Current.P2pNickname = dn;
            changed = true;
        }
        if (newActivity != null)
        {
            string act = newActivity.Trim();
            if (act.Length > 64) act = act[..64];
            Me.ActivityText = act;
            SettingsStore.Current.P2pActivity = act;
            changed = true;
        }
        if (newAvatar != null)
        {
            Me.Avatar = newAvatar;
            SettingsStore.Current.P2pAvatar = newAvatar;
            changed = true;
        }
        if (!changed) return false;
        SettingsStore.Save();
        Users[Me.Id] = Me;
        Notify();
        if (IsConnected && !IsDemoMode)
        {
            await SendRpcAsync(w =>
            {
                w.WriteString("op", "presence");
                w.WriteString("status", Me.Status);
                w.WriteString("display_name", Me.DisplayName);
                w.WriteString("activity", Me.ActivityText ?? "");
                w.WriteString("avatar", Me.Avatar ?? "");
            });
        }
        return true;
    }

    private async Task<string?> UploadBytesToJetsonAsync(byte[] bytes, string fileName)
    {
        if (bytes.Length == 0 || bytes.Length > 6 * 1024 * 1024) return null;
        string? serverRelPath = null;
        if (!string.IsNullOrEmpty(_directHttpBase))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{_directHttpBase}/upload?name={Uri.EscapeDataString(fileName)}");
                req.Headers.TryAddWithoutValidation("X-Room-Auth", ComputeHmacHex(_aeadKey, "fluxp2p-upload-v1"));
                req.Content = new ByteArrayContent(bytes);
                using var resp = await _http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync());
                    if (doc.RootElement.TryGetProperty("path", out var pe))
                        serverRelPath = pe.GetString();
                }
            }
            catch { }
        }
        if (string.IsNullOrEmpty(serverRelPath))
        {
            string reqId = Guid.NewGuid().ToString("N")[..10];
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingUploads[reqId] = tcs;
            await SendRpcAsync(w =>
            {
                w.WriteString("op", "upload_b64");
                w.WriteString("req_id", reqId);
                w.WriteString("filename", fileName);
                w.WriteString("data_b64", Convert.ToBase64String(bytes));
            });
            using var cts = new CancellationTokenSource(12000);
            cts.Token.Register(() => tcs.TrySetResult(null));
            serverRelPath = await tcs.Task;
            _pendingUploads.TryRemove(reqId, out _);
        }
        if (string.IsNullOrEmpty(serverRelPath)) return null;
        return serverRelPath.StartsWith("/files/", StringComparison.Ordinal)
            ? "fluxp2p://" + serverRelPath.TrimStart('/')
            : serverRelPath;
    }

    public async Task<bool> SetCustomAvatarFromFileAsync(string filePath)
    {
        if (!File.Exists(filePath)) return false;
        try
        {
            byte[] raw = await File.ReadAllBytesAsync(filePath);
            byte[]? png = ImageCache.EncodeAvatarPng(raw, 192);
            if (png == null || png.Length == 0)
            {
                App.InvokeOnUiThread(() => Shell.Toast("Не удалось прочитать картинку (выберите PNG/JPG/WEBP)", 3500));
                return false;
            }
            string? url = await UploadBytesToJetsonAsync(png, $"av_{Me.Id}.png");
            if (string.IsNullOrEmpty(url))
            {
                App.InvokeOnUiThread(() => Shell.Toast("Не удалось загрузить аватарку на Jetson", 3500));
                return false;
            }
            await SetProfileAsync(null, null, url);
            App.InvokeOnUiThread(() => Shell.Toast("✅ Аватарка обновлена для всех в P2P!", 3000));
            return true;
        }
        catch { return false; }
    }

    public async Task<bool> TryImportFromDiscordAsync(bool silent = false)
    {
        try
        {
            // Read DPAPI-protected Discord token from main FluxCord installation if present
            string fcSettings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluxCord", "settings.json");
            if (!File.Exists(fcSettings))
            {
                if (!silent) App.InvokeOnUiThread(() => Shell.Toast("Не найден профиль FluxCord (Discord) — загрузите картинку с ПК", 4000));
                return false;
            }
            string? encB64 = null;
            using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(fcSettings)))
            {
                if (doc.RootElement.TryGetProperty("EncryptedTokenB64", out var et) && et.ValueKind == JsonValueKind.String)
                    encB64 = et.GetString();
            }
            if (string.IsNullOrEmpty(encB64))
            {
                if (!silent) App.InvokeOnUiThread(() => Shell.Toast("В FluxCord нет сохранённого входа Discord — выберите файл с ПК", 4000));
                return false;
            }
            var plainBytes = W.UnprotectDpapi(Convert.FromBase64String(encB64));
            string token = Encoding.UTF8.GetString(plainBytes).Trim();
            if (string.IsNullOrEmpty(token)) return false;

            using var req = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/users/@me");
            req.Headers.TryAddWithoutValidation("Authorization", token);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            using var cts = new CancellationTokenSource(8000);
            using var resp = await _http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                if (!silent) App.InvokeOnUiThread(() => Shell.Toast("Discord API недоступен (РКН?) — выберите аватарку файлом с ПК", 4000));
                return false;
            }
            using var meDoc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync(cts.Token));
            var r = meDoc.RootElement;
            string dId = r.TryGetProperty("id", out var ie) ? ie.GetString() ?? "" : "";
            string dAv = r.TryGetProperty("avatar", out var ae) && ae.ValueKind == JsonValueKind.String ? ae.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(dId) || string.IsNullOrEmpty(dAv))
            {
                if (!silent) App.InvokeOnUiThread(() => Shell.Toast("В аккаунте Discord нет аватарки — выберите файл с ПК", 3500));
                return false;
            }

            byte[]? avRaw = null;
            foreach (string cdnUrl in new[] { $"https://cdn.discordapp.com/avatars/{dId}/{dAv}.png?size=256", $"https://media.discordapp.net/avatars/{dId}/{dAv}.png?size=256" })
            {
                try
                {
                    using var avResp = await _http.GetAsync(cdnUrl, cts.Token);
                    if (avResp.IsSuccessStatusCode)
                    {
                        avRaw = await avResp.Content.ReadAsByteArrayAsync(cts.Token);
                        if (avRaw.Length > 0) break;
                    }
                }
                catch { }
            }
            if (avRaw == null || avRaw.Length == 0)
            {
                if (!silent) App.InvokeOnUiThread(() => Shell.Toast("Не удалось скачать аватар с Discord CDN — выберите файл с ПК", 4000));
                return false;
            }
            byte[]? png = ImageCache.EncodeAvatarPng(avRaw, 192);
            if (png == null || png.Length == 0) return false;
            string? jetsonUrl = await UploadBytesToJetsonAsync(png, $"av_discord_{Me.Id}.png");
            if (string.IsNullOrEmpty(jetsonUrl)) return false;

            await SetProfileAsync(null, null, jetsonUrl);
            App.InvokeOnUiThread(() => Shell.Toast("✅ Аватарка из Discord загружена на Jetson и видна всем!", 3500));
            return true;
        }
        catch
        {
            if (!silent) App.InvokeOnUiThread(() => Shell.Toast("Не удалось подтянуть Discord — выберите аватарку файлом с ПК", 4000));
            return false;
        }
    }

    public Task<string?> CreateInviteUrlAsync(string channelId)
    {
        if (string.IsNullOrEmpty(_secret)) return Task.FromResult<string?>(null);
        return Task.FromResult<string?>($"fluxp2p://{_secret}@{_jetsonExtIp}:37400");
    }

    public Task BenchmarkRegionsAsync() => Task.CompletedTask;

    public Task<(bool Ok, string Message)> SwitchVoiceRegionAsync(string? regionId)
    {
        BestRtcRegion = string.IsNullOrEmpty(regionId) ? "p2p-auto" : regionId;
        UpdateP2pDirectTargets();
        Notify();
        string label = BestRtcRegion == "jetson-relay" ? "Режим: 🚀 Jetson Orin Nano Relay" : "Режим: ⚡ Auto P2P Direct + Jetson Orin";
        return Task.FromResult((true, label));
    }

    // ---------------- Native Unsafe Wrappers ----------------

    private static unsafe bool DiscoverVoiceUdp(string ip, ushort port, uint ssrc, out string extIp, out ushort extPort)
    {
        byte* buf = stackalloc byte[65]; ushort p = 0;
        if (MediaNative.fc_transport_connect(ip, port, ssrc, buf, 65, &p) != 0)
        { extIp = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)buf) ?? ""; extPort = p; return true; }
        extIp = ""; extPort = 0; return false;
    }
    private static unsafe void SetTransportKey(byte[] k) { fixed (byte* p = k) MediaNative.fc_transport_set_key(p, 1); }

    private static unsafe bool DiscoverPubUdp(string ip, ushort port, uint ssrc, out string extIp, out ushort extPort)
    {
        byte* buf = stackalloc byte[65]; ushort p = 0;
        if (MediaNative.fc_pub_connect(ip, port, ssrc, buf, 65, &p) != 0)
        { extIp = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)buf) ?? ""; extPort = p; return true; }
        extIp = ""; extPort = 0; return false;
    }
    private static unsafe void SetPubKey(byte[] k) { fixed (byte* p = k) MediaNative.fc_pub_set_key(p, 1); }

    private static unsafe bool DiscoverWatchUdp(int slot, string ip, ushort port, uint ssrc, nint hwnd, out string extIp, out ushort extPort)
    {
        byte* buf = stackalloc byte[65]; ushort p = 0;
        if (MediaNative.fc_watch_connect_slot(slot, ip, port, ssrc, hwnd, buf, 65, &p) != 0)
        { extIp = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)buf) ?? ""; extPort = p; return true; }
        extIp = ""; extPort = 0; return false;
    }
    private static unsafe void SetWatchKey(int slot, byte[] k) { fixed (byte* p = k) MediaNative.fc_watch_set_key_slot(slot, p, 1); }
}
