using Vortice.Direct2D1;

namespace FluxCord;

public static unsafe partial class Shell
{
    private sealed class ShareSourceItem
    {
        public bool IsMonitor;
        public bool IsDualScreen;
        public int MonitorIndex;
        public ulong Hwnd;
        public string Title = "";
        public byte[]? PendingBgra;
        public int ThumbW = 240, ThumbH = 135;
        public ID2D1Bitmap1? Bitmap;
    }

    private static readonly object _shareLock = new();
    private static readonly Queue<ID2D1Bitmap1> _pendingBitmapDispose = new();
    private static List<ShareSourceItem> _shareSources = [];
    private static int _shareGen;
    private static int _selectedShareIdx;
    private static int _settingsTab; // 0 = Голос и видео, 1 = Стриминг 4K, 2 = Интерфейс и сеть, 3 = Обновления
    private static float _settingsScroll, _settingsMaxScroll = 540f;
    private static float _pinsScroll, _regionsScroll, _audioPopScroll;
    private static int _settingsOpenDropdown; // 0 = none, 1 = mic dropdown, 2 = speaker dropdown

    internal static void DrainPendingShareBitmaps()
    {
        lock (_shareLock)
        {
            while (_pendingBitmapDispose.Count > 0)
            {
                try { _pendingBitmapDispose.Dequeue().Dispose(); } catch { }
            }
        }
    }

    public static void ClearShareBitmaps()
    {
        lock (_shareLock)
        {
            foreach (var it in _shareSources) { it.Bitmap?.Dispose(); it.Bitmap = null; }
            while (_pendingBitmapDispose.Count > 0)
            {
                try { _pendingBitmapDispose.Dequeue().Dispose(); } catch { }
            }
        }
    }

    private static volatile bool _shareLoading;
    private static void RefreshShareSourcesAsync()
    {
        _shareLoading = true;
        int gen = Interlocked.Increment(ref _shareGen);
        _ = Task.Run(() =>
        {
            try
            {
                var mons = MediaNative.ListMonitors();
                _monitors = mons;
                int screenCards = mons.Count + (mons.Count >= 2 ? 1 : 0);
                var wins = MediaNative.ListWindows(App.Hwnd).Take(Math.Max(0, 6 - screenCards)).ToList();

                var items = new List<ShareSourceItem>();
                foreach (var m in mons)
                    items.Add(new ShareSourceItem { IsMonitor = true, MonitorIndex = m.Index, Title = $"Экран {m.Index + 1} ({m.W}x{m.H})" });
                if (mons.Count >= 2)
                {
                    items.Add(new ShareSourceItem
                    {
                        IsMonitor = true,
                        IsDualScreen = true,
                        MonitorIndex = -2,
                        Title = "⚡ 2 Экрана сразу (1 + 2)"
                    });
                }
                foreach (var w in wins)
                    items.Add(new ShareSourceItem { IsMonitor = false, Hwnd = w.Hwnd, Title = w.Title });

                if (Volatile.Read(ref _shareGen) != gen) return;
                lock (_shareLock)
                {
                    // NEVER dispose D2D bitmaps on a worker thread -> defer safely to UI thread!
                    foreach (var old in _shareSources)
                    {
                        if (old.Bitmap != null) { _pendingBitmapDispose.Enqueue(old.Bitmap); old.Bitmap = null; }
                    }
                    _shareSources = items;
                    _selectedShareIdx = 0;
                }
                _shareLoading = false;
                App.WakeUi();

                const int tw = 240, th = 135;
                foreach (var item in items)
                {
                    if (Volatile.Read(ref _shareGen) != gen) return;
                    if (item.IsDualScreen)
                    {
                        // Stitch live Monitor 0 (left 120px) + 2px neon divider + Monitor 1 (right 118px) into 240x135 BGRA!
                        const int halfW = 119;
                        var leftBuf = new byte[halfW * th * 4];
                        var rightBuf = new byte[halfW * th * 4];
                        int ok0, ok1;
                        fixed (byte* p0 = leftBuf) ok0 = MediaNative.fc_capture_monitor_thumb(0, p0, halfW, th);
                        fixed (byte* p1 = rightBuf) ok1 = MediaNative.fc_capture_monitor_thumb(1, p1, halfW, th);
                        if (ok0 != 0 || ok1 != 0)
                        {
                            var dst = new byte[tw * th * 4];
                            for (int row = 0; row < th; row++)
                            {
                                int dstOff = row * tw * 4;
                                if (ok0 != 0) Buffer.BlockCopy(leftBuf, row * halfW * 4, dst, dstOff, halfW * 4);
                                // 2px neon brand divider in the center
                                for (int dx = halfW; dx < halfW + 2; dx++)
                                {
                                    int p = dstOff + dx * 4;
                                    dst[p] = 0xF8; dst[p + 1] = 0xBD; dst[p + 2] = 0x38; dst[p + 3] = 0xFF; // Cyan BGRA
                                }
                                if (ok1 != 0) Buffer.BlockCopy(rightBuf, row * halfW * 4, dst, dstOff + (halfW + 2) * 4, halfW * 4);
                            }
                            item.PendingBgra = dst;
                            App.WakeUi();
                        }
                        continue;
                    }

                    var buf = new byte[tw * th * 4];
                    int ok;
                    fixed (byte* p = buf)
                    {
                        ok = item.IsMonitor
                            ? MediaNative.fc_capture_monitor_thumb(item.MonitorIndex, p, tw, th)
                            : MediaNative.fc_capture_window_thumb(item.Hwnd, p, tw, th);
                    }
                    if (ok != 0)
                    {
                        item.PendingBgra = buf;
                        App.WakeUi();
                    }
                }
            }
            catch { _shareLoading = false; App.WakeUi(); }
        });
    }

    // ---------------- 1. GoofCord Screen Share Modal (exact match to f_013.jpg & f_033.jpg!) ----------------

    private static void DrawScreenShareModal(R r)
    {
        Gfx.FillRect(r, 0xCC07080B);
        float bw = MathF.Min(640, r.W - 40), bh = MathF.Min(598, r.H - 36);
        var box = new R(r.X + (r.W - bw) * 0.5f, r.Y + (r.H - bh) * 0.5f, bw, bh);
        Gfx.FillRect(box, 0xFF1A1B21, 12);
        Gfx.StrokeRect(box, Th.Border, 1, 12);

        Gfx.DrawText("Screen Share", new R(box.X + 20, box.Y + 16, 260, 24), 18, Th.Text, 700);
        Gfx.DrawText("Выберите экран, окно или ⚡ 2 Экрана сразу (Zero-Copy GPU)", new R(box.X + 20, box.Y + 40, 440, 18), 12, Th.Sub);

        var refBtn = new R(box.Right - 78, box.Y + 14, 30, 30);
        Gfx.FillRect(refBtn, UI.Hover(refBtn) ? Th.Hover : Th.Card, 6);
        Gfx.DrawIcon(IcRefresh, "↻", refBtn, 15, Th.Text);
        if (UI.Click(UI.Id("sh_ref"), refBtn)) RefreshShareSourcesAsync();

        var clsBtn = new R(box.Right - 42, box.Y + 14, 30, 30);
        Gfx.FillRect(clsBtn, UI.Hover(clsBtn) ? Th.Hover : Th.Card, 6);
        Gfx.DrawIcon(IcX, "✕", clsBtn, 15, Th.Text);
        if (UI.Click(UI.Id("sh_cls"), clsBtn)) { _showShareModal = false; return; }

        Gfx.FillRect(new R(box.X, box.Y + 64, box.W, 1), Th.Border);

        // 3x2 Live Thumbnail Grid (exact f_013.jpg + Dual-Screen Hack card!)
        List<ShareSourceItem> items;
        lock (_shareLock) items = [.. _shareSources];

        float pad = 18f, gap = 12f;
        float tw = (box.W - pad * 2 - gap * 2) / 3f;
        float th = tw * 0.5625f; // 16:9
        float gy = box.Y + 76;

        if (items.Count == 0 && _shareLoading)
        {
            Gfx.DrawText("Поиск экранов и открытых окон...", new R(box.X + pad, gy + th * 0.8f, box.W - pad * 2, 24), 13.5f, Th.Sub, 600, 1);
            UI.Animate();
        }

        for (int i = 0; i < Math.Min(6, items.Count); i++)
        {
            var it = items[i];
            if (it.Bitmap == null && it.PendingBgra != null)
            {
                it.Bitmap = ImageCache.CreateBitmapFromBgra(it.PendingBgra, it.ThumbW, it.ThumbH);
                it.PendingBgra = null;
            }

            int col = i % 3, row = i / 3;
            var card = new R(box.X + pad + col * (tw + gap), gy + row * (th + 28), tw, th);
            bool sel = i == _selectedShareIdx;
            Gfx.FillRect(card, 0xFF0C0D12, 8);
            if (it.Bitmap != null) Gfx.DrawBitmap(it.Bitmap, card.Inset(2));
            else Gfx.DrawText("Захват...", new R(card.X, card.Y + card.H * 0.42f, card.W, 18), 11, Th.Dim, 500, 1);

            if (it.IsDualScreen)
            {
                var badgeR = new R(card.X + 6, card.Y + 6, 82, 18);
                Gfx.FillRect(badgeR, 0xEE0F172A, 9);
                Gfx.StrokeRect(badgeR, Th.Cyan, 1, 9);
                Gfx.DrawText("32:9 DUAL GPU", new R(badgeR.X, badgeR.Y + 2, badgeR.W, 14), 9f, Th.Cyan, 700, 1);
            }

            uint borderCol = sel ? (it.IsDualScreen ? Th.Cyan : Th.Brand) : (it.IsDualScreen ? 0x6638BDF8 : (UI.Hover(card) ? 0x66FFFFFF : Th.Border));
            Gfx.StrokeRect(card, borderCol, sel ? 2.5f : 1.2f, 8);

            string lbl = SafeTrunc(it.Title, 26);
            Gfx.DrawText(lbl, new R(card.X, card.Bottom + 4, card.W, 18), 11.5f, sel ? (it.IsDualScreen ? Th.Cyan : Th.Text) : (it.IsDualScreen ? Th.Cyan : Th.Sub), sel || it.IsDualScreen ? 700 : 500, 1);

            if (UI.Click(UI.Id("sh_src", i), new R(card.X, card.Y, card.W, card.H + 22)))
                _selectedShareIdx = i;
        }

        // STREAM SETTINGS section (exact f_013.jpg!)
        var s = SettingsStore.Current;
        bool changed = false;
        bool dualSelected = items.Count > 0 && _selectedShareIdx >= 0 && _selectedShareIdx < items.Count && items[_selectedShareIdx].IsDualScreen;
        float sy = gy + 2 * (th + 28) + 8;
        Gfx.FillRect(new R(box.X + pad, sy, box.W - pad * 2, 1), Th.Border);
        sy += 10;
        if (dualSelected)
        {
            Gfx.DrawText("⚡ DUAL-SCREEN HACK: Оба монитора склеиваются на GPU в 32:9 (3840×1080 / 2560×720) без нагрузки на CPU!",
                new R(box.X + pad, sy, box.W - pad * 2, 16), 10.5f, Th.Cyan, 700);
        }
        else
        {
            Gfx.DrawText("НАСТРОЙКИ СТРИМА", new R(box.X + pad, sy, 220, 16), 10.5f, Th.Dim, 700);
        }
        sy += 20;

        // Row 1: Optimization (Motion / Detail) + Resolution (480p / 720p / 1080p / 1440p / Source)
        float optW = 190f;
        Gfx.DrawText("Оптимизация", new R(box.X + pad, sy, optW, 16), 11.5f, Th.Sub, 600);
        Gfx.DrawText(dualSelected ? "Выход 32:9 (720p→2560×720, 1080p+→3840×1080)" : "Разрешение",
            new R(box.X + pad + optW + 16, sy, 360, 16), 11.5f, dualSelected ? Th.Cyan : Th.Sub, 600);
        sy += 20;

        if (UI.Button(UI.Id("sh_mot"), new R(box.X + pad, sy, 92, 30), "⚡ Motion", !s.StreamOptimizeDetail ? Th.Brand : Th.Card, Th.Text, 6, 12))
        { s.StreamOptimizeDetail = false; changed = true; }
        if (UI.Button(UI.Id("sh_det"), new R(box.X + pad + 96, sy, 92, 30), "🖼 Detail", s.StreamOptimizeDetail ? Th.Brand : Th.Card, Th.Text, 6, 12))
        { s.StreamOptimizeDetail = true; changed = true; }

        (int W, int H, string L)[] resList = [(848, 480, "480p"), (1280, 720, "720p"), (1920, 1080, "1080p"), (2560, 1440, "1440p"), (0, 0, "Source")];
        float rx = box.X + pad + optW + 16;
        float rw = (box.Right - pad - rx - 16) / 5f;
        for (int i = 0; i < resList.Length; i++)
        {
            bool sel = s.StreamHeight == resList[i].H;
            if (UI.Button(UI.Id("sh_res", i), new R(rx + i * (rw + 4), sy, rw, 30), resList[i].L, sel ? Th.Brand : Th.Card, Th.Text, 6, 11.5f))
            { s.StreamWidth = resList[i].W; s.StreamHeight = resList[i].H; changed = true; }
        }
        sy += 40;

        // Row 2: Frame Rate (15 / 30 / 60 / 120) + Codec (H.264 / HEVC / AV1)
        Gfx.DrawText("Частота кадров", new R(box.X + pad, sy, 260, 16), 11.5f, Th.Sub, 600);
        Gfx.DrawText("Кодек NVENC (Go Live = H.264)", new R(box.X + pad + 320, sy, 260, 16), 11.5f, Th.Sub, 600);
        sy += 20;

        int[] fpsList = [15, 30, 60, 120];
        float fw = (304f - 12f) / 4f;
        for (int i = 0; i < fpsList.Length; i++)
        {
            bool sel = s.StreamFps == fpsList[i];
            if (UI.Button(UI.Id("sh_fps", i), new R(box.X + pad + i * (fw + 4), sy, fw, 30), $"{fpsList[i]}fps", sel ? Th.Brand : Th.Card, Th.Text, 6, 12))
            { s.StreamFps = fpsList[i]; changed = true; }
        }

        float cx = box.X + pad + 320f;
        Gfx.FillRect(new R(cx, sy, box.Right - pad - cx, 30), Th.Brand, 6);
        Gfx.DrawText("H.264 NVENC (Go Live)", new R(cx + 4, sy + 7, box.Right - pad - cx - 8, 16), 11.5f, 0xFFFFFFFF, 700, 1);
        sy += 42;

        // Capture System Audio checkbox + big "Go Live" button
        var audRow = new R(box.X + pad, sy, 290, 36);
        if (UI.Hover(audRow)) Gfx.FillRect(audRow, Th.Hover, 6);
        var chk = new R(audRow.X + 4, audRow.Y + 8, 20, 20);
        Gfx.FillRect(chk, s.StreamCaptureAudio ? Th.Brand : Th.Card, 5);
        Gfx.StrokeRect(chk, s.StreamCaptureAudio ? Th.Brand : Th.Border, 1, 5);
        if (s.StreamCaptureAudio) Gfx.DrawIcon(IcCheck, "✓", chk, 13, 0xFFFFFFFF);
        Gfx.DrawText("Capture System Audio", new R(chk.Right + 10, audRow.Y + 3, 240, 16), 12.5f, Th.Text, 600);
        Gfx.DrawText("Транслировать звук компьютера", new R(chk.Right + 10, audRow.Y + 19, 240, 14), 10.5f, Th.Dim);
        if (UI.Click(UI.Id("sh_aud"), audRow)) { s.StreamCaptureAudio = !s.StreamCaptureAudio; changed = true; }

        string goLabel = dualSelected ? "⚡ Стримить 2 Экрана" : "Начать трансляцию";
        if (UI.Button(UI.Id("sh_go"), new R(box.Right - pad - 216, sy, 216, 38), goLabel, dualSelected ? Th.Cyan : Th.Brand, dualSelected ? 0xFF0A0B0E : 0xFFFFFFFF, 8, 13.5f, 700))
        {
            SettingsStore.Save();
            if (items.Count > 0 && _selectedShareIdx < items.Count)
            {
                var chosen = items[_selectedShareIdx];
                if (chosen.IsMonitor && !chosen.IsDualScreen) s.StreamMonitor = chosen.MonitorIndex;
                StartStream(chosen.IsMonitor ? chosen.MonitorIndex : 0, chosen.IsMonitor ? 0 : chosen.Hwnd);
            }
            else StartStream(s.StreamMonitor, 0);
            _showShareModal = false;
        }

        if (changed) SettingsStore.Save();
    }

