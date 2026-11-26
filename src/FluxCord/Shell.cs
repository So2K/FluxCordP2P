using System.Diagnostics;

namespace FluxCord;

public static unsafe partial class Shell
{
    public const float TopH = 38f, RailW = 68f, SideW = 252f, InputH = 54f;

    // Exact verified Lucide font codepoints from Assets/fonts/lucide.ttf
    internal const string IcCheck = "\ue070";
    internal const string IcChevDown = "\ue071";
    internal const string IcChevRight = "\ue073";
    internal const string IcChevUp = "\ue074";
    internal const string IcCopy = "\ue0a2";
    internal const string IcEye = "\ue0be";
    internal const string IcHash = "\ue0f2";
    internal const string IcHeadphoneOff = "\ue62d";
    internal const string IcHeadphones = "\ue0f4";
    internal const string IcImage = "\ue0f9";
    internal const string IcGrid = "\ue104";
    internal const string IcMax = "\ue116";
    internal const string IcChat = "\ue11a";
    internal const string IcMic = "\ue11b";
    internal const string IcMicOff = "\ue11c";
    internal const string IcMin = "\ue11e";
    internal const string IcMonitor = "\ue120";
    internal const string IcMonitorOff = "\ue1db";
    internal const string IcPhoneCall = "\ue138";
    internal const string IcPhoneMissed = "\ue13a";
    internal const string IcPhoneOff = "\ue13b";
    internal const string IcPlus = "\ue140";
    internal const string IcRefresh = "\ue148";
    internal const string IcShare = "\ue152";
    internal const string IcShareOff = "\ue153";
    internal const string IcSettings = "\ue157";
    internal const string IcSliders = "\ue299";
    internal const string IcVideoOff = "\ue1a5";
    internal const string IcVol = "\ue1aa";
    internal const string IcVolOff = "\ue1ab";
    internal const string IcX = "\ue1b1";
    internal const string IcZap = "\ue1b3";

    private static bool _showSettings;
    private static bool _showQuickSwitch;
    private static bool _showShareModal;
    private static bool _showChannelSearch;
    private static bool _showVoiceInfo;
    private static int _voiceInfoTab; // 0 = Интеграция (Ping Graph + DAVE), 1 = Серверы ⚡ (RTC Turbo Regions)
    private static bool _showStatusMenu;
    private static bool _showEmojiPicker;
    private static bool _showPinsPopup;
    private static bool _showFriendsView;
    private static int _friendsTab; // 0 = В сети, 1 = Все
    private static string? _lightboxUrl;
    private static string? _profileUserId;
    private static string _profileGuildId = "";
    private static float _profileX, _profileY;
    private static readonly HashSet<string> _collapsedCats = new();
    private static int _activeStreamMonitor;
    private static ulong _activeStreamHwnd;
    private static long _lastClockSec;
    private static bool _prevLocalSpkPoll;
    private static int _prevPeerSpkHash;
    private static bool _inVoiceStage;
    public static bool IsInVoiceStage => _inVoiceStage;
    public static bool IsMuted => _muted;
    public static bool IsDeafened => _deafened;
    public static bool IsLocalStreaming => _streaming;
    public static float TopBarRightZoneWidth { get; private set; } = 140f;
    private static bool _muted, _deafened, _loopback;
    private static bool _streaming;
    private static bool _qrStartQueued;
    private static long _voiceJoinedTick;
    private static long _cachedRamMb;
    private static long _lastRamSampleTick;
    private static float _railScroll, _chatScroll, _sideScroll, _memberScroll, _friendsScroll;
    private static int _quickSwitchIdx;
    private static string _hoverGuildTip = "";
    private static float _hoverGuildTipY;
    private static string _replyToId = "", _replyToLabel = "";
    private static string _editingMsgId = "", _editingOrigText = "";
    private static string _searchQuery = "";
    private static bool _pttPrevDown;
    private static readonly List<R> _prevPopupRects = new(8);

    // Sidebar Hover Stream Preview Card (exact GoofCord/Discord c_070.jpg!)
    private static VoiceMember? _hoverStreamVm;
    private static string _hoverStreamGuildId = "";
    private static float _hoverStreamY;
    private static long _hoverStreamLastTick;

    // Context Menus & Quick Audio Popup
    private static VoiceMember? _ctxUser;
    private static string _ctxGuildId = "";
    private static float _ctxX, _ctxY;

    private static string? _ctxStreamUserId; // "self" or remote streamer userId
    private static float _ctxStreamX, _ctxStreamY;

    // 0 = closed, 1 = full audio quick popup (mic chevron), 2 = output devices quick popup (headphones chevron)
    private static int _audioPopupMode;
    private static int _audioPopupSub; // 0 = none, 1 = input list flyout, 2 = output list flyout
    private static float _audioPopupX, _audioPopupY;

    private static string _toast = "";
    private static long _toastUntil;
    private static long _lastStatsTick;

    private static FcAudioStats _audioStats;
    private static float _localVoiceLevel, _localSpeakAnim;
    private static readonly Dictionary<string, float> _peerSpeakAnim = new();
    private static FcScreenStats _selfScreenStats;
    private static List<(string Id, string Name, bool Default)> _capDevices = [];
    private static List<(string Id, string Name, bool Default)> _renDevices = [];
    private static List<(int Index, int W, int H, int Hz, string Name)> _monitors = [];

    private static R _chatInputRect, _tokenInputRect, _searchInputRect;
    private static bool _wantChatInput, _wantTokenInput, _wantSearchInput;
    private static readonly (bool Show, R Rect)[] _watchSlotsLayout = new (bool, R)[App.MaxWatchSlots];
    private static bool _wantSelfHost;
    private static R _selfHostRect;
    private static readonly List<R> _overlayCutouts = new(12);

    internal readonly record struct QuickSwitchEntry(bool IsDm, string GuildId, string ChannelId, string Title, string Sub, bool IsVoice);

