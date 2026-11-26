using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FluxCord;

public enum TrayVoiceState
{
    Idle = 0,
    VoiceConnected = 1,
    Speaking = 2,
    Muted = 3,
    Deafened = 4
}

public static unsafe class App
{
    public const int MaxWatchSlots = 4;
    public static nint Hwnd { get; private set; }
    public static readonly nint[] StreamHwnds = new nint[MaxWatchSlots];
    public static nint SelfStreamHwnd { get; private set; }
    public static nint StreamHwnd => StreamHwnds[0];
    public static bool DemoArg { get; private set; }
    public static bool BenchArg { get; private set; }
    public static bool ResumedUpdateArg { get; private set; }

    private static nint _chatEdit, _tokenEdit, _searchEdit, _editFont, _editBrush, _blackBrush;
    private static nint _origChatProc, _origTokenProc, _origSearchProc;
    private static bool _searchWasVisible;
    private static nint _arrow, _hand, _trayIcon;
    private static readonly nint[] _trayStateIcons = new nint[5];
    private static TrayVoiceState _currentTrayState = TrayVoiceState.Idle;
    private static string _currentTrayTip = "FluxCord P2P • Jetson Orin";
    private static bool _wakePending, _tracking, _inRender, _shuttingDown, _releasingCapture;
    private static int _lastMouseRawX = int.MinValue, _lastMouseRawY = int.MinValue;
    private static bool _trayAdded, _hiddenToTray, _wasZoomedBeforeHide, _forceExit;
    private static uint _wmTaskbarCreated, _wmActivateExisting;
    public const nuint ForceCloseWParam = 0xFC01;
    public const uint WM_TRAY = W.WM_APP + 3;
    private const uint WM_WAKE = W.WM_APP + 1;
    private const uint WM_UI_ACTION = W.WM_APP + 2;
    private const uint TrayIconId = 1;
    private static readonly ConcurrentQueue<Action> _uiActions = new();

    public static bool IsHiddenToTray => _hiddenToTray;

    public static void RequestExit()
    {
        _forceExit = true;
        if (Hwnd != 0) W.PostMessageW(Hwnd, W.WM_CLOSE, ForceCloseWParam, 0);
    }

    private static readonly (int X, int Y, int W, int H, bool Visible, bool HadCutout, int CutoutHash)[] _videoRects =
        new (int, int, int, int, bool, bool, int)[MaxWatchSlots + 1];
    private static readonly Dictionary<nint, (int X, int Y, int W, int H, bool Visible)> _childState = new();

    public static void WakeUi()
    {
        if (Hwnd == 0 || _shuttingDown) return;
        if (Interlocked.Exchange(ref _wakePending, true)) return;
        W.PostMessageW(Hwnd, WM_WAKE, 0, 0);
    }

    /// <summary>Marshals an action safely onto the Win32 UI thread OUTSIDE Direct2D BeginDraw..EndDraw!</summary>
    public static void InvokeOnUiThread(Action action)
    {
        if (Hwnd == 0 || _shuttingDown) return;
        _uiActions.Enqueue(action);
        W.PostMessageW(Hwnd, WM_UI_ACTION, 0, 0);
    }

    public static void Invalidate() { if (Hwnd != 0 && !_shuttingDown) W.InvalidateRect(Hwnd, null, 0); }

    [STAThread]
    private static int Main(string[] args)
    {
        DemoArg = args.Contains("--demo");
        BenchArg = args.Contains("--bench");
        ResumedUpdateArg = args.Contains("--resumed-update");
        SettingsStore.Load();
        Updater.InitOnStartup(ResumedUpdateArg);

        try { return Run(); }
        catch (Exception ex) { SettingsStore.Log("FATAL: " + ex); return 1; }
        finally { ShutdownAll(); }
    }

    private static void SaveWindowBoundsIfNormal()
    {
        if (Hwnd == 0 || !W.IsWindowVisible(Hwnd) || W.IsZoomed(Hwnd) || W.IsIconic(Hwnd) || Gfx.Scale <= 0.1f)
            return;
        W.RECT wr;
        if (W.GetWindowRect(Hwnd, &wr))
        {
            int lw = (int)MathF.Round((wr.R - wr.L) / Gfx.Scale);
            int lh = (int)MathF.Round((wr.B - wr.T) / Gfx.Scale);
            if (lw >= 860 && lh >= 560 && lw <= 3840 && lh <= 2160)
            {
                SettingsStore.Current.WindowWidth = lw;
                SettingsStore.Current.WindowHeight = lh;
                SettingsStore.Save();
            }
        }
    }

