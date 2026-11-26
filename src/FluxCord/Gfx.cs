using System.Numerics;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DAlpha = Vortice.DCommon.AlphaMode;

namespace FluxCord;

public readonly record struct R(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;
    public bool Contains(float px, float py) => px >= X && px < Right && py >= Y && py < Bottom;
    public R Inset(float dx, float dy) => new(X + dx, Y + dy, MathF.Max(0, W - 2 * dx), MathF.Max(0, H - 2 * dy));
    public R Inset(float d) => Inset(d, d);
}

/// <summary>
/// Direct2D 1.1 on a DXGI FlipDiscard swap chain.
/// Renders only when dirty or animating; steady idle = 0.0% CPU/GPU.
/// </summary>
public static unsafe class Gfx
{
    private static ID3D11Device _dev = null!;
    private static IDXGISwapChain1 _swap = null!;
    private static ID2D1Factory1 _d2dFactory = null!;
    private static ID2D1Device _d2dDev = null!;
    private static ID2D1Bitmap1? _target;
    private static IDWriteFactory _dw = null!;
    private static ID2D1SolidColorBrush _brush = null!;
    private static nint _hwnd;

    public static ID2D1DeviceContext DC { get; private set; } = null!;
    public static float Scale { get; private set; } = 1f;
    public static int PxW { get; private set; }
    public static int PxH { get; private set; }
    public static float W => PxW / Scale;
    public static float H => PxH / Scale;
    public static R Clip { get; private set; }
    public static double LastFlushMs { get; private set; }
    public static bool IsLiveResizing;

    private static readonly Stack<R> _clipStack = new();
    private static int _nativeClipDepth;
    private static bool _inBeginDraw;
    private static int _deferredResizeW, _deferredResizeH;
    private static bool _deferredRefreshDpi;
    private static readonly Dictionary<(float Size, int Weight, int Align, bool Wrap), IDWriteTextFormat> _formats = new();
    private static readonly Dictionary<(string Text, float Size, int Weight, float MaxW), (IDWriteTextLayout Layout, float W, float H)> _layouts = new();
    private static IDWriteFontCollection? _lucideFonts;
    private static readonly Dictionary<float, IDWriteTextFormat> _iconFormats = new();