    internal static string SafeTrunc(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxLen) return s;
        int len = Math.Max(1, maxLen - 1);
        if (len < s.Length && char.IsHighSurrogate(s[len - 1])) len--;
        return s[..Math.Max(1, len)] + "…";
    }

    internal static string FirstGrapheme(string s)
    {
        if (string.IsNullOrEmpty(s)) return "?";
        if (s.Length >= 2 && char.IsHighSurrogate(s[0]) && char.IsLowSurrogate(s[1]))
            return s[..2];
        return s[..1].ToUpperInvariant();
    }

    internal static string PluralizeMembers(int n)
    {
        int m10 = n % 10, m100 = n % 100;
        if (m10 == 1 && m100 != 11) return "участник";
        if (m10 is >= 2 and <= 4 && (m100 < 10 || m100 >= 20)) return "участника";
        return "участников";
    }

    private static void DrawRedBadge(float rightX, float bottomY, int count, uint borderBg)
    {
        if (count <= 0) return;
        string txt = count > 99 ? "99+" : count.ToString();
        float w = MathF.Max(18f, Gfx.MeasureText(txt, 10.5f, 800, 48).W + 10f);
        var br = new R(rightX - w, bottomY - 18f, w, 18f);
        Gfx.FillRect(br.Inset(-2f), borderBg, 10f);
        Gfx.FillRect(br, Th.Red, 9f);
        Gfx.DrawText(txt, new R(br.X, br.Y + 2f, br.W, 14f), 10.5f, 0xFFFFFFFF, 800, 1);
    }

    internal static void SwitchToChannel(string guildId, string channelId, bool showFriends = false)
    {
        var dc = DiscordClient.Instance;
        dc.ActiveGuildId = guildId;
        if (!string.IsNullOrEmpty(channelId)) dc.ActiveChannelId = channelId;
        _inVoiceStage = false;
        _showFriendsView = showFriends;
        _showPinsPopup = false;
        _showEmojiPicker = false;
        _editingMsgId = "";
        _editingOrigText = "";
        _replyToId = "";
        _replyToLabel = "";
        _chatScroll = 0;
        if (!string.IsNullOrEmpty(channelId))
        {
            _ = dc.LoadMessagesAsync(channelId);
            SettingsStore.Current.LastGuildId = guildId;
            SettingsStore.Current.LastChannelId = channelId;
            SettingsStore.Save();
        }
    }

    internal static List<QuickSwitchEntry> GetQuickSwitchItems(string q)
    {
        var dc = DiscordClient.Instance;
        var res = new List<QuickSwitchEntry>(24);
        List<DiscordChannel> dms;
        lock (dc.DmChannels) dms = [.. dc.DmChannels];
        foreach (var dm in dms)
        {
            if (res.Count >= 18) break;
            if (!string.IsNullOrEmpty(q) && !dm.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            res.Add(new QuickSwitchEntry(true, "@me", dm.Id, $"@ {dm.Name}", "Личные сообщения", false));
        }
        List<DiscordGuild> guilds;
        lock (dc.Guilds) guilds = [.. dc.Guilds];
        foreach (var g in guilds)
        {
            foreach (var ch in g.Channels)
            {
                if (res.Count >= 18) break;
                if (ch.IsCategory) continue;
                if (!string.IsNullOrEmpty(q) &&
                    !ch.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !g.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                res.Add(new QuickSwitchEntry(false, g.Id, ch.Id, $"{(ch.IsVoice ? "🔊" : "#")} {ch.Name}", g.Name, ch.IsVoice));
            }
            if (res.Count >= 18) break;
        }
        return res;
    }

    public static void StepQuickSwitch(int dir)
    {
        if (!_showQuickSwitch) return;
        var items = GetQuickSwitchItems(_searchQuery);
        if (items.Count == 0) { _quickSwitchIdx = 0; return; }
        _quickSwitchIdx = (_quickSwitchIdx + dir + items.Count) % items.Count;
        App.Invalidate();
    }

    public static void ToggleQuickSwitch()
    {
        _showQuickSwitch = !_showQuickSwitch;
        if (_showQuickSwitch)
        {
            _showChannelSearch = false; _showSettings = false; _showShareModal = false;
            _searchQuery = ""; _quickSwitchIdx = 0; App.SetSearchInputText("");
        }
        App.Invalidate();
    }

    public static void ToggleChannelSearch()
    {
        _showChannelSearch = !_showChannelSearch;
        if (_showChannelSearch)
        {
            _showQuickSwitch = false; _inVoiceStage = false;
            _searchQuery = ""; App.SetSearchInputText("");
        }
        else _searchQuery = "";
        App.Invalidate();
    }

    public static void OnSearchChanged(string text)
    {
        _searchQuery = text.Trim();
        _quickSwitchIdx = 0;
        App.Invalidate();
    }

    public static void OnSearchEnter(string text)
    {
        _searchQuery = text.Trim();
        if (_showQuickSwitch)
        {
            var items = GetQuickSwitchItems(_searchQuery);
            if (items.Count > 0)
            {
                var pick = items[Math.Clamp(_quickSwitchIdx, 0, items.Count - 1)];
                var dc = DiscordClient.Instance;
                if (pick.IsVoice)
                {
                    dc.ActiveGuildId = pick.GuildId;
                    _ = dc.JoinVoiceAsync(pick.GuildId, pick.ChannelId, _muted, _deafened);
                    _inVoiceStage = true;
                }
                else
                {
                    SwitchToChannel(pick.GuildId, pick.ChannelId);
                }
                _showQuickSwitch = false;
                _searchQuery = "";
            }
        }
        App.Invalidate();
    }

    public static void OnFilesDropped(List<string> files)
    {
        var dc = DiscordClient.Instance;
        if ((!dc.IsConnected && !dc.IsDemoMode) || _inVoiceStage || _showFriendsView ||
            _showSettings || _showShareModal || _showQuickSwitch || _lightboxUrl != null) return;
        if (files.Count == 0 || string.IsNullOrEmpty(dc.ActiveChannelId)) return;
        foreach (var fp in files)
        {
            if (File.Exists(fp))
            {
                Toast("Загрузка " + Path.GetFileName(fp) + "...");
                _ = dc.UploadFileAsync(dc.ActiveChannelId, fp);
            }
        }
    }

    public static void OnClipboardImagePaste()
    {
        var dc = DiscordClient.Instance;
        if ((!dc.IsConnected && !dc.IsDemoMode) || _inVoiceStage || _showFriendsView || string.IsNullOrEmpty(dc.ActiveChannelId)) return;
        var tmp = W.SaveClipboardImageToTemp(App.Hwnd);
        if (!string.IsNullOrEmpty(tmp))
        {
            Toast("Отправка скриншота из буфера...");
            _ = ShellAsync.UploadAndDeleteTempAsync(dc.ActiveChannelId, tmp);
        }
    }

    public static void ToggleMute()
    {
        _muted = !_muted;
        ApplyAudioSettings();
        _ = DiscordClient.Instance.UpdateVoiceSelfStateAsync(_muted, _deafened);
        SoundFx.Play(_muted ? UiSound.Mute : UiSound.Unmute);
        App.Invalidate();
    }

    public static void ToggleDeafen()
    {
        _deafened = !_deafened;
        ApplyAudioSettings();
        _ = DiscordClient.Instance.UpdateVoiceSelfStateAsync(_muted, _deafened);
        SoundFx.Play(_deafened ? UiSound.Deafen : UiSound.Undeafen);
        App.Invalidate();
    }

    public static void OnChatTyping()
    {
        var dc = DiscordClient.Instance;
        if (!string.IsNullOrEmpty(dc.ActiveChannelId) && !_inVoiceStage)
            _ = dc.SendTypingAsync(dc.ActiveChannelId);
    }

    public static void StepChannel(int dir)
    {
        var dc = DiscordClient.Instance;
        List<DiscordChannel> list;
        if (dc.ActiveGuildId == "@me")
        {
            lock (dc.DmChannels) list = [.. dc.DmChannels];
        }
        else
        {
            DiscordGuild? g;
            lock (dc.Guilds) g = dc.Guilds.FirstOrDefault(x => x.Id == dc.ActiveGuildId);
            list = g?.Channels.Where(c => !c.IsCategory && !c.IsVoice).ToList() ?? [];
        }
        if (list.Count == 0) return;
        int cur = list.FindIndex(c => c.Id == dc.ActiveChannelId);
        int next = cur < 0 ? 0 : (cur + dir + list.Count) % list.Count;
        SwitchToChannel(dc.ActiveGuildId, list[next].Id);
        App.Invalidate();
    }

    // 100ms background poll: updates audio/video telemetry, PTT & RAM while keeping idle D2D CPU near 0%!
    public static bool PollBackgroundTick()
    {
        var dc = DiscordClient.Instance;
        long now = Environment.TickCount64;
        bool needRedraw = false;

        // Sample RAM once per second with proper `using` so we NEVER leak Process handles!
        if (now - _lastRamSampleTick >= 1000)
        {
            _lastRamSampleTick = now;
            try
            {
                using var proc = Process.GetCurrentProcess();
                long mb = proc.WorkingSet64 / (1024 * 1024);
                if (mb != _cachedRamMb) { _cachedRamMb = mb; needRedraw = true; }
            }
            catch { }
        }

        if (SettingsStore.Current.PushToTalk && (dc.VoiceConnected || _loopback))
        {
            int pttVk = W.ResolveKeyVk(SettingsStore.Current.PushToTalkKey);
            bool pttDown = W.AsyncKeyDown(pttVk);
            if (pttDown != _pttPrevDown)
            {
                _pttPrevDown = pttDown;
                MediaNative.fc_voice_set_muted((_muted || _deafened || !pttDown) ? 1 : 0);
                needRedraw = true;
            }
        }

        bool wantStats = _streaming || dc.VoiceConnected || dc.IsWatchingAnyStream || _loopback || _showSettings || _audioPopupMode != 0 || _showVoiceInfo;
        if (wantStats)
        {
            _lastStatsTick = now;
            fixed (FcAudioStats* pa = &_audioStats) MediaNative.fc_voice_get_stats(pa);
            fixed (FcScreenStats* ps = &_selfScreenStats) MediaNative.fc_screen_get_stats(ps);
            if (dc.IsWatchingAnyStream || dc.IsPublishingStream) dc.PollWatchStats();

            bool micLive = !_muted && !_deafened && (!SettingsStore.Current.PushToTalk || _pttPrevDown);
            bool selfSpk = micLive && (dc.VoiceConnected || _loopback || _showSettings || _audioPopupMode != 0) && (_audioStats.Speaking != 0 || _audioStats.CleanRms > 0.008f);
            dc.UpdateLocalSpeaking(dc.VoiceConnected && selfSpk, _localVoiceLevel);
            if (selfSpk != _prevLocalSpkPoll || selfSpk) { _prevLocalSpkPoll = selfSpk; needRedraw = true; }

            // Dynamic Discord-style Windows System Tray Voice/Speaking/Mute/Deafen icon!
            TrayVoiceState traySt = !dc.VoiceConnected ? TrayVoiceState.Idle
                : _deafened ? TrayVoiceState.Deafened
                : _muted ? TrayVoiceState.Muted
                : selfSpk ? TrayVoiceState.Speaking
                : TrayVoiceState.VoiceConnected;
            App.UpdateTrayVoiceState(traySt);

            // Check if any peer speaking state changed (Guild Voice OR DM / Group DM Call!)
            int peerHash = 0;
            if (dc.VoiceConnected)
            {
                foreach (var vm in dc.GetVoiceMembersSnapshot(dc.ConnectedVoiceGuildId, dc.ConnectedVoiceChannelId))
                    if (dc.IsUserActuallySpeaking(vm))
                        peerHash = HashCode.Combine(peerHash, vm.UserId);
            }
            if (peerHash != _prevPeerSpkHash) { _prevPeerSpkHash = peerHash; needRedraw = true; }

            long sec = now / 1000;
            if (sec != _lastClockSec) { _lastClockSec = sec; needRedraw = true; }
            if (_loopback || _showSettings || _audioPopupMode != 0 || _showVoiceInfo) needRedraw = true;
            if (_streaming && _selfScreenStats.FramesEncoded == 0) needRedraw = true;
        }
        if (!wantStats) App.UpdateTrayVoiceState(TrayVoiceState.Idle);
        if (!string.IsNullOrEmpty(_toast) && now >= _toastUntil) { _toast = ""; needRedraw = true; }
        return needRedraw;
    }

    private static void CloseSettingsModal()
    {
        _showSettings = false;
        _settingsOpenDropdown = 0;
        if (_loopback)
        {
            _loopback = false;
            ApplyAudioSettings();
            if (!DiscordClient.Instance.VoiceConnected) MediaNative.fc_voice_stop();
        }
    }

    public static void OnEscape()
    {
        if (_lightboxUrl != null) _lightboxUrl = null;
        else if (_profileUserId != null) _profileUserId = null;
        else if (_ctxStreamUserId != null) _ctxStreamUserId = null;
        else if (_ctxUser != null) _ctxUser = null;
        else if (_audioPopupMode != 0) { _audioPopupMode = 0; _audioPopupSub = 0; }
        else if (_showVoiceInfo) _showVoiceInfo = false;
        else if (_showStatusMenu) _showStatusMenu = false;
        else if (_showEmojiPicker) _showEmojiPicker = false;
        else if (_showPinsPopup) _showPinsPopup = false;
        else if (_showShareModal) _showShareModal = false;
        else if (_showSettings) CloseSettingsModal();
        else if (_showQuickSwitch) { _showQuickSwitch = false; _searchQuery = ""; }
        else if (_showChannelSearch) { _showChannelSearch = false; _searchQuery = ""; }
        else if (!string.IsNullOrEmpty(_editingMsgId)) { _editingMsgId = ""; _editingOrigText = ""; App.TakeChatInputText(); }
        else if (!string.IsNullOrEmpty(_replyToId)) { _replyToId = ""; _replyToLabel = ""; }
        else if (DiscordClient.Instance.FocusedStreamUserId != null) DiscordClient.Instance.FocusedStreamUserId = null;
        App.Invalidate();
    }

    public static void Init()
    {
        LocalCache.Init();
        MediaNative.fc_init();
        RefreshHardwareListsAsync();
        ApplyAudioSettings();

        DiscordClient.Instance.StateChanged += () =>
        {
            if (DiscordClient.Instance.IsConnected || DiscordClient.Instance.IsDemoMode)
                RemoteAuthQr.Instance.Stop();
            App.WakeUi();
        };
        DiscordClient.Instance.ReadyCompleted += () => _ = ShellAsync.RestoreAfterUpdateAsync();
        RemoteAuthQr.Instance.Changed += () => App.WakeUi();

        var savedToken = SettingsStore.GetToken();
        if (App.DemoArg) DiscordClient.Instance.EnterDemoMode();
        else if (!string.IsNullOrEmpty(savedToken)) _ = DiscordClient.Instance.ConnectAsync(savedToken);
    }

    private static int _hwRefreshing;
    public static void RefreshHardwareListsAsync()
    {
        if (Interlocked.Exchange(ref _hwRefreshing, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                var caps = MediaNative.ListAudioDevices(true);
                var rens = MediaNative.ListAudioDevices(false);
                var mons = MediaNative.ListMonitors();
                _capDevices = caps;
                _renDevices = rens;
                _monitors = mons;
                App.WakeUi();
            }
            catch { }
            finally { Interlocked.Exchange(ref _hwRefreshing, 0); }
        });
    }

    public static void ApplyAudioSettings()
    {
        var s = SettingsStore.Current;
        MediaNative.fc_voice_set_noise_config(s.NoiseMode, s.NoiseStrength, s.HpfEnabled ? 1 : 0, s.AgcEnabled ? 1 : 0, s.GateDb);
        MediaNative.fc_voice_set_input_gain(s.InputGain);
        MediaNative.fc_voice_set_output_volume(s.OutputVolume);
        bool effMute = _muted || _deafened || (s.PushToTalk && !_pttPrevDown);
        MediaNative.fc_voice_set_muted(effMute ? 1 : 0);
        MediaNative.fc_voice_set_deafened(_deafened ? 1 : 0);
        MediaNative.fc_voice_set_loopback(_loopback ? 1 : 0);
    }

    public static void StartStream(int monitorIndex, ulong targetHwnd)
    {
        var s = SettingsStore.Current;
        bool isDual = monitorIndex == -2 && targetHwnd == 0;
        int w, h;
        if (isDual)
        {
            // Zero-Copy 32:9 Dual-Screen Composite (Monitor 1 + Monitor 2 side-by-side in VRAM!)
            bool hiRes = s.StreamHeight == 0 || s.StreamHeight >= 1080;
            w = hiRes ? 3840 : 2560;
            h = hiRes ? 1080 : 720;
        }
        else
        {
            w = s.StreamWidth;
            h = s.StreamHeight;
            if (w <= 0 || h <= 0)
            {
                var mon = _monitors.Count > 0 && monitorIndex >= 0 && monitorIndex < _monitors.Count
                    ? _monitors[monitorIndex]
                    : (0, 1920, 1080, 60, "");
                w = mon.Item2; h = mon.Item3;
            }
            w = Math.Clamp(w, 640, 3840) & ~7;
            h = Math.Clamp(h, 360, 2160) & ~7;
        }

        _activeStreamMonitor = monitorIndex;
        _activeStreamHwnd = targetHwnd;

        int autoBitrate = isDual
            ? (w >= 3840 ? 14000 : 9500)
            : (s.StreamOptimizeDetail
                ? (h >= 2160 ? 11500 : h >= 1440 ? 9000 : h >= 1080 ? 7000 : h >= 720 ? 4500 : 2500)
                : (h >= 2160 ? 9500 : h >= 1440 ? 7500 : h >= 1080 ? 5500 : h >= 720 ? 3500 : 2000));
        int bitrate = s.StreamBitrateKbps > 0
            ? Math.Clamp(s.StreamBitrateKbps, Math.Min(2500, autoBitrate), 35000)
            : autoBitrate;

        int ok = MediaNative.fc_screen_start_ex(
            monitorIndex, targetHwnd, w, h, s.StreamFps, bitrate, (int)VideoCodec.H264,
            1, App.SelfStreamHwnd, s.StreamCaptureAudio ? 1 : 0, s.RenderDeviceId);

        _streaming = ok != 0;
        if (_streaming)
        {
            SoundFx.Play(UiSound.StreamStart);
            _ = DiscordClient.Instance.StartPublishingGoLiveAsync(w, h, s.StreamFps, isDualScreen: isDual);
            _inVoiceStage = true;
            Toast(isDual
                ? $"⚡ 2 Экрана сразу запущены ({w}×{h} @ {s.StreamFps}fps{(s.StreamCaptureAudio ? " + Звук" : "")})"
                : $"Трансляция запущена ({w}x{h} @ {s.StreamFps}fps{(s.StreamCaptureAudio ? " + Звук" : "")})");
        }
        else
        {
            Toast(isDual
                ? "Для режима 2 Экрана нужны 2 активных монитора"
                : "Не удалось запустить захват экрана");
        }
        App.Invalidate();
    }

    public static void StopStream()
    {
        if (!_streaming) return;
        MediaNative.fc_screen_stop();
        _streaming = false;
        SoundFx.Play(UiSound.StreamStop);
        DiscordClient.Instance.StopPublishingGoLive();
        Toast("Трансляция экрана остановлена");
        App.Invalidate();
    }

    public static void OpenScreenShareModal()
    {
        _ctxUser = null;
        _ctxStreamUserId = null;
        _audioPopupMode = 0;
        _showShareModal = true;
        RefreshShareSourcesAsync();
        App.Invalidate();
    }

    public static ResumeSession CaptureResumeSession(string targetVer)
    {
        var dc = DiscordClient.Instance;
        var snap = new ResumeSession
        {
            SavedUtcTicks = DateTime.UtcNow.Ticks,
            FromVersion = Updater.CurrentVersion,
            TargetVersion = targetVer,
            ActiveGuildId = dc.ActiveGuildId,
            ActiveChannelId = dc.ActiveChannelId,
            InVoiceStage = _inVoiceStage,
            ShowFriendsView = _showFriendsView,
            VoiceGuildId = dc.VoiceConnected ? dc.ConnectedVoiceGuildId : "",
            VoiceChannelId = dc.VoiceConnected ? dc.ConnectedVoiceChannelId : "",
            Muted = _muted,
            Deafened = _deafened,
            FocusedStreamUserId = dc.FocusedStreamUserId,
            WasStreaming = _streaming,
            StreamMonitor = _activeStreamMonitor,
            StreamHwnd = _activeStreamHwnd
        };
        foreach (var ws in dc.GetWatchSessionsSnapshot())
        {
            snap.WatchedStreams.Add(new ResumeWatchItem
            {
                UserId = ws.StreamerUserId,
                Name = ws.StreamerName,
                Volume = ws.Volume,
                Muted = ws.Muted
            });
        }
        return snap;
    }

    public static void ApplyRestoredUiState(ResumeSession snap)
    {
        var dc = DiscordClient.Instance;
        _muted = snap.Muted;
        _deafened = snap.Deafened;
        ApplyAudioSettings();
        if (!string.IsNullOrEmpty(snap.ActiveGuildId)) dc.ActiveGuildId = snap.ActiveGuildId;
        if (!string.IsNullOrEmpty(snap.ActiveChannelId))
        {
            dc.ActiveChannelId = snap.ActiveChannelId;
            _ = dc.LoadMessagesAsync(snap.ActiveChannelId);
        }
        _inVoiceStage = snap.InVoiceStage;
        _showFriendsView = snap.ShowFriendsView;
        App.WakeUi();
    }

    public static void Toast(string msg, int ms = 2600)
    {
        _toast = msg;
        _toastUntil = Environment.TickCount64 + ms;
        App.Invalidate();
    }

    public static void OnChatEnter(string? preTakenText = null)
    {
        var dc = DiscordClient.Instance;
        string raw = preTakenText ?? App.PeekChatInputText();
        string trimmed = raw.Trim();
        if (string.IsNullOrEmpty(dc.ActiveChannelId) || string.IsNullOrEmpty(trimmed)) return;
        if (trimmed.Length > 2000)
        {
            if (preTakenText != null) App.SetChatInputText(raw);
            Toast($"Сообщение слишком длинное ({trimmed.Length} / 2000 симв.)", 3500);
            return;
        }
        if (preTakenText == null) App.TakeChatInputText();

        // P2P Profile Slash Commands (/nick, /status, /avatar, /discord)
        if (trimmed.StartsWith("/nick ", StringComparison.OrdinalIgnoreCase))
        {
            string nn = trimmed[6..].Trim();
            if (!string.IsNullOrEmpty(nn))
            {
                _ = dc.SetProfileAsync(nn, null, null);
                Toast($"✅ Никнейм изменён на «{nn}»");
            }
            return;
        }
        if (trimmed.StartsWith("/status ", StringComparison.OrdinalIgnoreCase))
        {
            string st = trimmed[8..].Trim();
            _ = dc.SetProfileAsync(null, st, null);
            Toast($"✅ Статус обновлён: {st}");
            return;
        }
        if (string.Equals(trimmed, "/avatar", StringComparison.OrdinalIgnoreCase))
        {
            PickAndUploadAvatar();
            return;
        }
        if (string.Equals(trimmed, "/discord", StringComparison.OrdinalIgnoreCase))
        {
            Toast("🔄 Подтягиваю аватар из Discord...");
            _ = dc.TryImportFromDiscordAsync(silent: false);
            return;
        }
        if (trimmed.StartsWith("/channel ", StringComparison.OrdinalIgnoreCase))
        {
            string cname = trimmed[9..].Trim();
            if (!string.IsNullOrEmpty(cname)) { _ = dc.CreateChannelAsync(cname, isVoice: false); Toast($"✅ Создан текстовый канал #{cname}"); }
            return;
        }
        if (trimmed.StartsWith("/voice ", StringComparison.OrdinalIgnoreCase))
        {
            string vname = trimmed[7..].Trim();
            if (!string.IsNullOrEmpty(vname)) { _ = dc.CreateChannelAsync(vname, isVoice: true); Toast($"✅ Создан голосовой канал 🔊 {vname}"); }
            return;
        }

        if (!string.IsNullOrEmpty(_editingMsgId))
        {
            _ = dc.EditMessageAsync(dc.ActiveChannelId, _editingMsgId, trimmed);
            _editingMsgId = "";
            _editingOrigText = "";
            Toast("Сообщение изменено");
        }
        else
        {
            string cid = dc.ActiveChannelId;
            string? rep = string.IsNullOrEmpty(_replyToId) ? null : _replyToId;
            _replyToId = "";
            _replyToLabel = "";
            _chatScroll = 0;
            SendAndRestoreOnFailureAsync(cid, trimmed, rep);
        }
    }

    private static void SendAndRestoreOnFailureAsync(string channelId, string text, string? replyId)
    {
        _ = DiscordClient.Instance.SendMessageAsync(channelId, text, replyId).ContinueWith(t =>
        {
            if (t.IsFaulted || t.IsCanceled || !t.Result)
            {
                App.InvokeOnUiThread(() =>
                {
                    if (string.IsNullOrEmpty(App.PeekChatInputText()))
                        App.SetChatInputText(text);
                    Toast("Не удалось отправить сообщение (проверьте права / соединение)", 4000);
                });
            }
        }, TaskScheduler.Default);
    }

    public static void PickAndUploadAvatar()
    {
        App.InvokeOnUiThread(() =>
        {
            string? path = W.PickFile(App.Hwnd, imagesOnly: true);
            if (!string.IsNullOrEmpty(path))
            {
                Toast("⏳ Загрузка аватарки на Jetson...");
                _ = DiscordClient.Instance.SetCustomAvatarFromFileAsync(path);
            }
        });
    }

    public static void OnTokenSubmit(string token)
    {
        if (!string.IsNullOrWhiteSpace(token)) _ = DiscordClient.Instance.ConnectAsync(token);
    }

    public static void Draw()
    {
        DrainPendingShareBitmaps();
        long now = Environment.TickCount64;
        var dc = DiscordClient.Instance;

        if (dc.VoiceConnected && _voiceJoinedTick == 0) _voiceJoinedTick = now;
        else if (!dc.VoiceConnected) _voiceJoinedTick = 0;

        bool wantAudioStats = _streaming || dc.VoiceConnected || dc.IsWatchingAnyStream || _loopback || _showSettings || _audioPopupMode != 0;
        if (wantAudioStats && now - _lastStatsTick >= 40)
        {
            _lastStatsTick = now;
            fixed (FcAudioStats* pa = &_audioStats) MediaNative.fc_voice_get_stats(pa);
            fixed (FcScreenStats* ps = &_selfScreenStats) MediaNative.fc_screen_get_stats(ps);
            if (dc.IsWatchingAnyStream || dc.IsPublishingStream) dc.PollWatchStats();
            if (_loopback || _showSettings || _audioPopupMode != 0) UI.Animate();
        }

        // Smooth real-time local speaking state & logarithmic mic level (0..1)
        bool micLive = !_muted && !_deafened && (!SettingsStore.Current.PushToTalk || _pttPrevDown);
        bool selfRawSpk = micLive && (dc.VoiceConnected || _loopback || _showSettings || _audioPopupMode != 0) && (_audioStats.Speaking != 0 || _audioStats.CleanRms > 0.008f);
        float rms = MathF.Max(_audioStats.CleanRms, selfRawSpk ? _audioStats.RawRms * 0.55f : 0f);
        float db = 20f * MathF.Log10(MathF.Max(rms, 1e-5f));
        float targetLvl = selfRawSpk ? Math.Clamp((db + 52f) / 36f, 0.18f, 1f) : 0f;
        _localVoiceLevel += (targetLvl - _localVoiceLevel) * (targetLvl > _localVoiceLevel ? 0.44f : 0.18f);
        float targetSpkA = selfRawSpk ? 1f : 0f;
        _localSpeakAnim += (targetSpkA - _localSpeakAnim) * (targetSpkA > _localSpeakAnim ? 0.42f : 0.16f);
        if (_localVoiceLevel < 0.005f) _localVoiceLevel = 0f;
        if (_localSpeakAnim < 0.005f) _localSpeakAnim = 0f;
        dc.UpdateLocalSpeaking(dc.VoiceConnected && selfRawSpk, _localVoiceLevel);
        if (_localSpeakAnim > 0.005f) UI.Animate();

        _wantChatInput = false;
        _wantTokenInput = false;
        _wantSearchInput = false;
        _wantSelfHost = false;
        for (int i = 0; i < App.MaxWatchSlots; i++) _watchSlotsLayout[i] = (false, default);
        _overlayCutouts.Clear();

        bool fullModalOpen = _showSettings || _showShareModal || _showQuickSwitch || _lightboxUrl != null;
        bool anyPopupOpen = _showVoiceInfo || _showStatusMenu || _showEmojiPicker || _showPinsPopup ||
                            _profileUserId != null || _ctxUser != null || _ctxStreamUserId != null || _audioPopupMode != 0;
        bool hoverPreviewOver = false;
        for (int i = 0; i < _prevPopupRects.Count; i++)
            if (_prevPopupRects[i].Contains(UI.MX, UI.MY)) { hoverPreviewOver = true; break; }

        float w = Gfx.W, h = Gfx.H;
        UI.InputBlocked = false;
        DrawTopBar(new R(0, 0, w, TopH));

        // Block background click-through whenever ANY modal or popup is open (outside clicks dismiss ONLY the popup!)
        UI.InputBlocked = fullModalOpen || anyPopupOpen || hoverPreviewOver;

        if (!dc.IsConnected && !dc.HasActiveSession && !dc.IsDemoMode)
        {
            DrawLoginScreen(new R(0, TopH, w, h - TopH));
        }
        else
        {
            DrawGuildRail(new R(0, TopH, RailW, h - TopH));
            DrawChannelSidebar(new R(RailW, TopH, SideW, h - TopH));
            if (_inVoiceStage && (dc.VoiceConnected || dc.IsWatchingAnyStream || _streaming))
                DrawVoiceStage(new R(RailW + SideW, TopH, w - RailW - SideW, h - TopH));
            else if (dc.ActiveGuildId == "@me" && _showFriendsView)
                DrawFriendsStage(new R(RailW + SideW, TopH, w - RailW - SideW, h - TopH));
            else
                DrawMainStage(new R(RailW + SideW, TopH, w - RailW - SideW, h - TopH));
        }

        // Overlays (drawn on top; popups punch exact cutouts in live video HWNDs!)
        UI.InputBlocked = fullModalOpen;
        int popupCutoutStart = _overlayCutouts.Count;
        if (_hoverStreamVm != null && !fullModalOpen && _ctxUser == null && _ctxStreamUserId == null && _profileUserId == null)
            DrawHoverStreamPreview();
        if (_showVoiceInfo) DrawVoiceInfoPopup();
        if (_showStatusMenu) DrawStatusPopup();
        if (_showEmojiPicker) DrawEmojiPicker();
        if (_showPinsPopup) DrawPinnedPopup();
        if (_profileUserId != null) DrawUserProfilePopup();
        if (_ctxUser != null) DrawUserContextMenu();
        if (_ctxStreamUserId != null) DrawStreamContextMenu();
        if (_audioPopupMode != 0) DrawAudioQuickPopup();

        _prevPopupRects.Clear();
        for (int i = popupCutoutStart; i < _overlayCutouts.Count; i++)
            _prevPopupRects.Add(_overlayCutouts[i]);

        UI.InputBlocked = false;
        if (_lightboxUrl != null) DrawImageLightbox(new R(0, TopH, w, h - TopH));
        else if (_showShareModal) DrawScreenShareModal(new R(0, TopH, w, h - TopH));
        else if (_showSettings) DrawSettingsOverlay(new R(0, TopH, w, h - TopH));
        else if (_showQuickSwitch) DrawQuickSwitcher(new R(0, TopH, w, h - TopH));

        fullModalOpen = _showSettings || _showShareModal || _showQuickSwitch || _lightboxUrl != null;
        if (fullModalOpen)
        {
            for (int i = 0; i < App.MaxWatchSlots; i++) _watchSlotsLayout[i].Show = false;
            _wantSelfHost = false;
        }

        if (!string.IsNullOrEmpty(_toast) && now < _toastUntil)
        {
            var tr = new R((w - 460) * 0.5f, h - 56, 460, 36);
            Gfx.FillRect(tr, 0xEE1F2937, 8);
            Gfx.StrokeRect(tr, Th.Brand, 1, 8);
            Gfx.DrawText(_toast, new R(tr.X + 12, tr.Y + 9, tr.W - 24, 20), 12.5f, Th.Text, 600, 1);
            _overlayCutouts.Add(tr);
            UI.Animate();
        }

        ReadOnlySpan<R> cutoutsSpan = _overlayCutouts.Count > 0 ? System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_overlayCutouts) : [];
        App.SyncNativeChildren(_wantChatInput, _chatInputRect, _wantTokenInput, _tokenInputRect,
            _wantSearchInput, _searchInputRect,
            _watchSlotsLayout, _wantSelfHost, _selfHostRect, cutoutsSpan);
    }

    private static Vortice.Direct2D1.ID2D1Bitmap1? GetBrandIcon(int maxDim = 96, ImgMask mask = ImgMask.None)
        => ImageCache.GetLocal(Path.Combine(AppContext.BaseDirectory, "Assets", "fluxcord.png"), maxDim, mask);

    private static void DrawTopBar(R r)
    {
        Gfx.FillRect(r, Th.Rail);
        Gfx.FillRect(new R(0, r.Bottom - 1, r.W, 1), Th.Border);

        var dc = DiscordClient.Instance;
        var topIcon = GetBrandIcon(48);
        if (topIcon != null) Gfx.DrawBitmap(topIcon, new R(10, r.Y + (r.H - 20f) * 0.5f, 20, 20));
        else Gfx.FillCircle(20, r.Y + r.H * 0.5f, 7, Th.Brand);
        Gfx.DrawText("FluxP2P", new R(36, r.Y + 9, 90, 22), 13.5f, Th.Cyan, 700);

        if (_cachedRamMb == 0)
        {
            try { using var p = Process.GetCurrentProcess(); _cachedRamMb = p.WorkingSet64 / (1024 * 1024); }
            catch { }
        }
        int showPing = dc.VoiceConnected && dc.VoicePingMs > 0 ? dc.VoicePingMs : dc.GatewayPingMs;
        string modeTag = dc.VoiceConnected && !string.IsNullOrEmpty(dc.VoiceEndpointHost) ? $" • {dc.VoiceEndpointHost}" : "";
        string perf = (dc.HasActiveSession && !dc.IsConnected)
            ? $"RAM {_cachedRamMb} МБ • ⟳ Реконнект к Jetson P2P..."
            : $"RAM {_cachedRamMb} МБ • {Gfx.LastFlushMs:F1} мс • Пинг {showPing} мс{modeTag}";
        if (_streaming && dc.IsConnected) perf += _activeStreamMonitor == -2 ? $" • 2-ЭКР {_selfScreenStats.Fps:F0}fps" : $" • LIVE {_selfScreenStats.Fps:F0}fps";
        var pill = new R(108, r.Y + 8, MathF.Min(410, r.W * 0.36f), 22);
        Gfx.FillRect(pill, Th.Panel, 11);
        uint perfCol = (dc.HasActiveSession && !dc.IsConnected) ? Th.Amber : (_streaming ? Th.Green : (showPing > 0 && showPing < 95 ? Th.Green : Th.Cyan));
        Gfx.DrawText(perf, new R(pill.X + 10, pill.Y + 3, pill.W - 20, 16), 11f, perfCol, 600);

        // Centered server name (exact GoofCord top bar!)
        DiscordGuild? g;
        lock (dc.Guilds) g = dc.Guilds.FirstOrDefault(x => x.Id == dc.ActiveGuildId);
        string centerTitle = dc.ActiveGuildId == "@me" ? "Личные сообщения" : (g?.Name ?? "FluxCord");
        Gfx.DrawText(centerTitle, new R(r.X + r.W * 0.35f, r.Y + 9, r.W * 0.3f, 20), 13.5f, Th.Sub, 600, 1);

        float bx = r.Right - 132;
        float rightZone = 140f;

        // Ultra-Updater live top-bar pill (downloads silently in background, 1-click instant apply + auto-rejoin call!)
        if (Updater.HasUpdate || Updater.Downloading || Updater.Applying)
        {
            string uTxt = Updater.Applying ? "⚡ Установка..." :
                          Updater.Downloading ? $"⬇ v{Updater.AvailableVersion} {(int)(Updater.Progress * 100)}%" :
                          Updater.IsReadyToApply ? $"⬆ Обновить до v{Updater.AvailableVersion} ⚡" :
                          $"⬆ Доступно v{Updater.AvailableVersion}";
            float uw = Gfx.MeasureText(uTxt, 11.5f, 700, 240).W + 22;
            var ur = new R(bx - uw - 10, r.Y + 6, uw, 26);
            rightZone = r.Right - ur.X + 8f;
            uint ubg = Updater.IsReadyToApply ? Th.Green : (Updater.Downloading ? Th.Card : Th.Brand);
            Gfx.FillRect(ur, ubg, 13);
            if (Updater.Downloading)
                Gfx.FillRect(new R(ur.X, ur.Y, ur.W * (float)Updater.Progress, ur.H), 0x6623A55A, 13);
            if (UI.Hover(ur)) Gfx.FillRect(ur, Th.Hover, 13);
            Gfx.DrawText(uTxt, new R(ur.X, ur.Y + 5, ur.W, 16), 11.5f, 0xFFFFFFFF, 700, 1);
            if (UI.Click(UI.Id("top_upd"), ur)) Updater.StartUpdateNow();
        }
        TopBarRightZoneWidth = rightZone;

        if (UI.Button(UI.Id("win_min"), new R(bx, r.Y + 4, 40, 30), "—", 0x00000000, Th.Sub, 4))
            App.InvokeOnUiThread(() => W.ShowWindow(App.Hwnd, W.SW_MINIMIZE));
        if (UI.Button(UI.Id("win_max"), new R(bx + 44, r.Y + 4, 40, 30), "□", 0x00000000, Th.Sub, 4))
            App.InvokeOnUiThread(() => W.ShowWindow(App.Hwnd, W.IsZoomed(App.Hwnd) ? W.SW_SHOWNORMAL : W.SW_MAXIMIZE));
        if (UI.Button(UI.Id("win_cls"), new R(bx + 88, r.Y + 4, 40, 30), "✕", 0x00000000, Th.Red, 4))
            W.PostMessageW(App.Hwnd, W.WM_CLOSE, 0, 0);
    }

    private static void QueueQrStartIfNeeded()
    {
        if (_qrStartQueued) return;
        _qrStartQueued = true;
        App.InvokeOnUiThread(() =>
        {
            _qrStartQueued = false;
            var dc = DiscordClient.Instance;
            var qr = RemoteAuthQr.Instance;
            if (!dc.IsConnected && !dc.HasActiveSession && !dc.IsConnecting && !dc.HasToken && !dc.IsDemoMode && !qr.IsRunning)
                qr.Start();
        });
    }

    private static void DrawLoginScreen(R r)
    {
        Gfx.FillRect(r, Th.Bg);
        var dc = DiscordClient.Instance;
        if (dc.IsConnecting)
        {
            var splash = new R(r.X + (r.W - 460) * 0.5f, r.Y + (r.H - 220) * 0.5f, 460, 220);
            Gfx.FillRect(splash, Th.Panel, 14);
            Gfx.StrokeRect(splash, Th.Border, 1, 14);
            var spIcon = GetBrandIcon(96);
            if (spIcon != null) Gfx.DrawBitmap(spIcon, new R(splash.X + (splash.W - 52) * 0.5f, splash.Y + 26, 52, 52));
            Gfx.DrawText("Подключение к Jetson Orin P2P...", new R(splash.X + 20, splash.Y + 92, splash.W - 40, 26), 17, Th.Text, 700, 1);
            Gfx.DrawText(dc.StatusText, new R(splash.X + 20, splash.Y + 122, splash.W - 40, 20), 12.5f, Th.Cyan, 500, 1);
            if (UI.Button(UI.Id("btn_cancel_conn"), new R(splash.X + 120, splash.Y + 158, 220, 34), "Отмена / Настроить комнату", Th.Card, Th.Sub, 8, 12.5f))
                dc.Disconnect();
            UI.Animate();
            return;
        }

        var card = new R(r.X + (r.W - 540) * 0.5f, r.Y + (r.H - 390) * 0.5f, 540, 390);
        Gfx.FillRect(card, Th.Panel, 14);
        Gfx.StrokeRect(card, Th.Border, 1, 14);

        float lx = card.X + 32, lw = card.W - 64;
        var loginIcon = GetBrandIcon(96);
        if (loginIcon != null) Gfx.DrawBitmap(loginIcon, new R(lx, card.Y + 26, 44, 44));
        Gfx.DrawText("FluxCord P2P • Jetson Orin Nano", new R(lx + 54, card.Y + 26, lw - 54, 28), 20, Th.Text, 700);
        Gfx.DrawText("Прямой XChaCha20-Poly1305 UDP P2P + Jetson Relay • 4K60 NVENC", new R(lx + 54, card.Y + 54, lw - 54, 20), 12f, Th.Cyan);

        Gfx.DrawText($"Ваш никнейм: {SettingsStore.Current.P2pNickname}   •   Узел: Jetson Orin (185.27.71.230)", new R(lx, card.Y + 92, lw, 22), 12.5f, Th.Green, 600);

        Gfx.DrawText("Ключ приватной комнаты (Room Secret или Ник|Ключ):", new R(lx, card.Y + 132, lw, 20), 12.5f, Th.Sub);
        _tokenInputRect = new R(lx, card.Y + 158, lw, 38);
        _wantTokenInput = true;
        Gfx.FillRect(_tokenInputRect, Th.Bg, 8);
        Gfx.StrokeRect(_tokenInputRect, Th.Border, 1, 8);

        if (UI.Button(UI.Id("btn_login"), new R(lx, card.Y + 212, lw, 42), "⚡ Подключиться к P2P комнате (Jetson Orin)", Th.Brand, Th.Text, 8, 14, 700))
            OnTokenSubmit(App.GetTokenInputText());
        if (UI.Button(UI.Id("btn_demo"), new R(lx, card.Y + 266, lw, 40), "Запустить локальный Demo / Тест 4K Zero-Copy", Th.Card, Th.Cyan, 8, 13, 600))
            DiscordClient.Instance.EnterDemoMode();

        Gfx.DrawText(DiscordClient.Instance.StatusText, new R(lx, card.Y + 324, lw, 24), 12, Th.Sub);
    }

    private static void DrawGuildRail(R r)
    {
        Gfx.FillRect(r, Th.Rail);
        Gfx.FillRect(new R(r.Right - 1, r.Y, 1, r.H), Th.Border);
        var dc = DiscordClient.Instance;
        _hoverGuildTip = "";

        List<DiscordGuild> guilds;
        lock (dc.Guilds) guilds = [.. dc.Guilds];

        float contentH = 12 + 56 + guilds.Count * 54 + 20;
        float maxScroll = MathF.Max(0, contentH - r.H);
        if (UI.Hover(r) && UI.Wheel != 0) _railScroll = Math.Clamp(_railScroll - UI.Wheel * 54f, 0f, maxScroll);
        else _railScroll = Math.Clamp(_railScroll, 0f, maxScroll);

        Gfx.PushClip(r);
        float y = r.Y + 12 - _railScroll;

        bool dmAct = dc.ActiveGuildId == "@me";
        var dmRect = new R(r.X + 12, y, 44, 44);
        bool dmOver = UI.Hover(dmRect);
        if (dmAct) Gfx.FillRect(new R(r.X, y + 6, 4, 32), Th.Text, 2);
        else if (dmOver) Gfx.FillRect(new R(r.X, y + 14, 4, 16), Th.Text, 2);
        var dmLogo = GetBrandIcon(96, dmAct || dmOver ? ImgMask.None : ImgMask.Circle);
        if (dmLogo != null)
        {
            Gfx.DrawBitmap(dmLogo, dmRect);
            if (!dmAct && !dmOver) Gfx.FillRect(dmRect, 0x22000000, 22);
        }
        else
        {
            Gfx.FillRect(dmRect, dmAct || dmOver ? Th.Brand : Th.Card, dmAct || dmOver ? 14 : 22);
            Gfx.DrawIcon(IcChat, "ЛС", dmRect, 20, Th.Text);
        }
        if (dmOver) { _hoverGuildTip = "Личные сообщения"; _hoverGuildTipY = y + 8; }
        int dmBadge = 0;
        if (SettingsStore.Current.UnreadBadge)
        {
            lock (dc.Friends) dmBadge += dc.Friends.Count(f => f.Type == 3);
            lock (dc.DmChannels) foreach (var dmc in dc.DmChannels) dmBadge += dmc.MentionCount;
        }
        if (dmBadge > 0) DrawRedBadge(dmRect.Right + 3, dmRect.Bottom + 2, dmBadge, Th.Rail);
        if (UI.Click(UI.Id("guild_dm"), dmRect))
        {
            _sideScroll = 0;
            SwitchToChannel("@me", "", showFriends: true);
        }
        y += 56;
        Gfx.FillRect(new R(r.X + 18, y - 6, 32, 1), Th.Border);

        for (int i = 0; i < guilds.Count; i++)
        {
            if (y + 44 < r.Y - 10) { y += 54; continue; }
            if (y > r.Bottom + 10) break;

            var g = guilds[i];
            bool act = dc.ActiveGuildId == g.Id;
            bool hasUnread = !act && (g.MentionCount > 0 || g.Channels.Any(c => dc.UnreadChannels.ContainsKey(c.Id)));
            var gr = new R(r.X + 12, y, 44, 44);
            bool over = UI.Hover(gr);

            if (act) Gfx.FillRect(new R(r.X, y + 6, 4, 32), Th.Text, 2);
            else if (over) Gfx.FillRect(new R(r.X, y + 14, 4, 16), Th.Text, 2);
            else if (hasUnread) Gfx.FillRect(new R(r.X, y + 18, 4, 8), Th.Text, 2);

            var bmp = ImageCache.Get(g.IconUrl, 96, act || over ? ImgMask.Squircle : ImgMask.Circle);
            if (bmp != null) Gfx.DrawBitmap(bmp, gr);
            else
            {
                Gfx.FillRect(gr, act || over ? Th.Brand : Th.Card, act || over ? 14 : 22);
                string abbr = SafeTrunc(g.Name, 3).TrimEnd('…').ToUpperInvariant();
                if (abbr.Length > 2 && !char.IsHighSurrogate(abbr[0])) abbr = abbr[..2];
                Gfx.DrawText(abbr, new R(gr.X, gr.Y + 13, gr.W, 20), 13, Th.Text, 700, 1);
            }
            if (SettingsStore.Current.UnreadBadge && g.MentionCount > 0)
                DrawRedBadge(gr.Right + 3, gr.Bottom + 2, g.MentionCount, Th.Rail);

            if (over) { _hoverGuildTip = g.Name; _hoverGuildTipY = y + 8; }
            if (UI.Click(UI.Id("guild", i), gr))
            {
                _sideScroll = 0;
                var first = g.Channels.FirstOrDefault(c => c.Type == 0 || c.Type == 5);
                SwitchToChannel(g.Id, first?.Id ?? "");
            }
            y += 54;
        }
        Gfx.PopClip();

        if (maxScroll > 0)
        {
            float barH = MathF.Max(28, r.H * (r.H / contentH));
            float barY = r.Y + (_railScroll / maxScroll) * (r.H - barH);
            Gfx.FillRect(new R(r.Right - 4, barY, 3, barH), 0x33FFFFFF, 1.5f);
        }
    }

    private static void DrawChannelSidebar(R r)
    {
        Gfx.FillRect(r, Th.Sidebar);
        Gfx.FillRect(new R(r.Right - 1, r.Y, 1, r.H), Th.Border);

        var dc = DiscordClient.Instance;
        DiscordGuild? guild;
        lock (dc.Guilds) guild = dc.Guilds.FirstOrDefault(g => g.Id == dc.ActiveGuildId);
        bool isDmList = dc.ActiveGuildId == "@me";
        if (isDmList)
        {
            var qsBtn = new R(r.X + 10, r.Y + 8, r.W - 20, 28);
            Gfx.FillRect(qsBtn, UI.Hover(qsBtn) ? Th.Hover : Th.Bg, 6);
            Gfx.StrokeRect(qsBtn, Th.Border, 1, 6);
            Gfx.DrawText("Найти или начать беседу", new R(qsBtn.X + 10, qsBtn.Y + 5, qsBtn.W - 20, 18), 12f, Th.Sub, 500, 1);
            if (UI.Click(UI.Id("dm_qs_top"), qsBtn)) ToggleQuickSwitch();
        }
        else
        {
            string title = guild?.Name ?? "Сервер";
            Gfx.DrawText(title, new R(r.X + 14, r.Y + 12, r.W - 28, 24), 15, Th.Text, 700);
        }
        Gfx.FillRect(new R(r.X, r.Y + 44, r.W, 1), Th.Border);

        bool voiceConnecting = !dc.VoiceConnected && !string.IsNullOrEmpty(dc.ConnectedVoiceChannelId);
        float dockH = 60f + (dc.VoiceConnected ? 92f : (voiceConnecting ? 48f : 0f)) + (_streaming ? 42f : 0f);
        var listRect = new R(r.X, r.Y + 46, r.W, r.H - 46 - dockH);

        List<DiscordChannel> channels;
        if (isDmList) { lock (dc.DmChannels) channels = [.. dc.DmChannels]; }
        else channels = guild?.Channels ?? [];
        float totalH = 16 + (isDmList ? 66 : 0);
        foreach (var ch in channels)
        {
            bool catCollapsed = !ch.IsCategory && !string.IsNullOrEmpty(ch.ParentId) && _collapsedCats.Contains(ch.ParentId)
                                && ch.Id != dc.ActiveChannelId && ch.Id != dc.ConnectedVoiceChannelId;
            if (catCollapsed) continue;
            totalH += ch.IsCategory ? 28 : (ch.IsDmOrGroup ? 44 : 34);
            if (ch.IsVoice && guild != null)
            {
                lock (guild.VoiceStates)
                    totalH += guild.VoiceStates.Values.Count(v => v.ChannelId == ch.Id) * 28;
            }
        }
        float maxSideScroll = MathF.Max(0, totalH - listRect.H);
        if (UI.Hover(listRect) && UI.Wheel != 0) _sideScroll = Math.Clamp(_sideScroll - UI.Wheel * 44f, 0f, maxSideScroll);
        else _sideScroll = Math.Clamp(_sideScroll, 0f, maxSideScroll);

        Gfx.PushClip(listRect);
        float y = listRect.Y + 8 - _sideScroll;
        if (isDmList)
        {
            var frBtn = new R(r.X + 8, y, r.W - 16, 36);
            if (_showFriendsView) Gfx.FillRect(frBtn, Th.Active, 6);
            else if (UI.Hover(frBtn)) Gfx.FillRect(frBtn, Th.Hover, 6);
            int onCnt, reqCnt;
            lock (dc.Friends)
            {
                onCnt = dc.Friends.Count(f => f.Type == 1 && f.Status is not ("offline" or "invisible"));
                reqCnt = dc.Friends.Count(f => f.Type == 3);
            }
            Gfx.DrawText($"👥  Друзья ({onCnt} в сети)", new R(frBtn.X + 10, frBtn.Y + 9, frBtn.W - (reqCnt > 0 ? 48 : 20), 18), 13.5f, _showFriendsView ? Th.Text : Th.Sub, 600);
            if (reqCnt > 0) DrawRedBadge(frBtn.Right - 8, frBtn.Y + 27, reqCnt, Th.Sidebar);
            if (UI.Click(UI.Id("dm_friends_btn"), frBtn)) { _showFriendsView = true; _inVoiceStage = false; }
            y += 42;

            Gfx.DrawText("Личные сообщения", new R(r.X + 14, y + 2, r.W - 28, 18), 11.5f, Th.Dim, 700);
            y += 24;
        }
        for (int i = 0; i < channels.Count; i++)
        {
            var ch = channels[i];
            if (ch.IsCategory)
            {
                bool col = _collapsedCats.Contains(ch.Id);
                if (y + 28 >= listRect.Y && y <= listRect.Bottom)
                {
                    var catR = new R(r.X + 6, y + 2, r.W - 12, 24);
                    if (UI.Hover(catR)) Gfx.FillRect(catR, Th.Hover, 4);
                    Gfx.DrawText((col ? "▸ " : "▾ ") + ch.Name.ToUpperInvariant(), new R(r.X + 10, y + 7, r.W - 20, 18), 11, Th.Dim, 700);
                    if (UI.Click(UI.Id("cat", i), catR))
                    {
                        if (col) _collapsedCats.Remove(ch.Id);
                        else _collapsedCats.Add(ch.Id);
                    }
                }
                y += 28;
                continue;
            }
            if (!string.IsNullOrEmpty(ch.ParentId) && _collapsedCats.Contains(ch.ParentId) && ch.Id != dc.ActiveChannelId && ch.Id != dc.ConnectedVoiceChannelId)
                continue;

            bool isActive = !_inVoiceStage && !_showFriendsView && ch.Id == dc.ActiveChannelId;
            bool isVoiceJoined = (ch.IsVoice || ch.IsDmOrGroup) && ch.Id == dc.ConnectedVoiceChannelId;
            float chItemH = ch.IsDmOrGroup ? 42f : 32f;
            var cr = new R(r.X + 8, y, r.W - 16, chItemH);
            if (y + chItemH >= listRect.Y && y <= listRect.Bottom)
            {
                if (isVoiceJoined && !ch.IsDmOrGroup) Gfx.FillRect(cr, 0x2223A55A, 6);
                else if (isActive) Gfx.FillRect(cr, Th.Active, 6);
                else if (UI.Hover(cr)) Gfx.FillRect(cr, Th.Hover, 6);

                if (ch.IsDmOrGroup)
                {
                    bool unread = dc.UnreadChannels.ContainsKey(ch.Id);
                    if (unread && !isActive) Gfx.FillCircle(r.X + 4, cr.Y + 21, 3, Th.Text);

                    var avr = new R(cr.X + 6, cr.Y + 5, 32, 32);
                    if (ch.Type == 3 && string.IsNullOrEmpty(ch.ChannelIcon) && ch.RecipientIds.Count >= 2)
                    {
                        // Official Discord dual-avatar Group DM composite!
                        dc.Users.TryGetValue(ch.RecipientIds[0], out var u0);
                        dc.Users.TryGetValue(ch.RecipientIds[1], out var u1);
                        var b0 = ImageCache.Get(u0?.AvatarUrl, 48, ImgMask.Circle);
                        var b1 = ImageCache.Get(u1?.AvatarUrl, 48, ImgMask.Circle);
                        var r0 = new R(avr.X, avr.Y, 21, 21);
                        var r1 = new R(avr.X + 11, avr.Y + 11, 21, 21);
                        if (b0 != null) Gfx.DrawBitmap(b0, r0); else Gfx.FillCircle(r0.X + 10.5f, r0.Y + 10.5f, 10.5f, Th.Brand);
                        Gfx.FillCircle(r1.X + 10.5f, r1.Y + 10.5f, 12f, isActive ? Th.Active : Th.Sidebar);
                        if (b1 != null) Gfx.DrawBitmap(b1, r1); else Gfx.FillCircle(r1.X + 10.5f, r1.Y + 10.5f, 10.5f, Th.Card);
                    }
                    else
                    {
                        var av = ImageCache.Get(ch.DmAvatarUrl, 64, ImgMask.Circle);
                        if (av != null) Gfx.DrawBitmap(av, avr);
                        else
                        {
                            Gfx.FillCircle(avr.X + 16, avr.Y + 16, 16, ch.Type == 3 ? Th.Brand : Th.Card);
                            Gfx.DrawText(FirstGrapheme(ch.Name), new R(avr.X, avr.Y + 7, 32, 18), 13, Th.Text, 700, 1);
                        }
                        if (ch.Type == 1 && !string.IsNullOrEmpty(ch.RecipientId))
                        {
                            string ust = dc.Users.TryGetValue(ch.RecipientId, out var ru) ? ru.Status : "offline";
                            uint dot = ust switch { "online" => Th.Green, "idle" => Th.Amber, "dnd" => Th.Red, _ => 0xFF4E5058 };
                            Gfx.FillCircle(avr.Right - 4, avr.Bottom - 4, 5f, isActive ? Th.Active : Th.Sidebar);
                            Gfx.FillCircle(avr.Right - 4, avr.Bottom - 4, 3.6f, dot);
                        }
                    }

                    bool hasLiveCall = isVoiceJoined || (dc.DmCalls.TryGetValue(ch.Id, out var dmc) && dmc.VoiceStates.Count > 0);
                    string subLine = "";
                    if (ch.Type == 3)
                        subLine = $"{ch.GroupMemberCount} {PluralizeMembers(ch.GroupMemberCount)}";
                    else if (!string.IsNullOrEmpty(ch.RecipientId) && dc.Users.TryGetValue(ch.RecipientId, out var uAct) && !string.IsNullOrEmpty(uAct.ActivityText))
                        subLine = uAct.ActivityText;
                    if (hasLiveCall)
                        subLine = string.IsNullOrEmpty(subLine) ? "В звонке" : $"В звонке • {subLine}";

                    float rightPad = (hasLiveCall ? 24f : 6f) + (ch.MentionCount > 0 ? 28f : 0f);
                    if (!string.IsNullOrEmpty(subLine))
                    {
                        Gfx.DrawText(ch.Name, new R(cr.X + 46, cr.Y + 4, cr.W - 46 - rightPad, 18), 13.5f,
                            isActive || unread ? Th.Text : Th.Sub, isActive || unread ? 600 : 500);
                        Gfx.DrawText(subLine, new R(cr.X + 46, cr.Y + 22, cr.W - 46 - rightPad, 15), 11f,
                            hasLiveCall ? Th.Green : Th.Dim, hasLiveCall ? 600 : 400);
                    }
                    else
                    {
                        Gfx.DrawText(ch.Name, new R(cr.X + 46, cr.Y + 11, cr.W - 46 - rightPad, 20), 13.5f,
                            isActive || unread ? Th.Text : Th.Sub, isActive || unread ? 600 : 500);
                    }

                    if (hasLiveCall)
                        Gfx.DrawIcon(IcPhoneCall, "📞", new R(cr.Right - (ch.MentionCount > 0 ? 48 : 22), cr.Y + 12, 18, 18), 14, Th.Green);
                    if (ch.MentionCount > 0 && !isActive)
                        DrawRedBadge(cr.Right - 4, cr.Y + 30, ch.MentionCount, Th.Sidebar);
                }
                else
                {
                    var icR = new R(cr.X + 6, cr.Y + 5, 22, 22);
                    if (ch.IsVoice) Gfx.DrawIcon(IcVol, "🔊", icR, 15, isVoiceJoined ? Th.Green : Th.Dim);
                    else Gfx.DrawIcon(IcHash, "#", icR, 15, isActive ? Th.Text : Th.Dim);

                    bool unread = dc.UnreadChannels.ContainsKey(ch.Id);
                    if (unread && !isActive) Gfx.FillCircle(r.X + 4, cr.Y + 16, 3, Th.Text);

                    float nameRightPad = isVoiceJoined && _voiceJoinedTick > 0 ? 58f : 8f;
                    Gfx.DrawText(ch.Name, new R(cr.X + 30, cr.Y + 7, cr.W - 30 - nameRightPad, 20), 13.5f,
                        isVoiceJoined ? Th.Green : (isActive || unread ? Th.Text : Th.Sub), isActive || isVoiceJoined || unread ? 600 : 400);

                    if (isVoiceJoined && _voiceJoinedTick > 0)
                    {
                        var ts = TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - _voiceJoinedTick));
                        string dur = ts.Hours > 0 ? $"{ts.Hours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes:00}:{ts.Seconds:00}";
                        Gfx.DrawText(dur, new R(cr.Right - 56, cr.Y + 8, 50, 16), 11f, Th.Green, 600, 2);
                    }
                }

                if (UI.Click(UI.Id("ch", i), cr))
                {
                    if (ch.IsVoice)
                    {
                        if (!isVoiceJoined) _ = dc.JoinVoiceAsync(ch.GuildId, ch.Id, _muted, _deafened);
                        _inVoiceStage = true;
                        dc.FocusedStreamUserId = null;
                    }
                    else
                    {
                        SwitchToChannel(ch.GuildId, ch.Id);
                    }
                }
            }
            y += ch.IsDmOrGroup ? 44 : 34;

            if (ch.IsVoice && guild != null)
            {
                List<VoiceMember> vms;
                lock (guild.VoiceStates) vms = guild.VoiceStates.Values.Where(v => v.ChannelId == ch.Id).ToList();
                foreach (var vm in vms)
                {
                    if (y + 28 >= listRect.Y && y <= listRect.Bottom)
                    {
                        var vr = new R(r.X + 20, y, r.W - 28, 26);
                        bool rowOver = UI.Hover(vr);
                        if (rowOver) Gfx.FillRect(vr, Th.Hover, 5);

                        var vav = ImageCache.Get(vm.AvatarUrl, 48, ImgMask.Circle);
                        var vavr = new R(vr.X + 4, vr.Y + 3, 20, 20);
                        bool isMe = vm.UserId == dc.Me.Id;
                        bool spk = dc.IsUserActuallySpeaking(vm);
                        float sa = isMe ? _localSpeakAnim : StepSpeakAnim(vm.UserId, spk);
                        bool isMuted = vm.SelfMute || (isMe && (_muted || _deafened));
                        // Crisp glowing Discord neon-green speaking ring + animated equalizer + bright green name!
                        if (sa > 0.04f)
                        {
                            float cx = vavr.X + 10f, cy = vavr.Y + 10f;
                            Gfx.FillCircle(cx, cy, 13.4f, Gfx.Fade(0xFF23F56E, 0.35f * sa));
                            Gfx.FillCircle(cx, cy, 12.2f, Gfx.Fade(0xFF23F56E, sa));
                            Gfx.FillCircle(cx, cy, 10.3f, Th.Sidebar);
                        }
                        if (vav != null) Gfx.DrawBitmap(vav, vavr);
                        else
                        {
                            Gfx.FillCircle(vavr.X + 10, vavr.Y + 10, 10, Th.Brand);
                            Gfx.DrawText(FirstGrapheme(vm.Username), new R(vavr.X, vavr.Y + 2, 20, 16), 10.5f, 0xFFFFFFFF, 700, 1);
                        }

                        float rightUsed = (vm.SelfStream ? 58f : 4f) + (isMuted ? 18f : 0f) + (sa > 0.10f ? 18f : 0f);
                        float nameX = vr.X + 30;
                        if (sa > 0.10f && !isMuted)
                        {
                            DrawVoiceBars(vr.X + 30, vr.Y + 13, 3, 10f, isMe ? MathF.Max(0.35f, _localVoiceLevel) : 0.78f, vm.UserId.GetHashCode() * 0.19f, 0xFF23F56E, sa);
                            nameX += 15f;
                        }
                        Gfx.DrawText(vm.Username, new R(nameX, vr.Y + 5, MathF.Max(20f, vr.Right - nameX - rightUsed + (sa > 0.10f ? 16f : 0f)), 18), 12.5f, sa > 0.10f ? 0xFF4ADE80 : Th.Sub, sa > 0.10f ? 700 : 500);

                        if (isMuted)
                            Gfx.DrawIcon(IcMicOff, "M", new R(vr.Right - (vm.SelfStream ? 74 : 20), vr.Y + 5, 16, 16), 12, isMe ? Th.Red : Th.Dim);

                        if (vm.SelfStream)
                        {
                            if (rowOver && !isMe)
                            {
                                _hoverStreamVm = vm;
                                _hoverStreamGuildId = ch.GuildId;
                                _hoverStreamY = vr.Y;
                                _hoverStreamLastTick = Environment.TickCount64;
                            }
                            bool watching = !isMe && dc.IsWatchingStream(vm.UserId);
                            var br = new R(vr.Right - 54, vr.Y + 5, 50, 16);
                            Gfx.FillRect(br, Th.Red, 8);
                            Gfx.DrawText("В ЭФИРЕ", new R(br.X, br.Y + 1, br.W, 14), 9f, 0xFFFFFFFF, 700, 1);
                            if (watching) Gfx.FillCircle(br.X - 6, br.Y + 8, 3f, Th.Green);
                            if (UI.Click(UI.Id("watch_" + vm.UserId), vr))
                            {
                                _inVoiceStage = true;
                                _hoverStreamVm = null;
                                if (isMe) dc.FocusedStreamUserId = _streaming ? "self" : null;
                                else _ = dc.WatchStreamAsync(ch.GuildId, ch.Id, vm.UserId, vm.Username, focusImmediately: true, selfMute: _muted, selfDeaf: _deafened);
                            }
                        }

                        if (UI.RightClick(vr))
                        {
                            _ctxStreamUserId = null; _audioPopupMode = 0;
                            _ctxUser = vm;
                            _ctxGuildId = ch.GuildId;
                            _ctxX = Math.Clamp(UI.MX, 8, Gfx.W - 252);
                            _ctxY = Math.Clamp(UI.MY, TopH + 8, Gfx.H - 220);
                        }
                    }
                    y += 28;
                }
            }
        }
        Gfx.PopClip();

        if (maxSideScroll > 0)
        {
            float barH = MathF.Max(28, listRect.H * (listRect.H / totalH));
            float barY = listRect.Y + (_sideScroll / maxSideScroll) * (listRect.H - barH);
            Gfx.FillRect(new R(r.Right - 4, barY, 3, barH), 0x33FFFFFF, 1.5f);
        }

        // Exact GoofCord Bottom-Left Voice Dock + User Bar (matches f_004.jpg & f_037.jpg!)
        float dy = r.Bottom - dockH;
        Gfx.FillRect(new R(r.X, dy, r.W, dockH), Th.Rail);
        Gfx.FillRect(new R(r.X, dy, r.W, 1), Th.Border);

        // 1. Go Live mini-bar when streaming
        if (_streaming)
        {
            var glr = new R(r.X + 8, dy + 6, r.W - 16, 30);
            Gfx.FillRect(glr, Th.Panel, 6);
            Gfx.FillRect(new R(glr.X + 4, glr.Y + 4, 22, 22), _activeStreamMonitor == -2 ? Th.Cyan : Th.Amber, 5);
            Gfx.DrawIcon(_activeStreamMonitor == -2 ? IcZap : IcMonitor, "G", new R(glr.X + 4, glr.Y + 4, 22, 22), 13, 0xFF0A0B0E);
            Gfx.DrawText(string.IsNullOrEmpty(dc.PublishStatus) ? "Go Live" : dc.PublishStatus, new R(glr.X + 32, glr.Y + 7, glr.W - 64, 16), 12, Th.Text, 700);
            var stp = new R(glr.Right - 28, glr.Y + 3, 24, 24);
            if (UI.Hover(stp)) Gfx.FillRect(stp, 0x33ED4245, 5);
            Gfx.DrawIcon(IcMonitorOff, "X", stp, 14, Th.Sub);
            if (UI.Click(UI.Id("dock_stop_gl"), stp)) StopStream();
            dy += 42;
            Gfx.FillRect(new R(r.X, dy, r.W, 1), Th.Border);
        }

        // 2. Voice Connecting or Voice Connected Block
        if (voiceConnecting)
        {
            Gfx.DrawText(string.IsNullOrEmpty(dc.VoiceStatus) ? "Подключение к голосу..." : dc.VoiceStatus,
                new R(r.X + 12, dy + 14, r.W - 56, 18), 12f, Th.Amber, 700);
            var cdisc = new R(r.Right - 38, dy + 10, 28, 28);
            if (UI.Hover(cdisc)) Gfx.FillRect(cdisc, 0x33ED4245, 6);
            Gfx.DrawIcon(IcPhoneOff, "✕", cdisc, 16, Th.Red);
            if (UI.Click(UI.Id("btn_vdisc_c"), cdisc)) { dc.LeaveVoice(); _inVoiceStage = false; }
            dy += 48;
            Gfx.FillRect(new R(r.X, dy, r.W, 1), Th.Border);
        }
        else if (dc.VoiceConnected)
        {
            DiscordGuild? vg;
            lock (dc.Guilds) vg = dc.Guilds.FirstOrDefault(x => x.Id == dc.ConnectedVoiceGuildId);
            var vc = vg?.Channels.FirstOrDefault(x => x.Id == dc.ConnectedVoiceChannelId);
            DiscordChannel? vdm = null;
            if (vg == null) { lock (dc.DmChannels) vdm = dc.DmChannels.FirstOrDefault(x => x.Id == dc.ConnectedVoiceChannelId); }
            var connHit = new R(r.X + 4, dy + 4, r.W - 46, 40);
            if (UI.Hover(connHit) || _showVoiceInfo) Gfx.FillRect(connHit, Th.Hover, 6);

            // Crisp green 3-bar signal strength icon (exact c_026.jpg!)
            uint sigCol = dc.VoicePingMs > 180 ? Th.Red : (dc.VoicePingMs > 110 ? Th.Amber : Th.Green);
            Gfx.FillRect(new R(r.X + 10, dy + 24, 3, 6), sigCol, 1);
            Gfx.FillRect(new R(r.X + 15, dy + 19, 3, 11), sigCol, 1);
            Gfx.FillRect(new R(r.X + 20, dy + 14, 3, 16), sigCol, 1);

            string vHdr = dc.VoicePingMs > 0 ? $"Голосовая связь • {dc.VoicePingMs} мс" : dc.VoiceStatus;
            Gfx.DrawText(vHdr, new R(r.X + 29, dy + 8, r.W - 76, 18), 12f, sigCol, 700);
            string vSub = vdm != null ? $"{vdm.Name} (Звонок)" : $"{vc?.Name ?? "Voice"} / {vg?.Name ?? ""}";
            Gfx.DrawText(vSub, new R(r.X + 29, dy + 26, r.W - 76, 16), 11f, Th.Sub);
            if (UI.Click(UI.Id("btn_vinfo"), connHit))
            {
                _showVoiceInfo = !_showVoiceInfo;
                _showStatusMenu = false; _audioPopupMode = 0; _ctxUser = null; _ctxStreamUserId = null;
                if (_showVoiceInfo && dc.Regions.Count == 0) _ = dc.BenchmarkRegionsAsync();
            }

            var discBtn = new R(r.Right - 38, dy + 10, 28, 28);
            if (UI.Hover(discBtn)) Gfx.FillRect(discBtn, 0x33ED4245, 6);
            Gfx.DrawIcon(IcPhoneOff, "✕", discBtn, 16, Th.Sub);
            if (UI.Click(UI.Id("btn_vdisc"), discBtn))
            {
                dc.LeaveVoice();
                StopStream();
                _showVoiceInfo = false;
                _inVoiceStage = false;
            }

            // 4 crisp action buttons row (Dual-Screen Quick Hack / RTC, Share Screen, Grid Stage, Audio Settings)
            float bw = (r.W - 28) / 4f, by = dy + 50;
            var b1 = new R(r.X + 8, by, bw, 32);
            bool hasDual = _monitors.Count >= 2;
            bool dualActive = _streaming && _activeStreamMonitor == -2;
            Gfx.FillRect(b1, dualActive ? 0x3338BDF8 : (UI.Hover(b1) ? Th.Hover : Th.Card), 6);
            if (hasDual)
            {
                Gfx.DrawText("2×🖥", new R(b1.X, b1.Y + 7, b1.W, 18), 11.5f, dualActive ? Th.Cyan : Th.Text, 700, 1);
                if (UI.Click(UI.Id("dock_dual"), b1))
                {
                    if (dualActive) StopStream();
                    else StartStream(-2, 0);
                }
            }
            else
            {
                Gfx.DrawIcon(IcZap, "⚡", b1, 16, _showVoiceInfo ? Th.Cyan : Th.Sub);
                if (UI.Click(UI.Id("dock_rtc"), b1)) { _showVoiceInfo = !_showVoiceInfo; _voiceInfoTab = 1; }
            }

            var b2 = new R(b1.Right + 4, by, bw, 32);
            Gfx.FillRect(b2, _streaming ? 0x3323A55A : (UI.Hover(b2) ? Th.Hover : Th.Card), 6);
            Gfx.DrawIcon(_streaming ? IcShareOff : IcShare, "S", b2, 16, _streaming ? Th.Green : Th.Text);
            if (UI.Click(UI.Id("dock_share"), b2))
            {
                if (_streaming) StopStream();
                else OpenScreenShareModal();
            }

            var b3 = new R(b2.Right + 4, by, bw, 32);
            Gfx.FillRect(b3, _inVoiceStage ? Th.Active : (UI.Hover(b3) ? Th.Hover : Th.Card), 6);
            Gfx.DrawIcon(IcGrid, "G", b3, 16, _inVoiceStage ? Th.Text : Th.Sub);
            if (UI.Click(UI.Id("dock_grid"), b3)) { _inVoiceStage = !_inVoiceStage; dc.FocusedStreamUserId = null; }

            var b4 = new R(b3.Right + 4, by, bw, 32);
            Gfx.FillRect(b4, UI.Hover(b4) ? Th.Hover : Th.Card, 6);
            Gfx.DrawIcon(IcSliders, "A", b4, 16, Th.Sub);
            if (UI.Click(UI.Id("dock_aud"), b4)) { _showSettings = true; _settingsTab = 0; RefreshHardwareListsAsync(); }

            dy += 92;
            Gfx.FillRect(new R(r.X, dy, r.W, 1), Th.Border);
        }

        // 3. User Bar with clickable Profile/Status Switcher + Split [Mic | ⌄], [Headphones | ⌄], [⚙]
        var profHit = new R(r.X + 4, dy + 7, r.W - 134, 46);
        if (UI.Hover(profHit) || _showStatusMenu) Gfx.FillRect(profHit, Th.Hover, 8);
        if (UI.Click(UI.Id("u_prof"), profHit))
        {
            _showStatusMenu = !_showStatusMenu;
            _showVoiceInfo = false; _audioPopupMode = 0; _ctxUser = null; _ctxStreamUserId = null;
        }

        var myAv = ImageCache.Get(dc.Me.AvatarUrl, 64, ImgMask.Circle);
        var myAvR = new R(r.X + 8, dy + 14, 32, 32);
        if (_localSpeakAnim > 0.04f)
        {
            float cx = myAvR.X + 16f, cy = myAvR.Y + 16f;
            Gfx.FillCircle(cx, cy, 19.6f, Gfx.Fade(0xFF23F56E, 0.35f * _localSpeakAnim));
            Gfx.FillCircle(cx, cy, 18.4f, Gfx.Fade(0xFF23F56E, _localSpeakAnim));
            Gfx.FillCircle(cx, cy, 16.4f, Th.Rail);
        }
        if (myAv != null) Gfx.DrawBitmap(myAv, myAvR);
        else
        {
            Gfx.FillCircle(myAvR.X + 16, myAvR.Y + 16, 16, Th.Brand);
            Gfx.DrawText(FirstGrapheme(dc.Me.DisplayName), new R(myAvR.X, myAvR.Y + 6, 32, 20), 13.5f, 0xFFFFFFFF, 700, 1);
        }
        uint stDotCol = dc.Me.Status switch { "idle" => Th.Amber, "dnd" => Th.Red, "invisible" => Th.Dim, _ => Th.Green };
        Gfx.FillCircle(myAvR.Right - 4, myAvR.Bottom - 4, 5, Th.Rail);
        Gfx.FillCircle(myAvR.Right - 4, myAvR.Bottom - 4, 3.5f, stDotCol);

        Gfx.DrawText(dc.Me.DisplayName, new R(r.X + 46, dy + 13, r.W - 178, 18), 12f, Th.Text, 700);
        string stLabel = _deafened ? "Звук выкл" : (_muted ? "Мик выкл" : (dc.Me.Status switch { "idle" => "Неактивен", "dnd" => "Не беспокоить", "invisible" => "Невидимый", _ => (dc.VoiceConnected ? "В голосовом" : "В сети") }));
        Gfx.DrawText(stLabel, new R(r.X + 46, dy + 31, r.W - 178, 16), 10.5f, (_muted || _deafened) ? Th.Red : stDotCol);

        // Split Mic button [ 🎤 | ⌄ ]
        var rMicGrp = new R(r.Right - 126, dy + 16, 44, 28);
        bool micShownMuted = _muted || _deafened;
        Gfx.FillRect(rMicGrp, micShownMuted ? 0x33ED4245 : Th.Card, 6);
        var rMicBtn = new R(rMicGrp.X, rMicGrp.Y, 28, 28);
        if (UI.Hover(rMicBtn)) Gfx.FillRect(rMicBtn, Th.Hover, 6);
        Gfx.DrawIcon(micShownMuted ? IcMicOff : IcMic, "M", rMicBtn, 14, micShownMuted ? Th.Red : (_localSpeakAnim > 0.15f ? Th.Green : Th.Text));
        if (UI.Click(UI.Id("u_mute"), rMicBtn)) ToggleMute();

        var rMicChev = new R(rMicGrp.X + 28, rMicGrp.Y, 16, 28);
        if (UI.Hover(rMicChev)) Gfx.FillRect(rMicChev, Th.Hover, 4);
        Gfx.DrawIcon(IcChevDown, "v", rMicChev, 11, Th.Sub);
        if (UI.Click(UI.Id("u_mic_pop"), rMicChev))
        {
            RefreshHardwareListsAsync();
            _showStatusMenu = false; _showVoiceInfo = false;
            _audioPopupMode = _audioPopupMode == 1 ? 0 : 1;
            _audioPopupSub = 0;
            _audioPopupX = rMicGrp.X;
            _audioPopupY = rMicGrp.Y - 8;
        }

        // Split Headphones button [ 🎧 | ⌄ ]
        var rDeafGrp = new R(r.Right - 78, dy + 16, 44, 28);
        Gfx.FillRect(rDeafGrp, _deafened ? 0x33ED4245 : Th.Card, 6);
        var rDeafBtn = new R(rDeafGrp.X, rDeafGrp.Y, 28, 28);
        if (UI.Hover(rDeafBtn)) Gfx.FillRect(rDeafBtn, Th.Hover, 6);
        Gfx.DrawIcon(_deafened ? IcHeadphoneOff : IcHeadphones, "H", rDeafBtn, 14, _deafened ? Th.Red : Th.Text);
        if (UI.Click(UI.Id("u_deaf"), rDeafBtn)) ToggleDeafen();

        var rDeafChev = new R(rDeafGrp.X + 28, rDeafGrp.Y, 16, 28);
        if (UI.Hover(rDeafChev)) Gfx.FillRect(rDeafChev, Th.Hover, 4);
        Gfx.DrawIcon(IcChevDown, "v", rDeafChev, 11, Th.Sub);
        if (UI.Click(UI.Id("u_deaf_pop"), rDeafChev))
        {
            RefreshHardwareListsAsync();
            _audioPopupMode = _audioPopupMode == 2 ? 0 : 2;
            _audioPopupSub = 2;
            _audioPopupX = rDeafGrp.X;
            _audioPopupY = rDeafGrp.Y - 8;
        }

        var rSet = new R(r.Right - 30, dy + 16, 24, 28);
        Gfx.FillRect(rSet, _showSettings ? Th.Brand : (UI.Hover(rSet) ? Th.Hover : Th.Card), 6);
        Gfx.DrawIcon(IcSettings, "⚙", rSet, 14, Th.Text);
        if (UI.Click(UI.Id("u_set"), rSet))
        {
            if (_showSettings) CloseSettingsModal();
            else { _showSettings = true; RefreshHardwareListsAsync(); }
        }
    }

    // ---------------- 1-to-1 GoofCord Voice Stage (Multi-Stream Grid + Instant Focus/Unfocus!) ----------------

    private static void DrawStreamConnectingPlaceholder(R vidR, string streamerName, string diag)
    {
        Gfx.FillRect(vidR, 0xFF0E1015, 8);
        // Animated 3-dot Discord loading pulse
        int phase = (int)((Environment.TickCount64 / 260) % 3);
        float cx = vidR.X + vidR.W * 0.5f, cy = vidR.Y + vidR.H * 0.46f;
        for (int d = 0; d < 3; d++)
        {
            bool hi = d == phase;
            Gfx.FillCircle(cx + (d - 1) * 18f, cy, hi ? 5.5f : 4f, hi ? Th.Brand : 0xFF323646);
        }
        Gfx.DrawText($"Подключение к стриму {streamerName}...", new R(vidR.X + 12, cy + 16, vidR.W - 24, 20), 13, Th.Sub, 600, 1);
        if (!string.IsNullOrEmpty(diag))
            Gfx.DrawText(diag, new R(vidR.X + 12, cy + 38, vidR.W - 24, 16), 10.5f, Th.Dim, 500, 1);
        UI.Animate();
    }

    private static void DrawVoiceStage(R r)
    {
        Gfx.FillRect(r, 0xFF000000);
        var dc = DiscordClient.Instance;
        DiscordGuild? guild;
        lock (dc.Guilds) guild = dc.Guilds.FirstOrDefault(g => g.Id == dc.ConnectedVoiceGuildId);
        var vch = guild?.Channels.FirstOrDefault(c => c.Id == dc.ConnectedVoiceChannelId);
        DiscordChannel? vdm = null;
        if (guild == null) { lock (dc.DmChannels) vdm = dc.DmChannels.FirstOrDefault(c => c.Id == dc.ConnectedVoiceChannelId); }
        string voiceScopeGuildId = guild?.Id ?? "@me";

        // Validate focused stream still active
        WatchSession? focusedWs = null;
        bool focusSelf = dc.FocusedStreamUserId == "self" && _streaming;
        if (!focusSelf && !string.IsNullOrEmpty(dc.FocusedStreamUserId))
        {
            focusedWs = dc.GetWatchSession(dc.FocusedStreamUserId);
            if (focusedWs == null) dc.FocusedStreamUserId = null;
        }
        bool isFocused = focusSelf || focusedWs != null;

        // Top Voice Stage Header (exact f_004.jpg & f_022.jpg!)
        Gfx.DrawIcon(IcVol, "🔊", new R(r.X + 16, r.Y + 12, 22, 22), 16, Th.Sub);
        string hdr = vch?.Name ?? vdm?.Name ?? (_streaming ? "Локальный предпросмотр" : "Голосовой звонок");
        if (focusedWs != null) hdr += $"  •  Экран {focusedWs.StreamerName}";
        else if (focusSelf) hdr += _activeStreamMonitor == -2 ? "  •  ⚡ 2 Экрана сразу (32:9)" : "  •  Ваш экран";
        Gfx.DrawText(hdr, new R(r.X + 44, r.Y + 13, 460, 22), 14.5f, Th.Text, 700);

        if (isFocused)
        {
            var st = focusSelf ? _selfScreenStats : focusedWs!.Stats;
            string resPill = st.Width > 0
                ? (focusSelf && _activeStreamMonitor == -2
                    ? $"2×ЭКР {st.Width}×{st.Height} • {st.Fps:F0} FPS"
                    : $"{st.Height}P {st.Fps:F0} КАДРОВ В СЕКУНДУ")
                : (focusSelf ? "Запуск NVENC..." : "Подключение...");
            float pw = Gfx.MeasureText(resPill, 10.5f, 700, 280).W + 24;
            var rp = new R(r.Right - 134 - pw, r.Y + 12, pw, 22);
            Gfx.FillRect(rp, 0xFF1A1C23, 11);
            Gfx.DrawText(resPill, new R(rp.X, rp.Y + 4, rp.W, 14), 10.5f, focusSelf && _activeStreamMonitor == -2 ? Th.Cyan : Th.Text, 700, 1);

            var liveR = new R(r.Right - 126, r.Y + 12, 64, 22);
            Gfx.FillRect(liveR, Th.Red, 11);
            Gfx.DrawText("В ЭФИРЕ", new R(liveR.X, liveR.Y + 4, liveR.W, 14), 10f, 0xFFFFFFFF, 700, 1);
        }

        if (!isFocused && vch != null && UI.Button(UI.Id("vs_inv"), new R(r.Right - 144, r.Y + 10, 90, 28), "🔗 Пригласить", Th.Card, Th.Sub, 6, 11))
            _ = ShellAsync.CreateAndCopyInviteAsync(vch.Id);

        var chatBtn = new R(r.Right - 48, r.Y + 10, 34, 28);
        Gfx.FillRect(chatBtn, UI.Hover(chatBtn) ? Th.Hover : Th.Card, 6);
        Gfx.DrawIcon(IcChat, "💬", chatBtn, 16, Th.Text);
        if (UI.Click(UI.Id("vs_chat"), chatBtn)) _inVoiceStage = false;

        var stageArea = new R(r.X + 12, r.Y + 46, r.W - 24, isFocused ? (r.H - 146) : (r.H - 120));

        if (isFocused)
        {
            // MODE 2: 100% Full-Stage Zero-Copy Hardware Video (exact f_022.jpg!)
            Gfx.FillRect(stageArea, 0xFF0A0B0E, 8);
            if (focusSelf)
            {
                if (_selfScreenStats.FramesEncoded > 0)
                {
                    _selfHostRect = stageArea;
                    _wantSelfHost = true;
                }
                else DrawStreamConnectingPlaceholder(stageArea, "Ваш экран", "Инициализация NVENC...");
            }
            else if (focusedWs != null)
            {
                if (focusedWs.Stats.FramesEncoded > 0)
                    _watchSlotsLayout[focusedWs.Slot] = (true, stageArea);
                else
                    DrawStreamConnectingPlaceholder(stageArea, focusedWs.StreamerName, focusedWs.Stats.GetEncoderName());
            }

            // Clicking anywhere on the focused video returns to Grid View; right-click opens Stream Context Menu!
            if (UI.Click(UI.Id("vs_unfocus_area"), stageArea) && _ctxStreamUserId == null && _audioPopupMode == 0)
                dc.FocusedStreamUserId = null;
            if (UI.RightClick(stageArea))
            {
                _ctxUser = null; _audioPopupMode = 0;
                _ctxStreamUserId = focusSelf ? "self" : focusedWs!.StreamerUserId;
                _ctxStreamX = Math.Clamp(UI.MX, r.X + 8, Gfx.W - 248);
                _ctxStreamY = Math.Clamp(UI.MY, TopH + 8, Gfx.H - 220);
            }

            // Bottom-left streamer name pill
            var lblR = new R(r.X + 16, r.Bottom - 54, 210, 28);
            Gfx.FillRect(lblR, 0xDD111216, 6);
            Gfx.DrawIcon(IcMonitor, "", new R(lblR.X + 6, lblR.Y + 6, 16, 16), 12, Th.Sub);
            Gfx.DrawText(focusSelf ? "Ваш экран" : focusedWs!.StreamerName, new R(lblR.X + 26, lblR.Y + 6, lblR.W - 34, 16), 12, Th.Text, 600);

            // Cleanly spaced "^ Сетка" pill (zero overlap with bottom dock!)
            var gridPill = new R(r.X + (r.W - 108) * 0.5f, r.Bottom - 92, 108, 24);
            if (UI.Button(UI.Id("vs_grid"), gridPill, "▲ Сетка", 0xEE1A1C23, Th.Text, 12, 11.5f))
                dc.FocusedStreamUserId = null;

            // Bottom-right stream volume mute button
            if (focusedWs != null)
            {
                var svBtn = new R(r.Right - 92, r.Bottom - 54, 34, 34);
                Gfx.FillRect(svBtn, UI.Hover(svBtn) ? Th.Hover : Th.Card, 8);
                Gfx.DrawIcon(focusedWs.Muted || focusedWs.Volume < 0.01f ? IcVolOff : IcVol, "V", svBtn, 16, focusedWs.Muted ? Th.Red : Th.Text);
                if (UI.Click(UI.Id("vs_svol"), svBtn)) dc.ToggleStreamMute(focusedWs.StreamerUserId);
            }

            // Floating Discord-style Speaking HUD overlay on top of full-screen stream (punches exact cutout over video HWND!)
            if (dc.VoiceConnected)
            {
                var spkVms = dc.GetVoiceMembersSnapshot(voiceScopeGuildId, dc.ConnectedVoiceChannelId);
                float hudY = stageArea.Y + 14f;
                foreach (var svm in spkVms)
                {
                    bool isMeS = svm.UserId == dc.Me.Id;
                    bool spkS = dc.IsUserActuallySpeaking(svm);
                    float saS = isMeS ? _localSpeakAnim : StepSpeakAnim(svm.UserId, spkS);
                    if (saS <= 0.06f) continue;

                    string lbl = svm.Username;
                    float pw = MathF.Min(240f, Gfx.MeasureText(lbl, 12.5f, 700, 170f).W + 64f);
                    var pillR = new R(stageArea.X + 14f, hudY, pw, 34f);
                    _overlayCutouts.Add(pillR);
                    Gfx.FillRect(pillR, 0xEC101218, 17f);
                    Gfx.StrokeRect(pillR, Gfx.Fade(0xFF23F56E, saS), 1.8f, 17f);

                    var savR = new R(pillR.X + 5f, pillR.Y + 5f, 24f, 24f);
                    Gfx.FillCircle(savR.X + 12f, savR.Y + 12f, 13.5f, Gfx.Fade(0xFF23F56E, saS));
                    var savBmp = ImageCache.Get(svm.AvatarUrl, 48, ImgMask.Circle);
                    if (savBmp != null) Gfx.DrawBitmap(savBmp, savR);
                    else
                    {
                        Gfx.FillCircle(savR.X + 12f, savR.Y + 12f, 12f, Th.Brand);
                        Gfx.DrawText(FirstGrapheme(lbl), new R(savR.X, savR.Y + 4f, 24f, 16f), 11f, 0xFFFFFFFF, 700, 1);
                    }
                    DrawVoiceBars(savR.Right + 7f, pillR.Y + 17f, 3, 12f, isMeS ? MathF.Max(0.35f, _localVoiceLevel) : 0.8f, svm.UserId.GetHashCode() * 0.21f, 0xFF23F56E, saS);
                    Gfx.DrawText(lbl, new R(savR.Right + 22f, pillR.Y + 8f, pillR.W - 58f, 18f), 12.5f, 0xFF4ADE80, 700);
                    hudY += 40f;
                    if (hudY > stageArea.Bottom - 60f) break;
                }
            }
        }
        else if (guild != null || vdm != null || dc.ConnectedVoiceGuildId == "@me" || _streaming)
        {
            // MODE 1: Multi-Stream Grid View (works identically for Guild Voice AND DM / Group DM Calls!)
            List<VoiceMember> vms = dc.GetVoiceMembersSnapshot(voiceScopeGuildId, dc.ConnectedVoiceChannelId);

            var tiles = new List<(VoiceMember? Vm, bool IsStream, bool IsSelfStream)>();
            if (_streaming) tiles.Add((null, true, true));
            foreach (var vm in vms) if (vm.SelfStream && vm.UserId != dc.Me.Id) tiles.Add((vm, true, false));
            foreach (var vm in vms) tiles.Add((vm, false, false));

            if (tiles.Count == 0)
            {
                Gfx.DrawText("Подключено к голосовому каналу", new R(stageArea.X, stageArea.Y + stageArea.H * 0.45f, stageArea.W, 28), 16, Th.Sub, 600, 1);
            }
            else
            {
                int cols = tiles.Count <= 1 ? 1 : (tiles.Count <= 6 ? 2 : 3);
                int rows = (tiles.Count + cols - 1) / cols;
                float gap = 12f;
                float maxTileW = (stageArea.W - gap * (cols - 1)) / cols;
                float maxTileH = (stageArea.H - gap * (rows - 1)) / rows;
                float tw = MathF.Min(maxTileW, maxTileH * 1.78f);
                float th = MathF.Min(maxTileH, tw / 1.68f);
                if (tw < maxTileW * 0.85f) tw = MathF.Min(maxTileW, th * 1.78f);

                float totalGridH = rows * th + (rows - 1) * gap;
                float oy = stageArea.Y + (stageArea.H - totalGridH) * 0.5f;

                for (int i = 0; i < tiles.Count; i++)
                {
                    var (vm, isStream, isSelfStream) = tiles[i];
                    int row = i / cols;
                    int col = i % cols;
                    int itemsInRow = (row == rows - 1) ? (tiles.Count - row * cols) : cols;
                    float rowW = itemsInRow * tw + (itemsInRow - 1) * gap;
                    float ox = stageArea.X + (stageArea.W - rowW) * 0.5f;
                    var tr = new R(ox + col * (tw + gap), oy + row * (th + gap), tw, th);

                    if (isSelfStream)
                    {
                        bool isDual = _activeStreamMonitor == -2;
                        Gfx.FillRect(tr, 0xFF0E1015, 10);
                        Gfx.StrokeRect(tr, isDual ? Th.Cyan : Th.Green, 1.5f, 10);
                        var vidR = new R(tr.X + 2, tr.Y + 2, tr.W - 4, MathF.Max(16, tr.H - 28));
                        if (_selfScreenStats.FramesEncoded > 0)
                        {
                            _selfHostRect = vidR;
                            _wantSelfHost = true;
                        }
                        else DrawStreamConnectingPlaceholder(vidR, isDual ? "2 Экрана сразу" : "Ваш экран", "Запуск NVENC...");

                        var barR = new R(tr.X + 2, tr.Bottom - 26, tr.W - 4, 24);
                        Gfx.FillRect(barR, 0xFF101218, 6);
                        Gfx.DrawIcon(isDual ? IcZap : IcMonitor, "", new R(barR.X + 6, barR.Y + 4, 16, 16), 12, isDual ? Th.Cyan : Th.Green);
                        Gfx.DrawText(isDual ? "⚡ 2 Экрана сразу (1 + 2)" : "Ваш экран", new R(barR.X + 24, barR.Y + 4, barR.W - 156, 16), 11.5f, Th.Text, 600);
                        string badge = _selfScreenStats.Width > 0 ? (isDual ? $"{_selfScreenStats.Width}×{_selfScreenStats.Height} {_selfScreenStats.Fps:F0}FPS" : $"{_selfScreenStats.Height}P {_selfScreenStats.Fps:F0}FPS") : "LIVE";
                        Gfx.DrawText(badge, new R(barR.Right - 150, barR.Y + 4, 88, 16), 10f, isDual ? Th.Cyan : Th.Sub, 700, 2);
                        Gfx.FillRect(new R(barR.Right - 56, barR.Y + 4, 52, 16), Th.Red, 8);
                        Gfx.DrawText("В ЭФИРЕ", new R(barR.Right - 56, barR.Y + 5, 52, 14), 9f, 0xFFFFFFFF, 700, 1);

                        if (UI.Click(UI.Id("tile_self_focus"), tr) && _ctxStreamUserId == null && _audioPopupMode == 0)
                            dc.FocusedStreamUserId = "self";
                        if (UI.RightClick(tr))
                        {
                            _ctxUser = null; _audioPopupMode = 0;
                            _ctxStreamUserId = "self";
                            _ctxStreamX = Math.Clamp(UI.MX, r.X + 8, Gfx.W - 248);
                            _ctxStreamY = Math.Clamp(UI.MY, TopH + 8, Gfx.H - 220);
                        }
                    }
                    else if (isStream && vm != null)
                    {
                        var ws = dc.GetWatchSession(vm.UserId);
                        if (ws != null)
                        {
                            Gfx.FillRect(tr, 0xFF0E1015, 10);
                            Gfx.StrokeRect(tr, UI.Hover(tr) ? 0x55FFFFFF : Th.Border, 1f, 10);
                            var vidR = new R(tr.X + 2, tr.Y + 2, tr.W - 4, MathF.Max(16, tr.H - 28));
                            if (ws.Stats.FramesEncoded > 0)
                                _watchSlotsLayout[ws.Slot] = (true, vidR);
                            else
                                DrawStreamConnectingPlaceholder(vidR, vm.Username, ws.Stats.GetEncoderName());

                            var barR = new R(tr.X + 2, tr.Bottom - 26, tr.W - 4, 24);
                            Gfx.FillRect(barR, 0xFF101218, 6);
                            Gfx.DrawIcon(IcMonitor, "", new R(barR.X + 6, barR.Y + 4, 16, 16), 12, Th.Cyan);
                            Gfx.DrawText(vm.Username, new R(barR.X + 24, barR.Y + 4, barR.W - 156, 16), 11.5f, Th.Text, 600);
                            string qTxt = ws.Stats.Width > 0 ? $"{ws.Stats.Height}P {ws.Stats.Fps:F0}FPS" : "Загрузка...";
                            Gfx.DrawText(qTxt, new R(barR.Right - 148, barR.Y + 4, 86, 16), 10f, Th.Sub, 700, 2);
                            Gfx.FillRect(new R(barR.Right - 56, barR.Y + 4, 52, 16), Th.Red, 8);
                            Gfx.DrawText("В ЭФИРЕ", new R(barR.Right - 56, barR.Y + 5, 52, 14), 9f, 0xFFFFFFFF, 700, 1);

                            if (UI.Click(UI.Id("tile_focus_" + vm.UserId), tr) && _ctxStreamUserId == null && _audioPopupMode == 0)
                                dc.FocusedStreamUserId = vm.UserId;
                            if (UI.RightClick(tr))
                            {
                                _ctxUser = null; _audioPopupMode = 0;
                                _ctxStreamUserId = vm.UserId;
                                _ctxStreamX = Math.Clamp(UI.MX, r.X + 8, Gfx.W - 248);
                                _ctxStreamY = Math.Clamp(UI.MY, TopH + 8, Gfx.H - 220);
                            }
                        }
                        else
                        {
                            Gfx.FillRect(tr, 0xFF14161D, 10);
                            Gfx.StrokeRect(tr, UI.Hover(tr) ? 0x44FFFFFF : Th.Border, 1f, 10);

                            var livePill = new R(tr.Right - 68, tr.Y + 10, 58, 20);
                            Gfx.FillRect(livePill, Th.Red, 10);
                            Gfx.DrawText("В ЭФИРЕ", new R(livePill.X, livePill.Y + 3, livePill.W, 14), 9.5f, 0xFFFFFFFF, 700, 1);

                            float grpW = 148 + 8 + 38;
                            float gx = tr.X + (tr.W - grpW) * 0.5f, gy = tr.Y + (tr.H - 36) * 0.5f;
                            if (UI.Button(UI.Id("tile_watch_" + vm.UserId), new R(gx, gy, 148, 36), "Смотреть стрим", 0xDD2B2D31, Th.Text, 8, 12.5f, 700))
                                _ = dc.WatchStreamAsync(voiceScopeGuildId, dc.ConnectedVoiceChannelId, vm.UserId, vm.Username, focusImmediately: true, selfMute: _muted, selfDeaf: _deafened);

                            var gridWatchBtn = new R(gx + 156, gy, 38, 36);
                            Gfx.FillRect(gridWatchBtn, UI.Hover(gridWatchBtn) ? Th.Hover : 0xDD2B2D31, 8);
                            Gfx.DrawIcon(IcEye, "👁", gridWatchBtn, 16, Th.Text);
                            if (UI.Click(UI.Id("tile_watch_grid_" + vm.UserId), gridWatchBtn))
                                _ = dc.WatchStreamAsync(voiceScopeGuildId, dc.ConnectedVoiceChannelId, vm.UserId, vm.Username, focusImmediately: false, selfMute: _muted, selfDeaf: _deafened);

                            var nb = new R(tr.X + 10, tr.Bottom - 30, MathF.Min(tr.W - 20, 220), 22);
                            Gfx.FillRect(nb, 0xCC0A0B0E, 6);
                            Gfx.DrawIcon(IcMonitor, "", new R(nb.X + 5, nb.Y + 3, 16, 16), 12, Th.Sub);
                            Gfx.DrawText(vm.Username, new R(nb.X + 24, nb.Y + 3, nb.W - 30, 16), 11.5f, Th.Text, 600);
                        }
                    }
                    else if (vm != null)
                    {
                        bool isMe = vm.UserId == dc.Me.Id;
                        bool spk = dc.IsUserActuallySpeaking(vm);
                        float sa = isMe ? _localSpeakAnim : StepSpeakAnim(vm.UserId, spk);
                        float lvl = isMe ? _localVoiceLevel : (spk ? 0.72f : 0f);
                        bool isMuted = vm.SelfMute || (isMe && (_muted || _deafened));
                        uint cardBg = ImageCache.GetCardColor(vm.AvatarUrl, vm.UserId);
                        Gfx.FillRect(tr, cardBg, 10);
                        if (sa > 0.02f)
                        {
                            Gfx.FillRect(tr, Gfx.Fade(Th.Green, 0.08f * sa), 10);
                            Gfx.StrokeRect(tr.Inset(-1f), Gfx.Fade(0xFF34D399, 0.30f * sa), 4.5f, 11);
                            Gfx.StrokeRect(tr, Gfx.Fade(0xFF34D399, sa), 2.4f, 10);
                        }
                        else
                        {
                            Gfx.StrokeRect(tr, UI.Hover(tr) ? 0x33FFFFFF : Th.Border, 1f, 10);
                        }

                        var av = ImageCache.Get(vm.AvatarUrl, 128, ImgMask.Circle);
                        float avSz = MathF.Min(96f, th * 0.44f);
                        var avR = new R(tr.X + (tr.W - avSz) * 0.5f, tr.Y + (tr.H - avSz) * 0.5f - 4, avSz, avSz);
                        if (sa > 0.02f)
                        {
                            float cx = avR.X + avSz * 0.5f, cy = avR.Y + avSz * 0.5f;
                            Gfx.FillCircle(cx, cy, avSz * 0.5f + 5.5f + lvl * 11f, Gfx.Fade(0xFF23A55A, 0.22f * sa));
                            Gfx.FillCircle(cx, cy, avSz * 0.5f + 3.6f, Gfx.Fade(0xFF34D399, sa));
                            Gfx.FillCircle(cx, cy, avSz * 0.5f + 1.2f, cardBg);
                        }
                        if (av != null) Gfx.DrawBitmap(av, avR);
                        else
                        {
                            Gfx.FillCircle(avR.X + avSz * 0.5f, avR.Y + avSz * 0.5f, avSz * 0.5f, Th.Brand);
                            Gfx.DrawText(FirstGrapheme(vm.Username), new R(avR.X, avR.Y + avSz * 0.30f, avSz, 28), 24, 0xFFFFFFFF, 700, 1);
                        }

                        var nb = new R(tr.X + 10, tr.Bottom - 30, MathF.Min(tr.W - 20, 224), 22);
                        Gfx.FillRect(nb, 0xCC0A0B0E, 6);
                        if (sa > 0.04f) Gfx.StrokeRect(nb, Gfx.Fade(0xFF34D399, 0.55f * sa), 1f, 6);
                        if (isMuted)
                        {
                            Gfx.DrawIcon(IcMicOff, "M", new R(nb.X + 5, nb.Y + 3, 16, 16), 12, Th.Red);
                            Gfx.DrawText(vm.Username, new R(nb.X + 24, nb.Y + 3, nb.W - 30, 16), 11.5f, Th.Text, 600);
                        }
                        else if (sa > 0.05f)
                        {
                            DrawVoiceBars(nb.X + 7, nb.Y + 11, 3, 11f, MathF.Max(0.32f, lvl), vm.UserId.GetHashCode() * 0.17f, 0xFF34D399, sa);
                            Gfx.DrawText(vm.Username, new R(nb.X + 23, nb.Y + 3, nb.W - 29, 16), 11.5f, 0xFFFFFFFF, 700);
                        }
                        else
                        {
                            Gfx.DrawText(vm.Username, new R(nb.X + 8, nb.Y + 3, nb.W - 16, 16), 11.5f, Th.Text, 600);
                        }

                        if (UI.RightClick(tr))
                        {
                            _ctxStreamUserId = null; _audioPopupMode = 0;
                            _ctxUser = vm;
                            _ctxGuildId = voiceScopeGuildId;
                            _ctxX = Math.Clamp(UI.MX, 8, Gfx.W - 252);
                            _ctxY = Math.Clamp(UI.MY, TopH + 8, Gfx.H - 220);
                        }
                    }
                }
            }
        }

        // Exact GoofCord Floating Bottom Control Bar with tooltips
        float barW = 304, barH = 48;
        var bar = new R(r.X + (r.W - barW) * 0.5f, r.Bottom - 60, barW, barH);
        Gfx.FillRect(bar, 0xEE18191E, 14);
        Gfx.StrokeRect(bar, Th.Border, 1, 14);
        string barTip = ""; float barTipX = 0;

        bool barMuted = _muted || _deafened;
        var bmGrp = new R(bar.X + 10, bar.Y + 6, 56, 36);
        Gfx.FillRect(bmGrp, barMuted ? 0x33ED4245 : (_localSpeakAnim > 0.02f ? Gfx.Fade(Th.Green, 0.24f * _localSpeakAnim) : Th.Card), 10);
        if (!barMuted && _localSpeakAnim > 0.02f)
            Gfx.StrokeRect(bmGrp, Gfx.Fade(0xFF34D399, 0.7f * _localSpeakAnim), 1.35f, 10);
        var bmBtn = new R(bmGrp.X, bmGrp.Y, 36, 36);
        if (UI.Hover(bmBtn)) { Gfx.FillRect(bmBtn, Th.Hover, 10); barTip = barMuted ? "Включить микрофон" : "Выключить микрофон"; barTipX = bmBtn.X + 18; }
        Gfx.DrawIcon(barMuted ? IcMicOff : IcMic, "M", bmBtn, 16, barMuted ? Th.Red : (_localSpeakAnim > 0.15f ? 0xFF34D399 : Th.Text));
        if (UI.Click(UI.Id("vb_mute"), bmBtn)) ToggleMute();

        var bmChev = new R(bmGrp.X + 36, bmGrp.Y, 20, 36);
        if (UI.Hover(bmChev)) { Gfx.FillRect(bmChev, Th.Hover, 6); barTip = "Устройства аудио"; barTipX = bmChev.X + 10; }
        Gfx.DrawIcon(IcChevDown, "v", bmChev, 12, Th.Sub);
        if (UI.Click(UI.Id("vb_mic_pop"), bmChev))
        {
            RefreshHardwareListsAsync();
            _ctxUser = null; _ctxStreamUserId = null;
            _audioPopupMode = _audioPopupMode == 1 ? 0 : 1;
            _audioPopupSub = 0;
            _audioPopupX = bmGrp.X;
            _audioPopupY = bar.Y - 8;
        }

        var bdBtn = new R(bmGrp.Right + 8, bar.Y + 6, 40, 36);
        if (UI.Hover(bdBtn)) { barTip = _deafened ? "Включить звук" : "Отключить звук"; barTipX = bdBtn.X + 20; }
        Gfx.FillRect(bdBtn, _deafened ? 0x33ED4245 : (UI.Hover(bdBtn) ? Th.Hover : Th.Card), 10);
        Gfx.DrawIcon(_deafened ? IcHeadphoneOff : IcHeadphones, "H", bdBtn, 16, _deafened ? Th.Red : Th.Text);
        if (UI.Click(UI.Id("vb_deaf"), bdBtn)) ToggleDeafen();

        var bsBtn = new R(bdBtn.Right + 8, bar.Y + 6, 40, 36);
        if (UI.Hover(bsBtn)) { barTip = _streaming ? "Остановить трансляцию" : "Демонстрация экрана"; barTipX = bsBtn.X + 20; }
        Gfx.FillRect(bsBtn, _streaming ? 0x3323A55A : (UI.Hover(bsBtn) ? Th.Hover : Th.Card), 10);
        Gfx.DrawIcon(_streaming ? IcShareOff : IcShare, "S", bsBtn, 16, _streaming ? Th.Green : Th.Text);
        if (UI.Click(UI.Id("vb_share"), bsBtn))
        {
            if (_streaming) StopStream();
            else OpenScreenShareModal();
        }

        var bgBtn = new R(bsBtn.Right + 8, bar.Y + 6, 40, 36);
        if (UI.Hover(bgBtn)) { barTip = "Сетка участников"; barTipX = bgBtn.X + 20; }
        Gfx.FillRect(bgBtn, UI.Hover(bgBtn) ? Th.Hover : Th.Card, 10);
        Gfx.DrawIcon(IcGrid, "G", bgBtn, 16, Th.Text);
        if (UI.Click(UI.Id("vb_grid"), bgBtn)) dc.FocusedStreamUserId = null;

        var bsetBtn = new R(bgBtn.Right + 8, bar.Y + 6, 40, 36);
        if (UI.Hover(bsetBtn)) { barTip = "Настройки голоса"; barTipX = bsetBtn.X + 20; }
        Gfx.FillRect(bsetBtn, UI.Hover(bsetBtn) ? Th.Hover : Th.Card, 10);
        Gfx.DrawIcon(IcSettings, "⚙", bsetBtn, 16, Th.Text);
        if (UI.Click(UI.Id("vb_set"), bsetBtn)) { _showSettings = true; _settingsTab = 0; RefreshHardwareListsAsync(); }

        var bdisc = new R(bsetBtn.Right + 8, bar.Y + 6, 46, 36);
        if (UI.Hover(bdisc)) { barTip = "Отключиться"; barTipX = bdisc.X + 23; }
        Gfx.FillRect(bdisc, Th.Red, 10);
        if (UI.Hover(bdisc)) Gfx.FillRect(bdisc, Th.Hover, 10);
        Gfx.DrawIcon(IcPhoneOff, "✕", bdisc, 17, 0xFFFFFFFF);
        if (UI.Click(UI.Id("vb_disc"), bdisc))
        {
            dc.LeaveVoice();
            StopStream();
            _inVoiceStage = false;
        }

        if (!string.IsNullOrEmpty(barTip) && _audioPopupMode == 0 && _ctxStreamUserId == null && !isFocused)
        {
            float ttw = Gfx.MeasureText(barTip, 11.5f, 600, 220).W + 18;
            var ttr = new R(Math.Clamp(barTipX - ttw * 0.5f, r.X + 8, r.Right - ttw - 8), bar.Y - 32, ttw, 24);
            Gfx.FillRect(ttr, 0xF5111216, 6);
            Gfx.StrokeRect(ttr, Th.Border, 1, 6);
            Gfx.DrawText(barTip, new R(ttr.X, ttr.Y + 4, ttr.W, 16), 11.5f, Th.Text, 600, 1);
            _overlayCutouts.Add(ttr);
        }

        // Bottom-right Fullscreen toggle (exact f_004.jpg & f_022.jpg!)
        var fsBtn = new R(r.Right - 48, r.Bottom - 54, 34, 34);
        Gfx.FillRect(fsBtn, UI.Hover(fsBtn) ? Th.Hover : Th.Card, 8);
        Gfx.DrawIcon(W.IsZoomed(App.Hwnd) ? IcMin : IcMax, "⛶", fsBtn, 16, Th.Text);
        if (UI.Click(UI.Id("vs_fs"), fsBtn))
            App.InvokeOnUiThread(() => W.ShowWindow(App.Hwnd, W.IsZoomed(App.Hwnd) ? W.SW_SHOWNORMAL : W.SW_MAXIMIZE));
    }

    // ---------------- Text Chat Stage (Search, Pins, Member List, Markdown Code Blocks, Lightbox, Reactions, Edit, Emoji) ----------------

    private static void DrawMainStage(R r)
    {
        Gfx.FillRect(r, Th.Bg);
        var dc = DiscordClient.Instance;
        DiscordGuild? guild;
        lock (dc.Guilds) guild = dc.Guilds.FirstOrDefault(g => g.Id == dc.ActiveGuildId);
        DiscordChannel? ch;
        if (dc.ActiveGuildId == "@me") { lock (dc.DmChannels) ch = dc.DmChannels.FirstOrDefault(c => c.Id == dc.ActiveChannelId); }
        else ch = guild?.Channels.FirstOrDefault(c => c.Id == dc.ActiveChannelId);

        // Channel Header Bar
        Gfx.FillRect(new R(r.X, r.Y, r.W, 44), Th.Panel);
        Gfx.FillRect(new R(r.X, r.Y + 44, r.W, 1), Th.Border);

        string hdrPrefix = ch == null ? "# " : (ch.Type == 1 ? "@ " : (ch.Type == 3 ? "👥 " : "# "));
        float rightX = r.Right - 10;
        bool canShowMembers = guild != null || ch?.Type == 3;
        bool showMembers = SettingsStore.Current.ShowMemberList && canShowMembers && r.W > 760;

        // Right-side header controls: [👥 Members] [📌 Pins] [🔍 Search] [📞 Call / Stream / Back to Voice]
        if (canShowMembers)
        {
            rightX -= 32;
            var mbBtn = new R(rightX, r.Y + 8, 28, 28);
            if (UI.Button(UI.Id("hdr_mem"), mbBtn, "👥", showMembers ? Th.Active : Th.Card, showMembers ? Th.Text : Th.Sub, 6, 13))
            { SettingsStore.Current.ShowMemberList = !SettingsStore.Current.ShowMemberList; SettingsStore.Save(); }
            rightX -= 6;
        }
        if (ch != null)
        {
            rightX -= 32;
            var pinBtn = new R(rightX, r.Y + 8, 28, 28);
            if (UI.Button(UI.Id("hdr_pin"), pinBtn, "📌", _showPinsPopup ? Th.Active : Th.Card, Th.Text, 6, 13))
            {
                _showPinsPopup = !_showPinsPopup;
                if (_showPinsPopup) _ = dc.LoadPinnedMessagesAsync(ch.Id);
            }
            rightX -= 6;

            rightX -= 32;
            var srcBtn = new R(rightX, r.Y + 8, 28, 28);
            if (UI.Button(UI.Id("hdr_src"), srcBtn, "🔍", _showChannelSearch ? Th.Brand : Th.Card, Th.Text, 6, 13))
                ToggleChannelSearch();
            rightX -= 8;
        }

        bool inThisDmCall = ch != null && ch.IsDmOrGroup && dc.ConnectedVoiceChannelId == ch.Id && (dc.VoiceConnected || !string.IsNullOrEmpty(dc.ConnectedVoiceChannelId));
        bool dmHasActiveCall = ch != null && ch.IsDmOrGroup && dc.DmCalls.TryGetValue(ch.Id, out var hdrCall) && hdrCall.VoiceStates.Count > 0;

        if (dc.VoiceConnected || dc.IsWatchingAnyStream || _streaming)
        {
            rightX -= 154;
            if (UI.Button(UI.Id("hdr_vstage"), new R(rightX, r.Y + 8, 154, 28), "🔊 К стриму / голосу", Th.Green, 0xFFFFFFFF, 6, 11.5f))
                _inVoiceStage = true;
            rightX -= 8;
        }
        else if (ch != null && ch.IsDmOrGroup)
        {
            string callLbl = dmHasActiveCall ? "📞 Присоединиться" : "📞 Позвонить";
            float callW = dmHasActiveCall ? 148f : 118f;
            rightX -= callW;
            if (UI.Button(UI.Id("hdr_dmcall"), new R(rightX, r.Y + 8, callW, 28), callLbl, Th.Green, 0xFFFFFFFF, 6, 12f, 700))
                _ = dc.JoinVoiceAsync("@me", ch.Id, _muted, _deafened, ringIfEmpty: !dmHasActiveCall);
            rightX -= 8;
        }
        else
        {
            rightX -= 130;
            if (UI.Button(UI.Id("hdr_4k"), new R(rightX, r.Y + 8, 130, 28), "▶ Screen Share", Th.Card, Th.Cyan, 6, 12))
                OpenScreenShareModal();
            rightX -= 8;
        }

        float maxTitleW = MathF.Max(140f, rightX - (r.X + 24f));
        Gfx.DrawText(hdrPrefix + (ch?.Name ?? "выберите-канал"), new R(r.X + 16, r.Y + 12, MathF.Min(460f, maxTitleW), 22), 15, Th.Text, 700);
        float titleEnd = r.X + 24 + MathF.Min(460f, Gfx.MeasureText(hdrPrefix + (ch?.Name ?? ""), 15, 700, 460f).W);
        if (!string.IsNullOrEmpty(ch?.Topic) && rightX - titleEnd > 80)
            Gfx.DrawText(ch.Topic, new R(titleEnd + 12, r.Y + 14, rightX - titleEnd - 18, 20), 12.5f, Th.Sub);

        float memW = showMembers ? 224f : 0f;
        float chatW = r.W - memW;
        float contentTop = r.Y + 45;

        // Official Discord Inline DM / Group DM Active Call Stage Banner (matches Screenshot 3!)
        if (ch != null && ch.IsDmOrGroup && (inThisDmCall || dmHasActiveCall))
        {
            var callVms = dc.GetVoiceMembersSnapshot("@me", ch.Id);
            if (inThisDmCall && callVms.All(v => v.UserId != dc.Me.Id))
                callVms.Insert(0, new VoiceMember { UserId = dc.Me.Id, ChannelId = ch.Id, Username = dc.Me.DisplayName, Avatar = dc.Me.Avatar, SelfMute = _muted || _deafened, SelfDeaf = _deafened, SelfStream = _streaming });

            var banner = new R(r.X, contentTop, chatW, 168);
            Gfx.FillRect(banner, 0xFF0C0D11);
            Gfx.FillRect(new R(banner.X, banner.Bottom - 1, banner.W, 1), Th.Border);

            int cnt = Math.Max(1, callVms.Count);
            float avSz = 64f, avGap = 22f;
            float totalAvW = cnt * avSz + (cnt - 1) * avGap;
            float ax0 = banner.X + (banner.W - totalAvW) * 0.5f;
            float ay0 = banner.Y + 20f;

            for (int vi = 0; vi < callVms.Count; vi++)
            {
                var vm = callVms[vi];
                bool isMe = vm.UserId == dc.Me.Id;
                bool spk = dc.IsUserActuallySpeaking(vm);
                float sa = isMe ? _localSpeakAnim : StepSpeakAnim(vm.UserId, spk);
                var avr = new R(ax0 + vi * (avSz + avGap), ay0, avSz, avSz);
                float cx = avr.X + avSz * 0.5f, cy = avr.Y + avSz * 0.5f;
                if (sa > 0.03f)
                {
                    Gfx.FillCircle(cx, cy, avSz * 0.5f + 5f, Gfx.Fade(0xFF23A55A, 0.35f * sa));
                    Gfx.FillCircle(cx, cy, avSz * 0.5f + 3f, Gfx.Fade(0xFF34D399, sa));
                    Gfx.FillCircle(cx, cy, avSz * 0.5f + 1f, 0xFF0C0D11);
                }
                var avBmp = ImageCache.Get(vm.AvatarUrl, 128, ImgMask.Circle);
                if (avBmp != null) Gfx.DrawBitmap(avBmp, avr);
                else { Gfx.FillCircle(cx, cy, avSz * 0.5f, Th.Brand); Gfx.DrawText(FirstGrapheme(vm.Username), new R(avr.X, avr.Y + 18, avSz, 28), 22, 0xFFFFFFFF, 700, 1); }

                if (vm.SelfMute || vm.SelfDeaf || (isMe && (_muted || _deafened)))
                {
                    Gfx.FillCircle(avr.Right - 8, avr.Bottom - 8, 10, 0xFF0C0D11);
                    Gfx.FillCircle(avr.Right - 8, avr.Bottom - 8, 8.5f, Th.Red);
                    Gfx.DrawIcon(IcMicOff, "M", new R(avr.Right - 15, avr.Bottom - 15, 14, 14), 10, 0xFFFFFFFF);
                }
                if (vm.SelfStream)
                {
                    var lp = new R(avr.X + (avSz - 52) * 0.5f, avr.Y - 8, 52, 16);
                    Gfx.FillRect(lp, Th.Red, 8);
                    Gfx.DrawText("В ЭФИРЕ", new R(lp.X, lp.Y + 1, lp.W, 14), 9f, 0xFFFFFFFF, 700, 1);
                    if (UI.Click(UI.Id("dm_call_watch_" + vm.UserId), avr))
                    {
                        _inVoiceStage = true;
                        if (isMe) dc.FocusedStreamUserId = "self";
                        else _ = dc.WatchStreamAsync("@me", ch.Id, vm.UserId, vm.Username, focusImmediately: true, selfMute: _muted, selfDeaf: _deafened);
                    }
                }
                else if (UI.RightClick(avr))
                {
                    _ctxStreamUserId = null; _audioPopupMode = 0;
                    _ctxUser = vm; _ctxGuildId = "@me";
                    _ctxX = Math.Clamp(UI.MX, 8, Gfx.W - 252);
                    _ctxY = Math.Clamp(UI.MY, TopH + 8, Gfx.H - 220);
                }
            }

            float btnY = banner.Bottom - 50f;
            if (inThisDmCall)
            {
                float totalBtnW = 128 + 8 + 116 + 8 + 46;
                float bx = banner.X + (banner.W - totalBtnW) * 0.5f;
                if (UI.Button(UI.Id("dmc_sh"), new R(bx, btnY, 128, 36), _streaming ? "Стоп экрана" : "🖥 Демонстрация", _streaming ? Th.Green : Th.Card, 0xFFFFFFFF, 8, 12f, 600))
                {
                    if (_streaming) StopStream();
                    else OpenScreenShareModal();
                }
                if (UI.Button(UI.Id("dmc_grid"), new R(bx + 136, btnY, 116, 36), "⛶ На весь экран", Th.Card, Th.Text, 8, 12f, 600))
                    _inVoiceStage = true;
                var hup = new R(bx + 260, btnY, 46, 36);
                Gfx.FillRect(hup, Th.Red, 8);
                if (UI.Hover(hup)) Gfx.FillRect(hup, Th.Hover, 8);
                Gfx.DrawIcon(IcPhoneOff, "✕", hup, 16, 0xFFFFFFFF);
                if (UI.Click(UI.Id("dmc_hangup"), hup)) { dc.LeaveVoice(); StopStream(); }
            }
            else
            {
                float bx = banner.X + (banner.W - (46 + 10 + 210)) * 0.5f;
                var shJoin = new R(bx, btnY, 46, 38);
                Gfx.FillRect(shJoin, 0xFF23A55A, 8);
                if (UI.Hover(shJoin)) Gfx.FillRect(shJoin, Th.Hover, 8);
                Gfx.DrawIcon(IcShare, "🖥", shJoin, 16, 0xFFFFFFFF);
                if (UI.Click(UI.Id("dmc_join_sh"), shJoin))
                {
                    _ = dc.JoinVoiceAsync("@me", ch.Id, _muted, _deafened);
                    OpenScreenShareModal();
                }
                if (UI.Button(UI.Id("dmc_join"), new R(bx + 56, btnY, 210, 38), "📞 Присоединиться к звонку", 0xFF23A55A, 0xFFFFFFFF, 8, 13f, 700))
                    _ = dc.JoinVoiceAsync("@me", ch.Id, _muted, _deafened);
            }
            contentTop += 168f;
        }

        // Live In-Channel Search Bar (Ctrl+F / 🔍)
        if (_showChannelSearch && !_showQuickSwitch)
        {
            var sbBar = new R(r.X, contentTop, chatW, 42);
            Gfx.FillRect(sbBar, Th.Sidebar);
            Gfx.FillRect(new R(sbBar.X, sbBar.Bottom - 1, sbBar.W, 1), Th.Border);
            Gfx.DrawText("🔍 Поиск в канале:", new R(sbBar.X + 14, sbBar.Y + 12, 132, 18), 12f, Th.Sub, 600);
            _searchInputRect = new R(sbBar.X + 148, sbBar.Y + 6, MathF.Max(140, chatW - 246), 30);
            _wantSearchInput = !_showSettings && !_showShareModal && _lightboxUrl == null;
            Gfx.FillRect(_searchInputRect, Th.Panel, 6);
            Gfx.StrokeRect(_searchInputRect, Th.Brand, 1, 6);
            if (UI.Button(UI.Id("cs_cls"), new R(sbBar.Right - 86, sbBar.Y + 7, 74, 28), "Закрыть", Th.Card, Th.Sub, 6, 11.5f))
            { _showChannelSearch = false; _searchQuery = ""; App.SetSearchInputText(""); }
            contentTop += 42;
        }

        var msgArea = new R(r.X, contentTop, chatW, r.Bottom - contentTop - InputH - 12);
        if (UI.Hover(msgArea) && UI.Wheel != 0) _chatScroll = MathF.Max(0, _chatScroll + UI.Wheel * 64f);

        Gfx.PushClip(msgArea);
        if (ch != null && dc.Messages.TryGetValue(ch.Id, out var list))
        {
            List<DiscordMessage> snap;
            lock (list) snap = [.. list];
            if (_showChannelSearch && !string.IsNullOrWhiteSpace(_searchQuery))
            {
                string q = _searchQuery;
                snap = snap.Where(x => x.Content.Contains(q, StringComparison.OrdinalIgnoreCase) || x.AuthorName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
                Gfx.DrawText($"Найдено сообщений: {snap.Count}", new R(msgArea.X + 16, msgArea.Y + 6, 260, 18), 11.5f, Th.Cyan, 600);
            }

            float y = msgArea.Bottom - 14 + _chatScroll;
            float totalMsgH = 0;

            for (int i = snap.Count - 1; i >= 0; i--)
            {
                var m = snap[i];
                bool showDateDivider = !string.IsNullOrEmpty(m.DateLabel) && (i == 0 || snap[i - 1].DateLabel != m.DateLabel);
                float dateDivH = showDateDivider ? 28f : 0f;

                if (m.IsSystemOrCall)
                {
                    float reactH = m.Reactions.Length > 0 ? 28f : 0f;
                    float sysRowH = 30f + reactH;
                    float totalItemH = dateDivH + sysRowH;
                    totalMsgH += totalItemH;
                    y -= totalItemH;

                    if (y + totalItemH >= msgArea.Y - 40 && y <= msgArea.Bottom + 40)
                    {
                        float scy = y;
                        if (showDateDivider)
                        {
                            DrawDateDivider(msgArea.X + 16, scy + 6, msgArea.W - 32, m.DateLabel);
                            scy += dateDivH;
                        }

                        var sRowRect = new R(msgArea.X + 4, scy, msgArea.W - 12, sysRowH);
                        if (UI.Hover(sRowRect)) Gfx.FillRect(sRowRect, 0x0AFFFFFF, 6);

                        var icBox = new R(msgArea.X + 24, scy + 6, 22, 18);
                        if (m.MsgType == 3)
                            Gfx.DrawIcon(m.CallMissed ? IcPhoneMissed : IcPhoneCall, "📞", icBox, 14, m.CallMissed ? Th.Dim : Th.Green);
                        else
                        {
                            string sGlyph = m.MsgType == 6 ? "📌" : (m.MsgType is 1 or 7 ? "→" : (m.MsgType == 2 ? "←" : "✦"));
                            uint sCol = m.MsgType is 1 or 7 ? Th.Green : (m.MsgType == 2 ? Th.Red : Th.Sub);
                            Gfx.DrawText(sGlyph, icBox, 13f, sCol, 700, 1);
                        }

                        float tx = msgArea.X + 64f;
                        float maxTxW = MathF.Max(120f, msgArea.W - 160f);
                        string line = m.Content;
                        int nameIdx = !string.IsNullOrEmpty(m.AuthorName) ? line.IndexOf(m.AuthorName, StringComparison.Ordinal) : -1;
                        if (nameIdx == 0)
                        {
                            float nw = Gfx.MeasureText(m.AuthorName, 13.5f, 700, 260f).W;
                            Gfx.DrawText(m.AuthorName, new R(tx, scy + 5, nw + 4, 20), 13.5f, Th.Text, 700);
                            string rest = line[m.AuthorName.Length..];
                            float rw = Gfx.MeasureText(rest, 13.5f, 400, maxTxW - nw).W;
                            Gfx.DrawText(rest, new R(tx + nw, scy + 5, MathF.Max(40, maxTxW - nw), 20), 13.5f, Th.Sub, 400);
                            tx += nw + rw + 8f;
                        }
                        else if (nameIdx > 0)
                        {
                            string pre = line[..nameIdx];
                            float pw = Gfx.MeasureText(pre, 13.5f, 400, 240f).W;
                            Gfx.DrawText(pre, new R(tx, scy + 5, pw + 4, 20), 13.5f, Th.Sub, 400);
                            float nw = Gfx.MeasureText(m.AuthorName, 13.5f, 700, 240f).W;
                            Gfx.DrawText(m.AuthorName, new R(tx + pw, scy + 5, nw + 4, 20), 13.5f, Th.Text, 700);
                            string post = line[(nameIdx + m.AuthorName.Length)..];
                            float postW = Gfx.MeasureText(post, 13.5f, 400, MathF.Max(40, maxTxW - pw - nw)).W;
                            Gfx.DrawText(post, new R(tx + pw + nw, scy + 5, MathF.Max(40, maxTxW - pw - nw), 20), 13.5f, Th.Sub, 400);
                            tx += pw + nw + postW + 8f;
                        }
                        else
                        {
                            float lw2 = Gfx.MeasureText(line, 13.5f, 400, maxTxW).W;
                            Gfx.DrawText(line, new R(tx, scy + 5, maxTxW, 20), 13.5f, Th.Sub, 400);
                            tx += lw2 + 8f;
                        }

                        if (m.CallActive && ch != null)
                        {
                            const string joinTxt = "— Присоединиться к звонку";
                            float jw = Gfx.MeasureText(joinTxt, 13f, 600, 240f).W + 8f;
                            var jr = new R(MathF.Min(tx, msgArea.Right - jw - 80), scy + 4, jw, 22);
                            if (UI.Hover(jr)) Gfx.FillRect(jr, Th.Hover, 4);
                            Gfx.DrawText(joinTxt, new R(jr.X + 2, jr.Y + 2, jr.W, 18), 13f, Th.Cyan, 600);
                            if (UI.Click(UI.Id("sys_call_join_" + m.Id), jr))
                                _ = dc.JoinVoiceAsync(guild?.Id ?? "@me", ch.Id, _muted, _deafened);
                            tx = jr.Right + 8f;
                        }

                        Gfx.DrawText(m.Timestamp, new R(MathF.Min(tx, msgArea.Right - 96), scy + 7, 92, 16), 11f, Th.Dim);

                        if (m.Reactions.Length > 0)
                        {
                            float rx = msgArea.X + 64;
                            int rIdx = 0;
                            foreach (var re in m.Reactions.Take(8))
                            {
                                string label = $"{re.Display} {re.Count}";
                                bool isMine = re.Me;
                                float rw = Gfx.MeasureText(label, 12, 600, 120).W + 16;
                                var rr = new R(rx, scy + 28, rw, 22);
                                Gfx.FillRect(rr, isMine ? 0x335865F2 : (UI.Hover(rr) ? Th.Hover : Th.Card), 6);
                                Gfx.StrokeRect(rr, isMine ? Th.Brand : Th.Border, 1, 6);
                                Gfx.DrawText(label, new R(rx, scy + 31, rw, 16), 12, Th.Text, 600, 1);
                                if (UI.Click(UI.Id("mreact_" + m.Id, rIdx++), rr))
                                    _ = dc.ToggleReactionAsync(m.ChannelId, m.Id, re.ApiKey);
                                rx += rw + 6;
                            }
                        }
                    }
                    continue;
                }

                bool grouped = !showDateDivider && i > 0 && !snap[i - 1].IsSystemOrCall &&
                               snap[i - 1].AuthorId == m.AuthorId && snap[i - 1].Timestamp == m.Timestamp && string.IsNullOrEmpty(m.ReplyPreview);
                float textW = msgArea.W - 88;
                bool hasCode = m.Content.Contains("```", StringComparison.Ordinal);
                bool isQuote = !hasCode && m.Content.TrimStart().StartsWith("> ", StringComparison.Ordinal);
                float textH = string.IsNullOrEmpty(m.Content)
                    ? 0f
                    : (hasCode
                        ? Gfx.MeasureText(m.Content.Replace("```", "").Trim(), 13f, 400, MathF.Max(60f, MathF.Min(textW, 640f) - 20f)).H + 20f
                        : Gfx.MeasureText(m.Content, 14f, 400, textW).H);
                float replyH = string.IsNullOrEmpty(m.ReplyPreview) ? 0f : 18f;

                // Measure ALL attachments (images + file cards!)
                float attTotalH = 0f;
                var imgAtts = new List<(string Url, Vortice.Direct2D1.ID2D1Bitmap1? Bmp, float W, float H)>();
                var fileAtts = new List<DiscordAttachment>();
                if (m.Attachments.Count > 0)
                {
                    foreach (var a in m.Attachments.Take(4))
                    {
                        if (a.IsImage)
                        {
                            var b = ImageCache.Get(a.Url, 420, ImgMask.None);
                            float dh = b != null ? MathF.Min(240f, b.PixelSize.Height * MathF.Min(1f, 360f / MathF.Max(1, b.PixelSize.Width))) : 132f;
                            float dw = b != null ? MathF.Min(360f, b.PixelSize.Width * (dh / MathF.Max(1, b.PixelSize.Height))) : 210f;
                            imgAtts.Add((a.Url, b, dw, dh));
                            attTotalH += dh + 6f;
                        }
                        else
                        {
                            fileAtts.Add(a);
                            attTotalH += 44f;
                        }
                    }
                }
                else if (m.AttachmentUrls.Count > 0)
                {
                    string u = m.AttachmentUrls[0];
                    if (u.Contains(".png", StringComparison.OrdinalIgnoreCase) || u.Contains(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        u.Contains(".jpeg", StringComparison.OrdinalIgnoreCase) || u.Contains(".webp", StringComparison.OrdinalIgnoreCase) ||
                        u.Contains(".gif", StringComparison.OrdinalIgnoreCase) || u.Contains(".bmp", StringComparison.OrdinalIgnoreCase))
                    {
                        var b = ImageCache.Get(u, 420, ImgMask.None);
                        float dh = b != null ? MathF.Min(240f, b.PixelSize.Height * MathF.Min(1f, 360f / MathF.Max(1, b.PixelSize.Width))) : 132f;
                        float dw = b != null ? MathF.Min(360f, b.PixelSize.Width * (dh / MathF.Max(1, b.PixelSize.Height))) : 210f;
                        imgAtts.Add((u, b, dw, dh));
                        attTotalH += dh + 6f;
                    }
                }

                float embH = 0f;
                foreach (var eb in m.Embeds)
                {
                    float eImgH = !string.IsNullOrEmpty(eb.ImageUrl) ? 148f : 0f;
                    float eMeta = (!string.IsNullOrEmpty(eb.Author) ? 16f : 0f) + (!string.IsNullOrEmpty(eb.Footer) ? 16f : 0f);
                    embH += 22f + eMeta + eImgH + (string.IsNullOrEmpty(eb.Description) ? 0f : Gfx.MeasureText(eb.Description, 12.5f, 400, MathF.Min(420, textW - 24)).H) + 14f;
                }
                float reactH2 = m.Reactions.Length > 0 ? 28f : 0f;
                float headerH = grouped ? 4f : 24f;
                float rowH = MathF.Max(grouped ? 24f : 44f, replyH + headerH + textH + attTotalH + embH + reactH2 + 8f);
                float fullH = dateDivH + rowH;
                totalMsgH += fullH;
                y -= fullH;

                if (y + fullH < msgArea.Y - 40 || y > msgArea.Bottom + 40) continue;

                float msgTop = y;
                if (showDateDivider)
                {
                    DrawDateDivider(msgArea.X + 16, msgTop + 6, msgArea.W - 32, m.DateLabel);
                    msgTop += dateDivH;
                }

                int mk = m.Id.GetHashCode();
                var rowRect2 = new R(msgArea.X + 4, msgTop, msgArea.W - 12, rowH);
                bool rowHover2 = UI.Hover(rowRect2);
                if (rowHover2) Gfx.FillRect(rowRect2, 0x0AFFFFFF, 6);

                float cy2 = msgTop + 2;
                if (!string.IsNullOrEmpty(m.ReplyPreview)) { Gfx.DrawText(m.ReplyPreview, new R(msgArea.X + 64, cy2, textW, 16), 11.5f, Th.Dim); cy2 += 18; }
                float cy = cy2;
                var rowRect = rowRect2;
                bool rowHover = rowHover2;
                if (!grouped)
                {
                    var avBmp = ImageCache.Get(m.AvatarUrl, 64, ImgMask.Circle);
                    var avR = new R(msgArea.X + 18, cy + 2, 36, 36);
                    if (avBmp != null) Gfx.DrawBitmap(avBmp, avR);
                    else { Gfx.FillCircle(avR.X + 18, avR.Y + 18, 18, m.AuthorColor); Gfx.DrawText(FirstGrapheme(m.AuthorName), new R(avR.X, avR.Y + 9, 36, 20), 14, 0xFF0A0B0E, 700, 1); }
                    float nw = Gfx.MeasureText(m.AuthorName, 14, 700, 260).W;
                    var nameR = new R(msgArea.X + 64, cy, MathF.Min(260, nw + 4), 20);
                    Gfx.DrawText(m.AuthorName, nameR, 14, m.AuthorColor, 700);
                    if ((UI.Click(UI.Id("mav", mk), avR) || UI.Click(UI.Id("mna", mk), nameR)) && !string.IsNullOrEmpty(m.AuthorId) && m.AuthorId != "sys")
                        OpenUserProfile(m.AuthorId, guild?.Id ?? "", UI.MX, UI.MY);
                    string tsLabel = m.IsEdited ? $"{m.Timestamp} (изм.)" : m.Timestamp;
                    Gfx.DrawText(tsLabel, new R(msgArea.X + 64 + nw + 10, cy + 2, 120, 18), 11.5f, Th.Dim);
                    cy += 22;
                }
                if (!string.IsNullOrEmpty(m.Content))
                {
                    if (hasCode)
                    {
                        var codeCard = new R(msgArea.X + 64, cy, MathF.Min(textW, 640), textH - 4);
                        Gfx.FillRect(codeCard, 0xFF0E1016, 6);
                        Gfx.StrokeRect(codeCard, Th.Border, 1, 6);
                        Gfx.DrawText(m.Content.Replace("```", "").Trim(), new R(codeCard.X + 10, codeCard.Y + 6, codeCard.W - 20, codeCard.H - 10), 13, 0xFFE2E8F0, 400, 0, true);
                        cy += textH + 2;
                    }
                    else if (isQuote)
                    {
                        Gfx.FillRect(new R(msgArea.X + 64, cy + 2, 3.5f, MathF.Max(16, textH - 2)), Th.Dim, 1.5f);
                        Gfx.DrawText(m.Content.TrimStart().Substring(2), new R(msgArea.X + 74, cy, textW - 10, textH + 6), 14, Th.Sub, 400, 0, true);
                        cy += textH + 4;
                    }
                    else
                    {
                        Gfx.DrawText(m.Content, new R(msgArea.X + 64, cy, textW, textH + 6), 14, Th.Text, 400, 0, true);
                        cy += textH + 4;
                    }
                }
                for (int ai = 0; ai < imgAtts.Count; ai++)
                {
                    var (u, bmp, dw, dh) = imgAtts[ai];
                    var imgR = new R(msgArea.X + 64, cy + 2, dw, dh);
                    if (bmp != null)
                    {
                        Gfx.DrawBitmap(bmp, imgR);
                        if (UI.Hover(imgR)) Gfx.StrokeRect(imgR, Th.Brand, 1.5f, 4);
                        if (UI.Click(UI.Id("mimg_" + m.Id, ai), imgR)) _lightboxUrl = u;
                    }
                    else { Gfx.FillRect(imgR, Th.Panel, 8); Gfx.DrawText("Загрузка...", new R(imgR.X, imgR.Y + dh * 0.42f, imgR.W, 20), 12, Th.Dim, 500, 1); }
                    cy += dh + 6f;
                }
                for (int fi = 0; fi < fileAtts.Count; fi++)
                {
                    var fa = fileAtts[fi];
                    var fr = new R(msgArea.X + 64, cy + 2, MathF.Min(360, textW), 38);
                    Gfx.FillRect(fr, UI.Hover(fr) ? Th.Hover : Th.Panel, 8);
                    Gfx.StrokeRect(fr, Th.Border, 1, 8);
                    string szStr = fa.Size >= 1024 * 1024 ? $"{fa.Size / (1024f * 1024f):F1} МБ" : (fa.Size >= 1024 ? $"{fa.Size / 1024f:F0} КБ" : $"{fa.Size} Б");
                    Gfx.DrawText($"📄  {fa.Filename}", new R(fr.X + 10, fr.Y + 4, fr.W - 20, 16), 12.5f, Th.Cyan, 600);
                    Gfx.DrawText($"{szStr} • Нажмите, чтобы открыть/скачать", new R(fr.X + 10, fr.Y + 20, fr.W - 20, 14), 10.5f, Th.Dim);
                    if (UI.Click(UI.Id("mfil_" + m.Id, fi), fr)) _ = dc.OpenP2pFileLocallyAsync(fa.Url, fa.Filename);
                    cy += 44f;
                }
                for (int ei = 0; ei < m.Embeds.Length; ei++)
                {
                    var eb = m.Embeds[ei];
                    float ew = MathF.Min(440, textW);
                    float dh = string.IsNullOrEmpty(eb.Description) ? 0f : Gfx.MeasureText(eb.Description, 12.5f, 400, ew - 24).H;
                    float eImgH = !string.IsNullOrEmpty(eb.ImageUrl) ? 148f : 0f;
                    float eMeta = (!string.IsNullOrEmpty(eb.Author) ? 16f : 0f) + (!string.IsNullOrEmpty(eb.Footer) ? 16f : 0f);
                    float bh = 22f + eMeta + dh + eImgH + 10f;
                    Gfx.FillRect(new R(msgArea.X + 64, cy + 2, ew, bh), Th.Panel, 6);
                    Gfx.FillRect(new R(msgArea.X + 64, cy + 2, 4, bh), eb.Color, 2);
                    float ey = cy + 6;
                    if (!string.IsNullOrEmpty(eb.Author)) { Gfx.DrawText(eb.Author, new R(msgArea.X + 76, ey, ew - 20, 15), 11, Th.Sub, 600); ey += 16; }
                    if (!string.IsNullOrEmpty(eb.Title))
                    {
                        var tr = new R(msgArea.X + 76, ey, ew - 20, 18);
                        Gfx.DrawText(eb.Title, tr, 13, !string.IsNullOrEmpty(eb.Url) ? Th.Cyan : Th.Text, 700);
                        if (!string.IsNullOrEmpty(eb.Url) && UI.Click(UI.Id("memb_" + m.Id, ei), tr)) W.OpenUrl(eb.Url);
                        ey += 18;
                    }
                    if (!string.IsNullOrEmpty(eb.Description)) { Gfx.DrawText(eb.Description, new R(msgArea.X + 76, ey, ew - 20, dh + 4), 12.5f, Th.Sub, 400, 0, true); ey += dh + 4; }
                    if (!string.IsNullOrEmpty(eb.ImageUrl))
                    {
                        var ebmp = ImageCache.Get(eb.ImageUrl, 400, ImgMask.None);
                        var eir = new R(msgArea.X + 76, ey + 2, MathF.Min(260, ew - 24), 140);
                        if (ebmp != null)
                        {
                            float sc = MathF.Min(1f, MathF.Min(eir.W / MathF.Max(1, ebmp.PixelSize.Width), eir.H / MathF.Max(1, ebmp.PixelSize.Height)));
                            var fitR = new R(eir.X, eir.Y, ebmp.PixelSize.Width * sc, ebmp.PixelSize.Height * sc);
                            Gfx.DrawBitmap(ebmp, fitR);
                            if (UI.Click(UI.Id("mebi_" + m.Id, ei), fitR)) _lightboxUrl = eb.ImageUrl;
                        }
                        ey += 148;
                    }
                    if (!string.IsNullOrEmpty(eb.Footer)) Gfx.DrawText(eb.Footer, new R(msgArea.X + 76, ey, ew - 20, 15), 10.5f, Th.Dim);
                    cy += bh + 6f;
                }
                if (m.Reactions.Length > 0)
                {
                    float rx = msgArea.X + 64;
                    int rIdx = 0;
                    foreach (var re in m.Reactions.Take(8))
                    {
                        string label = $"{re.Display} {re.Count}";
                        bool isMine = re.Me;
                        float rw = Gfx.MeasureText(label, 12, 600, 120).W + 16;
                        var rr = new R(rx, cy + 2, rw, 22);
                        Gfx.FillRect(rr, isMine ? 0x335865F2 : (UI.Hover(rr) ? Th.Hover : Th.Card), 6);
                        Gfx.StrokeRect(rr, isMine ? Th.Brand : Th.Border, 1, 6);
                        Gfx.DrawText(label, new R(rx, cy + 5, rw, 16), 12, Th.Text, 600, 1);
                        if (UI.Click(UI.Id("mreact_" + m.Id, rIdx++), rr))
                            _ = dc.ToggleReactionAsync(m.ChannelId, m.Id, re.ApiKey);
                        rx += rw + 6;
                    }
                }
                if (rowHover)
                {
                    // Solid top-right floating Discord action toolbar (never collides transparently over text!)
                    bool ownMsg = m.AuthorId == dc.Me.Id;
                    string? foundUrl = FindFirstHttpUrl(m.Content);
                    float pillW = 4 * 26f + 60f + 52f + 30f + (foundUrl != null ? 58f : 0f) + (ownMsg ? 98f : 0f) + 12f;
                    var pillR = new R(rowRect.Right - pillW - 6, msgTop + 2, pillW, 26);
                    Gfx.FillRect(pillR, 0xF416181F, 7);
                    Gfx.StrokeRect(pillR, Th.Border, 1, 7);

                    float ax = pillR.Right - 4;
                    if (ownMsg)
                    {
                        ax -= 48;
                        if (UI.Button(UI.Id("mdel", mk), new R(ax, pillR.Y + 2, 46, 22), "Удал.", 0x00000000, Th.Red, 5, 11))
                            _ = dc.DeleteMessageAsync(m.ChannelId, m.Id);
                        ax -= 46;
                        if (UI.Button(UI.Id("medit", mk), new R(ax, pillR.Y + 2, 44, 22), "Изм.", 0x00000000, Th.Amber, 5, 11))
                        {
                            _editingMsgId = m.Id;
                            _editingOrigText = m.Content;
                            App.SetChatInputText(_editingOrigText);
                        }
                    }
                    ax -= 28;
                    if (UI.Button(UI.Id("mpin", mk), new R(ax, pillR.Y + 2, 26, 22), "📌", m.IsPinned ? 0x33F59E0Bu : 0x00000000u, m.IsPinned ? Th.Amber : Th.Sub, 5, 11))
                    {
                        _ = dc.PinMessageAsync(m.ChannelId, m.Id);
                        Toast(m.IsPinned ? "Сообщение откреплено" : "📌 Сообщение закреплено");
                    }
                    if (foundUrl != null)
                    {
                        ax -= 56;
                        if (UI.Button(UI.Id("murl", mk), new R(ax, pillR.Y + 2, 54, 22), "Ссылка", 0x00000000, Th.Cyan, 5, 11))
                            W.OpenUrl(foundUrl);
                    }
                    ax -= 50;
                    if (UI.Button(UI.Id("mcpy", mk), new R(ax, pillR.Y + 2, 48, 22), "Копир.", 0x00000000, Th.Sub, 5, 11))
                    { W.CopyText(App.Hwnd, m.Content); Toast("Скопировано в буфер"); }
                    ax -= 58;
                    if (UI.Button(UI.Id("mrep", mk), new R(ax, pillR.Y + 2, 56, 22), "Ответить", 0x00000000, Th.Text, 5, 11))
                    { _replyToId = m.Id; _replyToLabel = $"Ответ на @{m.AuthorName}"; }

                    // 1-Click Quick Reactions (👍 ❤️ 😂 🔥)
                    string[] qEmo = ["🔥", "😂", "❤️", "👍"];
                    for (int e = 0; e < qEmo.Length; e++)
                    {
                        ax -= 26;
                        if (UI.Button(UI.Id("mqe_" + m.Id, e), new R(ax, pillR.Y + 2, 24, 22), qEmo[e], 0x00000000, Th.Text, 5, 11.5f))
                            _ = dc.ToggleReactionAsync(m.ChannelId, m.Id, qEmo[e]);
                    }
                }
            }

            float maxChatScroll = MathF.Max(0, totalMsgH - msgArea.H + 32);
            if (_chatScroll > maxChatScroll) _chatScroll = maxChatScroll;
            if (!_showChannelSearch && maxChatScroll > 100 && _chatScroll >= maxChatScroll - 120) _ = dc.LoadOlderMessagesAsync(ch.Id);
            if (maxChatScroll > 0)
            {
                float barH = MathF.Max(32, msgArea.H * (msgArea.H / MathF.Max(msgArea.H, totalMsgH)));
                float barY = msgArea.Bottom - barH - (_chatScroll / maxChatScroll) * (msgArea.H - barH);
                Gfx.FillRect(new R(msgArea.Right - 5, barY, 4, barH), 0x33FFFFFF, 2);
            }
        }
        Gfx.PopClip();

        // Floating "↓ К новым сообщениям" pill when scrolled up in chat history
        if (_chatScroll > 180f)
        {
            var jmp = new R(msgArea.X + (msgArea.W - 190) * 0.5f, msgArea.Bottom - 36, 190, 28);
            if (UI.Button(UI.Id("chat_bot"), jmp, "↓ К новым сообщениям", 0xEE5865F2, 0xFFFFFFFF, 14, 12, 700))
                _chatScroll = 0;
        }

        // Right-Side Member List Sidebar (224px — works for BOTH Guilds AND Group DMs!)
        if (showMembers)
        {
            if (guild != null)
                DrawMemberListSidebar(new R(r.X + chatW, r.Y + 45, memW, r.H - 45), guild);
            else if (ch?.Type == 3)
                DrawGroupDmMemberSidebar(new R(r.X + chatW, r.Y + 45, memW, r.H - 45), ch);
        }

        if (!string.IsNullOrEmpty(_hoverGuildTip))
        {
            float tw = Gfx.MeasureText(_hoverGuildTip, 13, 600, 260).W + 22;
            var tr = new R(RailW + 6, Math.Clamp(_hoverGuildTipY, TopH + 6, Gfx.H - 40), tw, 28);
            Gfx.FillRect(tr, 0xF0111318, 6);
            Gfx.StrokeRect(tr, Th.Border, 1, 6);
            Gfx.DrawText(_hoverGuildTip, new R(tr.X + 10, tr.Y + 6, tr.W - 20, 18), 13, Th.Text, 600);
        }

        if (!string.IsNullOrEmpty(_editingMsgId))
        {
            var eb = new R(r.X + 16, r.Bottom - InputH - 28, chatW - 32, 22);
            Gfx.FillRect(eb, 0x33F59E0B, 6);
            Gfx.DrawText("✏️ Редактирование сообщения (Enter — сохранить, Esc — отмена)", new R(eb.X + 10, eb.Y + 3, eb.W - 40, 16), 11.5f, Th.Amber, 600);
            if (UI.Button(UI.Id("ed_x"), new R(eb.Right - 22, eb.Y + 2, 18, 18), "✕", 0x00000000, Th.Sub, 4, 11))
            { _editingMsgId = ""; _editingOrigText = ""; App.TakeChatInputText(); }
        }
        else if (!string.IsNullOrEmpty(_replyToId))
        {
            var rb = new R(r.X + 16, r.Bottom - InputH - 28, chatW - 32, 22);
            Gfx.FillRect(rb, Th.Card, 6);
            Gfx.DrawText(_replyToLabel, new R(rb.X + 10, rb.Y + 3, rb.W - 40, 16), 11.5f, Th.Cyan, 600);
            if (UI.Button(UI.Id("rep_x"), new R(rb.Right - 22, rb.Y + 2, 18, 18), "✕", 0x00000000, Th.Sub, 4, 11))
            { _replyToId = ""; _replyToLabel = ""; }
        }
        else if (ch != null && dc.TypingUsers.TryGetValue(ch.Id, out var typ) && Environment.TickCount64 < typ.Until)
        {
            Gfx.DrawText($"{typ.Name} печатает...", new R(r.X + 20, r.Bottom - InputH - 22, 300, 18), 11.5f, Th.Sub, 600);
        }

        _chatInputRect = new R(r.X + 54, r.Bottom - InputH - 4, MathF.Max(120, chatW - 186), 40);
        _wantChatInput = !_showSettings && !_showQuickSwitch && !_showShareModal && _lightboxUrl == null;
        Gfx.FillRect(new R(r.X + 12, _chatInputRect.Y - 2, chatW - 28, 44), Th.Panel, 10);
        Gfx.StrokeRect(new R(r.X + 12, _chatInputRect.Y - 2, chatW - 28, 44), Th.Border, 1, 10);

        if (UI.Button(UI.Id("btn_up"), new R(r.X + 18, _chatInputRect.Y + 4, 32, 32), "+", Th.Card, Th.Text, 16, 16, 700) && ch != null)
        {
            string uploadChId = ch.Id;
            App.InvokeOnUiThread(() =>
            {
                var fp = W.PickFile(App.Hwnd);
                if (!string.IsNullOrEmpty(fp))
                {
                    Toast("Загрузка " + Path.GetFileName(fp) + "...");
                    _ = DiscordClient.Instance.UploadFileAsync(uploadChId, fp);
                }
            });
        }
        if (UI.Button(UI.Id("btn_emo"), new R(r.X + chatW - 126, _chatInputRect.Y + 4, 32, 32), "😀", _showEmojiPicker ? Th.Brand : Th.Card, Th.Text, 7, 14))
            _showEmojiPicker = !_showEmojiPicker;
        if (UI.Button(UI.Id("btn_send"), new R(r.X + chatW - 88, _chatInputRect.Y + 3, 66, 34), string.IsNullOrEmpty(_editingMsgId) ? "Отпр." : "Сохр.", Th.Brand, Th.Text, 7, 13))
            OnChatEnter();
    }

    private static void DrawDateDivider(float x, float y, float w, string dateText)
    {
        float tw = Gfx.MeasureText(dateText, 11.5f, 700, 220f).W + 20f;
        float lineW = MathF.Max(12f, (w - tw) * 0.5f);
        Gfx.FillRect(new R(x, y + 8f, lineW, 1f), Th.Border);
        Gfx.DrawText(dateText, new R(x + lineW, y, tw, 16f), 11.5f, Th.Dim, 700, 1);
        Gfx.FillRect(new R(x + lineW + tw, y + 8f, lineW, 1f), Th.Border);
    }

    private static void DrawGroupDmMemberSidebar(R r, DiscordChannel groupCh)
    {
        Gfx.FillRect(r, Th.Sidebar);
        Gfx.FillRect(new R(r.X, r.Y, 1, r.H), Th.Border);
        var dc = DiscordClient.Instance;

        var callSet = new HashSet<string>();
        if (dc.DmCalls.TryGetValue(groupCh.Id, out var dmCall))
        {
            lock (dmCall) foreach (var k in dmCall.VoiceStates.Keys) callSet.Add(k);
        }
        if (dc.ConnectedVoiceChannelId == groupCh.Id) callSet.Add(dc.Me.Id);

        var allIds = new List<string> { dc.Me.Id };
        foreach (var rid in groupCh.RecipientIds)
            if (!allIds.Contains(rid)) allIds.Add(rid);

        var rows = new List<(string Id, string Name, string? AvUrl, string Status, string Activity, bool InCall)>(allIds.Count);
        foreach (var uid in allIds)
        {
            if (uid == dc.Me.Id)
            {
                string? myAv = string.IsNullOrEmpty(dc.Me.Avatar) ? null : $"https://cdn.discordapp.com/avatars/{dc.Me.Id}/{dc.Me.Avatar}.png?size=64";
                rows.Add((uid, dc.Me.DisplayName, myAv, dc.Me.Status, dc.Me.ActivityText, callSet.Contains(uid)));
            }
            else
            {
                dc.Users.TryGetValue(uid, out var uo);
                string nm = uo?.DisplayName ?? ("User " + (uid.Length >= 4 ? uid[^4..] : uid));
                rows.Add((uid, nm, uo?.AvatarUrl, uo?.Status ?? "offline", uo?.ActivityText ?? "", callSet.Contains(uid)));
            }
        }
        rows = rows
            .OrderByDescending(x => x.InCall)
            .ThenBy(x => x.Status is "offline" or "invisible" ? 1 : 0)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        float totalH = 38f + rows.Count * 44f;
        float maxSc = MathF.Max(0, totalH - r.H);
        if (UI.Hover(r) && UI.Wheel != 0) _memberScroll = Math.Clamp(_memberScroll - UI.Wheel * 44f, 0f, maxSc);
        else _memberScroll = Math.Clamp(_memberScroll, 0f, maxSc);

        Gfx.PushClip(r);
        float y = r.Y + 12 - _memberScroll;
        Gfx.DrawText($"Участники — {rows.Count}", new R(r.X + 14, y, r.W - 24, 18), 11.5f, Th.Dim, 700);
        y += 24;

        foreach (var u in rows)
        {
            if (y + 42 >= r.Y && y <= r.Bottom)
            {
                bool online = u.Status is not ("offline" or "invisible") || u.InCall;
                uint dotCol = u.Status switch { "online" => Th.Green, "idle" => Th.Amber, "dnd" => Th.Red, _ => 0xFF4E5058 };
                var row = new R(r.X + 8, y, r.W - 16, 40);
                if (u.InCall) Gfx.FillRect(row, 0x185865F2, 8);
                if (UI.Hover(row)) Gfx.FillRect(row, Th.Hover, 8);

                var av = ImageCache.Get(u.AvUrl, 64, ImgMask.Circle);
                var avr = new R(row.X + 6, row.Y + 5, 30, 30);
                if (av != null) Gfx.DrawBitmap(av, avr);
                else { Gfx.FillCircle(avr.X + 15, avr.Y + 15, 15, Th.Brand); Gfx.DrawText(FirstGrapheme(u.Name), new R(avr.X, avr.Y + 6, 30, 18), 12, 0xFFFFFFFF, 700, 1); }

                Gfx.FillCircle(avr.Right - 3, avr.Bottom - 3, 4.6f, Th.Sidebar);
                Gfx.FillCircle(avr.Right - 3, avr.Bottom - 3, 3.3f, u.InCall ? Th.Green : dotCol);

                string sub = u.InCall
                    ? (string.IsNullOrEmpty(u.Activity) ? "📞 В звонке" : $"📞 • {u.Activity}")
                    : u.Activity;
                if (!string.IsNullOrEmpty(sub))
                {
                    Gfx.DrawText(u.Name, new R(row.X + 42, row.Y + 4, row.W - 48, 17), 12.5f, online ? Th.Text : Th.Sub, online ? 600 : 400);
                    Gfx.DrawText(sub, new R(row.X + 42, row.Y + 21, row.W - 48, 15), 10.5f, u.InCall ? Th.Green : Th.Dim, 500);
                }
                else
                {
                    Gfx.DrawText(u.Name, new R(row.X + 42, row.Y + 11, row.W - 48, 18), 12.5f, online ? Th.Text : Th.Sub, online ? 600 : 400);
                }

                if (UI.Click(UI.Id("gdm_mem_" + u.Id), row))
                    OpenUserProfile(u.Id, "@me", UI.MX, UI.MY);
            }
            y += 44;
        }
        Gfx.PopClip();
    }

    private static string? FindFirstHttpUrl(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        int idx = s.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = s.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        int end = s.IndexOfAny([' ', '\t', '\n', '\r', '>', '"', '\'', ')', ']'], idx);
        string raw = (end > idx ? s[idx..end] : s[idx..]).TrimEnd('.', ',', ';', '!', '?');
        return raw.Length > 8 ? raw : null;
    }

    private static void DrawMemberListSidebar(R r, DiscordGuild guild)
    {
        Gfx.FillRect(r, Th.Sidebar);
        Gfx.FillRect(new R(r.X, r.Y, 1, r.H), Th.Border);
        var dc = DiscordClient.Instance;

        List<VoiceMember> voiceList;
        lock (guild.VoiceStates) voiceList = guild.VoiceStates.Values.OrderBy(v => v.Username).ToList();
        var inVoiceIds = new HashSet<string>(voiceList.Select(v => v.UserId));

        var otherUsers = new List<(string Id, string Name, string? AvUrl, string Status)>();
        if (dc.GuildMemberIds.TryGetValue(guild.Id, out var idSet))
        {
            List<string> ids;
            lock (idSet) ids = [.. idSet];
            foreach (var uid in ids)
            {
                if (inVoiceIds.Contains(uid)) continue;
                dc.Users.TryGetValue(uid, out var uo);
                string name = dc.GuildNicks.TryGetValue(guild.Id + ":" + uid, out var nk) ? nk : (uo?.DisplayName ?? "");
                if (string.IsNullOrEmpty(name)) continue;
                string? av = uo != null && !string.IsNullOrEmpty(uo.Avatar)
                    ? $"https://cdn.discordapp.com/avatars/{uid}/{uo.Avatar}.png?size=48" : null;
                string st = uid == dc.Me.Id ? dc.Me.Status : (uo?.Status ?? "offline");
                otherUsers.Add((uid, name, av, st));
            }
        }
        if (!inVoiceIds.Contains(dc.Me.Id) && !otherUsers.Any(x => x.Id == dc.Me.Id))
        {
            string? myAv = string.IsNullOrEmpty(dc.Me.Avatar) ? null : $"https://cdn.discordapp.com/avatars/{dc.Me.Id}/{dc.Me.Avatar}.png?size=48";
            otherUsers.Insert(0, (dc.Me.Id, dc.Me.DisplayName, myAv, dc.Me.Status));
        }
        otherUsers = otherUsers
            .OrderBy(x => x.Status is "offline" or "invisible" ? 1 : 0)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .ToList();

        float totalH = 20 + (voiceList.Count > 0 ? 26 + voiceList.Count * 38 : 0) + 26 + otherUsers.Count * 38;
        float maxScroll = MathF.Max(0, totalH - r.H);
        if (UI.Hover(r) && UI.Wheel != 0) _memberScroll = Math.Clamp(_memberScroll - UI.Wheel * 44f, 0f, maxScroll);
        else _memberScroll = Math.Clamp(_memberScroll, 0f, maxScroll);

        Gfx.PushClip(r);
        float y = r.Y + 10 - _memberScroll;
        if (voiceList.Count > 0)
        {
            Gfx.DrawText($"В ГОЛОСОВОМ — {voiceList.Count}", new R(r.X + 14, y, r.W - 24, 18), 10.5f, Th.Dim, 700);
            y += 22;
            foreach (var vm in voiceList)
            {
                if (y + 36 >= r.Y && y <= r.Bottom)
                {
                    var row = new R(r.X + 8, y, r.W - 16, 34);
                    if (UI.Hover(row)) Gfx.FillRect(row, Th.Hover, 6);
                    var av = ImageCache.Get(vm.AvatarUrl, 48, ImgMask.Circle);
                    var avr = new R(row.X + 6, row.Y + 4, 26, 26);
                    bool spk = dc.IsUserActuallySpeaking(vm);
                    if (spk) { Gfx.FillCircle(avr.X + 13, avr.Y + 13, 15, Th.Green); Gfx.FillCircle(avr.X + 13, avr.Y + 13, 13.3f, Th.Sidebar); }
                    if (av != null) Gfx.DrawBitmap(av, avr);
                    else Gfx.FillCircle(avr.X + 13, avr.Y + 13, 13, Th.Brand);
                    Gfx.FillCircle(avr.Right - 3, avr.Bottom - 3, 4.2f, Th.Sidebar);
                    Gfx.FillCircle(avr.Right - 3, avr.Bottom - 3, 3f, Th.Green);
                    Gfx.DrawText(vm.Username, new R(row.X + 38, row.Y + 8, row.W - (vm.SelfStream ? 86 : 44), 18), 12.5f, Th.Text, 600);
                    if (vm.SelfStream)
                    {
                        Gfx.FillRect(new R(row.Right - 44, row.Y + 9, 40, 16), Th.Red, 8);
                        Gfx.DrawText("LIVE", new R(row.Right - 44, row.Y + 10, 40, 14), 9f, 0xFFFFFFFF, 700, 1);
                    }
                    if (UI.RightClick(row))
                    {
                        _ctxStreamUserId = null; _audioPopupMode = 0;
                        _ctxUser = vm; _ctxGuildId = guild.Id;
                        _ctxX = Math.Clamp(UI.MX, 8, Gfx.W - 252);
                        _ctxY = Math.Clamp(UI.MY, TopH + 8, Gfx.H - 220);
                    }
                }
                y += 38;
            }
            y += 8;
        }

        Gfx.DrawText($"УЧАСТНИКИ — {otherUsers.Count}", new R(r.X + 14, y, r.W - 24, 18), 10.5f, Th.Dim, 700);
        y += 22;
        foreach (var u in otherUsers)
        {
            if (y + 36 >= r.Y && y <= r.Bottom)
            {
                bool online = u.Status is not ("offline" or "invisible");
                uint dotCol = u.Status switch { "online" => Th.Green, "idle" => Th.Amber, "dnd" => Th.Red, _ => Th.Dim };
                var row = new R(r.X + 8, y, r.W - 16, 34);
                if (UI.Hover(row)) Gfx.FillRect(row, Th.Hover, 6);
                var av = ImageCache.Get(u.AvUrl, 48, ImgMask.Circle);
                var avr = new R(row.X + 6, row.Y + 4, 26, 26);
                if (av != null) Gfx.DrawBitmap(av, avr);
                else { Gfx.FillCircle(avr.X + 13, avr.Y + 13, 13, Th.Card); Gfx.DrawText(FirstGrapheme(u.Name), new R(avr.X, avr.Y + 5, 26, 18), 11, Th.Text, 700, 1); }
                if (online)
                {
                    Gfx.FillCircle(avr.Right - 3, avr.Bottom - 3, 4.2f, Th.Sidebar);
                    Gfx.FillCircle(avr.Right - 3, avr.Bottom - 3, 3f, dotCol);
                }
                Gfx.DrawText(u.Name, new R(row.X + 38, row.Y + 8, row.W - 44, 18), 12.5f, online ? Th.Text : Th.Sub, online ? 600 : 400);
                if (UI.Click(UI.Id("mem_" + u.Id), row))
                    OpenUserProfile(u.Id, guild.Id, UI.MX, UI.MY);
            }
            y += 38;
        }
        Gfx.PopClip();
    }

    private static void OpenUserProfile(string userId, string guildId, float mx, float my)
    {
        _ctxUser = null; _ctxStreamUserId = null; _audioPopupMode = 0; _showStatusMenu = false; _showVoiceInfo = false;
        _profileUserId = userId;
        _profileGuildId = guildId;
        _profileX = Math.Clamp(mx + 12, RailW + 8, Gfx.W - 296);
        _profileY = Math.Clamp(my - 40, TopH + 8, Gfx.H - 290);
    }

    // ---------------- Friends List Home View (@me -> Друзья) ----------------

    private static void DrawFriendsStage(R r)
    {
        Gfx.FillRect(r, Th.Bg);
        var dc = DiscordClient.Instance;

        List<DiscordFriend> allRels;
        lock (dc.Friends) allRels = [.. dc.Friends];
        int incomingCnt = allRels.Count(f => f.Type == 3);
        int pendingCnt = allRels.Count(f => f.Type is 3 or 4);

        Gfx.FillRect(new R(r.X, r.Y, r.W, 44), Th.Panel);
        Gfx.FillRect(new R(r.X, r.Y + 44, r.W, 1), Th.Border);
        Gfx.DrawText("👥 Друзья", new R(r.X + 16, r.Y + 12, 110, 22), 15, Th.Text, 700);
        if (UI.Button(UI.Id("fr_t0"), new R(r.X + 128, r.Y + 8, 86, 28), "В сети", _friendsTab == 0 ? Th.Active : Th.Card, Th.Text, 6, 12)) { _friendsTab = 0; _friendsScroll = 0; }
        if (UI.Button(UI.Id("fr_t1"), new R(r.X + 220, r.Y + 8, 72, 28), "Все", _friendsTab == 1 ? Th.Active : Th.Card, Th.Text, 6, 12)) { _friendsTab = 1; _friendsScroll = 0; }
        string pendLbl = pendingCnt > 0 ? $"Ожидание ({pendingCnt})" : "Ожидание";
        if (UI.Button(UI.Id("fr_t2"), new R(r.X + 298, r.Y + 8, 126, 28), pendLbl, _friendsTab == 2 ? Th.Active : (incomingCnt > 0 ? 0x33ED4245 : Th.Card), incomingCnt > 0 && _friendsTab != 2 ? Th.Red : Th.Text, 6, 12, incomingCnt > 0 ? 700 : 600))
        { _friendsTab = 2; _friendsScroll = 0; }

        List<DiscordFriend> list = _friendsTab switch
        {
            0 => allRels.Where(f => f.Type == 1 && f.Status is not ("offline" or "invisible")).ToList(),
            1 => allRels.Where(f => f.Type == 1).ToList(),
            _ => allRels.Where(f => f.Type is 3 or 4).ToList()
        };

        var area = new R(r.X, r.Y + 45, r.W, r.H - 45);
        float totalH = 52 + list.Count * 56f;
        float maxFrScroll = MathF.Max(0, totalH - area.H);
        if (UI.Hover(area) && UI.Wheel != 0) _friendsScroll = Math.Clamp(_friendsScroll - UI.Wheel * 56f, 0f, maxFrScroll);
        else _friendsScroll = Math.Clamp(_friendsScroll, 0f, maxFrScroll);

        Gfx.PushClip(area);
        float y = area.Y + 16 - _friendsScroll;
        string secHdr = _friendsTab switch { 0 => "В СЕТИ — ", 1 => "ВСЕ ДРУЗЬЯ — ", _ => "ЗАПРОСЫ ДРУЖБЫ — " } + list.Count;
        Gfx.DrawText(secHdr, new R(area.X + 20, y, 260, 18), 11, Th.Dim, 700);
        y += 28;

        if (list.Count == 0)
        {
            Gfx.DrawText("Нет пользователей в этой категории", new R(area.X + 20, y + 20, area.W - 40, 24), 14, Th.Dim);
        }
        else
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (y + 56 >= area.Y && y <= area.Bottom)
                {
                    var f = list[i];
                    var row = new R(area.X + 16, y, MathF.Min(760, area.W - 32), 54);
                    Gfx.FillRect(new R(row.X, row.Y, row.W, 1), Th.Border);
                    if (UI.Hover(row)) Gfx.FillRect(row, Th.Hover, 8);

                    var av = ImageCache.Get(f.AvatarUrl, 64, ImgMask.Circle);
                    var avr = new R(row.X + 10, row.Y + 10, 34, 34);
                    if (av != null) Gfx.DrawBitmap(av, avr);
                    else { Gfx.FillCircle(avr.X + 17, avr.Y + 17, 17, Th.Brand); Gfx.DrawText(FirstGrapheme(f.DisplayName), new R(avr.X, avr.Y + 8, 34, 20), 14, 0xFFFFFFFF, 700, 1); }

                    uint dot = f.Status switch { "online" => Th.Green, "idle" => Th.Amber, "dnd" => Th.Red, _ => 0xFF4E5058 };
                    Gfx.FillCircle(avr.Right - 4, avr.Bottom - 4, 5.2f, Th.Bg);
                    Gfx.FillCircle(avr.Right - 4, avr.Bottom - 4, 3.8f, dot);

                    Gfx.DrawText(f.DisplayName, new R(row.X + 54, row.Y + 9, row.W - 260, 18), 14, Th.Text, 700);
                    string stTxt = f.Type == 3
                        ? "Входящий запрос дружбы"
                        : (f.Type == 4
                            ? "Исходящий запрос дружбы"
                            : (!string.IsNullOrEmpty(f.ActivityText)
                                ? f.ActivityText
                                : (f.Status switch { "online" => "В сети", "idle" => "Неактивен", "dnd" => "Не беспокоить", _ => "Не в сети" })));
                    Gfx.DrawText($"@{f.Username}  •  {stTxt}", new R(row.X + 54, row.Y + 28, row.W - 260, 16), 11.5f, f.Type == 3 ? Th.Cyan : Th.Sub);

                    if (f.Type == 3)
                    {
                        if (UI.Button(UI.Id("fr_acc_" + f.UserId), new R(row.Right - 198, row.Y + 12, 92, 30), "✓ Принять", Th.Green, 0xFFFFFFFF, 6, 12, 700))
                            _ = dc.AcceptFriendRequestAsync(f.UserId);
                        if (UI.Button(UI.Id("fr_dec_" + f.UserId), new R(row.Right - 98, row.Y + 12, 88, 30), "✕ Скрыть", 0x22ED4245, Th.Red, 6, 12))
                            _ = dc.RemoveRelationshipAsync(f.UserId);
                    }
                    else if (f.Type == 4)
                    {
                        if (UI.Button(UI.Id("fr_can_" + f.UserId), new R(row.Right - 118, row.Y + 12, 108, 30), "Отменить", 0x22ED4245, Th.Red, 6, 12))
                            _ = dc.RemoveRelationshipAsync(f.UserId);
                    }
                    else
                    {
                        if (UI.Button(UI.Id("fr_call_" + f.UserId), new R(row.Right - 216, row.Y + 12, 90, 30), "📞 Звонок", 0x2223A55A, Th.Green, 6, 12, 600))
                        {
                            _showFriendsView = false;
                            _ = ShellAsync.OpenDmAndCallAsync(f.UserId, _muted, _deafened);
                        }
                        if (UI.Button(UI.Id("fr_dm_" + f.UserId), new R(row.Right - 118, row.Y + 12, 108, 30), "💬 Написать", Th.Card, Th.Text, 6, 12))
                        {
                            _showFriendsView = false;
                            _ = ShellAsync.OpenDmAsync(f.UserId);
                        }
                    }
                }
                y += 56;
            }
        }
        Gfx.PopClip();

        if (maxFrScroll > 0)
        {
            float barH = MathF.Max(28, area.H * (area.H / totalH));
            float barY = area.Y + (_friendsScroll / maxFrScroll) * (area.H - barH);
            Gfx.FillRect(new R(area.Right - 5, barY, 4, barH), 0x33FFFFFF, 2);
        }
    }

    private static float StepSpeakAnim(string uid, bool speaking)
    {
        _peerSpeakAnim.TryGetValue(uid, out float cur);
        float target = speaking ? 1f : 0f;
        cur += (target - cur) * (speaking ? 0.42f : 0.16f);
        if (cur < 0.005f) { _peerSpeakAnim.Remove(uid); return 0f; }
        _peerSpeakAnim[uid] = cur;
        UI.Animate();
        return cur;
    }

    private static void DrawVoiceBars(float x, float cy, int count, float maxH, float level, float seed, uint color, float alpha = 1f)
    {
        if (alpha <= 0.02f) return;
        float t = Environment.TickCount64 * 0.012f;
        uint col = Gfx.Fade(color, alpha);
        for (int i = 0; i < count; i++)
        {
            float env = count <= 1 ? 1f : 1f - MathF.Abs(i - (count - 1) * 0.5f) / MathF.Max(1f, count * 0.65f);
            float wave = 0.52f + 0.48f * MathF.Sin(t + seed + i * 1.45f);
            float bh = MathF.Max(3f, maxH * Math.Clamp(level * env * wave, 0.22f, 1f));
            Gfx.FillRect(new R(x + i * 4f, cy - bh * 0.5f, 2.5f, bh), col, 1.25f);
        }
    }
}
