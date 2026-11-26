using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.WIC;
using D2DAlpha = Vortice.DCommon.AlphaMode;

namespace FluxCord;

public enum ImgMask { None = 0, Circle = 1, Squircle = 2 }

/// <summary>
/// Fast native WIC image decoder + bounded disk/VRAM bitmap cache (FluxTube-style).
/// Fixes inflight leak on GPU device reset / transient network failures & bounds VRAM via LRU eviction.
/// </summary>
public static partial class ImageCache
{
    private const int MaxGpuBitmaps = 256;

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    private sealed class DecodedImg
    {
        public byte[] Pixels = [];
        public int W, H;
    }

    private static readonly string CacheDir = Path.Combine(SettingsStore.DataDir, "cache", "img");
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 12,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        EnableMultipleHttp2Connections = true,
    })
    { Timeout = TimeSpan.FromSeconds(12) };

    private static readonly ConcurrentDictionary<string, ID2D1Bitmap1> _gpu = new();
    private static readonly ConcurrentDictionary<string, long> _gpuLastUsed = new();
    private static readonly ConcurrentDictionary<string, DecodedImg> _ready = new();
    private static readonly ConcurrentDictionary<string, byte> _inflight = new();
    private static readonly ConcurrentDictionary<string, long> _failCooldownUntil = new();
    private static readonly ConcurrentDictionary<string, uint> _dominant = new();
    private static readonly SemaphoreSlim _sem = new(6);

    public static uint GetCardColor(string? url, string fallbackKey)
    {
        if (!string.IsNullOrEmpty(url) && _dominant.TryGetValue(url, out var col)) return col;
        uint h = 2166136261u;
        if (!string.IsNullOrEmpty(fallbackKey))
            foreach (char c in fallbackKey) h = (h ^ c) * 16777619u;
        uint[] palette = [0xFF15171E, 0xFF1B191C, 0xFF161A22, 0xFF1D1820, 0xFF161D1A];
        return palette[h % (uint)palette.Length];
    }

    static ImageCache()
    {
        Directory.CreateDirectory(CacheDir);
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    }

    public static unsafe ID2D1Bitmap1? Get(string? url, int maxDim = 96, ImgMask mask = ImgMask.None)
    {
        if (string.IsNullOrEmpty(url) || Gfx.DC == null) return null;
        int physDim = maxDim > 0 ? Math.Clamp((int)MathF.Ceiling(maxDim * MathF.Max(1f, Gfx.Scale)), 16, 2048) : 0;
        string key = $"{url}|{physDim}|{(int)mask}";

        if (_gpu.TryGetValue(key, out var bmp))
        {
            _gpuLastUsed[key] = Environment.TickCount64;
            return bmp;
        }

        if (_ready.TryRemove(key, out var dec))
        {
            EvictGpuIfNeeded();
            fixed (byte* p = dec.Pixels)
            {
                var created = Gfx.DC.CreateBitmap(
                    new Vortice.Mathematics.SizeI(dec.W, dec.H),
                    (nint)p,
                    (uint)(dec.W * 4),
                    new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.None));
                _gpu[key] = created;
                _gpuLastUsed[key] = Environment.TickCount64;
                return created;
            }
        }

        if (_failCooldownUntil.TryGetValue(key, out var until) && Environment.TickCount64 < until)
            return null;

        if (_inflight.TryAdd(key, 1))
        {
            _ = Task.Run(() => FetchAndDecodeAsync(url, key, physDim, mask));
        }
        return null;
    }

    /// <summary>
    /// Synchronously decodes a local bundled asset (e.g. Assets\fluxcord.png) via WIC directly into GPU VRAM.
    /// </summary>
    public static unsafe ID2D1Bitmap1? GetLocal(string filePath, int maxDim = 96, ImgMask mask = ImgMask.None)
    {
        if (string.IsNullOrEmpty(filePath) || Gfx.DC == null) return null;
        int physDim = maxDim > 0 ? Math.Clamp((int)MathF.Ceiling(maxDim * MathF.Max(1f, Gfx.Scale)), 16, 2048) : 0;
        string key = $"local:{filePath}|{physDim}|{(int)mask}";
        if (_gpu.TryGetValue(key, out var bmp))
        {
            _gpuLastUsed[key] = Environment.TickCount64;
            return bmp;
        }
        if (!File.Exists(filePath)) return null;
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            var dec = DecodeWic(bytes, physDim, mask, key);
            if (dec == null) return null;
            fixed (byte* p = dec.Pixels)
            {
                var created = Gfx.DC.CreateBitmap(
                    new Vortice.Mathematics.SizeI(dec.W, dec.H),
                    (nint)p,
                    (uint)(dec.W * 4),
                    new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.None));
                _gpu[key] = created;
                _gpuLastUsed[key] = Environment.TickCount64;
                return created;
            }
        }
        catch { return null; }
    }

    private static void EvictGpuIfNeeded()
    {
        if (_gpu.Count < MaxGpuBitmaps) return;
        // Evict oldest 48 non-local GPU bitmaps on UI thread to keep VRAM bounded (<40 MB)
        var victims = _gpuLastUsed
            .Where(kv => !kv.Key.StartsWith("local:", StringComparison.Ordinal))
            .OrderBy(kv => kv.Value)
            .Take(48)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var k in victims)
        {
            _gpuLastUsed.TryRemove(k, out _);
            if (_gpu.TryRemove(k, out var b))
                try { b.Dispose(); } catch { }
        }
    }

    private static async Task FetchAndDecodeAsync(string url, string key, int maxDim, ImgMask mask)
    {
        await _sem.WaitAsync();
        bool ok = false;
        try
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24];
            string diskPath = Path.Combine(CacheDir, hash + ".bin");

            byte[]? bytes = null;
            if (File.Exists(diskPath))
            {
                bytes = await File.ReadAllBytesAsync(diskPath);
            }
            else if (url.StartsWith("fluxp2p://", StringComparison.OrdinalIgnoreCase))
            {
                bytes = await DiscordClient.Instance.FetchP2pAttachmentAsync(url);
                if (bytes is { Length: > 0 and < 8 * 1024 * 1024 })
                {
                    try
                    {
                        Directory.CreateDirectory(CacheDir);
                        string tmp = diskPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                        await File.WriteAllBytesAsync(tmp, bytes);
                        File.Move(tmp, diskPath, overwrite: true);
                    }
                    catch { }
                }
            }
            else
            {
                using var resp = await Http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return;
                bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length > 0 && bytes.Length < 8 * 1024 * 1024)
                {
                    try
                    {
                        Directory.CreateDirectory(CacheDir);
                        string tmp = diskPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                        await File.WriteAllBytesAsync(tmp, bytes);
                        File.Move(tmp, diskPath, overwrite: true);
                    }
                    catch { }
                }
            }

            if (bytes == null || bytes.Length == 0) return;

            var dec = DecodeWic(bytes, maxDim, mask, url);
            if (dec != null)
            {
                _ready[key] = dec;
                _failCooldownUntil.TryRemove(key, out _);
                ok = true;
                App.WakeUi();
            }
        }
        catch { }
        finally
        {
            if (!ok) _failCooldownUntil[key] = Environment.TickCount64 + 30_000; // retry after 30s on network glitch
            _inflight.TryRemove(key, out _);
            _sem.Release();
        }
    }

    /// <summary>Encodes raw 32bpp/24bpp BMP bytes into a compact PNG file (used by Ctrl+V screenshot paste!).</summary>
    public static byte[]? ConvertImageToPng(byte[] raw)
    {
        int coHr = CoInitializeEx(0, 0); // COINIT_MULTITHREADED
        try
        {
            using var fac = new IWICImagingFactory();
            using var inStream = fac.CreateStream(raw);
            using var decoder = fac.CreateDecoderFromStream(inStream, DecodeOptions.CacheOnLoad);
            using var frame = decoder.GetFrame(0);
            using var ms = new MemoryStream();
            using var outStream = fac.CreateStream(ms);
            using var encoder = fac.CreateEncoder(ContainerFormat.Png);
            encoder.Initialize(outStream, BitmapEncoderCacheOption.NoCache);
            using var outFrame = encoder.CreateNewFrame(out _);
            outFrame.Initialize();
            outFrame.WriteSource(frame);
            outFrame.Commit();
            encoder.Commit();
            return ms.ToArray();
        }
        catch { return null; }
        finally { if (coHr >= 0) CoUninitialize(); }
    }

    /// <summary>Center-crops and scales any image (PNG/JPG/WEBP/GIF/BMP) into a crisp square PNG avatar.</summary>
    public static byte[]? EncodeAvatarPng(byte[] raw, int targetDim = 192)
    {
        int coHr = CoInitializeEx(0, 0);
        try
        {
            using var fac = new IWICImagingFactory();
            using var inStream = fac.CreateStream(raw);
            using var decoder = fac.CreateDecoderFromStream(inStream, DecodeOptions.CacheOnLoad);
            using var frame = decoder.GetFrame(0);
            int sw = (int)frame.Size.Width, sh = (int)frame.Size.Height;
            if (sw <= 0 || sh <= 0) return null;

            int side = Math.Min(sw, sh);
            int cx = (sw - side) / 2, cy = (sh - side) / 2;
            IWICBitmapSource src = frame;
            IWICBitmapClipper? clipper = null;
            if (sw != sh)
            {
                clipper = fac.CreateBitmapClipper();
                clipper.Initialize(frame, new Vortice.Mathematics.RectI(cx, cy, side, side));
                src = clipper;
            }
            IWICBitmapScaler? scaler = null;
            int outDim = Math.Min(targetDim, side);
            if (outDim < 64) outDim = Math.Min(targetDim, 192);
            if (side != outDim)
            {
                scaler = fac.CreateBitmapScaler();
                scaler.Initialize(src, (uint)outDim, (uint)outDim, Vortice.WIC.BitmapInterpolationMode.Fant);
                src = scaler;
            }
            using var ms = new MemoryStream();
            using var outStream = fac.CreateStream(ms);
            using var encoder = fac.CreateEncoder(ContainerFormat.Png);
            encoder.Initialize(outStream, BitmapEncoderCacheOption.NoCache);
            using var outFrame = encoder.CreateNewFrame(out _);
            outFrame.Initialize();
            outFrame.WriteSource(src);
            outFrame.Commit();
            encoder.Commit();
            scaler?.Dispose();
            clipper?.Dispose();
            return ms.ToArray();
        }
        catch { return null; }
        finally { if (coHr >= 0) CoUninitialize(); }
    }

    /// <summary>Decodes a local PNG into straight 32bpp BGRA pixels at exact size (for crisp dynamic Tray HICONs).</summary>
    public static byte[]? DecodeLocalIconBgra(string filePath, int size = 32)
    {
        if (!File.Exists(filePath)) return null;
        int coHr = CoInitializeEx(0, 0);
        try
        {
            byte[] raw = File.ReadAllBytes(filePath);
            using var fac = new IWICImagingFactory();
            using var stream = fac.CreateStream(raw);
            using var decoder = fac.CreateDecoderFromStream(stream, DecodeOptions.CacheOnLoad);
            using var frame = decoder.GetFrame(0);
            using var scaler = fac.CreateBitmapScaler();
            scaler.Initialize(frame, (uint)size, (uint)size, Vortice.WIC.BitmapInterpolationMode.Fant);
            using var conv = fac.CreateFormatConverter();
            conv.Initialize(scaler, Vortice.WIC.PixelFormat.Format32bppBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
            byte[] pixels = new byte[size * size * 4];
            conv.CopyPixels((uint)(size * 4), pixels);
            return pixels;
        }
        catch { return null; }
        finally { if (coHr >= 0) CoUninitialize(); }
    }

    private static DecodedImg? DecodeWic(byte[] raw, int maxDim, ImgMask mask, string url)
    {
        int coHr = CoInitializeEx(0, 0); // Ensure COM initialized & balanced on ThreadPool worker under NativeAOT
        try
        {
            using var fac = new IWICImagingFactory();
            using var stream = fac.CreateStream(raw);
            using var decoder = fac.CreateDecoderFromStream(stream, DecodeOptions.CacheOnLoad);
            using var frame = decoder.GetFrame(0);

            int srcW = (int)frame.Size.Width;
            int srcH = (int)frame.Size.Height;
            if (srcW <= 0 || srcH <= 0) return null;

            int dstW = srcW, dstH = srcH;
            if (maxDim > 0 && (srcW > maxDim || srcH > maxDim))
            {
                float scale = MathF.Min((float)maxDim / srcW, (float)maxDim / srcH);
                dstW = Math.Max(1, (int)MathF.Round(srcW * scale));
                dstH = Math.Max(1, (int)MathF.Round(srcH * scale));
            }

            IWICBitmapSource src = frame;
            IWICBitmapScaler? scaler = null;
            if (dstW != srcW || dstH != srcH)
            {
                scaler = fac.CreateBitmapScaler();
                scaler.Initialize(frame, (uint)dstW, (uint)dstH, Vortice.WIC.BitmapInterpolationMode.Fant);
                src = scaler;
            }

            using var conv = fac.CreateFormatConverter();
            conv.Initialize(src, Vortice.WIC.PixelFormat.Format32bppPBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);

            byte[] pixels = new byte[dstW * dstH * 4];
            conv.CopyPixels((uint)(dstW * 4), pixels);
            scaler?.Dispose();

            if (mask == ImgMask.Circle && !_dominant.ContainsKey(url))
            {
                long sr = 0, sg = 0, sb = 0, cnt = 0;
                int step = Math.Max(1, (dstW * dstH) / 256);
                for (int i = 0; i < dstW * dstH; i += step)
                {
                    int o = i * 4;
                    if (pixels[o + 3] < 128) continue;
                    sb += pixels[o]; sg += pixels[o + 1]; sr += pixels[o + 2]; cnt++;
                }
                if (cnt > 0)
                {
                    int r = (int)Math.Clamp((sr / cnt) * 0.58 + 10, 14, 158);
                    int g = (int)Math.Clamp((sg / cnt) * 0.58 + 10, 14, 148);
                    int b = (int)Math.Clamp((sb / cnt) * 0.58 + 12, 16, 158);
                    _dominant[url] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
                }
            }

            if (mask != ImgMask.None)
                ApplyRoundedMask(pixels, dstW, dstH, mask == ImgMask.Circle ? MathF.Min(dstW, dstH) * 0.5f : MathF.Min(dstW, dstH) * 0.30f);

            return new DecodedImg { Pixels = pixels, W = dstW, H = dstH };
        }
        catch { return null; }
        finally { if (coHr >= 0) CoUninitialize(); }
    }

    private static void ApplyRoundedMask(byte[] px, int w, int h, float radius)
    {
        float cx = w * 0.5f, cy = h * 0.5f;
        float bx = cx - radius, by = cy - radius;
        for (int y = 0; y < h; y++)
        {
            float dy = MathF.Max(MathF.Abs(y + 0.5f - cy) - by, 0f);
            for (int x = 0; x < w; x++)
            {
                float dx = MathF.Max(MathF.Abs(x + 0.5f - cx) - bx, 0f);
                float dist = MathF.Sqrt(dx * dx + dy * dy) - radius;
                if (dist > 0.5f)
                {
                    int o = (y * w + x) * 4;
                    px[o] = px[o + 1] = px[o + 2] = px[o + 3] = 0;
                }
                else if (dist > -0.5f)
                {
                    float a = Math.Clamp(0.5f - dist, 0f, 1f);
                    int o = (y * w + x) * 4;
                    px[o] = (byte)(px[o] * a);
                    px[o + 1] = (byte)(px[o + 1] * a);
                    px[o + 2] = (byte)(px[o + 2] * a);
                    px[o + 3] = (byte)(px[o + 3] * a);
                }
            }
        }
    }

    public static unsafe ID2D1Bitmap1? CreateBitmapFromBgra(byte[] bgra, int w, int h)
    {
        if (bgra.Length < w * h * 4 || w <= 0 || h <= 0 || Gfx.DC == null) return null;
        fixed (byte* p = bgra)
        {
            return Gfx.DC.CreateBitmap(
                new Vortice.Mathematics.SizeI(w, h),
                (nint)p,
                (uint)(w * 4),
                new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.None));
        }
    }

    public static void ClearGpu()
    {
        foreach (var b in _gpu.Values)
            try { b.Dispose(); } catch { }
        _gpu.Clear();
        _gpuLastUsed.Clear();
        _ready.Clear();
        _inflight.Clear();
        _failCooldownUntil.Clear();
    }
}