    // ---------------- 2. Quick Audio Device Popup (exact match to f_005.jpg!) ----------------

    private static void DrawAudioQuickPopup()
    {
        var s = SettingsStore.Current;
        bool changed = false;

        if (_audioPopupMode == 2)
        {
            // Direct Output Device Picker (from headphones ⌄ chevron) — scrollable for systems with many audio devices!
            float fullH = 38 + Math.Max(1, _renDevices.Count) * 32 + 12;
            float h = MathF.Min(fullH, MathF.Max(160, Gfx.H - TopH - 90));
            float w = 290;
            float px = Math.Clamp(_audioPopupX, 8, Gfx.W - w - 8);
            float py = Math.Clamp(_audioPopupY - h, TopH + 8, Gfx.H - h - 8);
            var menu = new R(px, py, w, h);
            _overlayCutouts.Add(menu);

            if ((UI.Pressed || UI.RightClicked) && !menu.Contains(UI.MX, UI.MY))
            { _audioPopupMode = 0; _audioPopScroll = 0; return; }

            Gfx.FillRect(menu, 0xFA14151B, 10);
            Gfx.StrokeRect(menu, Th.Border, 1, 10);
            Gfx.DrawText("Устройство вывода", new R(menu.X + 12, menu.Y + 10, menu.W - 24, 18), 12.5f, Th.Sub, 700);
            Gfx.FillRect(new R(menu.X + 10, menu.Y + 32, menu.W - 20, 1), Th.Border);

            var listArea = new R(menu.X, menu.Y + 36, menu.W, menu.H - 42);
            float maxSc = MathF.Max(0, _renDevices.Count * 32f - listArea.H);
            if (UI.Hover(listArea) && UI.Wheel != 0) _audioPopScroll = Math.Clamp(_audioPopScroll - UI.Wheel * 36f, 0f, maxSc);
            else _audioPopScroll = Math.Clamp(_audioPopScroll, 0f, maxSc);

            Gfx.PushClip(listArea);
            float y = listArea.Y + 2 - _audioPopScroll;
            for (int i = 0; i < _renDevices.Count; i++)
            {
                var d = _renDevices[i];
                bool sel = string.IsNullOrEmpty(s.RenderDeviceId) ? d.Default : d.Id == s.RenderDeviceId;
                var rr = new R(menu.X + 6, y, menu.W - 12, 30);
                if (rr.Bottom >= listArea.Y && rr.Y <= listArea.Bottom)
                {
                    if (UI.Hover(rr)) Gfx.FillRect(rr, Th.Hover, 6);
                    Gfx.DrawText(d.Name, new R(rr.X + 8, rr.Y + 7, rr.W - 34, 16), 12, sel ? Th.Text : Th.Sub, sel ? 700 : 500);
                    DrawRadioDot(rr.Right - 14, rr.Y + 15, sel);
                    if (UI.Click(UI.Id("qp_ren", i), rr))
                    {
                        s.RenderDeviceId = d.Id; changed = true;
                        if (DiscordClient.Instance.VoiceConnected || _loopback) MediaNative.fc_voice_start(s.CaptureDeviceId, s.RenderDeviceId);
                        _audioPopupMode = 0; _audioPopScroll = 0;
                    }
                }
                y += 32;
            }
            Gfx.PopClip();
            if (changed) { ApplyAudioSettings(); SettingsStore.Save(); }
            return;
        }

        // Full Quick Audio Popup (from microphone ⌄ chevron — exact match to f_005.jpg!)
        float mw = 248, mh = 292;
        float mx = Math.Clamp(_audioPopupX, 8, Gfx.W - mw - 12);
        float my = Math.Clamp(_audioPopupY - mh, TopH + 8, Gfx.H - mh - 8);
        var mainMenu = new R(mx, my, mw, mh);
        _overlayCutouts.Add(mainMenu);

        R flyout = default;
        if (_audioPopupSub is 1 or 2)
        {
            var devList = _audioPopupSub == 1 ? _capDevices : _renDevices;
            float fw = 276, fh = MathF.Min(16 + Math.Max(1, devList.Count) * 32, MathF.Max(160, Gfx.H - TopH - 90));
            float fx = (mainMenu.Right + 6 + fw < Gfx.W) ? mainMenu.Right + 6 : mainMenu.X - fw - 6;
            float fy = Math.Clamp(_audioPopupSub == 1 ? mainMenu.Y : mainMenu.Y + 46, TopH + 8, Gfx.H - fh - 8);
            flyout = new R(fx, fy, fw, fh);
            _overlayCutouts.Add(flyout);
        }

        if ((UI.Pressed || UI.RightClicked) && !mainMenu.Contains(UI.MX, UI.MY) && (_audioPopupSub == 0 || !flyout.Contains(UI.MX, UI.MY)))
        { _audioPopupMode = 0; _audioPopupSub = 0; _audioPopScroll = 0; return; }

        Gfx.FillRect(mainMenu, 0xFA14151B, 10);
        Gfx.StrokeRect(mainMenu, Th.Border, 1, 10);

        string capName = _capDevices.FirstOrDefault(d => d.Id == s.CaptureDeviceId).Name ?? (_capDevices.FirstOrDefault(d => d.Default).Name ?? "По умолчанию");
        string renName = _renDevices.FirstOrDefault(d => d.Id == s.RenderDeviceId).Name ?? (_renDevices.FirstOrDefault(d => d.Default).Name ?? "По умолчанию");

        // Input Device row
        var inRow = new R(mainMenu.X + 6, mainMenu.Y + 8, mainMenu.W - 12, 40);
        if (UI.Hover(inRow) || _audioPopupSub == 1) { Gfx.FillRect(inRow, Th.Hover, 6); if (UI.Hover(inRow) && _audioPopupSub != 1) { _audioPopupSub = 1; _audioPopScroll = 0; } }
        Gfx.DrawText("Устройство ввода", new R(inRow.X + 8, inRow.Y + 4, inRow.W - 28, 16), 12.5f, Th.Text, 600);
        Gfx.DrawText(capName, new R(inRow.X + 8, inRow.Y + 21, inRow.W - 28, 14), 10.5f, Th.Sub);
        Gfx.DrawIcon(IcChevRight, ">", new R(inRow.Right - 20, inRow.Y + 12, 16, 16), 13, Th.Sub);
        if (UI.Click(UI.Id("qp_in_row"), inRow)) { _audioPopupSub = _audioPopupSub == 1 ? 0 : 1; _audioPopScroll = 0; }

        // Output Device row
        var outRow = new R(mainMenu.X + 6, mainMenu.Y + 52, mainMenu.W - 12, 40);
        if (UI.Hover(outRow) || _audioPopupSub == 2) { Gfx.FillRect(outRow, Th.Hover, 6); if (UI.Hover(outRow) && _audioPopupSub != 2) { _audioPopupSub = 2; _audioPopScroll = 0; } }
        Gfx.DrawText("Устройство вывода", new R(outRow.X + 8, outRow.Y + 4, outRow.W - 28, 16), 12.5f, Th.Text, 600);
        Gfx.DrawText(renName, new R(outRow.X + 8, outRow.Y + 21, outRow.W - 28, 14), 10.5f, Th.Sub);
        Gfx.DrawIcon(IcChevRight, ">", new R(outRow.Right - 20, outRow.Y + 12, 16, 16), 13, Th.Sub);
        if (UI.Click(UI.Id("qp_out_row"), outRow)) { _audioPopupSub = _audioPopupSub == 2 ? 0 : 2; _audioPopScroll = 0; }

        Gfx.FillRect(new R(mainMenu.X + 10, mainMenu.Y + 98, mainMenu.W - 20, 1), Th.Border);

        float micPct = s.InputGain * 100f;
        if (UI.Slider(UI.Id("qp_mic_vol"), new R(mainMenu.X + 12, mainMenu.Y + 106, mainMenu.W - 24, 36), "Громкость микрофона", ref micPct, 0f, 200f, "%"))
        { s.InputGain = micPct / 100f; changed = true; }

        // Live 24-segment input VU bar (exact f_005.jpg!)
        Gfx.DrawText("Входная чувствительность", new R(mainMenu.X + 12, mainMenu.Y + 148, mainMenu.W - 24, 16), 11.5f, Th.Sub, 500);
        int segs = 24;
        float barW2 = mainMenu.W - 24, sw2 = (barW2 - (segs - 1) * 2f) / segs;
        int lit = (int)(Math.Clamp(_audioStats.CleanRms * 5.5f, 0f, 1f) * segs);
        for (int i = 0; i < segs; i++)
            Gfx.FillRect(new R(mainMenu.X + 12 + i * (sw2 + 2f), mainMenu.Y + 168, sw2, 12), i < lit ? Th.Green : 0xFF2A2D38, 1.5f);

        float spkPct = s.OutputVolume * 100f;
        if (UI.Slider(UI.Id("qp_spk_vol"), new R(mainMenu.X + 12, mainMenu.Y + 188, mainMenu.W - 24, 36), "Громкость звука", ref spkPct, 0f, 200f, "%"))
        { s.OutputVolume = spkPct / 100f; changed = true; }

        Gfx.FillRect(new R(mainMenu.X + 10, mainMenu.Y + 230, mainMenu.W - 20, 1), Th.Border);

        // Open full Voice & Video Settings
        var goSet = new R(mainMenu.X + 6, mainMenu.Y + 238, mainMenu.W - 12, 24);
        if (UI.Hover(goSet)) Gfx.FillRect(goSet, Th.Hover, 5);
        Gfx.DrawIcon(IcSettings, "⚙", new R(goSet.X + 6, goSet.Y + 4, 16, 16), 13, Th.Sub);
        Gfx.DrawText("Настройки голоса", new R(goSet.X + 28, goSet.Y + 4, goSet.W - 34, 16), 12.5f, Th.Text, 600);
        if (UI.Click(UI.Id("qp_open_set"), goSet))
        { _audioPopupMode = 0; _audioPopupSub = 0; _showSettings = true; _settingsTab = 0; }

        var deafRow = new R(mainMenu.X + 6, mainMenu.Y + 264, mainMenu.W - 12, 22);
        if (UI.Hover(deafRow)) Gfx.FillRect(deafRow, Th.Hover, 5);
        Gfx.DrawText("Откл. звук", new R(deafRow.X + 8, deafRow.Y + 3, 140, 16), 12, Th.Sub, 500);
        var dchk = new R(deafRow.Right - 22, deafRow.Y + 3, 16, 16);
        Gfx.FillRect(dchk, _deafened ? Th.Brand : Th.Card, 4);
        Gfx.StrokeRect(dchk, Th.Border, 1, 4);
        if (_deafened) Gfx.DrawIcon(IcCheck, "✓", dchk, 11, 0xFFFFFFFF);
        if (UI.Click(UI.Id("qp_deaf"), deafRow)) ToggleDeafen();

        // Flyout Device List (exact right-hand panel in f_005.jpg, scrollable!)
        if (_audioPopupSub is 1 or 2)
        {
            Gfx.FillRect(flyout, 0xFA14151B, 10);
            Gfx.StrokeRect(flyout, Th.Border, 1, 10);
            var devList = _audioPopupSub == 1 ? _capDevices : _renDevices;
            float maxFlySc = MathF.Max(0, 16 + devList.Count * 32f - flyout.H);
            if (UI.Hover(flyout) && UI.Wheel != 0) _audioPopScroll = Math.Clamp(_audioPopScroll - UI.Wheel * 36f, 0f, maxFlySc);
            else _audioPopScroll = Math.Clamp(_audioPopScroll, 0f, maxFlySc);

            Gfx.PushClip(flyout.Inset(2));
            float y = flyout.Y + 8 - _audioPopScroll;
            for (int i = 0; i < devList.Count; i++)
            {
                var d = devList[i];
                bool sel = _audioPopupSub == 1
                    ? (string.IsNullOrEmpty(s.CaptureDeviceId) ? d.Default : d.Id == s.CaptureDeviceId)
                    : (string.IsNullOrEmpty(s.RenderDeviceId) ? d.Default : d.Id == s.RenderDeviceId);
                var rr = new R(flyout.X + 6, y, flyout.W - 12, 30);
                if (rr.Bottom >= flyout.Y && rr.Y <= flyout.Bottom)
                {
                    if (UI.Hover(rr)) Gfx.FillRect(rr, Th.Hover, 6);
                    Gfx.DrawText(d.Name, new R(rr.X + 8, rr.Y + 7, rr.W - 34, 16), 12, sel ? Th.Text : Th.Sub, sel ? 700 : 500);
                    DrawRadioDot(rr.Right - 14, rr.Y + 15, sel);
                    if (UI.Click(UI.Id("qp_dev_" + _audioPopupSub, i), rr))
                    {
                        if (_audioPopupSub == 1) s.CaptureDeviceId = d.Id;
                        else s.RenderDeviceId = d.Id;
                        changed = true;
                        if (DiscordClient.Instance.VoiceConnected || _loopback) MediaNative.fc_voice_start(s.CaptureDeviceId, s.RenderDeviceId);
                        _audioPopupMode = 0; _audioPopupSub = 0; _audioPopScroll = 0;
                    }
                }
                y += 32;
            }
            Gfx.PopClip();
        }

        if (changed) { ApplyAudioSettings(); SettingsStore.Save(); }
    }

