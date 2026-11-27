using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FluxCord;

/// <summary>
/// Discord Overlay v3-style lightweight click-through TopMost Game Overlay.
/// Zero DLL injection -> 0% anti-cheat risk in Valorant, CS2, Faceit, EAC, BattlEye.
/// Uses Win32 WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE + UpdateLayeredWindow.
/// </summary>
public static unsafe class GameOverlay
{
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    private const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;
    private const uint ULW_ALPHA = 2;

    private static nint _hwnd;
    private static bool _classRegistered;
    private static bool _visible;
    private static string _lastHash = "";
    private static string _sbBanner = "";
    private static long _sbBannerUntil;

    public static void ShowSoundboardBanner(string author, string label)
    {
        _sbBanner = $"🎺 {author}: {label}";
        _sbBannerUntil = Environment.TickCount64 + 3500;
    }

    public static void Destroy()
    {
        if (_hwnd != 0)
        {
            W.DestroyWindow(_hwnd);
            _hwnd = 0;
            _visible = false;
            _lastHash = "";
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint OverlayProc(nint h, uint m, nuint w, nint l) => W.DefWindowProcW(h, m, w, l);

    private static void EnsureWindow()
    {
        if (_hwnd != 0) return;
        nint inst = W.GetModuleHandleW(0);
        if (!_classRegistered)
        {
            fixed (char* cls = "FluxCordP2PGameOverlay")
            {
                var wc = new W.WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(W.WNDCLASSEXW),
                    lpfnWndProc = &OverlayProc,
                    hInstance = inst,
                    lpszClassName = cls
                };
                W.RegisterClassExW(&wc);
            }
            _classRegistered = true;
        }
        _hwnd = W.CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "FluxCordP2PGameOverlay", "FluxCord Overlay", WS_POPUP,
            16, 16, 240, 80, 0, 0, inst, 0);
    }

    public static void Tick()
    {
        if (App.BenchArg || App.Hwnd == 0) return;
        var s = SettingsStore.Current;
        var dc = DiscordClient.Instance;
        bool mainFocused = !App.IsHiddenToTray && !W.IsIconic(App.Hwnd) && W.GetForegroundWindow() == App.Hwnd;
        bool want = s.GameOverlayEnabled && !s.StreamerMode && dc.VoiceConnected && !mainFocused;

        if (!want)
        {
            if (_visible && _hwnd != 0)
            {
                W.ShowWindow(_hwnd, W.SW_HIDE);
                _visible = false;
                _lastHash = "";
            }
            return;
        }

        EnsureWindow();
        if (_hwnd == 0) return;

        var members = dc.GetVoiceMembersSnapshot(dc.ConnectedVoiceGuildId, dc.ConnectedVoiceChannelId);
        if (members.Count == 0)
        {
            members.Add(new VoiceMember
            {
                UserId = dc.Me.Id,
                Username = dc.Me.DisplayName,
                SelfMute = Shell.IsMuted,
                SelfDeaf = Shell.IsDeafened,
                Speaking = Shell.IsLocalSpeaking
            });
        }

        bool hasSb = Environment.TickCount64 < _sbBannerUntil && !string.IsNullOrEmpty(_sbBanner);
        var hashSb = new System.Text.StringBuilder();
        hashSb.Append(s.GameOverlayPos).Append('|').Append(hasSb ? _sbBanner : "").Append('|');
        foreach (var m in members)
        {
            bool spk = m.UserId == dc.Me.Id ? Shell.IsLocalSpeaking : dc.IsUserActuallySpeaking(m);
            bool mute = m.UserId == dc.Me.Id ? Shell.IsMuted : m.SelfMute;
            bool deaf = m.UserId == dc.Me.Id ? Shell.IsDeafened : m.SelfDeaf;
            hashSb.Append(m.Username).Append(':').Append(spk ? '1' : '0').Append(mute ? 'M' : '0').Append(deaf ? 'D' : '0').Append(m.SelfStream ? 'S' : '0').Append(';');
        }
        string hash = hashSb.ToString();
        if (_visible && hash == _lastHash) return;
        _lastHash = hash;

        const int w = 248;
        const int rowH = 30;
        const int pad = 8;
        int extraH = hasSb ? 26 : 0;
        int h = pad * 2 + Math.Max(1, members.Count) * rowH + extraH;

        int scrW = W.GetSystemMetrics(SM_CXSCREEN);
        int scrH = W.GetSystemMetrics(SM_CYSCREEN);
        int px = (s.GameOverlayPos is 1 or 3) ? Math.Max(12, scrW - w - 18) : 18;
        int py = (s.GameOverlayPos is 2 or 3) ? Math.Max(12, scrH - h - 56) : 18;

        RenderLayered(px, py, w, h, members, dc, hasSb ? _sbBanner : null);
        if (!_visible)
        {
            W.ShowWindow(_hwnd, 4 /*SW_SHOWNOACTIVATE*/);
            _visible = true;
        }
    }