    public static void Init(nint hwnd)
    {
        _hwnd = hwnd;
        _dw = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        try
        {
            var fontPath = Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "lucide.ttf");
            if (File.Exists(fontPath))
            {
                using var f5 = _dw.QueryInterface<IDWriteFactory5>();
                using var ff = _dw.CreateFontFileReference(fontPath);
                using var bld = f5.CreateFontSetBuilder();
                bld.AddFontFile(ff);
                using var set = bld.CreateFontSet();
                _lucideFonts = f5.CreateFontCollectionFromFontSet(set);
            }
        }
        catch { _lucideFonts = null; }
        CreateDevice();
    }

    private static void CreateDevice()
    {
        var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_0], out var dev);
        if (hr.Failure || dev == null)
            D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                [Vortice.Direct3D.FeatureLevel.Level_11_0], out dev).CheckError();

        _dev = dev!;
        using var dxgiDev = _dev.QueryInterface<IDXGIDevice1>();
        dxgiDev.MaximumFrameLatency = 1;
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();

        FluxCord.W.RECT rc;
        FluxCord.W.GetClientRect(_hwnd, &rc);
        PxW = Math.Max(1, rc.R - rc.L);
        PxH = Math.Max(1, rc.B - rc.T);

        var desc = new SwapChainDescription1
        {
            Width = (uint)PxW,
            Height = (uint)PxH,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.None,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
        };
        _swap = factory.CreateSwapChainForHwnd(_dev, _hwnd, desc);
        factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);

        _d2dDev = _d2dFactory.CreateDevice(dxgiDev);
        DC = _d2dDev.CreateDeviceContext(DeviceContextOptions.None);
        DC.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        _brush = DC.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        CreateTarget();
    }

    public static void RefreshDpi()
    {
        if (_swap == null || DC == null) return;
        if (_inBeginDraw) { _deferredRefreshDpi = true; return; }
        CreateTarget();
    }

    private static void CreateTarget()
    {
        _target?.Dispose();
        var dpi = FluxCord.W.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = 96;
        Scale = (dpi / 96f) * Math.Clamp(SettingsStore.Current.UiScale, 0.8f, 1.5f);
        using var surface = _swap.GetBuffer<IDXGISurface>(0);
        var props = new BitmapProperties1(
            new PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied),
            96f * Scale, 96f * Scale,
            BitmapOptions.Target | BitmapOptions.CannotDraw);
        _target = DC.CreateBitmapFromDxgiSurface(surface, props);
        DC.Target = _target;
        DC.SetDpi(96f * Scale, 96f * Scale);
    }

    public static void Resize(int w, int h)
    {
        if (w <= 0 || h <= 0 || _swap == null || DC == null) return;
        if (_inBeginDraw)
        {
            _deferredResizeW = w;
            _deferredResizeH = h;
            return;
        }
        PxW = w;
        PxH = h;
        DC.Target = null;
        _target?.Dispose();
        _target = null;
        var rhr = _swap.ResizeBuffers(2, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
        if (rhr.Failure)
        {
            RecoverDevice();
            return;
        }
        CreateTarget();
    }

    public static void BeginDraw(uint clearArgb)
    {
        _clipStack.Clear();
        _nativeClipDepth = 0;
        _inBeginDraw = true;
        DC.BeginDraw();
        DC.Clear(ToColor4(clearArgb));
        Clip = new R(0, 0, W, H);
    }

    public static void UnwindClips()
    {
        if (DC == null) { _nativeClipDepth = 0; _clipStack.Clear(); return; }
        while (_nativeClipDepth > 0)
        {
            try { DC.PopAxisAlignedClip(); } catch { }
            _nativeClipDepth--;
        }
        _clipStack.Clear();
        Clip = new R(0, 0, W, H);
    }

    private static void RecoverDevice()
    {
        try
        {
            DisposeDevice();
            CreateDevice();
        }
        catch (Exception ex)
        {
            SettingsStore.Log("Gfx RecoverDevice failed: " + ex.Message);
        }
    }

    public static void EndDraw()
    {
        if (DC == null || _swap == null) { _inBeginDraw = false; return; }
        UnwindClips();
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var hr = DC.EndDraw();
        _inBeginDraw = false;
        LastFlushMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (hr.Success)
        {
            var phr = _swap.Present(IsLiveResizing ? 0u : 1u, PresentFlags.None);
            if (phr.Code == unchecked((int)0x887A0005) || phr.Code == unchecked((int)0x887A0007)) // DXGI_ERROR_DEVICE_REMOVED / RESET
            {
                RecoverDevice();
            }
        }
        else
        {
            // Recover cleanly on ANY D2D EndDraw failure (D2DERR_RECREATE_TARGET, D2DERR_WRONG_STATE, etc.)
            SettingsStore.Log($"Gfx EndDraw hr=0x{hr.Code:X8} -> recreating device");
            RecoverDevice();
        }
        if (_deferredResizeW > 0 && _deferredResizeH > 0)
        {
            int rw = _deferredResizeW, rh = _deferredResizeH;
            _deferredResizeW = _deferredResizeH = 0;
            _deferredRefreshDpi = false;
            Resize(rw, rh);
        }
        else if (_deferredRefreshDpi)
        {
            _deferredRefreshDpi = false;
            RefreshDpi();
        }
        if (_layouts.Count > 1200)
        {
            foreach (var v in _layouts.Values) v.Layout.Dispose();
            _layouts.Clear();
        }
    }

    public static void PushClip(R r)
    {
        _clipStack.Push(Clip);
        DC.PushAxisAlignedClip(new Rect(r.X, r.Y, r.W, r.H), AntialiasMode.Aliased);
        _nativeClipDepth++;
        // Intersect logical hit-test clip with parent clip so nested clips never leak hover/clicks outside scroll panes!
        float x1 = MathF.Max(Clip.X, r.X);
        float y1 = MathF.Max(Clip.Y, r.Y);
        float x2 = MathF.Min(Clip.Right, r.Right);
        float y2 = MathF.Min(Clip.Bottom, r.Bottom);
        Clip = new R(x1, y1, MathF.Max(0, x2 - x1), MathF.Max(0, y2 - y1));
    }

    public static void PopClip()
    {
        if (_nativeClipDepth > 0)
        {
            DC.PopAxisAlignedClip();
            _nativeClipDepth--;
        }
        Clip = _clipStack.Count > 0 ? _clipStack.Pop() : new R(0, 0, W, H);
    }

    private static Color4 ToColor4(uint argb)
        => new(((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f, (argb & 0xFF) / 255f, ((argb >> 24) & 0xFF) / 255f);

    public static void FillRect(R r, uint argb, float radius = 0)
    {
        if (r.W <= 0 || r.H <= 0) return;
        _brush.Color = ToColor4(argb);
        if (radius > 0.5f)
            DC.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(r.X, r.Y, r.W, r.H), radius, radius), _brush);
        else
            DC.FillRectangle(new Rect(r.X, r.Y, r.W, r.H), _brush);
    }

    public static void StrokeRect(R r, uint argb, float stroke = 1f, float radius = 0)
    {
        if (r.W <= 0 || r.H <= 0) return;
        _brush.Color = ToColor4(argb);
        if (radius > 0.5f)
            DC.DrawRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(r.X, r.Y, r.W, r.H), radius, radius), _brush, stroke);
        else
            DC.DrawRectangle(new Rect(r.X, r.Y, r.W, r.H), _brush, stroke);
    }

    public static uint Fade(uint argb, float alpha)
    {
        uint a = (uint)Math.Clamp((int)(((argb >> 24) & 0xFF) * Math.Clamp(alpha, 0f, 1f)), 0, 255);
        return (a << 24) | (argb & 0x00FFFFFF);
    }

    public static void FillCircle(float cx, float cy, float rad, uint argb)
    {
        _brush.Color = ToColor4(argb);
        DC.FillEllipse(new Ellipse(new Vector2(cx, cy), rad, rad), _brush);
    }

    public static void StrokeCircle(float cx, float cy, float rad, uint argb, float stroke = 1.5f)
    {
        if (rad <= 0.1f || stroke <= 0.1f) return;
        _brush.Color = ToColor4(argb);
        DC.DrawEllipse(new Ellipse(new Vector2(cx, cy), rad, rad), _brush, stroke);
    }

    private static IDWriteTextFormat GetFormat(float size, int weight, int align, bool wrap)
    {
        var key = (size, weight, align, wrap);
        if (_formats.TryGetValue(key, out var f)) return f;
        var fw = weight >= 700 ? FontWeight.Bold : (weight >= 600 ? FontWeight.SemiBold : FontWeight.Normal);
        f = _dw.CreateTextFormat("Segoe UI", null, fw, FontStyle.Normal, FontStretch.Normal, size, "ru-RU");
        f.TextAlignment = align switch
        {
            1 => TextAlignment.Center,
            2 => TextAlignment.Trailing,
            _ => TextAlignment.Leading
        };
        f.ParagraphAlignment = ParagraphAlignment.Near;
        f.WordWrapping = wrap ? WordWrapping.Wrap : WordWrapping.NoWrap;
        _formats[key] = f;
        return f;
    }

    public static (float W, float H) MeasureText(string text, float size, int weight, float maxW)
    {
        if (string.IsNullOrEmpty(text)) return (0, size * 1.3f);
        var qW = MathF.Ceiling(maxW / 4f) * 4f;
        var key = (text, size, weight, qW);
        if (_layouts.TryGetValue(key, out var cached)) return (cached.W, cached.H);

        var fmt = GetFormat(size, weight, 0, wrap: true);
        var layout = _dw.CreateTextLayout(text, fmt, MathF.Max(16f, qW), 4096f);
        var m = layout.Metrics;
        _layouts[key] = (layout, m.Width, m.Height);
        return (m.Width, m.Height);
    }

    public static void DrawText(string text, R r, float size, uint argb, int weight = 400, int align = 0, bool wrap = false)
    {
        if (string.IsNullOrEmpty(text) || r.W <= 1 || r.H <= 1) return;
        _brush.Color = ToColor4(argb);
        if (!wrap && align == 0)
        {
            var fmt = GetFormat(size, weight, 0, wrap: false);
            DC.DrawText(text, fmt, new Rect(r.X, r.Y, r.W, r.H), _brush, DrawTextOptions.Clip | DrawTextOptions.EnableColorFont);
            return;
        }
        if (wrap && align == 0)
        {
            var qW = MathF.Ceiling(r.W / 4f) * 4f;
            var key = (text, size, weight, qW);
            if (!_layouts.TryGetValue(key, out var cached))
            {
                var fmt = GetFormat(size, weight, 0, wrap: true);
                var layout = _dw.CreateTextLayout(text, fmt, MathF.Max(16f, qW), 4096f);
                var m = layout.Metrics;
                cached = (layout, m.Width, m.Height);
                _layouts[key] = cached;
            }
            bool needClip = cached.H > r.H + 1f;
            if (needClip) PushClip(r);
            DC.DrawTextLayout(new Vector2(r.X, r.Y), cached.Layout, _brush, DrawTextOptions.Clip | DrawTextOptions.EnableColorFont);
            if (needClip) PopClip();
            return;
        }

        var f2 = GetFormat(size, weight, align, wrap);
        DC.DrawText(text, f2, new Rect(r.X, r.Y, r.W, r.H), _brush, DrawTextOptions.Clip | DrawTextOptions.EnableColorFont);
    }

    public static void DrawBitmap(ID2D1Bitmap1 bmp, R dst)
    {
        DC.DrawBitmap(bmp, new System.Drawing.RectangleF(dst.X, dst.Y, dst.W, dst.H), 1f, Vortice.Direct2D1.BitmapInterpolationMode.Linear, null);
    }

    public static void DrawIcon(string lucideGlyph, string fallback, R r, float size, uint argb)
    {
        _brush.Color = ToColor4(argb);
        if (_lucideFonts != null)
        {
            if (!_iconFormats.TryGetValue(size, out var fmt))
            {
                fmt = _dw.CreateTextFormat("lucide", _lucideFonts, FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, size, "en-US");
                fmt.TextAlignment = TextAlignment.Center;
                fmt.ParagraphAlignment = ParagraphAlignment.Center;
                _iconFormats[size] = fmt;
            }
            DC.DrawText(lucideGlyph, fmt, new Rect(r.X, r.Y, r.W, r.H), _brush);
        }
        else
        {
            DrawText(fallback, r, size * 0.9f, argb, 600, 1);
        }
    }

    public static void DisposeDevice()
    {
        Shell.ClearShareBitmaps();
        ImageCache.ClearGpu();
        _nativeClipDepth = 0;
        _clipStack.Clear();
        foreach (var f in _iconFormats.Values) { try { f.Dispose(); } catch { } }
        _iconFormats.Clear();
        foreach (var v in _layouts.Values) { try { v.Layout.Dispose(); } catch { } }
        _layouts.Clear();
        foreach (var f in _formats.Values) { try { f.Dispose(); } catch { } }
        _formats.Clear();
        try { if (DC != null) DC.Target = null; } catch { }
        try { _brush?.Dispose(); } catch { }
        try { _target?.Dispose(); } catch { }
        try { DC?.Dispose(); } catch { }
        try { _d2dDev?.Dispose(); } catch { }
        try { _swap?.Dispose(); } catch { }
        try { _dev?.Dispose(); } catch { }
        _brush = null!;
        _target = null;
        DC = null!;
        _d2dDev = null!;
        _swap = null!;
        _dev = null!;
    }
}