    private static void DrawRadioDot(float cx, float cy, bool selected)
    {
        Gfx.FillCircle(cx, cy, 7f, selected ? Th.Brand : 0xFF2A2D38);
        if (selected) Gfx.FillCircle(cx, cy, 3f, 0xFFFFFFFF);
    }

    // ---------------- 3. Stream Right-Click Context Menu (exact match to f_022.jpg!) ----------------

    private static void DrawStreamContextMenu()
    {
        if (_ctxStreamUserId == null) return;
        var dc = DiscordClient.Instance;
        bool isSelf = _ctxStreamUserId == "self";
        var ws = isSelf ? null : dc.GetWatchSession(_ctxStreamUserId);
        if (!isSelf && ws == null) { _ctxStreamUserId = null; return; }

        float mh = isSelf ? 190f : 186f;
        var menu = new R(_ctxStreamX, _ctxStreamY, 244, mh);
        _overlayCutouts.Add(menu);

        if ((UI.Pressed || UI.RightClicked) && !menu.Contains(UI.MX, UI.MY))
        { _ctxStreamUserId = null; return; }

        Gfx.FillRect(menu, 0xFA14151B, 10);
        Gfx.StrokeRect(menu, Th.Border, 1, 10);

        float y = menu.Y + 8;
        var stopRow = new R(menu.X + 8, y, menu.W - 16, 32);
        if (UI.Hover(stopRow)) Gfx.FillRect(stopRow, 0x33ED4245, 6);
        Gfx.DrawIcon(IcMonitorOff, "X", new R(stopRow.X + 6, stopRow.Y + 8, 16, 16), 14, Th.Red);
        Gfx.DrawText(isSelf ? "Остановить трансляцию" : "Прекратить просмотр", new R(stopRow.X + 28, stopRow.Y + 8, stopRow.W - 34, 16), 12.5f, Th.Text, 600);
        if (UI.Click(UI.Id("sctx_stop"), stopRow))
        {
            if (isSelf) StopStream();
            else dc.StopWatchingStream(_ctxStreamUserId);
            _ctxStreamUserId = null;
            return;
        }
        y += 38;

        if (isSelf)
        {
            // Live 1-Click Stream Resolution & System Audio Hot-Switch!
            var s = SettingsStore.Current;
            Gfx.FillRect(new R(menu.X + 10, y - 3, menu.W - 20, 1), Th.Border);
            Gfx.DrawText("БЫСТРАЯ СМЕНА КАЧЕСТВА", new R(menu.X + 12, y + 3, menu.W - 24, 14), 10f, Th.Dim, 700);
            y += 20;
            (int W, int H, string L)[] presets = [(1280, 720, "720p"), (1920, 1080, "1080p"), (2560, 1440, "1440p"), (3840, 2160, "4K")];
            float pw = (menu.W - 28) / 4f;
            for (int i = 0; i < presets.Length; i++)
            {
                bool sel = s.StreamHeight == presets[i].H;
                if (UI.Button(UI.Id("sctx_q", i), new R(menu.X + 8 + i * (pw + 4), y, pw, 26), presets[i].L, sel ? Th.Brand : Th.Card, Th.Text, 5, 11))
                {
                    s.StreamWidth = presets[i].W; s.StreamHeight = presets[i].H; SettingsStore.Save();
                    StartStream(_activeStreamMonitor, _activeStreamHwnd);
                    _ctxStreamUserId = null;
                    return;
                }
            }
            y += 32;
            var audRow = new R(menu.X + 8, y, menu.W - 16, 28);
            if (UI.Hover(audRow)) Gfx.FillRect(audRow, Th.Hover, 6);
            Gfx.DrawText("Звук трансляции", new R(audRow.X + 8, audRow.Y + 6, 150, 16), 12f, Th.Text, 500);
            var achk = new R(audRow.Right - 24, audRow.Y + 6, 16, 16);
            Gfx.FillRect(achk, s.StreamCaptureAudio ? Th.Brand : Th.Card, 4);
            if (s.StreamCaptureAudio) Gfx.DrawIcon(IcCheck, "✓", achk, 11, 0xFFFFFFFF);
            if (UI.Click(UI.Id("sctx_aud"), audRow))
            {
                s.StreamCaptureAudio = !s.StreamCaptureAudio; SettingsStore.Save();
                StartStream(_activeStreamMonitor, _activeStreamHwnd);
                _ctxStreamUserId = null;
                return;
            }
            y += 34;
        }
        else if (ws != null)
        {
            Gfx.FillRect(new R(menu.X + 10, y - 3, menu.W - 20, 1), Th.Border);
            var muteRow = new R(menu.X + 8, y + 2, menu.W - 16, 28);
            if (UI.Hover(muteRow)) Gfx.FillRect(muteRow, Th.Hover, 6);
            Gfx.DrawText("Заглушить", new R(muteRow.X + 8, muteRow.Y + 6, 140, 16), 12.5f, Th.Text, 500);
            var mchk = new R(muteRow.Right - 24, muteRow.Y + 6, 16, 16);
            Gfx.FillRect(mchk, ws.Muted ? Th.Brand : Th.Card, 4);
            Gfx.StrokeRect(mchk, Th.Border, 1, 4);
            if (ws.Muted) Gfx.DrawIcon(IcCheck, "✓", mchk, 11, 0xFFFFFFFF);
            if (UI.Click(UI.Id("sctx_mute"), muteRow)) dc.ToggleStreamMute(ws.StreamerUserId);
            y += 34;

            float volPct = (ws.Muted ? 0f : ws.Volume) * 100f;
            if (UI.Slider(UI.Id("sctx_vol"), new R(menu.X + 14, y, menu.W - 28, 38), "Громкость стрима", ref volPct, 0f, 200f, "%"))
                dc.SetStreamVolume(ws.StreamerUserId, volPct / 100f);
            y += 46;
        }

        Gfx.FillRect(new R(menu.X + 10, y - 3, menu.W - 20, 1), Th.Border);
        bool isFocused = dc.FocusedStreamUserId == _ctxStreamUserId;
        var focRow = new R(menu.X + 8, y + 4, menu.W - 16, 32);
        if (UI.Hover(focRow)) Gfx.FillRect(focRow, Th.Hover, 6);
        Gfx.DrawIcon(isFocused ? IcGrid : IcMax, "", new R(focRow.X + 6, focRow.Y + 8, 16, 16), 14, Th.Sub);
        Gfx.DrawText(isFocused ? "Вернуться в сетку" : "Развернуть на весь экран", new R(focRow.X + 28, focRow.Y + 8, focRow.W - 34, 16), 12.5f, Th.Text, 500);
        if (UI.Click(UI.Id("sctx_focus"), focRow))
        {
            dc.FocusedStreamUserId = isFocused ? null : _ctxStreamUserId;
            _ctxStreamUserId = null;
        }
    }

    // ---------------- 4. User Right-Click Context Menu ----------------

    private static void DrawUserContextMenu()
    {
        if (_ctxUser == null) return;
        var vm = _ctxUser;
        var dc = DiscordClient.Instance;
        var menu = new R(_ctxX, _ctxY, 244, vm.SelfStream ? 202 : 168);
        _overlayCutouts.Add(menu);

        if ((UI.Pressed || UI.RightClicked) && !menu.Contains(UI.MX, UI.MY))
        { _ctxUser = null; return; }

        Gfx.FillRect(menu, 0xFA14151B, 10);
        Gfx.StrokeRect(menu, Th.Border, 1, 10);

        Gfx.DrawText(vm.Username, new R(menu.X + 14, menu.Y + 10, menu.W - 28, 20), 13.5f, Th.Text, 700);
        Gfx.FillRect(new R(menu.X + 10, menu.Y + 34, menu.W - 20, 1), Th.Border);

        float volPct = dc.GetUserVolume(vm.UserId) * 100f;
        if (UI.Slider(UI.Id("ctx_vol"), new R(menu.X + 14, menu.Y + 42, menu.W - 28, 38), "Громкость пользователя", ref volPct, 0f, 200f, "%"))
            dc.SetUserVolume(vm.UserId, volPct / 100f);

        bool isMuted = volPct < 1f;
        if (UI.Toggle(UI.Id("ctx_mute"), new R(menu.X + 8, menu.Y + 86, menu.W - 16, 30), "Заглушить", isMuted))
            dc.SetUserVolume(vm.UserId, isMuted ? 1.0f : 0.0f);

        float by = menu.Y + 124;
        if (vm.SelfStream)
        {
            if (UI.Button(UI.Id("ctx_watch"), new R(menu.X + 10, by, menu.W - 20, 30), "Смотреть стрим", Th.Brand, Th.Text, 6, 12.5f))
            {
                _inVoiceStage = true;
                _ = dc.WatchStreamAsync(_ctxGuildId, vm.ChannelId, vm.UserId, vm.Username, focusImmediately: true, selfMute: _muted, selfDeaf: _deafened);
                _ctxUser = null;
                return;
            }
            by += 36;
        }

        if (UI.Button(UI.Id("ctx_id"), new R(menu.X + 10, by, menu.W - 20, 28), "Копировать ID пользователя", Th.Card, Th.Sub, 6, 12))
        {
            W.CopyText(App.Hwnd, vm.UserId);
            Toast("ID скопирован: " + vm.UserId);
            _ctxUser = null;
        }
    }