    private static void BuildDynamicTrayIcons()
    {
        try
        {
            const int sz = 32;
            string pngPath = Path.Combine(AppContext.BaseDirectory, "Assets", "fluxcord.png");
            byte[]? basePx = ImageCache.DecodeLocalIconBgra(pngPath, sz);
            if (basePx == null || basePx.Length != sz * sz * 4) return;

            for (int st = 0; st < 5; st++)
            {
                byte[] buf = new byte[basePx.Length];
                Buffer.BlockCopy(basePx, 0, buf, 0, buf.Length);

                // Anti-aliased Discord-style ring & bottom-right status badge!
                static void BlendPx(byte[] b, int x, int y, byte r, byte g, byte bl, float a)
                {
                    if ((uint)x >= sz || (uint)y >= sz || a <= 0.001f) return;
                    int o = (y * sz + x) * 4;
                    float da = b[o + 3] / 255f;
                    float outA = a + da * (1f - a);
                    if (outA <= 0.001f) return;
                    b[o] = (byte)Math.Clamp((bl * a + b[o] * da * (1f - a)) / outA, 0, 255);
                    b[o + 1] = (byte)Math.Clamp((g * a + b[o + 1] * da * (1f - a)) / outA, 0, 255);
                    b[o + 2] = (byte)Math.Clamp((r * a + b[o + 2] * da * (1f - a)) / outA, 0, 255);
                    b[o + 3] = (byte)Math.Clamp(outA * 255f, 0, 255);
                }
                static float Dist(float dx, float dy) => MathF.Sqrt(dx * dx + dy * dy);
                static void DrawCircle(byte[] b, float cx, float cy, float rad, byte r, byte g, byte bl, float maxA = 1f)
                {
                    for (int y = 0; y < sz; y++)
                        for (int x = 0; x < sz; x++)
                        {
                            float d = Dist(x + 0.5f - cx, y + 0.5f - cy) - rad;
                            if (d < 0.7f) BlendPx(b, x, y, r, g, bl, Math.Clamp(0.7f - d, 0f, 1f) * maxA);
                        }
                }
                static void DrawRing(byte[] b, float cx, float cy, float rOuter, float rInner, byte r, byte g, byte bl)
                {
                    for (int y = 0; y < sz; y++)
                        for (int x = 0; x < sz; x++)
                        {
                            float dist = Dist(x + 0.5f - cx, y + 0.5f - cy);
                            float aOut = Math.Clamp(rOuter + 0.6f - dist, 0f, 1f);
                            float aIn = Math.Clamp(dist - (rInner - 0.6f), 0f, 1f);
                            float a = aOut * aIn;
                            if (a > 0.01f) BlendPx(b, x, y, r, g, bl, a);
                        }
                }
                static void DrawSlash(byte[] b, float x0, float y0, float x1, float y1, float halfW, byte r, byte g, byte bl)
                {
                    float dx = x1 - x0, dy = y1 - y0;
                    float len2 = dx * dx + dy * dy;
                    for (int y = 0; y < sz; y++)
                        for (int x = 0; x < sz; x++)
                        {
                            float t = Math.Clamp(((x + 0.5f - x0) * dx + (y + 0.5f - y0) * dy) / len2, 0f, 1f);
                            float dist = Dist(x + 0.5f - (x0 + t * dx), y + 0.5f - (y0 + t * dy)) - halfW;
                            if (dist < 0.6f) BlendPx(b, x, y, r, g, bl, Math.Clamp(0.6f - dist, 0f, 1f));
                        }
                }

                if (st == (int)TrayVoiceState.Speaking)
                {
                    // Full glowing Discord neon-green ring around the entire tray icon + bright green voice dot!
                    DrawRing(buf, 16f, 16f, 15.8f, 12.6f, 0x23, 0xF5, 0x6E);
                    DrawCircle(buf, 24.5f, 24.5f, 7.2f, 0x10, 0x12, 0x18);
                    DrawCircle(buf, 24.5f, 24.5f, 5.6f, 0x23, 0xF5, 0x6E);
                    DrawCircle(buf, 24.5f, 24.5f, 2.2f, 0xFF, 0xFF, 0xFF);
                }
                else if (st == (int)TrayVoiceState.VoiceConnected)
                {
                    // Crisp Discord green voice-connected badge in bottom-right
                    DrawCircle(buf, 24.5f, 24.5f, 6.8f, 0x10, 0x12, 0x18);
                    DrawCircle(buf, 24.5f, 24.5f, 5.2f, 0x23, 0xA5, 0x5A);
                }
                else if (st == (int)TrayVoiceState.Muted)
                {
                    // Red muted badge with crisp white diagonal slash (exact Discord mute indicator!)
                    DrawCircle(buf, 24.0f, 24.0f, 7.5f, 0x10, 0x12, 0x18);
                    DrawCircle(buf, 24.0f, 24.0f, 6.0f, 0xED, 0x42, 0x45);
                    DrawSlash(buf, 20.5f, 20.5f, 27.5f, 27.5f, 1.15f, 0xFF, 0xFF, 0xFF);
                }
                else if (st == (int)TrayVoiceState.Deafened)
                {
                    // Red outer ring + red deafened badge with crisp white X
                    DrawRing(buf, 16f, 16f, 15.8f, 13.4f, 0xED, 0x42, 0x45);
                    DrawCircle(buf, 24.0f, 24.0f, 7.5f, 0x10, 0x12, 0x18);
                    DrawCircle(buf, 24.0f, 24.0f, 6.0f, 0xED, 0x42, 0x45);
                    DrawSlash(buf, 20.5f, 20.5f, 27.5f, 27.5f, 1.1f, 0xFF, 0xFF, 0xFF);
                    DrawSlash(buf, 27.5f, 20.5f, 20.5f, 27.5f, 1.1f, 0xFF, 0xFF, 0xFF);
                }

                _trayStateIcons[st] = W.CreateHIconFromBgra(buf, sz);
            }
        }
        catch { }
    }

    public static void UpdateTrayVoiceState(TrayVoiceState state, string? tip = null)
    {
        if (Hwnd == 0 || _shuttingDown || BenchArg) return;
        string wantTip = tip ?? state switch
        {
            TrayVoiceState.Speaking => "FluxCord P2P • Вы говорите 🎤",
            TrayVoiceState.VoiceConnected => "FluxCord P2P • В голосовом канале",
            TrayVoiceState.Muted => "FluxCord P2P • Микрофон выключен",
            TrayVoiceState.Deafened => "FluxCord P2P • Звук и микрофон выключены",
            _ => "FluxCord P2P • Jetson Orin"
        };
        if (state == _currentTrayState && wantTip == _currentTrayTip) return;
        _currentTrayState = state;
        _currentTrayTip = wantTip;
        if (_trayAdded) AddOrUpdateTrayIcon(modify: true);
    }

    private static void AddOrUpdateTrayIcon(bool modify = false)
    {
        if (Hwnd == 0 || _shuttingDown) return;
        nint stateIcon = _trayStateIcons[(int)_currentTrayState];
        nint useIcon = stateIcon != 0 ? stateIcon : (_trayIcon != 0 ? _trayIcon : W.LoadCursorW(0, 32512));
        var nid = new W.NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(W.NOTIFYICONDATAW),
            hWnd = Hwnd,
            uID = TrayIconId,
            uFlags = W.NIF_MESSAGE | W.NIF_ICON | W.NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon = useIcon
        };
        string tip = _currentTrayTip;
        for (int i = 0; i < tip.Length && i < 127; i++) nid.szTip[i] = tip[i];
        nid.szTip[Math.Min(tip.Length, 127)] = '\0';