    private static void RenderLayered(int x, int y, int w, int h, List<VoiceMember> members, DiscordClient dc, string? sbBanner)
    {
        nint screenDc = W.GetDC(0);
        nint memDc = W.CreateCompatibleDC(screenDc);
        W.BITMAPINFO bmi = default;
        bmi.biSize = 40;
        bmi.biWidth = w;
        bmi.biHeight = -h; // top-down
        bmi.biPlanes = 1;
        bmi.biBitCount = 32;
        bmi.biCompression = 0;

        byte* bits = null;
        nint hBmp = W.CreateDIBSection(memDc, &bmi, 0, out bits, 0, 0);
        if (hBmp == 0 || bits == null)
        {
            W.DeleteDC(memDc);
            W.ReleaseDC(0, screenDc);
            return;
        }
        nint oldBmp = W.SelectObject(memDc, hBmp);
        nint font = W.CreateFontW(-15, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 4 /*ANTIALIASED_QUALITY*/, 0, "Segoe UI");
        nint oldFont = W.SelectObject(memDc, font);

        try
        {
            int totalBytes = w * h * 4;
            new Span<byte>(bits, totalBytes).Clear();

            // Draw translucent rounded dark glass container
            FillRoundedRect(bits, w, h, 0, 0, w, h, 10, 14, 16, 22, 205);

            int ry = 8;
            foreach (var m in members)
            {
                bool isMe = m.UserId == dc.Me.Id;
                bool spk = isMe ? Shell.IsLocalSpeaking : dc.IsUserActuallySpeaking(m);
                bool mute = isMe ? Shell.IsMuted : m.SelfMute;
                bool deaf = isMe ? Shell.IsDeafened : m.SelfDeaf;

                if (spk)
                    FillRoundedRect(bits, w, h, 5, ry, w - 10, 26, 6, 20, 48, 32, 220);

                // Speaking / Mute / Idle indicator dot
                if (deaf || mute)
                    FillCircle(bits, w, h, 18, ry + 13, 6.5f, 237, 66, 69, 255);
                else if (spk)
                {
                    FillCircle(bits, w, h, 18, ry + 13, 8.2f, 35, 245, 110, 110);
                    FillCircle(bits, w, h, 18, ry + 13, 6.0f, 35, 245, 110, 255);
                }
                else
                    FillCircle(bits, w, h, 18, ry + 13, 5.5f, 110, 118, 134, 220);

                string label = m.Username;
                if (m.SelfStream) label += " [LIVE]";
                else if (deaf) label += " (deaf)";
                else if (mute) label += " (mute)";

                DrawCrispText(memDc, bits, w, h, label, 32, ry + 4, w - 40, 20,
                    spk ? W.Rgb(0x6E, 0xFF, 0xA8) : (mute || deaf ? W.Rgb(0xB0, 0xB6, 0xC4) : W.Rgb(0xF3, 0xF4, 0xF6)));
                ry += 30;
            }

            if (!string.IsNullOrEmpty(sbBanner))
            {
                FillRoundedRect(bits, w, h, 6, ry, w - 12, 22, 5, 42, 48, 86, 235);
                DrawCrispText(memDc, bits, w, h, sbBanner, 12, ry + 3, w - 24, 18, W.Rgb(0x7D, 0xD3, 0xFC));
            }

            // Premultiply RGB by Alpha for UpdateLayeredWindow AC_SRC_ALPHA
            for (int i = 0; i < totalBytes; i += 4)
            {
                byte a = bits[i + 3];
                if (a == 255) continue;
                if (a == 0) { bits[i] = bits[i + 1] = bits[i + 2] = 0; continue; }
                bits[i] = (byte)(bits[i] * a / 255);
                bits[i + 1] = (byte)(bits[i + 1] * a / 255);
                bits[i + 2] = (byte)(bits[i + 2] * a / 255);
            }

            var ptDst = new W.POINT { X = x, Y = y };
            var sz = new W.POINT { X = w, Y = h };
            var ptSrc = new W.POINT { X = 0, Y = 0 };
            var blend = new W.BLENDFUNCTION { BlendOp = AC_SRC_OVER, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
            W.UpdateLayeredWindow(_hwnd, screenDc, &ptDst, &sz, memDc, &ptSrc, 0, &blend, ULW_ALPHA);
        }
        finally
        {
            W.SelectObject(memDc, oldFont);
            W.DeleteObject(font);
            W.SelectObject(memDc, oldBmp);
            W.DeleteObject(hBmp);
            W.DeleteDC(memDc);
            W.ReleaseDC(0, screenDc);
        }
    }

    private static void DrawCrispText(nint memDc, byte* bits, int w, int h, string text, int x, int y, int rw, int rh, uint colorRef)
    {
        // GDI DrawTextW zeroes the alpha channel on touched pixels; mark alpha=254 first, then restore touched pixels to 255!
        int x0 = Math.Clamp(x, 0, w), y0 = Math.Clamp(y, 0, h);
        int x1 = Math.Clamp(x + rw, 0, w), y1 = Math.Clamp(y + rh, 0, h);
        for (int py = y0; py < y1; py++)
            for (int px = x0; px < x1; px++)
                if (bits[(py * w + px) * 4 + 3] == 0) bits[(py * w + px) * 4 + 3] = 1;

        // Snapshot alpha before GDI draw
        var origA = new byte[(x1 - x0) * (y1 - y0)];
        int idx = 0;
        for (int py = y0; py < y1; py++)
            for (int px = x0; px < x1; px++)
            {
                origA[idx++] = bits[(py * w + px) * 4 + 3];
                bits[(py * w + px) * 4 + 3] = 254;
            }

        W.SetBkMode(memDc, 1 /*TRANSPARENT*/);
        W.SetTextColor(memDc, colorRef);
        var rc = new W.RECT { L = x, T = y, R = x + rw, B = y + rh };
        W.DrawTextW(memDc, text, text.Length, &rc, 0x00008020 /*DT_SINGLELINE | DT_END_ELLIPSIS*/);

        idx = 0;
        for (int py = y0; py < y1; py++)
            for (int px = x0; px < x1; px++)
            {
                int o = (py * w + px) * 4;
                if (bits[o + 3] != 254) bits[o + 3] = 255;
                else bits[o + 3] = origA[idx] == 1 ? (byte)0 : origA[idx];
                idx++;
            }
    }

    private static void FillRoundedRect(byte* bits, int w, int h, int rx, int ry, int rw, int rh, int rad, byte r, byte g, byte b, byte a)
    {
        for (int py = Math.Max(0, ry); py < Math.Min(h, ry + rh); py++)
        {
            for (int px = Math.Max(0, rx); px < Math.Min(w, rx + rw); px++)
            {
                float dx = 0, dy = 0;
                if (px < rx + rad) dx = (rx + rad) - px;
                else if (px >= rx + rw - rad) dx = px - (rx + rw - rad - 1);
                if (py < ry + rad) dy = (ry + rad) - py;
                else if (py >= ry + rh - rad) dy = py - (ry + rh - rad - 1);
                if (dx > 0 && dy > 0 && dx * dx + dy * dy > rad * rad) continue;
                int o = (py * w + px) * 4;
                bits[o] = b; bits[o + 1] = g; bits[o + 2] = r; bits[o + 3] = a;
            }
        }
    }

    private static void FillCircle(byte* bits, int w, int h, float cx, float cy, float rad, byte r, byte g, byte b, byte a)
    {
        int x0 = Math.Max(0, (int)(cx - rad - 1)), x1 = Math.Min(w, (int)(cx + rad + 2));
        int y0 = Math.Max(0, (int)(cy - rad - 1)), y1 = Math.Min(h, (int)(cy + rad + 2));
        for (int py = y0; py < y1; py++)
        {
            for (int px = x0; px < x1; px++)
            {
                float d = MathF.Sqrt((px + 0.5f - cx) * (px + 0.5f - cx) + (py + 0.5f - cy) * (py + 0.5f - cy)) - rad;
                if (d >= 0.8f) continue;
                float cov = Math.Clamp(0.8f - d, 0f, 1f) * (a / 255f);
                int o = (py * w + px) * 4;
                float da = bits[o + 3] / 255f;
                float outA = cov + da * (1f - cov);
                if (outA <= 0.001f) continue;
                bits[o] = (byte)Math.Clamp((b * cov + bits[o] * da * (1f - cov)) / outA, 0, 255);
                bits[o + 1] = (byte)Math.Clamp((g * cov + bits[o + 1] * da * (1f - cov)) / outA, 0, 255);
                bits[o + 2] = (byte)Math.Clamp((r * cov + bits[o + 2] * da * (1f - cov)) / outA, 0, 255);
                bits[o + 3] = (byte)Math.Clamp(outA * 255f, 0, 255);
            }
        }
    }
}