    // ---------------- 5. 2-Pane Discord Settings Modal (exact match to f_008.jpg & f_009.jpg!) ----------------

    private static void DrawSettingsOverlay(R r)
    {
        Gfx.FillRect(r, 0xCC07080B);
        float bw = MathF.Min(920, r.W - 48), bh = MathF.Min(700, r.H - 44);
        var box = new R(r.X + (r.W - bw) * 0.5f, r.Y + (r.H - bh) * 0.5f, bw, bh);
        Gfx.FillRect(box, 0xFF1A1B21, 14);
        Gfx.StrokeRect(box, Th.Border, 1, 14);

        // Left Sidebar Nav (212px, matches f_008.jpg!)
        float navW = 212;
        Gfx.FillRect(new R(box.X, box.Y, navW, box.H), 0xFF14151B, 14);
        Gfx.FillRect(new R(box.X + navW, box.Y, 1, box.H), Th.Border);

        var dc = DiscordClient.Instance;
        var myAv = ImageCache.Get(dc.Me.AvatarUrl, 64, ImgMask.Circle);
        var avR = new R(box.X + 16, box.Y + 18, 34, 34);
        if (myAv != null) Gfx.DrawBitmap(myAv, avR);
        else
        {
            Gfx.FillCircle(avR.X + 17, avR.Y + 17, 17, Th.Brand);
            Gfx.DrawText(FirstGrapheme(dc.Me.DisplayName), new R(avR.X, avR.Y + 7, 34, 20), 13.5f, 0xFFFFFFFF, 700, 1);
        }
        Gfx.DrawText(dc.Me.DisplayName, new R(avR.Right + 10, box.Y + 18, navW - 68, 18), 13, Th.Text, 700);
        Gfx.DrawText("Настройки FluxCord", new R(avR.Right + 10, box.Y + 36, navW - 68, 16), 11, Th.Dim);

        if (UI.Button(UI.Id("set_av_pick"), new R(box.X + 12, box.Y + 60, 92, 24), "🖼 Аватар", Th.Card, Th.Cyan, 5, 11f, 600))
            PickAndUploadAvatar();
        if (UI.Button(UI.Id("set_av_dc"), new R(box.X + 108, box.Y + 60, 92, 24), "🔄 Discord", Th.Card, Th.Text, 5, 11f, 600))
        {
            Toast("🔄 Подтягиваю аватар из Discord...");
            _ = dc.TryImportFromDiscordAsync(silent: false);
        }

        Gfx.DrawText("ВЗАИМОДЕЙСТВИЕ", new R(box.X + 16, box.Y + 94, navW - 32, 16), 10.5f, Th.Dim, 700);
        (string Icon, string Label)[] tabs =
        [
            (IcMic, "Голос и видео"),
            (IcMonitor, "Стриминг 4K"),
            (IcSliders, "Интерфейс и сеть"),
            (IcRefresh, "Обновления")
        ];
        for (int i = 0; i < tabs.Length; i++)
        {
            var tr = new R(box.X + 10, box.Y + 116 + i * 36, navW - 20, 32);
            bool sel = _settingsTab == i;
            if (sel) Gfx.FillRect(tr, Th.Active, 6);
            else if (UI.Hover(tr)) Gfx.FillRect(tr, Th.Hover, 6);
            Gfx.DrawIcon(tabs[i].Icon, "", new R(tr.X + 8, tr.Y + 8, 16, 16), 14, sel ? Th.Text : Th.Sub);
            Gfx.DrawText(tabs[i].Label, new R(tr.X + 30, tr.Y + 7, tr.W - 36, 18), 13, sel ? Th.Text : Th.Sub, sel ? 700 : 500);
            if (UI.Click(UI.Id("stab", i), tr)) { _settingsTab = i; _settingsScroll = 0; _settingsOpenDropdown = 0; }
        }

        // Right Pane Header + Close
        float px = box.X + navW + 24, pw = box.W - navW - 48;
        Gfx.DrawText(tabs[_settingsTab].Label, new R(px, box.Y + 18, pw - 50, 26), 18, Th.Text, 700);
        if (UI.Button(UI.Id("set_close"), new R(box.Right - 46, box.Y + 14, 32, 30), "✕", Th.Card, Th.Text, 6))
        { CloseSettingsModal(); return; }
        Gfx.FillRect(new R(box.X + navW, box.Y + 54, box.W - navW, 1), Th.Border);

        var pane = new R(box.X + navW + 1, box.Y + 55, box.W - navW - 1, box.H - 56);
        if (UI.Hover(pane) && UI.Wheel != 0) _settingsScroll = Math.Clamp(_settingsScroll - UI.Wheel * 48f, 0f, _settingsMaxScroll);
        else _settingsScroll = Math.Clamp(_settingsScroll, 0f, _settingsMaxScroll);

        Gfx.PushClip(pane);
        var s = SettingsStore.Current;
        bool changed = false;
        float startY = pane.Y + 18 - _settingsScroll;
        float y = startY;

        if (_settingsTab == 0) // Голос и видео (exact f_009.jpg!)
        {
            float halfW = (pw - 16) * 0.5f;
            Gfx.DrawText("УСТРОЙСТВО ВВОДА", new R(px, y, halfW, 16), 11, Th.Sub, 700);
            Gfx.DrawText("УСТРОЙСТВО ВЫВОДА", new R(px + halfW + 16, y, halfW, 16), 11, Th.Sub, 700);
            y += 20;

            string capName = _capDevices.FirstOrDefault(d => d.Id == s.CaptureDeviceId).Name ?? (_capDevices.FirstOrDefault(d => d.Default).Name ?? "По умолчанию");
            string renName = _renDevices.FirstOrDefault(d => d.Id == s.RenderDeviceId).Name ?? (_renDevices.FirstOrDefault(d => d.Default).Name ?? "По умолчанию");

            var capBox = new R(px, y, halfW, 34);
            Gfx.FillRect(capBox, 0xFF101218, 6);
            Gfx.StrokeRect(capBox, _settingsOpenDropdown == 1 ? Th.Brand : Th.Border, 1, 6);
            Gfx.DrawText(capName, new R(capBox.X + 10, capBox.Y + 9, capBox.W - 32, 16), 12, Th.Text, 500);
            Gfx.DrawIcon(IcChevDown, "v", new R(capBox.Right - 24, capBox.Y + 9, 16, 16), 13, Th.Sub);
            if (UI.Click(UI.Id("s_cap_dd"), capBox)) _settingsOpenDropdown = _settingsOpenDropdown == 1 ? 0 : 1;

            var renBox = new R(px + halfW + 16, y, halfW, 34);
            Gfx.FillRect(renBox, 0xFF101218, 6);
            Gfx.StrokeRect(renBox, _settingsOpenDropdown == 2 ? Th.Brand : Th.Border, 1, 6);
            Gfx.DrawText(renName, new R(renBox.X + 10, renBox.Y + 9, renBox.W - 32, 16), 12, Th.Text, 500);
            Gfx.DrawIcon(IcChevDown, "v", new R(renBox.Right - 24, renBox.Y + 9, 16, 16), 13, Th.Sub);
            if (UI.Click(UI.Id("s_ren_dd"), renBox)) _settingsOpenDropdown = _settingsOpenDropdown == 2 ? 0 : 2;
            y += 42;

            // Inline expanded full device list when clicked!
            if (_settingsOpenDropdown is 1 or 2)
            {
                var list = _settingsOpenDropdown == 1 ? _capDevices : _renDevices;
                float ddX = _settingsOpenDropdown == 1 ? px : px + halfW + 16;
                var ddRect = new R(ddX, y - 4, halfW, Math.Max(1, list.Count) * 30 + 8);
                Gfx.FillRect(ddRect, 0xFF111218, 8);
                Gfx.StrokeRect(ddRect, Th.Brand, 1, 8);
                float dy2 = ddRect.Y + 4;
                for (int i = 0; i < list.Count; i++)
                {
                    var d = list[i];
                    bool sel = _settingsOpenDropdown == 1
                        ? (string.IsNullOrEmpty(s.CaptureDeviceId) ? d.Default : d.Id == s.CaptureDeviceId)
                        : (string.IsNullOrEmpty(s.RenderDeviceId) ? d.Default : d.Id == s.RenderDeviceId);
                    var rr = new R(ddRect.X + 4, dy2, ddRect.W - 8, 28);
                    if (UI.Hover(rr)) Gfx.FillRect(rr, Th.Hover, 5);
                    Gfx.DrawText(d.Name, new R(rr.X + 8, rr.Y + 6, rr.W - 30, 16), 12, sel ? Th.Text : Th.Sub, sel ? 700 : 500);
                    DrawRadioDot(rr.Right - 14, rr.Y + 14, sel);
                    if (UI.Click(UI.Id("s_dd_it_" + _settingsOpenDropdown, i), rr))
                    {
                        if (_settingsOpenDropdown == 1) s.CaptureDeviceId = d.Id;
                        else s.RenderDeviceId = d.Id;
                        changed = true;
                        if (dc.VoiceConnected || _loopback) MediaNative.fc_voice_start(s.CaptureDeviceId, s.RenderDeviceId);
                        _settingsOpenDropdown = 0;
                    }
                    dy2 += 30;
                }
                y = ddRect.Bottom + 10;
            }

            float micPct = s.InputGain * 100f;
            if (UI.Slider(UI.Id("sl_mic"), new R(px, y, halfW, 38), "Громкость микрофона", ref micPct, 0f, 200f, "%")) { s.InputGain = micPct / 100f; changed = true; }
            float spkPct = s.OutputVolume * 100f;
            if (UI.Slider(UI.Id("sl_spk"), new R(px + halfW + 16, y, halfW, 38), "Громкость динамика", ref spkPct, 0f, 200f, "%")) { s.OutputVolume = spkPct / 100f; changed = true; }
            y += 48;

            // Проверка микрофона + 40-segment VU bar (exact f_009.jpg!)
            if (UI.Button(UI.Id("btn_mictest"), new R(px, y, 162, 32), _loopback ? "Стоп проверки" : "Проверка микрофона", _loopback ? Th.Red : Th.Brand, 0xFFFFFFFF, 6, 12.5f))
            {
                _loopback = !_loopback;
                if (_loopback && !dc.VoiceConnected) MediaNative.fc_voice_start(s.CaptureDeviceId, s.RenderDeviceId);
                ApplyAudioSettings();
                if (!_loopback && !dc.VoiceConnected) MediaNative.fc_voice_stop();
            }
            float mx = px + 174, mw = pw - 174;
            int segs = 40;
            float sw = (mw - (segs - 1) * 2f) / segs;
            int lit = (int)(Math.Clamp(_audioStats.CleanRms * 5.5f, 0f, 1f) * segs);
            for (int i = 0; i < segs; i++)
                Gfx.FillRect(new R(mx + i * (sw + 2f), y + 7, sw, 18), i < lit ? (i > 30 ? Th.Amber : Th.Green) : 0xFF2A2D38, 1.5f);
            y += 46;

            Gfx.FillRect(new R(px, y, pw, 1), Th.Border);
            y += 14;
            Gfx.DrawText($"ШУМОПОДАВЛЕНИЕ  •  Активно: {_audioStats.GetBackendName()} (−{_audioStats.NoiseReductionDb:F1} dB)", new R(px, y, pw, 18), 11.5f, Th.Cyan, 700);
            y += 24;

            (int Mode, string Label, string Sub)[] modes =
            [
                ((int)NoiseMode.AutoBest, "Auto Best (Максимум)", "Автовыбор лучшего нейросетевого движка"),
                ((int)NoiseMode.DeepFilterNet3, "Изоляция голоса — DeepFilter / MMSE-LSA + KeyKill", "Чистый COLA STFT без металлических артефактов"),
                ((int)NoiseMode.NvidiaBroadcast, "NVIDIA Broadcast AFX (RTX Tensor)", "Аппаратное шумоподавление RTX 4070 Ti"),
                ((int)NoiseMode.Rnnoise, "RNNoise Neural (Ультралёгкий)", "Минимальная задержка и <0.5% CPU"),
                ((int)NoiseMode.StudioDsp, "Студия — Studio DSP (HPF 80Hz + Gate + AGC)", "Открытый чистый микрофон для студийных USB-микрофонов"),
                ((int)NoiseMode.Off, "Выкл (Bypass)", "Без обработки сигнала")
            ];
            for (int i = 0; i < modes.Length; i++)
            {
                bool sel = s.NoiseMode == modes[i].Mode;
                var mr = new R(px, y, pw, 38);
                if (sel) Gfx.FillRect(mr, 0x225865F2, 6);
                else if (UI.Hover(mr)) Gfx.FillRect(mr, Th.Hover, 6);
                DrawRadioDot(mr.X + 14, mr.Y + 19, sel);
                Gfx.DrawText(modes[i].Label, new R(mr.X + 32, mr.Y + 4, mr.W - 40, 16), 12.5f, Th.Text, sel ? 700 : 600);
                Gfx.DrawText(modes[i].Sub, new R(mr.X + 32, mr.Y + 20, mr.W - 40, 14), 10.5f, Th.Dim);
                if (UI.Click(UI.Id("nmode", i), mr)) { s.NoiseMode = modes[i].Mode; changed = true; }
                y += 40;
            }
            y += 8;

            float str = s.NoiseStrength;
            if (UI.Slider(UI.Id("sl_str"), new R(px, y, halfW, 38), "Сила шумоподавления", ref str, 0f, 1f)) { s.NoiseStrength = str; changed = true; }
            float gate = s.GateDb;
            if (UI.Slider(UI.Id("sl_gate"), new R(px + halfW + 16, y, halfW, 38), "Чувствительность ввода (Gate)", ref gate, -70f, -15f, " dB")) { s.GateDb = gate; changed = true; }
            y += 46;

            if (UI.Toggle(UI.Id("tg_hpf"), new R(px, y, pw, 30), "High-Pass 80 Hz (убирает гул стола и вентиляторов)", s.HpfEnabled)) { s.HpfEnabled = !s.HpfEnabled; changed = true; }
            y += 34;
            if (UI.Toggle(UI.Id("tg_agc"), new R(px, y, pw, 30), "Soft-Knee AGC + Лимитер (-0.5 dBFS)", s.AgcEnabled)) { s.AgcEnabled = !s.AgcEnabled; changed = true; }
            y += 40;

            Gfx.FillRect(new R(px, y, pw, 1), Th.Border);
            y += 12;
            Gfx.DrawText("ЗВУКИ ИНТЕРФЕЙСА (ВХОД / ВЫХОД / МИКРОФОН / СООБЩЕНИЯ)", new R(px, y, pw, 16), 11, Th.Cyan, 700);
            y += 22;
            if (UI.Toggle(UI.Id("tg_uisnd"), new R(px, y, pw, 30), "Звуки интерфейса Discord (вход, выход, мьют, стрим)", s.UiSoundsEnabled))
            { s.UiSoundsEnabled = !s.UiSoundsEnabled; changed = true; if (s.UiSoundsEnabled) SoundFx.Play(UiSound.Unmute); }
            y += 34;
            float uvol = s.UiSoundsVolume * 100f;
            if (UI.Slider(UI.Id("sl_uvol"), new R(px, y, pw - 118, 38), "Громкость звуков UI", ref uvol, 0f, 100f, "%"))
            { s.UiSoundsVolume = uvol / 100f; changed = true; }
            if (UI.Button(UI.Id("btn_sndtest"), new R(px + pw - 108, y + 6, 108, 28), "🔊 Тест", Th.Card, Th.Text, 6, 12))
            { SettingsStore.Save(); SoundFx.Play(UiSound.VoiceJoin); }
            y += 46;
        }
        else if (_settingsTab == 1) // Стриминг 4K
        {
            Gfx.DrawText("4K ZERO-COPY NVENC (RTX 4070 Ti)", new R(px, y, pw, 18), 12, Th.Cyan, 700);
            y += 28;
            (int W, int H, string L)[] resP = [(1280, 720, "720p"), (1920, 1080, "1080p"), (2560, 1440, "1440p"), (3840, 2160, "4K 2160p"), (0, 0, "Source")];
            float rw = (pw - 24) / 5f;
            for (int i = 0; i < resP.Length; i++)
            {
                bool sel = s.StreamHeight == resP[i].H;
                if (UI.Button(UI.Id("sr", i), new R(px + i * (rw + 6), y, rw, 34), resP[i].L, sel ? Th.Brand : Th.Card, Th.Text, 6, 12.5f))
                { s.StreamWidth = resP[i].W; s.StreamHeight = resP[i].H; changed = true; }
            }
            y += 44;

            int[] fpsP = [15, 30, 60, 120];
            float fw = (pw - 18) / 4f;
            for (int i = 0; i < fpsP.Length; i++)
            {
                bool sel = s.StreamFps == fpsP[i];
                if (UI.Button(UI.Id("sf", i), new R(px + i * (fw + 6), y, fw, 34), $"{fpsP[i]} FPS", sel ? Th.Brand : Th.Card, Th.Text, 6, 12.5f))
                { s.StreamFps = fpsP[i]; changed = true; }
            }
            y += 44;

            Gfx.FillRect(new R(px, y, pw, 34), 0x225865F2, 6);
            Gfx.StrokeRect(new R(px, y, pw, 34), Th.Brand, 1, 6);
            Gfx.DrawText("Кодек трансляции: H.264 NVENC (аппаратный профиль Discord Go Live + DAVE E2EE)", new R(px + 12, y + 9, pw - 24, 16), 12f, Th.Text, 600);
            y += 48;

            float kbps = s.StreamBitrateKbps / 1000f;
            if (UI.Slider(UI.Id("s_br"), new R(px, y, pw, 40), "Битрейт трансляции", ref kbps, 4f, 35f, " Мбит/с"))
            { s.StreamBitrateKbps = (int)(kbps * 1000f); changed = true; }
            y += 52;

            if (UI.Button(UI.Id("s_open_sh"), new R(px, y, 260, 38), "Открыть выбор экрана / окна...", Th.Brand, 0xFFFFFFFF, 8, 13, 700))
            { CloseSettingsModal(); OpenScreenShareModal(); }
            if (_monitors.Count >= 2 && UI.Button(UI.Id("s_go_dual"), new R(px + 272, y, 236, 38), "⚡ Стримить 2 Экрана сразу", Th.Cyan, 0xFF0A0B0E, 8, 13, 700))
            { CloseSettingsModal(); StartStream(-2, 0); }
            y += 48;
        }
        else if (_settingsTab == 2) // Интерфейс, Турбо-сеть и миграция
        {
            Gfx.DrawText("⚡ ТУРБО-СЕТЬ И РЕГИОНЫ RTC (QOS DSCP EF46 + 1ms TIMER + 20ms JITTER)", new R(px, y, pw, 18), 11.5f, Th.Green, 700);
            y += 24;
            string curReg = string.IsNullOrEmpty(s.PreferredRtcRegion)
                ? $"Авто-быстрейший ({(string.IsNullOrEmpty(dc.BestRtcRegion) ? "замер..." : dc.BestRtcRegion)})"
                : s.PreferredRtcRegion;
            Gfx.DrawText($"Текущий сервер голоса: {(string.IsNullOrEmpty(dc.VoiceEndpointHost) ? "не подключён" : dc.VoiceEndpointHost)}  •  Предпочтительный: {curReg}", new R(px, y, pw, 18), 12f, Th.Text, 600);
            y += 24;
            if (UI.Button(UI.Id("set_bench"), new R(px, y, 210, 32), dc.IsBenchmarkingRegions ? "Замер серверов..." : "⚡ Замерить и выбрать быстрейший", Th.Green, 0xFFFFFFFF, 6, 12))
                _ = ShellAsync.AutoBestRegionAsync(forceBenchmark: true);
            if (UI.Button(UI.Id("set_vinfo"), new R(px + 220, y, 220, 32), "Открыть список серверов RTC...", Th.Card, Th.Cyan, 6, 12))
            { CloseSettingsModal(); _showVoiceInfo = true; _voiceInfoTab = 1; }
            y += 44;

            Gfx.FillRect(new R(px, y, pw, 1), Th.Border);
            y += 14;
            Gfx.DrawText("ПЕРЕНЕСЁННЫЕ НАСТРОЙКИ (DORION / DISCORD)", new R(px, y, pw, 18), 12, Th.Cyan, 700);
            y += 24;
            Gfx.DrawText(SettingsStore.MigrationSummary, new R(px, y, pw, 20), 12.5f, Th.Green, 600);
            y += 30;
            if (UI.Toggle(UI.Id("tg_ptt"), new R(px, y, pw, 34), $"Push-to-Talk ({s.PushToTalkKey})", s.PushToTalk)) { s.PushToTalk = !s.PushToTalk; changed = true; }
            y += 38;
            if (UI.Toggle(UI.Id("tg_tray"), new R(px, y, pw, 34), "Сворачивать в фоновый трей при закрытии на крестик (✕)", s.CloseToTray)) { s.CloseToTray = !s.CloseToTray; changed = true; }
            y += 38;
            if (UI.Toggle(UI.Id("tg_not"), new R(px, y, pw, 34), "Уведомления рабочего стола", s.DesktopNotifications)) { s.DesktopNotifications = !s.DesktopNotifications; changed = true; }
            y += 38;
            if (UI.Toggle(UI.Id("tg_stm"), new R(px, y, pw, 34), "Режим стримера (скрывать личные данные)", s.StreamerMode)) { s.StreamerMode = !s.StreamerMode; changed = true; }
            y += 48;
            if (UI.Button(UI.Id("btn_remig"), new R(px, y, 310, 36), "Повторно импортировать из Dorion/Discord", Th.Card, Th.Text, 6, 12.5f))
            { SettingsStore.MigrateFromExistingClients(); SettingsStore.Save(); Toast(SettingsStore.MigrationSummary); }
            if (UI.Button(UI.Id("btn_quit_full"), new R(px + 320, y, 210, 36), "Полностью закрыть (Ctrl+Q)", 0x22ED4245, Th.Red, 6, 12f))
                App.RequestExit();
            y += 46;
        }
        else // Ультра-обновления (GitHub Releases + возврат в звонок и стримы!)
        {
            Gfx.DrawText("⚡ УЛЬТРА-ОБНОВЛЕНИЯ FLUXCORD (GITHUB RELEASES)", new R(px, y, pw, 18), 12, Th.Green, 700);
            y += 24;
            Gfx.DrawText($"Установлена версия: v{Updater.CurrentVersion}  •  Репозиторий: github.com/{Updater.Repo}", new R(px, y, pw, 18), 12.5f, Th.Text, 600);
            y += 24;
            Gfx.DrawText(Updater.StatusText, new R(px, y, pw, 18), 12.5f, Updater.HasUpdate ? Th.Green : Th.Sub, 600);
            y += 26;

            if (Updater.Downloading || Updater.IsReadyToApply)
            {
                var pb = new R(px, y, pw, 10);
                Gfx.FillRect(pb, 0xFF232630, 5);
                Gfx.FillRect(new R(pb.X, pb.Y, pb.W * (float)Updater.Progress, pb.H), Th.Green, 5);
                y += 18;
            }

            if (Updater.HasUpdate)
            {
                string actLbl = Updater.IsReadyToApply
                    ? $"⚡ Установить v{Updater.AvailableVersion} сейчас (вернёт в звонок и стримы!)"
                    : (Updater.Downloading ? "Скачивание..." : $"⬇ Скачать и установить v{Updater.AvailableVersion}");
                if (UI.Button(UI.Id("upd_go"), new R(px, y, MathF.Min(pw, 440), 38), actLbl, Th.Green, 0xFFFFFFFF, 8, 13, 700))
                    Updater.StartUpdateNow();
                y += 46;
            }

            if (UI.Button(UI.Id("upd_chk"), new R(px, y, 220, 34), Updater.Checking ? "Проверка..." : "↻ Проверить обновления", Th.Brand, 0xFFFFFFFF, 6, 12.5f))
                Updater.Check(manual: true);
            if (UI.Button(UI.Id("upd_gh"), new R(px + 230, y, 210, 34), "Открыть GitHub Releases", Th.Card, Th.Cyan, 6, 12.5f))
                W.OpenUrl($"https://github.com/{Updater.Repo}/releases");
            y += 46;

            if (!string.IsNullOrWhiteSpace(Updater.ReleaseNotes))
            {
                var notesBox = new R(px, y, pw, 96);
                Gfx.FillRect(notesBox, Th.Panel, 8);
                Gfx.StrokeRect(notesBox, Th.Border, 1, 8);
                Gfx.DrawText("Что нового:", new R(notesBox.X + 12, notesBox.Y + 8, notesBox.W - 24, 16), 11.5f, Th.Cyan, 700);
                Gfx.DrawText(Updater.ReleaseNotes, new R(notesBox.X + 12, notesBox.Y + 26, notesBox.W - 24, 64), 11.5f, Th.Sub, 400, 0, true);
                y += 108;
            }

            Gfx.FillRect(new R(px, y, pw, 1), Th.Border);
            y += 14;
            if (UI.Toggle(UI.Id("tg_uchk"), new R(px, y, pw, 34), "Автоматически проверять обновления на GitHub", s.AutoCheckUpdates)) { s.AutoCheckUpdates = !s.AutoCheckUpdates; changed = true; }
            y += 38;
            if (UI.Toggle(UI.Id("tg_udl"), new R(px, y, pw, 34), "Фоновая предзагрузка (установка за <1 сек с автовозвратом в голосовой канал)", s.AutoDownloadUpdates)) { s.AutoDownloadUpdates = !s.AutoDownloadUpdates; changed = true; }
            y += 44;
            Gfx.DrawText("Во время обновления FluxCord сохраняет твой голосовой канал, статус микрофона/наушников, открытые стримы друзей и твою трансляцию экрана — и автоматически возвращает всё обратно сразу после запуска новой версии.", new R(px, y, pw, 48), 11.5f, Th.Dim, 400, 0, true);
            y += 56;
        }

        _settingsMaxScroll = MathF.Max(0f, (y - startY) - pane.H + 36f);
        Gfx.PopClip();
        if (changed) { ApplyAudioSettings(); SettingsStore.Save(); }
    }