        uint cmd = (modify && _trayAdded) ? W.NIM_MODIFY : W.NIM_ADD;
        if (W.Shell_NotifyIconW(cmd, &nid)) _trayAdded = true;
    }

    private static void RemoveTrayIcon()
    {
        if (!_trayAdded || Hwnd == 0) return;
        var nid = new W.NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(W.NOTIFYICONDATAW),
            hWnd = Hwnd,
            uID = TrayIconId
        };
        W.Shell_NotifyIconW(W.NIM_DELETE, &nid);
        _trayAdded = false;
    }

    public static void HideToTray()
    {
        if (Hwnd == 0 || _shuttingDown) return;
        SaveWindowBoundsIfNormal();
        _wasZoomedBeforeHide = W.IsZoomed(Hwnd);
        _hiddenToTray = true;
        _tracking = false;
        UI.CancelDrag();
        UI.MX = UI.MY = -1;
        W.KillTimer(Hwnd, 1);
        if (!_trayAdded) AddOrUpdateTrayIcon(false);
        W.ShowWindow(Hwnd, W.SW_HIDE);
    }

    public static void RestoreFromTray()
    {
        if (Hwnd == 0 || _shuttingDown) return;
        bool wasHidden = _hiddenToTray || !W.IsWindowVisible(Hwnd) || W.IsIconic(Hwnd);
        _hiddenToTray = false;
        if (wasHidden)
        {
            int cmd = _wasZoomedBeforeHide ? W.SW_MAXIMIZE : W.SW_SHOWNORMAL;
            W.ShowWindow(Hwnd, cmd);
        }
        W.SetForegroundWindow(Hwnd);
        Invalidate();
        Render();
    }

    private static void ShowTrayContextMenu()
    {
        if (Hwnd == 0 || _shuttingDown) return;
        nint menu = W.CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            W.AppendMenuW(menu, W.MF_STRING, 1, "Открыть FluxCord");
            W.SetMenuDefaultItem(menu, 1, 0);
            W.AppendMenuW(menu, W.MF_SEPARATOR, 0, null);
            W.AppendMenuW(menu, W.MF_STRING | (Shell.IsMuted ? W.MF_CHECKED : 0), 2, "Отключить микрофон (Ctrl+Shift+M)");
            W.AppendMenuW(menu, W.MF_STRING | (Shell.IsDeafened ? W.MF_CHECKED : 0), 3, "Отключить звук (Ctrl+Shift+D)");
            W.AppendMenuW(menu, W.MF_SEPARATOR, 0, null);
            W.AppendMenuW(menu, W.MF_STRING, 4, "Выйти из FluxCord");

            W.POINT pt;
            W.GetCursorPos(&pt);
            W.SetForegroundWindow(Hwnd);
            int picked = W.TrackPopupMenu(menu, W.TPM_RIGHTBUTTON | W.TPM_BOTTOMALIGN | W.TPM_NONOTIFY | W.TPM_RETURNCMD, pt.X, pt.Y, 0, Hwnd, 0);
            W.PostMessageW(Hwnd, 0, 0, 0);

            switch (picked)
            {
                case 1: RestoreFromTray(); break;
                case 2: Shell.ToggleMute(); break;
                case 3: Shell.ToggleDeafen(); break;
                case 4: RequestExit(); break;
            }
        }
        finally
        {
            W.DestroyMenu(menu);
        }
    }

    private static void ShutdownAll()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        try
        {
            if (Hwnd != 0)
            {
                W.KillTimer(Hwnd, 1);
                W.KillTimer(Hwnd, 2);
                W.KillTimer(Hwnd, 99);
                SaveWindowBoundsIfNormal();
                RemoveTrayIcon();
                for (int i = 0; i < _trayStateIcons.Length; i++)
                {
                    if (_trayStateIcons[i] != 0) { W.DestroyIcon(_trayStateIcons[i]); _trayStateIcons[i] = 0; }
                }
            }
        }
        catch { }
        try { Shell.StopStream(); } catch { }
        try { RemoteAuthQr.Instance.Stop(); } catch { }
        try { DiscordClient.Instance.Disconnect(); } catch { }
        try { MediaNative.fc_shutdown(); } catch { }
        try { Gfx.DisposeDevice(); } catch { }
        try { LocalCache.Close(); } catch { }
    }

    private static int Run()
    {
        _wmTaskbarCreated = W.RegisterWindowMessageW("TaskbarCreated");
        _wmActivateExisting = W.RegisterWindowMessageW("FluxCordP2P_ActivateExisting");

        // Single-instance behavior like official Discord: clicking shortcut while hidden in tray pops up existing window!
        if (!DemoArg && !BenchArg && !ResumedUpdateArg &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLUXCORD_DATA_DIR")))
        {
            nint existing = W.FindWindowW("FluxCordP2PMain", null);
            if (existing != 0 && _wmActivateExisting != 0)
            {
                W.PostMessageW(existing, _wmActivateExisting, 0, 0);
                return 0;
            }
        }

        W.SetPreferredAppMode(2);
        var inst = W.GetModuleHandleW(0);
        _arrow = W.LoadCursorW(0, 32512);
        _hand = W.LoadCursorW(0, 32649);
        nint iconBig = W.LoadImageW(inst, 32512, W.IMAGE_ICON, W.GetSystemMetrics(W.SM_CXICON), W.GetSystemMetrics(W.SM_CYICON), 0);
        nint iconSm = W.LoadImageW(inst, 32512, W.IMAGE_ICON, W.GetSystemMetrics(W.SM_CXSMICON), W.GetSystemMetrics(W.SM_CYSMICON), 0);
        _trayIcon = iconSm != 0 ? iconSm : iconBig;

        fixed (char* cls = "FluxCordP2PMain")
        fixed (char* scls = "FluxCordP2PStreamHost")
        {
            var wc = new W.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(W.WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = inst,
                hIcon = iconBig,
                hIconSm = iconSm,
                hCursor = _arrow,
                lpszClassName = cls
            };
            W.RegisterClassExW(&wc);

            _blackBrush = W.CreateSolidBrush(0);
            var sc = new W.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(W.WNDCLASSEXW),
                lpfnWndProc = &StreamProc,
                hInstance = inst,
                hCursor = _hand,
                hbrBackground = _blackBrush,
                lpszClassName = scls
            };
            W.RegisterClassExW(&sc);
        }

        var s = SettingsStore.Current;
        var sys = W.GetDpiForSystem() / 96f;
        Hwnd = W.CreateWindowExW(0, "FluxCordP2PMain", "FluxCord P2P • Jetson Orin", W.WS_OVERLAPPEDWINDOW | W.WS_CLIPCHILDREN,
            unchecked((int)0x80000000), unchecked((int)0x80000000),
            (int)(s.WindowWidth * sys), (int)(s.WindowHeight * sys), 0, 0, inst, 0);

        int dark = 1;
        W.DwmSetWindowAttribute(Hwnd, 20, &dark, 4);
        int corner = 2; // DWMWCP_ROUND (native Windows 11 rounded corners & smooth Aero Snap)
        W.DwmSetWindowAttribute(Hwnd, 33, &corner, 4);
        uint border = W.Rgb(0x28, 0x2B, 0x36);
        W.DwmSetWindowAttribute(Hwnd, 34, &border, 4);
        var margins = new W.MARGINS { L = 0, R = 0, T = 0, B = 1 };
        W.DwmExtendFrameIntoClientArea(Hwnd, &margins);
        if (iconBig != 0) W.SendMessageW(Hwnd, W.WM_SETICON, 1, iconBig);
        if (iconSm != 0) W.SendMessageW(Hwnd, W.WM_SETICON, 0, iconSm);
        W.DragAcceptFiles(Hwnd, true);
        W.SetWindowPos(Hwnd, 0, 0, 0, 0, 0, W.SWP_NOMOVE | W.SWP_NOSIZE | W.SWP_NOZORDER | W.SWP_NOACTIVATE | W.SWP_FRAMECHANGED);

        for (int i = 0; i < MaxWatchSlots; i++)
            StreamHwnds[i] = W.CreateWindowExW(0, "FluxCordP2PStreamHost", null, W.WS_CHILD | W.WS_CLIPSIBLINGS, 0, 0, 16, 9, Hwnd, 0, inst, 0);
        SelfStreamHwnd = W.CreateWindowExW(0, "FluxCordP2PStreamHost", null, W.WS_CHILD | W.WS_CLIPSIBLINGS, 0, 0, 16, 9, Hwnd, 0, inst, 0);

        _editFont = W.CreateFontW(-(int)(15 * sys), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _editBrush = W.CreateSolidBrush(W.Rgb(0x17, 0x19, 0x20));

        _chatEdit = W.CreateWindowExW(0, "EDIT", "", W.WS_CHILD | W.ES_AUTOHSCROLL, 0, 0, 100, 30, Hwnd, 0, inst, 0);
        W.SendMessageW(_chatEdit, W.WM_SETFONT, (nuint)_editFont, 1);
        _origChatProc = W.SetWindowLongPtrW(_chatEdit, W.GWLP_WNDPROC, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&ChatEditProc);

        _tokenEdit = W.CreateWindowExW(0, "EDIT", "", W.WS_CHILD | W.ES_AUTOHSCROLL | W.ES_PASSWORD, 0, 0, 100, 30, Hwnd, 0, inst, 0);
        W.SendMessageW(_tokenEdit, W.WM_SETFONT, (nuint)_editFont, 1);
        _origTokenProc = W.SetWindowLongPtrW(_tokenEdit, W.GWLP_WNDPROC, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&TokenEditProc);

        _searchEdit = W.CreateWindowExW(0, "EDIT", "", W.WS_CHILD | W.ES_AUTOHSCROLL, 0, 0, 100, 28, Hwnd, 0, inst, 0);
        W.SendMessageW(_searchEdit, W.WM_SETFONT, (nuint)_editFont, 1);
        _origSearchProc = W.SetWindowLongPtrW(_searchEdit, W.GWLP_WNDPROC, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&SearchEditProc);

        Gfx.Init(Hwnd);
        Shell.Init();
        RebuildEditFont();
        if (!BenchArg)
        {
            BuildDynamicTrayIcons();
            AddOrUpdateTrayIcon(false);
        }

        W.ShowWindow(Hwnd, W.SW_SHOWNORMAL);

        // Fast 60ms background poll timer (drives real-time Tray Speaking indicator & voice rings even when minimized!)
        W.SetTimer(Hwnd, 2, 60, 0);
        if (BenchArg) W.SetTimer(Hwnd, 99, 2500, 0);

        W.MSG msg;
        while (W.GetMessageW(&msg, 0, 0, 0) > 0)
        {
            W.TranslateMessage(&msg);
            W.DispatchMessageW(&msg);
        }

        ShutdownAll();
        return 0;
    }

    private static void RebuildEditFont()
    {
        if (Gfx.Scale <= 0.1f) return;
        nint newFont = W.CreateFontW(-(int)(15 * Gfx.Scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        if (newFont == 0) return;
        if (_chatEdit != 0) W.SendMessageW(_chatEdit, W.WM_SETFONT, (nuint)newFont, 1);
        if (_tokenEdit != 0) W.SendMessageW(_tokenEdit, W.WM_SETFONT, (nuint)newFont, 1);
        if (_searchEdit != 0) W.SendMessageW(_searchEdit, W.WM_SETFONT, (nuint)newFont, 1);
        if (_editFont != 0) W.DeleteObject(_editFont);
        _editFont = newFont;
    }

    private static string ReadEdit(nint h)
    {
        if (h == 0) return "";
        int len = W.GetWindowTextLengthW(h);
        if (len <= 0) return "";
        var buf = new char[len + 1];
        fixed (char* p = buf) W.GetWindowTextW(h, p, len + 1);
        return new string(buf, 0, len);
    }

    public static string PeekChatInputText() => ReadEdit(_chatEdit);

    public static string TakeChatInputText()
    {
        string s = ReadEdit(_chatEdit);
        if (_chatEdit != 0) W.SetWindowTextW(_chatEdit, "");
        return s;
    }

    public static void SetChatInputText(string text)
    {
        if (_chatEdit == 0) return;
        W.SetWindowTextW(_chatEdit, text);
        W.SetFocus(_chatEdit);
        W.SendMessageW(_chatEdit, 0x00B1 /*EM_SETSEL*/, (nuint)text.Length, text.Length);
    }

    public static void AppendChatInputText(string suffix)
    {
        string cur = ReadEdit(_chatEdit);
        SetChatInputText(cur + suffix);
    }

    public static string GetTokenInputText() => ReadEdit(_tokenEdit);
    public static void ClearTokenInput() { if (_tokenEdit != 0) W.SetWindowTextW(_tokenEdit, ""); }
    public static string GetSearchInputText() => ReadEdit(_searchEdit);
    public static void SetSearchInputText(string text)
    {
        if (_searchEdit == 0) return;
        W.SetWindowTextW(_searchEdit, text);
    }

    public static void SyncNativeChildren(
        bool showChat, R chatR,
        bool showToken, R tokenR,
        bool showSearch, R searchR,
        ReadOnlySpan<(bool Show, R Rect)> watchSlots,
        bool showSelf, R selfR,
        ReadOnlySpan<R> overlayCutouts)
    {
        float sc = Gfx.Scale;
        SyncChild(_chatEdit, showChat, chatR.Inset(6, 9), sc);
        SyncChild(_tokenEdit, showToken, tokenR.Inset(10, 9), sc);
        SyncChild(_searchEdit, showSearch, searchR.Inset(8, 6), sc);
        if (showSearch && !_searchWasVisible) W.SetFocus(_searchEdit);
        _searchWasVisible = showSearch;

        for (int i = 0; i < MaxWatchSlots; i++)
        {
            bool sh = i < watchSlots.Length && watchSlots[i].Show;
            R sr = sh ? watchSlots[i].Rect : default;
            SyncVideoHost(i, StreamHwnds[i], sh, sr, sc, overlayCutouts, isSelf: false);
        }
        SyncVideoHost(MaxWatchSlots, SelfStreamHwnd, showSelf, selfR, sc, overlayCutouts, isSelf: true);
    }

    private static void SyncVideoHost(int idx, nint hwnd, bool show, R r, float sc, ReadOnlySpan<R> cutouts, bool isSelf)
    {
        if (hwnd == 0) return;
        ref var prev = ref _videoRects[idx];
        if (!show || r.W < 16 || r.H < 16)
        {
            if (prev.Visible)
            {
                if (prev.HadCutout)
                {
                    W.SetWindowRgn(hwnd, 0, false);
                    prev.HadCutout = false;
                    prev.CutoutHash = 0;
                }
                W.ShowWindow(hwnd, W.SW_HIDE);
                prev.Visible = false;
            }
            return;
        }

        int px = (int)MathF.Round(r.X * sc);
        int py = (int)MathF.Round(r.Y * sc);
        int pw = Math.Max(16, (int)MathF.Round(r.W * sc));
        int ph = Math.Max(16, (int)MathF.Round(r.H * sc));

        bool movedOrResized = !prev.Visible || prev.X != px || prev.Y != py || prev.W != pw || prev.H != ph;
        if (movedOrResized)
        {
            W.SetWindowPos(hwnd, 0, px, py, pw, ph, W.SWP_NOZORDER | W.SWP_NOACTIVATE | W.SWP_SHOWWINDOW);
            if (isSelf) MediaNative.fc_screen_resize_preview(pw, ph);
            else MediaNative.fc_watch_resize_slot(idx, pw, ph);
            prev.X = px; prev.Y = py; prev.W = pw; prev.H = ph; prev.Visible = true;
            prev.CutoutHash = -1; // force cutout re-eval on resize
        }

        // Compute deterministic hash of intersecting cutouts so we NEVER churn GDI SetWindowRgn on unchanged frames!
        int hash = 17;
        bool hasAnyCutout = false;
        for (int c = 0; c < cutouts.Length; c++)
        {
            var cr = cutouts[c];
            int cx1 = (int)MathF.Floor(cr.X * sc) - px;
            int cy1 = (int)MathF.Floor(cr.Y * sc) - py;
            int cx2 = (int)MathF.Ceiling(cr.Right * sc) - px;
            int cy2 = (int)MathF.Ceiling(cr.Bottom * sc) - py;
            if (cx2 <= 0 || cy2 <= 0 || cx1 >= pw || cy1 >= ph) continue;
            hasAnyCutout = true;
            hash = HashCode.Combine(hash, Math.Max(0, cx1), Math.Max(0, cy1), Math.Min(pw, cx2), Math.Min(ph, cy2));
        }
        if (!hasAnyCutout) hash = 0;
        if (hash == prev.CutoutHash && hasAnyCutout == prev.HadCutout) return;

        if (hasAnyCutout)
        {
            nint rgn = W.CreateRectRgn(0, 0, pw, ph);
            for (int c = 0; c < cutouts.Length; c++)
            {
                var cr = cutouts[c];
                int cx1 = (int)MathF.Floor(cr.X * sc) - px;
                int cy1 = (int)MathF.Floor(cr.Y * sc) - py;
                int cx2 = (int)MathF.Ceiling(cr.Right * sc) - px;
                int cy2 = (int)MathF.Ceiling(cr.Bottom * sc) - py;
                if (cx2 <= 0 || cy2 <= 0 || cx1 >= pw || cy1 >= ph) continue;
                nint hole = W.CreateRectRgn(Math.Max(0, cx1), Math.Max(0, cy1), Math.Min(pw, cx2), Math.Min(ph, cy2));
                W.CombineRgn(rgn, rgn, hole, 4); // RGN_DIFF
                W.DeleteObject(hole);
            }
            if (W.SetWindowRgn(hwnd, rgn, true) == 0) W.DeleteObject(rgn);
            prev.HadCutout = true;
            prev.CutoutHash = hash;
        }
        else if (prev.HadCutout)
        {
            W.SetWindowRgn(hwnd, 0, true);
            prev.HadCutout = false;
            prev.CutoutHash = 0;
        }
    }

    private static void SyncChild(nint h, bool show, R r, float sc)
    {
        if (h == 0) return;
        _childState.TryGetValue(h, out var prev);
        if (!show)
        {
            if (prev.Visible)
            {
                W.ShowWindow(h, W.SW_HIDE);
                _childState[h] = (prev.X, prev.Y, prev.W, prev.H, false);
            }
            return;
        }
        int x = (int)(r.X * sc), y = (int)(r.Y * sc);
        int w = Math.Max(10, (int)(r.W * sc)), ch = Math.Max(16, (int)(r.H * sc));
        if (!prev.Visible || prev.X != x || prev.Y != y || prev.W != w || prev.H != ch)
        {
            W.SetWindowPos(h, 0, x, y, w, ch, W.SWP_NOZORDER | W.SWP_NOACTIVATE | W.SWP_SHOWWINDOW);
            _childState[h] = (x, y, w, ch, true);
        }
    }

    private static void Render()
    {
        if (_inRender || _shuttingDown || _hiddenToTray || W.IsIconic(Hwnd) || Gfx.DC == null) return;
        _inRender = true;
        bool beganDraw = false;
        try
        {
            UI.BeginFrame();
            Gfx.BeginDraw(Th.Bg);
            beganDraw = true;
            try
            {
                Shell.Draw();
            }
            finally
            {
                if (beganDraw)
                {
                    Gfx.UnwindClips();
                    Gfx.EndDraw();
                }
                UI.EndFrame();
            }

            if (UI.TakeAnimate()) W.SetTimer(Hwnd, 1, 16, 0);
            else W.KillTimer(Hwnd, 1);
        }
        catch (Exception ex)
        {
            SettingsStore.Log("Render error: " + ex.Message);
        }
        finally
        {
            _inRender = false;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint StreamProc(nint hwnd, uint msg, nuint w, nint l)
    {
        try
        {
            if (msg is W.WM_MOUSEMOVE or W.WM_LBUTTONDOWN or W.WM_LBUTTONUP or W.WM_RBUTTONUP)
            {
                int idx = -1;
                if (hwnd == SelfStreamHwnd) idx = MaxWatchSlots;
                else
                {
                    for (int i = 0; i < MaxWatchSlots; i++)
                        if (StreamHwnds[i] == hwnd) { idx = i; break; }
                }
                if (idx >= 0)
                {
                    int px = W.LoWord(l) + _videoRects[idx].X;
                    int py = W.HiWord(l) + _videoRects[idx].Y;
                    nint mapped = (nint)(((py & 0xFFFF) << 16) | (px & 0xFFFF));
                    W.PostMessageW(Hwnd, msg, w, mapped);
                    return 0;
                }
            }
            if (msg == W.WM_MOUSEWHEEL)
            {
                W.PostMessageW(Hwnd, W.WM_MOUSEWHEEL, w, l);
                return 0;
            }
        }
        catch (Exception ex) { SettingsStore.Log("StreamProc error: " + ex.Message); }
        return W.DefWindowProcW(hwnd, msg, w, l);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint ChatEditProc(nint hwnd, uint msg, nuint w, nint l)
    {
        try
        {
            if (msg == W.WM_MOUSEWHEEL)
            {
                W.PostMessageW(Hwnd, W.WM_MOUSEWHEEL, w, l);
                return 0;
            }
            if (msg == W.WM_KEYDOWN && (int)w == W.VK_A && W.KeyDown(W.VK_CONTROL))
            {
                W.SendMessageW(hwnd, 0x00B1 /*EM_SETSEL*/, 0, -1);
                return 0;
            }
            if (msg == W.WM_KEYDOWN && (int)w == W.VK_K && W.KeyDown(W.VK_CONTROL))
            {
                Shell.ToggleQuickSwitch();
                return 0;
            }
            if (msg == W.WM_KEYDOWN && (int)w == W.VK_F && W.KeyDown(W.VK_CONTROL))
            {
                Shell.ToggleChannelSearch();
                return 0;
            }
            if (msg == W.WM_KEYDOWN && (int)w == W.VK_V && W.KeyDown(W.VK_CONTROL))
            {
                // Allow pasting screenshots & browser "Copy Image" (CF_DIB) seamlessly (ignore key auto-repeat bit 30!)
                if (W.IsClipboardFormatAvailable(8) && !W.IsClipboardFormatAvailable(13))
                {
                    if ((l.ToInt64() & (1L << 30)) == 0)
                        Shell.OnClipboardImagePaste();
                    return 0;
                }
            }
            if (msg == W.WM_KEYDOWN && (int)w == W.VK_Q && W.KeyDown(W.VK_CONTROL))
            {
                RequestExit();
                return 0;
            }
            if (msg == W.WM_KEYDOWN && (int)w == W.VK_ESCAPE)
            {
                Shell.OnEscape();
                return 0;
            }
            if (msg == W.WM_KEYDOWN && (int)w == W.VK_M && W.KeyDown(W.VK_CONTROL) && W.KeyDown(W.VK_SHIFT))
            {
                Shell.ToggleMute();
                return 0;
            }
            if (msg == W.WM_KEYDOWN && (int)w == W.VK_D && W.KeyDown(W.VK_CONTROL) && W.KeyDown(W.VK_SHIFT))
            {
                Shell.ToggleDeafen();
                return 0;
            }
            if (msg == W.WM_KEYDOWN && (int)w == W.VK_RETURN && !W.KeyDown(W.VK_SHIFT))
            {
                Shell.OnChatEnter();
                return 0;
            }
            // Suppress Windows error beep on Enter/Esc/Ctrl+A in single-line EDIT control
            if (msg == W.WM_CHAR && ((int)w == 13 || (int)w == 27 || (int)w == 1))
                return 0;
            if (msg == W.WM_CHAR && (int)w >= 32)
            {
                Shell.OnChatTyping();
            }
        }
        catch (Exception ex) { SettingsStore.Log("ChatEditProc error: " + ex.Message); }
        return W.CallWindowProcW(_origChatProc, hwnd, msg, w, l);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint SearchEditProc(nint hwnd, uint msg, nuint w, nint l)
    {
        try
        {
            if (msg == W.WM_KEYDOWN && (int)w == W.VK_A && W.KeyDown(W.VK_CONTROL))
            {
                W.SendMessageW(hwnd, 0x00B1, 0, -1);
                return 0;
            }
            if (msg == W.WM_KEYDOWN && (int)w == W.VK_UP)
            {
                Shell.StepQuickSwitch(-1);
                return 0;
            }
            if (msg == W.WM_KEYDOWN && (int)w == W.VK_DOWN)
            {
                Shell.StepQuickSwitch(1);
                return 0;
            }
            if (msg == W.WM_KEYDOWN && (int)w == W.VK_Q && W.KeyDown(W.VK_CONTROL))
            {
                RequestExit();
                return 0;
            }
            if (msg == W.WM_KEYDOWN && (int)w == W.VK_ESCAPE)
            {
                Shell.OnEscape();
                return 0;
            }
            if (msg == W.WM_KEYDOWN && (int)w == W.VK_RETURN)
            {
                Shell.OnSearchEnter(GetSearchInputText());
                return 0;
            }
            if (msg == W.WM_KEYDOWN && (int)w == W.VK_K && W.KeyDown(W.VK_CONTROL))
            {
                Shell.ToggleQuickSwitch();
                return 0;
            }
            if (msg == W.WM_CHAR && ((int)w == 13 || (int)w == 27 || (int)w == 1))
                return 0;
        }
        catch (Exception ex) { SettingsStore.Log("SearchEditProc error: " + ex.Message); }
        return W.CallWindowProcW(_origSearchProc, hwnd, msg, w, l);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint TokenEditProc(nint hwnd, uint msg, nuint w, nint l)
    {
        try
        {
            if (msg == W.WM_KEYDOWN && (int)w == W.VK_A && W.KeyDown(W.VK_CONTROL))
            {
                W.SendMessageW(hwnd, 0x00B1, 0, -1);
                return 0;
            }
            if (msg == W.WM_KEYDOWN && (int)w == W.VK_RETURN)
            {
                Shell.OnTokenSubmit(GetTokenInputText());
                return 0;
            }
            if (msg == W.WM_CHAR && ((int)w == 13 || (int)w == 27 || (int)w == 1))
                return 0;
        }
        catch (Exception ex) { SettingsStore.Log("TokenEditProc error: " + ex.Message); }
        return W.CallWindowProcW(_origTokenProc, hwnd, msg, w, l);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nuint w, nint l)
    {
        try
        {
            if (msg != 0 && msg == _wmTaskbarCreated)
            {
                _trayAdded = false;
                if (!BenchArg) AddOrUpdateTrayIcon(false);
                return 0;
            }
            if (msg != 0 && msg == _wmActivateExisting)
            {
                RestoreFromTray();
                return 0;
            }

            switch (msg)
            {
                case W.WM_NCCALCSIZE when w != 0:
                {
                    // When maximized, inset client rect by exact per-monitor DPI OS border frame so top bar & close button NEVER clip!
                    if (W.IsZoomed(hwnd))
                    {
                        var ncp = (W.NCCALCSIZE_PARAMS*)l;
                        uint dpi = W.GetDpiForWindow(hwnd);
                        if (dpi == 0) dpi = 96;
                        int pad = W.GetSystemMetricsForDpi(W.SM_CXPADDEDBORDER, dpi);
                        int fx = W.GetSystemMetricsForDpi(W.SM_CXSIZEFRAME, dpi) + pad;
                        int fy = W.GetSystemMetricsForDpi(W.SM_CYSIZEFRAME, dpi) + pad;
                        ncp->r0.L += fx;
                        ncp->r0.T += fy;
                        ncp->r0.R -= fx;
                        ncp->r0.B -= fy;
                    }
                    return 0;
                }
                case W.WM_NCHITTEST:
                {
                    W.RECT wr;
                    W.GetWindowRect(hwnd, &wr);
                    int sx = W.LoWord(l), sy = W.HiWord(l);
                    uint dpi = W.GetDpiForWindow(hwnd);
                    if (dpi == 0) dpi = 96;
                    bool zoomed = W.IsZoomed(hwnd);
                    int padX = zoomed ? W.GetSystemMetricsForDpi(W.SM_CXSIZEFRAME, dpi) + W.GetSystemMetricsForDpi(W.SM_CXPADDEDBORDER, dpi) : 0;
                    int padY = zoomed ? W.GetSystemMetricsForDpi(W.SM_CYSIZEFRAME, dpi) + W.GetSystemMetricsForDpi(W.SM_CXPADDEDBORDER, dpi) : 0;
                    float cy = (sy - (wr.T + padY)) / MathF.Max(0.1f, Gfx.Scale);
                    float cx = (sx - (wr.L + padX)) / MathF.Max(0.1f, Gfx.Scale);
                    float rightZone = Shell.TopBarRightZoneWidth;
                    // Give top-right caption buttons (-, □, ✕, Update pill) client hit-test priority even at the very corner pixel!
                    if (cy >= 0 && cy < Shell.TopH && cx >= Gfx.W - rightZone) return 1;
                    if (!zoomed)
                    {
                        int b = Math.Max(6, (int)MathF.Round(6f * dpi / 96f));
                        bool left = sx < wr.L + b, right = sx >= wr.R - b, top = sy < wr.T + b, bot = sy >= wr.B - b;
                        if (top && left) return 13;
                        if (top && right) return 14;
                        if (bot && left) return 16;
                        if (bot && right) return 17;
                        if (left) return 10;
                        if (right) return 11;
                        if (top) return 12;
                        if (bot) return 15;
                    }
                    if (cy < Shell.TopH && cx < Gfx.W - rightZone) return 2;
                    return 1;
                }
                case W.WM_DPICHANGED when !_shuttingDown:
                {
                    Gfx.RefreshDpi();
                    RebuildEditFont();
                    var nr = (W.RECT*)l;
                    W.SetWindowPos(hwnd, 0, nr->L, nr->T, nr->R - nr->L, nr->B - nr->T, W.SWP_NOZORDER | W.SWP_NOACTIVATE);
                    Render();
                    return 0;
                }
                case W.WM_ERASEBKGND:
                    return 1;
                case W.WM_CTLCOLOREDIT:
                    W.SetTextColor((nint)w, W.Rgb(0xF3, 0xF4, 0xF6));
                    W.SetBkColor((nint)w, W.Rgb(0x17, 0x19, 0x20));
                    return _editBrush;
                case W.WM_COMMAND:
                    if (l == _searchEdit && (((uint)w >> 16) & 0xFFFF) == W.EN_CHANGE)
                    {
                        Shell.OnSearchChanged(GetSearchInputText());
                        return 0;
                    }
                    break;
                case W.WM_DROPFILES:
                    Shell.OnFilesDropped(W.ReadDroppedFiles((nint)w));
                    return 0;
                case W.WM_SIZE when !_shuttingDown && Gfx.DC != null:
                    if ((int)w == 1 /*SIZE_MINIMIZED*/ || W.IsIconic(hwnd))
                    {
                        W.KillTimer(hwnd, 1);
                        return 0;
                    }
                    Gfx.Resize(W.LoWord(l), W.HiWord(l));
                    Render();
                    W.ValidateRect(hwnd, null);
                    return 0;
                case W.WM_SIZING:
                    Gfx.IsLiveResizing = true;
                    break;
                case W.WM_ENTERSIZEMOVE:
                    W.SetTimer(hwnd, 3, 16, 0);
                    break;
                case W.WM_EXITSIZEMOVE:
                    W.KillTimer(hwnd, 3);
                    Gfx.IsLiveResizing = false;
                    Invalidate();
                    break;
                case W.WM_PAINT:
                    W.ValidateRect(hwnd, null);
                    if (Gfx.DC != null) Render();
                    return 0;
                case WM_WAKE:
                    _wakePending = false;
                    Render();
                    return 0;
                case WM_UI_ACTION:
                    while (_uiActions.TryDequeue(out var act))
                    {
                        try { act(); } catch (Exception ex) { SettingsStore.Log("UI action error: " + ex.Message); }
                    }
                    Render();
                    return 0;
                case W.WM_TIMER:
                    if (w == 99)
                    {
                        using var p = System.Diagnostics.Process.GetCurrentProcess();
                        long ramMb = p.WorkingSet64 / (1024 * 1024);
                        SettingsStore.Log($"BENCH_OK RAM={ramMb}MB FlushMs={Gfx.LastFlushMs:F2}");
                        RequestExit();
                        return 0;
                    }
                    if (w == 2)
                    {
                        if (Shell.PollBackgroundTick() && !_hiddenToTray && !W.IsIconic(hwnd)) Render();
                        return 0;
                    }
                    Render();
                    return 0;
                case WM_TRAY:
                {
                    uint ev = (uint)(l.ToInt64() & 0xFFFF);
                    if (ev is W.WM_LBUTTONUP or W.WM_LBUTTONDBLCLK)
                        RestoreFromTray();
                    else if (ev is W.WM_RBUTTONUP or W.WM_CONTEXTMENU)
                        ShowTrayContextMenu();
                    return 0;
                }
                case W.WM_MOUSEMOVE:
                {
                    int rx = W.LoWord(l), ry = W.HiWord(l);
                    if (rx == _lastMouseRawX && ry == _lastMouseRawY && _tracking) return 0;
                    _lastMouseRawX = rx; _lastMouseRawY = ry;
                    if (!_tracking)
                    {
                        var tme = new W.TRACKMOUSEEVENT { cbSize = (uint)sizeof(W.TRACKMOUSEEVENT), dwFlags = 2, hwndTrack = hwnd };
                        W.TrackMouseEvent(&tme);
                        _tracking = true;
                    }
                    UI.MX = rx / Gfx.Scale;
                    UI.MY = ry / Gfx.Scale;
                    Invalidate();
                    return 0;
                }
                case W.WM_MOUSELEAVE:
                    _tracking = false;
                    _lastMouseRawX = _lastMouseRawY = int.MinValue;
                    if (!UI.Down) UI.MX = UI.MY = -1;
                    Invalidate();
                    return 0;
                case W.WM_LBUTTONDOWN:
                    W.SetCapture(hwnd);
                    UI.MX = W.LoWord(l) / Gfx.Scale;
                    UI.MY = W.HiWord(l) / Gfx.Scale;
                    UI.Down = UI.Pressed = true;
                    Render();
                    return 0;
                case W.WM_LBUTTONUP:
                    UI.MX = W.LoWord(l) / Gfx.Scale;
                    UI.MY = W.HiWord(l) / Gfx.Scale;
                    UI.Down = false;
                    UI.Released = true;
                    Render();
                    _releasingCapture = true;
                    W.ReleaseCapture();
                    _releasingCapture = false;
                    return 0;
                case W.WM_CAPTURECHANGED:
                    if (!_releasingCapture)
                    {
                        UI.CancelDrag();
                        Render();
                    }
                    return 0;
                case W.WM_RBUTTONUP:
                    UI.MX = W.LoWord(l) / Gfx.Scale;
                    UI.MY = W.HiWord(l) / Gfx.Scale;
                    UI.RightClicked = true;
                    Render();
                    return 0;
                case W.WM_MOUSEWHEEL:
                    UI.Wheel = W.HiWord((nint)w) / 120f;
                    Render();
                    return 0;
                case W.WM_SETCURSOR when (l.ToInt64() & 0xFFFF) == 1:
                    W.SetCursor(UI.WantHand ? _hand : _arrow);
                    return 1;
                case W.WM_GETMINMAXINFO:
                {
                    var mmi = (W.MINMAXINFO*)l;
                    uint dpi = hwnd != 0 ? W.GetDpiForWindow(hwnd) : W.GetDpiForSystem();
                    if (dpi == 0) dpi = 96;
                    float sc = (dpi / 96f) * Math.Clamp(SettingsStore.Current.UiScale, 0.8f, 1.5f);
                    mmi->minTrackSize.X = (int)MathF.Round(860f * sc);
                    mmi->minTrackSize.Y = (int)MathF.Round(560f * sc);
                    return 0;
                }
                case W.WM_KEYDOWN when (int)w == W.VK_K && W.KeyDown(W.VK_CONTROL):
                    Shell.ToggleQuickSwitch();
                    return 0;
                case W.WM_KEYDOWN when (int)w == W.VK_F && W.KeyDown(W.VK_CONTROL):
                    Shell.ToggleChannelSearch();
                    return 0;
                case W.WM_KEYDOWN when (int)w == W.VK_M && W.KeyDown(W.VK_CONTROL) && W.KeyDown(W.VK_SHIFT):
                    Shell.ToggleMute();
                    return 0;
                case W.WM_KEYDOWN when (int)w == W.VK_D && W.KeyDown(W.VK_CONTROL) && W.KeyDown(W.VK_SHIFT):
                    Shell.ToggleDeafen();
                    return 0;
                case W.WM_KEYDOWN when ((int)w == W.VK_UP || (int)w == W.VK_DOWN) && W.KeyDown(W.VK_MENU):
                    Shell.StepChannel((int)w == W.VK_UP ? -1 : 1);
                    return 0;
                case W.WM_KEYDOWN when (int)w == W.VK_Q && W.KeyDown(W.VK_CONTROL):
                    RequestExit();
                    return 0;
                case W.WM_KEYDOWN when (int)w == W.VK_ESCAPE:
                    Shell.OnEscape();
                    return 0;
                case W.WM_QUERYENDSESSION:
                    return 1;
                case W.WM_ENDSESSION:
                    if (w != 0)
                    {
                        _forceExit = true;
                        ShutdownAll();
                    }
                    return 0;
                case W.WM_CLOSE:
                {
                    bool forceQuit = _forceExit || w == ForceCloseWParam || BenchArg || !SettingsStore.Current.CloseToTray;
                    if (!forceQuit)
                    {
                        HideToTray();
                        return 0;
                    }
                    // Cleanly stop media, tray icon, Discord sessions, and D2D BEFORE destroying child video HWNDs!
                    ShutdownAll();
                    W.DestroyWindow(hwnd);
                    return 0;
                }
                case W.WM_DESTROY:
                    W.PostQuitMessage(0);
                    return 0;
            }
        }
        catch (Exception ex) { SettingsStore.Log("WndProc error: " + ex.Message); }
        return W.DefWindowProcW(hwnd, msg, w, l);
    }
}