    // ---------------- 6. Live-Search Ctrl+K Quick Switcher ----------------

    private static void DrawQuickSwitcher(R r)
    {
        Gfx.FillRect(r, 0xCC07080B);
        var box = new R(r.X + (r.W - 560) * 0.5f, r.Y + 54, 560, MathF.Min(500, r.H - 110));
        Gfx.FillRect(box, Th.Panel, 12);
        Gfx.StrokeRect(box, Th.Brand, 1.5f, 12);
        Gfx.DrawText("Быстрый переход (Ctrl+K) — ↑/↓ и Enter", new R(box.X + 18, box.Y + 14, box.W - 60, 22), 14.5f, Th.Text, 700);
        if (UI.Button(UI.Id("qs_x"), new R(box.Right - 36, box.Y + 10, 26, 26), "✕", Th.Card, Th.Text, 6)) { _showQuickSwitch = false; _searchQuery = ""; return; }

        _searchInputRect = new R(box.X + 16, box.Y + 44, box.W - 32, 34);
        _wantSearchInput = true;
        Gfx.FillRect(_searchInputRect, Th.Bg, 8);
        Gfx.StrokeRect(_searchInputRect, Th.Border, 1, 8);

        var dc = DiscordClient.Instance;
        var items = GetQuickSwitchItems(_searchQuery);
        if (items.Count > 0) _quickSwitchIdx = Math.Clamp(_quickSwitchIdx, 0, items.Count - 1);
        else _quickSwitchIdx = 0;

        float y = box.Y + 88;
        for (int i = 0; i < items.Count && y + 32 <= box.Bottom - 10; i++)
        {
            var it = items[i];
            var rr = new R(box.X + 12, y, box.W - 24, 30);
            bool sel = i == _quickSwitchIdx;
            if (sel) Gfx.FillRect(rr, Th.Active, 6);
            else if (UI.Hover(rr)) { Gfx.FillRect(rr, Th.Hover, 6); _quickSwitchIdx = i; }
            Gfx.DrawText(it.Title, new R(rr.X + 10, rr.Y + 6, 300, 18), 13, it.IsVoice ? Th.Green : Th.Text, 600);
            Gfx.DrawText(it.Sub, new R(rr.Right - 190, rr.Y + 6, 180, 18), 11.5f, Th.Dim, 400, 2);
            if (UI.Click(UI.Id("qs", i), rr))
            {
                if (it.IsVoice)
                {
                    dc.ActiveGuildId = it.GuildId;
                    _ = dc.JoinVoiceAsync(it.GuildId, it.ChannelId, _muted, _deafened);
                    _inVoiceStage = true;
                }
                else
                {
                    SwitchToChannel(it.GuildId, it.ChannelId);
                }
                _showQuickSwitch = false; _searchQuery = "";
                return;
            }
            y += 32;
        }
        if (items.Count == 0)
            Gfx.DrawText("Ничего не найдено по запросу «" + _searchQuery + "»", new R(box.X + 16, y + 12, box.W - 32, 22), 13, Th.Dim, 500, 1);
    }

    // ---------------- 7. Exact Discord Voice Connection Info + Live Ping Graph + RTC Turbo Region Switcher (c_026.jpg!) ----------------

    private static void DrawVoiceInfoPopup()
    {
        var dc = DiscordClient.Instance;
        float w = 310, h = _voiceInfoTab == 0 ? 316 : 360;
        float x = RailW + 8;
        float y = Math.Clamp(Gfx.H - 166 - h, TopH + 8, Gfx.H - h - 68);
        var pop = new R(x, y, w, h);
        _overlayCutouts.Add(pop);

        if ((UI.Pressed || UI.RightClicked) && !pop.Contains(UI.MX, UI.MY))
        { _showVoiceInfo = false; return; }

        Gfx.FillRect(pop, 0xFA14151B, 12);
        Gfx.StrokeRect(pop, Th.Border, 1, 12);

        // Tabs: Интеграция | Серверы ⚡
        var t0 = new R(pop.X + 12, pop.Y + 10, 132, 26);
        var t1 = new R(pop.X + 150, pop.Y + 10, 148, 26);
        Gfx.DrawText("Интеграция", new R(t0.X, t0.Y + 4, t0.W, 18), 12.5f, _voiceInfoTab == 0 ? Th.Text : Th.Sub, 700, 1);
        Gfx.DrawText("Серверы RTC ⚡", new R(t1.X, t1.Y + 4, t1.W, 18), 12.5f, _voiceInfoTab == 1 ? Th.Cyan : Th.Sub, 700, 1);
        Gfx.FillRect(new R(_voiceInfoTab == 0 ? t0.X : t1.X, pop.Y + 38, _voiceInfoTab == 0 ? t0.W : t1.W, 2), Th.Brand, 1);
        Gfx.FillRect(new R(pop.X + 10, pop.Y + 40, pop.W - 20, 1), Th.Border);
        if (UI.Click(UI.Id("vi_t0"), t0)) _voiceInfoTab = 0;
        if (UI.Click(UI.Id("vi_t1"), t1))
        {
            _voiceInfoTab = 1;
            if (dc.Regions.Count == 0) _ = dc.BenchmarkRegionsAsync();
        }

        if (_voiceInfoTab == 0)
        {
            // Live Ping Bar Graph (exact c_026.jpg!)
            var gr = new R(pop.X + 14, pop.Y + 50, pop.W - 28, 76);
            Gfx.FillRect(gr, 0xFF0E1016, 6);
            Gfx.StrokeRect(gr, Th.Border, 1, 6);
            Gfx.FillRect(new R(gr.X + 4, gr.Y + gr.H * 0.5f, gr.W - 32, 1), 0x16FFFFFF);
            Gfx.DrawText("150", new R(gr.Right - 28, gr.Y + 4, 24, 14), 9.5f, Th.Dim, 600, 2);
            Gfx.DrawText("75", new R(gr.Right - 28, gr.Y + gr.H * 0.44f, 24, 14), 9.5f, Th.Dim, 600, 2);
            Gfx.DrawText("0", new R(gr.Right - 28, gr.Bottom - 16, 24, 14), 9.5f, Th.Dim, 600, 2);

            List<int> hist;
            lock (dc.VoicePingHistory) hist = [.. dc.VoicePingHistory];
            if (hist.Count > 0)
            {
                float plotW = gr.W - 34;
                float bw = MathF.Max(3f, plotW / 40f);
                int start = Math.Max(0, hist.Count - 40);
                for (int i = start; i < hist.Count; i++)
                {
                    int p = hist[i];
                    float frac = Math.Clamp(p / 160f, 0.06f, 1f);
                    float bh = frac * (gr.H - 12);
                    uint col = p > 150 ? Th.Red : (p > 95 ? Th.Amber : Th.Green);
                    float bx = gr.X + 4 + (i - start) * bw;
                    Gfx.FillRect(new R(bx, gr.Bottom - 6 - bh, MathF.Max(2f, bw - 1.5f), bh), col, 1);
                }
            }
            else
            {
                Gfx.DrawText("Замер задержки...", new R(gr.X, gr.Y + 28, gr.W - 28, 18), 11.5f, Th.Dim, 500, 1);
            }

            float ty = gr.Bottom + 10;
            string host = string.IsNullOrEmpty(dc.VoiceEndpointHost) ? "Подключение..." : dc.VoiceEndpointHost;
            Gfx.DrawText(host, new R(pop.X + 14, ty, pop.W - 28, 18), 13.5f, Th.Text, 700);
            ty += 22;
            Gfx.DrawText($"Средняя задержка: {dc.VoicePingAvgMs} мс", new R(pop.X + 14, ty, pop.W - 28, 16), 12f, Th.Sub, 600);
            ty += 18;
            Gfx.DrawText($"Последняя задержка: {dc.VoicePingMs} мс", new R(pop.X + 14, ty, pop.W - 28, 16), 12f, Th.Sub, 600);
            ty += 20;
            string qos = $"QoS DSCP EF46 • Opus 128k • TX {_audioStats.TxPackets} / RX {_audioStats.RxPackets}";
            Gfx.DrawText(qos, new R(pop.X + 14, ty, pop.W - 28, 16), 10.5f, Th.Cyan, 600);
            ty += 22;
            if (dc.IsPublishingStream)
            {
                Gfx.DrawText($"Стрим ({dc.PubEndpointHost}): V={dc.PubVideoTxPkts} A={dc.PubAudioTxPkts}", new R(pop.X + 14, ty, pop.W - 28, 16), 10.5f, Th.Amber, 600);
                ty += 20;
            }

            if (UI.Button(UI.Id("vi_hop"), new R(pop.X + 14, pop.Bottom - 78, pop.W - 28, 30), "⚡ Выбрать быстрейший сервер RTC", Th.Card, Th.Cyan, 6, 12))
                _voiceInfoTab = 1;

            // Green E2EE DAVE verified footer (exact c_026.jpg!)
            var e2e = new R(pop.X + 14, pop.Bottom - 40, pop.W - 28, 28);
            Gfx.FillRect(e2e, 0x2223A55A, 6);
            Gfx.DrawText("🔒 Защищено сквозным шифрованием (DAVE)", new R(e2e.X + 6, e2e.Y + 6, e2e.W - 12, 16), 11f, Th.Green, 600, 1);
        }
        else
        {
            // Серверы RTC ⚡ — Live Region Benchmarker & 1-Click Server Hopper!
            var topBtn = new R(pop.X + 12, pop.Y + 48, 138, 28);
            if (UI.Button(UI.Id("vi_rebench"), topBtn, dc.IsBenchmarkingRegions ? "Замер..." : "↻ Замерить пинг", Th.Card, Th.Text, 6, 11.5f))
                _ = dc.BenchmarkRegionsAsync();

            var autoBtn = new R(pop.X + 156, pop.Y + 48, 142, 28);
            if (UI.Button(UI.Id("vi_autobest"), autoBtn, "⚡ Авто-лучший", Th.Green, 0xFFFFFFFF, 6, 11.5f))
                _ = ShellAsync.AutoBestRegionAsync(forceBenchmark: false);

            List<RtcRegionInfo> regs;
            lock (dc.Regions) regs = [.. dc.Regions];
            var regArea = new R(pop.X + 6, pop.Y + 82, pop.W - 12, pop.H - 90);
            float totalRegH = 28 + regs.Count * 27f + 8;
            float maxRegSc = MathF.Max(0, totalRegH - regArea.H);
            if (UI.Hover(regArea) && UI.Wheel != 0) _regionsScroll = Math.Clamp(_regionsScroll - UI.Wheel * 40f, 0f, maxRegSc);
            else _regionsScroll = Math.Clamp(_regionsScroll, 0f, maxRegSc);

            Gfx.PushClip(regArea);
            float ry = regArea.Y + 2 - _regionsScroll;
            string curPref = SettingsStore.Current.PreferredRtcRegion;

            // Automatic row
            var autoRow = new R(pop.X + 10, ry, pop.W - 20, 26);
            if (autoRow.Bottom >= regArea.Y && autoRow.Y <= regArea.Bottom)
            {
                if (UI.Hover(autoRow) || string.IsNullOrEmpty(curPref)) Gfx.FillRect(autoRow, string.IsNullOrEmpty(curPref) ? 0x2223A55A : Th.Hover, 5);
                Gfx.DrawText("Авто (лучший сервер)", new R(autoRow.X + 8, autoRow.Y + 5, 190, 16), 12f, string.IsNullOrEmpty(curPref) ? Th.Green : Th.Text, 600);
                if (UI.Click(UI.Id("vi_reg_auto"), autoRow))
                {
                    _ = ShellAsync.SwitchRegionAsync(null);
                    _showVoiceInfo = false;
                    Gfx.PopClip();
                    return;
                }
            }
            ry += 28;

            for (int i = 0; i < regs.Count; i++)
            {
                var reg = regs[i];
                var rr = new R(pop.X + 10, ry, pop.W - 20, 26);
                if (rr.Bottom >= regArea.Y && rr.Y <= regArea.Bottom)
                {
                    bool sel = string.Equals(curPref, reg.Id, StringComparison.OrdinalIgnoreCase);
                    if (sel) Gfx.FillRect(rr, 0x225865F2, 5);
                    else if (UI.Hover(rr)) Gfx.FillRect(rr, Th.Hover, 5);

                    Gfx.DrawText(reg.Name, new R(rr.X + 8, rr.Y + 5, 185, 16), 12f, sel ? Th.Text : Th.Sub, sel ? 700 : 500);
                    string pTxt = reg.PingMs > 0 ? $"{reg.PingMs} мс" : "RTC";
                    uint pCol = reg.PingMs > 0 ? (reg.PingMs < 100 ? Th.Green : (reg.PingMs < 160 ? Th.Amber : Th.Red)) : Th.Dim;
                    Gfx.DrawText(pTxt, new R(rr.Right - 66, rr.Y + 5, 58, 16), 11.5f, pCol, 700, 2);

                    if (UI.Click(UI.Id("vi_reg", i), rr))
                    {
                        _ = ShellAsync.SwitchRegionAsync(reg.Id);
                        _showVoiceInfo = false;
                        Gfx.PopClip();
                        return;
                    }
                }
                ry += 27;
            }
            Gfx.PopClip();
        }
    }

    // ---------------- 8. Sidebar Hover Stream Preview Card (exact c_070.jpg!) ----------------

    private static void DrawHoverStreamPreview()
    {
        if (_hoverStreamVm == null) return;
        var dc = DiscordClient.Instance;
        float w = 236, h = 158;
        float x = RailW + SideW + 8;
        float y = Math.Clamp(_hoverStreamY - 12, TopH + 8, Gfx.H - h - 12);
        var card = new R(x, y, w, h);

        bool stillHover = card.Contains(UI.MX, UI.MY) || (Environment.TickCount64 - _hoverStreamLastTick < 260);
        if (!stillHover) { _hoverStreamVm = null; return; }

        _overlayCutouts.Add(card);
        Gfx.FillRect(card, 0xFA14151B, 10);
        Gfx.StrokeRect(card, Th.Border, 1, 10);

        Gfx.DrawText("Сейчас стримит", new R(card.X + 12, card.Y + 10, 140, 16), 11.5f, Th.Sub, 600);
        Gfx.FillRect(new R(card.Right - 66, card.Y + 9, 54, 18), Th.Red, 9);
        Gfx.DrawText("В ЭФИРЕ", new R(card.Right - 66, card.Y + 10, 54, 15), 9.5f, 0xFFFFFFFF, 700, 1);

        var prev = new R(card.X + 12, card.Y + 34, card.W - 24, 72);
        Gfx.FillRect(prev, 0xFF22252E, 6);
        Gfx.DrawText(_hoverStreamVm.Username, new R(prev.X + 8, prev.Y + 16, prev.W - 16, 18), 12.5f, Th.Text, 700, 1);
        Gfx.DrawText("Стрим начался. Не пропустите его!", new R(prev.X + 8, prev.Y + 38, prev.W - 16, 18), 11f, Th.Sub, 500, 1);

        bool watching = dc.IsWatchingStream(_hoverStreamVm.UserId);
        if (UI.Button(UI.Id("hsp_watch"), new R(card.X + 12, card.Bottom - 40, card.W - 24, 30), watching ? "Перейти к стриму" : "Смотреть стрим", watching ? Th.Card : Th.Brand, Th.Text, 6, 12))
        {
            var vm = _hoverStreamVm;
            _hoverStreamVm = null;
            _inVoiceStage = true;
            _ = dc.WatchStreamAsync(_hoverStreamGuildId, vm.ChannelId, vm.UserId, vm.Username, focusImmediately: true, selfMute: _muted, selfDeaf: _deafened);
        }
    }

    // ---------------- 9. Presence Status Switcher + Logout Popup ----------------

    private static void DrawStatusPopup()
    {
        var dc = DiscordClient.Instance;
        float w = 244, h = 352;
        var pop = new R(RailW + 8, Gfx.H - 68 - h, w, h);
        _overlayCutouts.Add(pop);
        if ((UI.Pressed || UI.RightClicked) && !pop.Contains(UI.MX, UI.MY))
        { _showStatusMenu = false; return; }

        Gfx.FillRect(pop, 0xFA14151B, 10);
        Gfx.StrokeRect(pop, Th.Border, 1, 10);
        Gfx.DrawText(dc.Me.DisplayName, new R(pop.X + 12, pop.Y + 10, pop.W - 24, 18), 13f, Th.Text, 700);
        Gfx.FillRect(new R(pop.X + 10, pop.Y + 34, pop.W - 20, 1), Th.Border);

        (string Key, string Label, uint Dot)[] sts =
        [
            ("online", "В сети", Th.Green),
            ("idle", "Неактивен", Th.Amber),
            ("dnd", "Не беспокоить", Th.Red),
            ("invisible", "Невидимый", Th.Dim)
        ];
        float y = pop.Y + 42;
        for (int i = 0; i < sts.Length; i++)
        {
            bool sel = dc.Me.Status == sts[i].Key;
            var rr = new R(pop.X + 8, y, pop.W - 16, 28);
            if (sel) Gfx.FillRect(rr, Th.Active, 6);
            else if (UI.Hover(rr)) Gfx.FillRect(rr, Th.Hover, 6);
            Gfx.FillCircle(rr.X + 14, rr.Y + 14, 5, sts[i].Dot);
            Gfx.DrawText(sts[i].Label, new R(rr.X + 28, rr.Y + 6, rr.W - 34, 16), 12.5f, Th.Text, sel ? 700 : 500);
            if (UI.Click(UI.Id("st_opt", i), rr))
            {
                _ = dc.SetPresenceStatusAsync(sts[i].Key);
                _showStatusMenu = false;
                return;
            }
            y += 30;
        }

        Gfx.FillRect(new R(pop.X + 10, y + 3, pop.W - 20, 1), Th.Border);
        y += 9;
        if (UI.Button(UI.Id("st_av_pick"), new R(pop.X + 8, y, pop.W - 16, 28), "🖼 Сменить аватарку (файл)", Th.Brand, 0xFFFFFFFF, 6, 12f, 600))
        {
            _showStatusMenu = false;
            PickAndUploadAvatar();
            return;
        }
        y += 33;
        if (UI.Button(UI.Id("st_av_dc"), new R(pop.X + 8, y, pop.W - 16, 28), "🔄 Подтянуть аватар из Discord", Th.Card, Th.Cyan, 6, 12f, 600))
        {
            _showStatusMenu = false;
            Toast("🔄 Подтягиваю аватар из Discord...");
            _ = dc.TryImportFromDiscordAsync(silent: false);
            return;
        }
        y += 33;
        if (UI.Button(UI.Id("st_nick"), new R(pop.X + 8, y, pop.W - 16, 28), "✏ Сменить ник (/nick)", Th.Card, Th.Text, 6, 12f, 600))
        {
            _showStatusMenu = false;
            _inVoiceStage = false;
            _showFriendsView = false;
            App.SetChatInputText("/nick " + dc.Me.DisplayName);
            Toast("Введите новый ник в поле чата и нажмите Enter");
            return;
        }
        y += 35;

        Gfx.FillRect(new R(pop.X + 10, y, pop.W - 20, 1), Th.Border);
        if (UI.Button(UI.Id("st_logout"), new R(pop.X + 8, y + 6, pop.W - 16, 28), "Выйти из комнаты", Th.Card, Th.Sub, 6, 12))
        {
            _showStatusMenu = false;
            dc.Logout();
            return;
        }
        if (UI.Button(UI.Id("st_quit"), new R(pop.X + 8, y + 38, pop.W - 16, 28), "Полностью закрыть (Ctrl+Q)", 0x22ED4245, Th.Red, 6, 12))
        {
            _showStatusMenu = false;
            App.RequestExit();
        }
    }

    // ---------------- 10. Quick Emoji Picker, Pinned Messages & Full-Screen Image Lightbox ----------------

    private static void DrawEmojiPicker()
    {
        float w = 264, h = 198;
        var pop = new R(Math.Clamp(_chatInputRect.Right - w + 42, RailW + SideW + 8, Gfx.W - w - 12), _chatInputRect.Y - h - 10, w, h);
        _overlayCutouts.Add(pop);
        if ((UI.Pressed || UI.RightClicked) && !pop.Contains(UI.MX, UI.MY))
        { _showEmojiPicker = false; return; }

        Gfx.FillRect(pop, 0xFA14151B, 10);
        Gfx.StrokeRect(pop, Th.Border, 1, 10);
        Gfx.DrawText("Эмодзи", new R(pop.X + 12, pop.Y + 8, 140, 16), 12f, Th.Sub, 700);

        string[] emojis =
        [
            "😀", "😂", "🥹", "😎", "🤔", "😭", "😡", "🤯",
            "👍", "👎", "🔥", "❤️", "💀", "👀", "⚡", "🎉",
            "🎮", "🎧", "🎤", "💻", "🚀", "✅", "❌", "💯"
        ];
        float sx = pop.X + 10, sy = pop.Y + 30;
        for (int i = 0; i < emojis.Length; i++)
        {
            int col = i % 8, row = i / 8;
            var er = new R(sx + col * 30.5f, sy + row * 34f, 28, 30);
            if (UI.Hover(er)) Gfx.FillRect(er, Th.Hover, 6);
            Gfx.DrawText(emojis[i], new R(er.X, er.Y + 4, er.W, 22), 16, Th.Text, 400, 1);
            if (UI.Click(UI.Id("emo_pick", i), er))
            {
                App.AppendChatInputText(emojis[i]);
                _showEmojiPicker = false;
                return;
            }
        }
    }

    private static void DrawPinnedPopup()
    {
        var dc = DiscordClient.Instance;
        float w = 420, h = 360;
        var pop = new R(Gfx.W - w - 20, TopH + 48, w, h);
        _overlayCutouts.Add(pop);
        if ((UI.Pressed || UI.RightClicked) && !pop.Contains(UI.MX, UI.MY))
        { _showPinsPopup = false; return; }

        Gfx.FillRect(pop, 0xFA14151B, 10);
        Gfx.StrokeRect(pop, Th.Border, 1, 10);
        Gfx.DrawText("📌 Закреплённые сообщения", new R(pop.X + 14, pop.Y + 12, pop.W - 50, 20), 14, Th.Text, 700);
        if (UI.Button(UI.Id("pin_cls"), new R(pop.Right - 32, pop.Y + 8, 24, 24), "✕", Th.Card, Th.Text, 5, 11)) { _showPinsPopup = false; return; }
        Gfx.FillRect(new R(pop.X + 10, pop.Y + 38, pop.W - 20, 1), Th.Border);

        var body = new R(pop.X + 10, pop.Y + 44, pop.W - 20, pop.H - 54);
        if (dc.PinnedMessages.TryGetValue(dc.ActiveChannelId, out var pins) && pins.Count > 0)
        {
            float totalPinsH = 8f;
            for (int i = 0; i < pins.Count; i++)
                totalPinsH += MathF.Min(64, Gfx.MeasureText(pins[i].Content, 12.5f, 400, body.W - 20).H) + 40f;
            float maxPinsSc = MathF.Max(0, totalPinsH - body.H);
            if (UI.Hover(body) && UI.Wheel != 0) _pinsScroll = Math.Clamp(_pinsScroll - UI.Wheel * 44f, 0f, maxPinsSc);
            else _pinsScroll = Math.Clamp(_pinsScroll, 0f, maxPinsSc);

            Gfx.PushClip(body);
            float y = body.Y + 4 - _pinsScroll;
            for (int i = 0; i < pins.Count; i++)
            {
                var p = pins[i];
                float th = MathF.Min(64, Gfx.MeasureText(p.Content, 12.5f, 400, body.W - 20).H);
                var card = new R(body.X, y, body.W, th + 32);
                if (card.Bottom >= body.Y && card.Y <= body.Bottom)
                {
                    Gfx.FillRect(card, Th.Panel, 8);
                    Gfx.DrawText($"{p.AuthorName}  •  {p.Timestamp}", new R(card.X + 10, card.Y + 6, card.W - 20, 16), 12, p.AuthorColor, 700);
                    Gfx.DrawText(p.Content, new R(card.X + 10, card.Y + 24, card.W - 20, th + 4), 12.5f, Th.Text, 400, 0, true);
                }
                y += card.H + 8;
            }
            Gfx.PopClip();
        }
        else
        {
            Gfx.DrawText("В этом канале нет закреплённых сообщений", new R(body.X, body.Y + 120, body.W, 24), 13, Th.Dim, 500, 1);
        }
    }

    private static void DrawImageLightbox(R r)
    {
        if (_lightboxUrl == null) return;
        Gfx.FillRect(r, 0xDD050608);
        var bmp = ImageCache.Get(_lightboxUrl, 1600, ImgMask.None);
        float maxW = r.W - 120, maxH = r.H - 120;
        R imgR = new(r.X + (r.W - 360) * 0.5f, r.Y + (r.H - 220) * 0.5f, 360, 220);
        if (bmp != null)
        {
            float sc = MathF.Min(1f, MathF.Min(maxW / MathF.Max(1, bmp.PixelSize.Width), maxH / MathF.Max(1, bmp.PixelSize.Height)));
            float dw = bmp.PixelSize.Width * sc, dh = bmp.PixelSize.Height * sc;
            imgR = new R(r.X + (r.W - dw) * 0.5f, r.Y + (r.H - dh) * 0.5f - 10, dw, dh);
            Gfx.DrawBitmap(bmp, imgR);
            Gfx.StrokeRect(imgR, Th.Border, 1, 4);
        }
        else
        {
            Gfx.FillRect(imgR, Th.Panel, 10);
            Gfx.DrawText("Загрузка оригинала...", new R(imgR.X, imgR.Y + 96, imgR.W, 24), 14, Th.Sub, 600, 1);
        }

        var bar = new R(r.X + (r.W - 320) * 0.5f, r.Bottom - 54, 320, 34);
        if (UI.Button(UI.Id("lb_open"), new R(bar.X, bar.Y, 150, 32), "Открыть в браузере", Th.Card, Th.Cyan, 6, 12))
            W.OpenUrl(_lightboxUrl);
        if (UI.Button(UI.Id("lb_cpy"), new R(bar.X + 158, bar.Y, 110, 32), "Копир. URL", Th.Card, Th.Text, 6, 12))
        { W.CopyText(App.Hwnd, _lightboxUrl); Toast("Ссылка скопирована"); }
        if (UI.Button(UI.Id("lb_cls"), new R(bar.X + 276, bar.Y, 44, 32), "✕", Th.Red, 0xFFFFFFFF, 6, 13))
        { _lightboxUrl = null; return; }

        if (UI.Pressed && !imgR.Contains(UI.MX, UI.MY) && !bar.Contains(UI.MX, UI.MY))
            _lightboxUrl = null;
    }

    // ---------------- 11. Discord User Profile Popout Card ----------------

    private static void DrawUserProfilePopup()
    {
        if (string.IsNullOrEmpty(_profileUserId)) return;
        var dc = DiscordClient.Instance;
        string uid = _profileUserId;
        dc.Users.TryGetValue(uid, out var user);
        string nick = !string.IsNullOrEmpty(_profileGuildId) && dc.GuildNicks.TryGetValue(_profileGuildId + ":" + uid, out var nk) ? nk : "";
        string display = !string.IsNullOrEmpty(nick) ? nick : (user?.DisplayName ?? (uid == dc.Me.Id ? dc.Me.DisplayName : "Пользователь"));
        string uname = user?.Username ?? (uid == dc.Me.Id ? dc.Me.Username : uid);
        string? avUrl = user?.AvatarUrl ?? (uid == dc.Me.Id ? dc.Me.AvatarUrl : null);
        string status = uid == dc.Me.Id ? dc.Me.Status : (user?.Status ?? "offline");

        bool inVoice = false;
        if (!string.IsNullOrEmpty(_profileGuildId) && _profileGuildId != "@me")
        {
            DiscordGuild? g;
            lock (dc.Guilds) g = dc.Guilds.FirstOrDefault(x => x.Id == _profileGuildId);
            if (g != null) lock (g.VoiceStates) inVoice = g.VoiceStates.ContainsKey(uid);
        }
        else if (!string.IsNullOrEmpty(dc.ConnectedVoiceChannelId) && dc.DmCalls.TryGetValue(dc.ConnectedVoiceChannelId, out var dmCall))
        {
            lock (dmCall) inVoice = dmCall.VoiceStates.ContainsKey(uid);
        }

        float w = 282, h = inVoice && uid != dc.Me.Id ? 276 : 230;
        float x = Math.Clamp(_profileX, RailW + 8, Gfx.W - w - 12);
        float y = Math.Clamp(_profileY, TopH + 8, Gfx.H - h - 12);
        var card = new R(x, y, w, h);
        _overlayCutouts.Add(card);

        if ((UI.Pressed || UI.RightClicked) && !card.Contains(UI.MX, UI.MY))
        { _profileUserId = null; return; }

        Gfx.FillRect(card, 0xFA14151B, 12);
        Gfx.StrokeRect(card, Th.Border, 1, 12);

        // Dominant-color banner header
        uint banner = ImageCache.GetCardColor(avUrl, uid);
        Gfx.FillRect(new R(card.X + 1, card.Y + 1, card.W - 2, 54), banner, 11);

        var av = ImageCache.Get(avUrl, 128, ImgMask.Circle);
        var avr = new R(card.X + 16, card.Y + 24, 60, 60);
        Gfx.FillCircle(avr.X + 30, avr.Y + 30, 33, 0xFA14151B);
        if (av != null) Gfx.DrawBitmap(av, avr);
        else { Gfx.FillCircle(avr.X + 30, avr.Y + 30, 30, Th.Brand); Gfx.DrawText(FirstGrapheme(display), new R(avr.X, avr.Y + 16, 60, 28), 22, 0xFFFFFFFF, 700, 1); }

        uint dot = status switch { "online" => Th.Green, "idle" => Th.Amber, "dnd" => Th.Red, _ => Th.Dim };
        Gfx.FillCircle(avr.Right - 8, avr.Bottom - 8, 8.5f, 0xFA14151B);
        Gfx.FillCircle(avr.Right - 8, avr.Bottom - 8, 6f, dot);

        Gfx.DrawText(display, new R(card.X + 16, card.Y + 92, card.W - 32, 22), 16, Th.Text, 700);
        Gfx.DrawText($"@{uname}", new R(card.X + 16, card.Y + 114, card.W - 32, 16), 12, Th.Sub);
        Gfx.FillRect(new R(card.X + 14, card.Y + 136, card.W - 28, 1), Th.Border);

        float by = card.Y + 144;
        if (inVoice && uid != dc.Me.Id)
        {
            float vol = dc.GetUserVolume(uid) * 100f;
            if (UI.Slider(UI.Id("prof_vol"), new R(card.X + 16, by, card.W - 32, 38), "Громкость в голосовом", ref vol, 0f, 200f, "%"))
                dc.SetUserVolume(uid, vol / 100f);
            by += 44;
        }

        if (uid != dc.Me.Id)
        {
            if (UI.Button(UI.Id("prof_dm"), new R(card.X + 14, by, card.W - 28, 32), "💬 Написать в ЛС", Th.Brand, 0xFFFFFFFF, 6, 12.5f))
            {
                _profileUserId = null; _showFriendsView = false; _inVoiceStage = false;
                _ = ShellAsync.OpenDmAsync(uid);
                return;
            }
            by += 38;
        }

        float bw2 = (card.W - 34) * 0.5f;
        if (UI.Button(UI.Id("prof_ment"), new R(card.X + 14, by, bw2, 28), "Упомянуть", Th.Card, Th.Text, 6, 12))
        {
            App.AppendChatInputText($"<@{uid}> ");
            _profileUserId = null;
            return;
        }
        if (UI.Button(UI.Id("prof_id"), new R(card.X + 20 + bw2, by, bw2, 28), "Копир. ID", Th.Card, Th.Sub, 6, 12))
        {
            W.CopyText(App.Hwnd, uid);
            Toast("ID скопирован: " + uid);
            _profileUserId = null;
        }
    }
}

internal static class ShellAsync
{
    public static async Task UploadAndDeleteTempAsync(string channelId, string tempPath)
    {
        try { await DiscordClient.Instance.UploadFileAsync(channelId, tempPath); }
        finally { try { File.Delete(tempPath); } catch { } }
    }

    public static async Task AutoBestRegionAsync(bool forceBenchmark)
    {
        var dc = DiscordClient.Instance;
        if (forceBenchmark || string.IsNullOrEmpty(dc.BestRtcRegion))
            await dc.BenchmarkRegionsAsync();
        var (_, msg) = await dc.SwitchVoiceRegionAsync(string.IsNullOrEmpty(dc.BestRtcRegion) ? null : dc.BestRtcRegion);
        Shell.Toast(msg);
    }

    public static async Task SwitchRegionAsync(string? regionId)
    {
        var (_, msg) = await DiscordClient.Instance.SwitchVoiceRegionAsync(regionId);
        Shell.Toast(msg);
    }

    public static async Task OpenDmAsync(string userId)
    {
        var cid = await DiscordClient.Instance.OpenOrCreateDmAsync(userId);
        if (string.IsNullOrEmpty(cid)) Shell.Toast("Не удалось открыть ЛС");
    }

    public static async Task OpenDmAndCallAsync(string userId, bool selfMute, bool selfDeaf)
    {
        var cid = await DiscordClient.Instance.OpenOrCreateDmAsync(userId);
        if (string.IsNullOrEmpty(cid)) { Shell.Toast("Не удалось открыть ЛС"); return; }
        await DiscordClient.Instance.JoinVoiceAsync("@me", cid, selfMute, selfDeaf, ringIfEmpty: true);
    }

    public static async Task CreateAndCopyInviteAsync(string channelId)
    {
        var url = await DiscordClient.Instance.CreateInviteUrlAsync(channelId);
        if (!string.IsNullOrEmpty(url))
        {
            W.CopyText(App.Hwnd, url);
            Shell.Toast("Приглашение скопировано: " + url);
        }
        else Shell.Toast("Нет прав для создания приглашения в этот канал");
    }

    public static async Task RestoreAfterUpdateAsync()
    {
        var snap = Updater.ConsumePendingResume();
        if (snap == null) return;
        var dc = DiscordClient.Instance;

        App.InvokeOnUiThread(() => Shell.ApplyRestoredUiState(snap));

        bool rejoinedVoice = false;
        if (!string.IsNullOrEmpty(snap.VoiceGuildId) && !string.IsNullOrEmpty(snap.VoiceChannelId))
        {
            await dc.JoinVoiceAsync(snap.VoiceGuildId, snap.VoiceChannelId, snap.Muted, snap.Deafened);
            rejoinedVoice = true;
            // Wait up to 6s for Voice Gateway handshake to complete instead of a blind 750ms sleep
            for (int wait = 0; wait < 60 && !dc.VoiceConnected; wait++)
                await Task.Delay(100);

            foreach (var w in snap.WatchedStreams)
            {
                bool focus = snap.FocusedStreamUserId == w.UserId;
                await dc.WatchStreamAsync(snap.VoiceGuildId, snap.VoiceChannelId, w.UserId, w.Name,
                    focusImmediately: focus, selfMute: snap.Muted, selfDeaf: snap.Deafened);
                dc.SetStreamVolume(w.UserId, w.Volume);
                var ws = dc.GetWatchSession(w.UserId);
                if (ws != null && w.Muted && !ws.Muted) dc.ToggleStreamMute(w.UserId);
            }
            if (!string.IsNullOrEmpty(snap.FocusedStreamUserId))
                dc.FocusedStreamUserId = snap.FocusedStreamUserId;

            if (snap.WasStreaming)
            {
                int mon = snap.StreamMonitor;
                ulong hwnd = snap.StreamHwnd;
                App.InvokeOnUiThread(() => Shell.StartStream(mon, hwnd));
            }
        }

        string msg = rejoinedVoice
            ? $"⚡ Обновлено до v{Updater.CurrentVersion} — звонок и стримы восстановлены!"
            : $"⚡ FluxCord успешно обновлён до v{Updater.CurrentVersion}!";
        App.InvokeOnUiThread(() => Shell.Toast(msg, 5000));
        SettingsStore.Log($"Updater resume complete: rejoinedVoice={rejoinedVoice}, watches={snap.WatchedStreams.Count}, streaming={snap.WasStreaming}");
    }
}
