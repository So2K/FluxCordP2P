using System.Runtime.InteropServices;

namespace FluxCord;

public enum UiSound
{
    VoiceJoin,
    VoiceLeave,
    UserJoin,
    UserLeave,
    Mute,
    Unmute,
    Deafen,
    Undeafen,
    StreamStart,
    StreamStop,
    Message
}

/// <summary>
/// Zero-dependency, <2ms latency Discord-style tactile UI sound engine.
/// Pre-synthesizes smooth anti-aliased harmonic PCM chimes into permanently pinned native memory buffers
/// and plays asynchronously via winmm.dll PlaySoundW (thread-safe, zero UAF on volume rebuild).
/// </summary>
public static unsafe partial class SoundFx
{
    private const uint SND_MEMORY = 0x0004;
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_NOWAIT = 0x00002000;

    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySoundW(byte* pszSound, nint hmod, uint fdwSound);

    private static readonly object _lock = new();
    private static readonly Dictionary<UiSound, (nint Ptr, int Len)> _nativeWavs = new();
    private static float _builtVolume = -1f;
    private static long _lastMsgSoundTick;

    private static void EnsureBuiltLocked(float vol)
    {
        if (_nativeWavs.Count > 0 && MathF.Abs(vol - _builtVolume) < 0.02f) return;
        // Stop any currently playing async sound BEFORE freeing old native buffers (inside _lock -> zero UAF!)
        PlaySoundW(null, 0, 0);
        foreach (var kv in _nativeWavs.Values)
        {
            if (kv.Ptr != 0) NativeMemory.Free((void*)kv.Ptr);
        }
        _nativeWavs.Clear();
        _builtVolume = vol;

        Store(UiSound.VoiceJoin,   BuildNotes([(523.25f, 0.00f, 0.085f, 0.28f), (659.25f, 0.065f, 0.130f, 0.30f)], vol));
        Store(UiSound.VoiceLeave,  BuildNotes([(523.25f, 0.00f, 0.075f, 0.25f), (392.00f, 0.060f, 0.135f, 0.26f)], vol));
        Store(UiSound.UserJoin,    BuildNotes([(698.46f, 0.00f, 0.070f, 0.20f), (880.00f, 0.055f, 0.095f, 0.22f)], vol));
        Store(UiSound.UserLeave,   BuildNotes([(587.33f, 0.00f, 0.065f, 0.18f), (440.00f, 0.050f, 0.090f, 0.19f)], vol));
        Store(UiSound.Mute,        BuildNotes([(440.00f, 0.00f, 0.050f, 0.24f), (311.13f, 0.040f, 0.070f, 0.24f)], vol));
        Store(UiSound.Unmute,      BuildNotes([(349.23f, 0.00f, 0.050f, 0.24f), (523.25f, 0.040f, 0.075f, 0.26f)], vol));
        Store(UiSound.Deafen,      BuildNotes([(415.30f, 0.00f, 0.055f, 0.25f), (277.18f, 0.050f, 0.090f, 0.25f)], vol));
        Store(UiSound.Undeafen,    BuildNotes([(329.63f, 0.00f, 0.055f, 0.25f), (554.37f, 0.050f, 0.095f, 0.26f)], vol));
        Store(UiSound.StreamStart, BuildNotes([(587.33f, 0.00f, 0.080f, 0.24f), (783.99f, 0.065f, 0.090f, 0.25f), (1046.50f, 0.130f, 0.150f, 0.24f)], vol));
        Store(UiSound.StreamStop,  BuildNotes([(783.99f, 0.00f, 0.075f, 0.22f), (523.25f, 0.065f, 0.120f, 0.22f)], vol));
        Store(UiSound.Message,     BuildNotes([(783.99f, 0.00f, 0.045f, 0.20f), (1046.50f, 0.040f, 0.070f, 0.18f)], vol));
    }

    public static void EnsureBuilt()
    {
        float vol = Math.Clamp(SettingsStore.Current.UiSoundsVolume, 0f, 1f);
        lock (_lock) EnsureBuiltLocked(vol);
    }

    private static void Store(UiSound key, byte[] wav)
    {
        void* mem = NativeMemory.Alloc((nuint)wav.Length);
        fixed (byte* src = wav) Buffer.MemoryCopy(src, mem, (nuint)wav.Length, (nuint)wav.Length);
        _nativeWavs[key] = ((nint)mem, wav.Length);
    }

    public static void Play(UiSound sound)
    {
        var s = SettingsStore.Current;
        if (!s.UiSoundsEnabled || s.UiSoundsVolume <= 0.01f) return;
        if (sound == UiSound.Message)
        {
            if (s.StreamerMode) return;
            long now = Environment.TickCount64;
            long prev = Volatile.Read(ref _lastMsgSoundTick);
            if (now - prev < 900) return;
            if (Interlocked.CompareExchange(ref _lastMsgSoundTick, now, prev) != prev) return;
        }
        float vol = Math.Clamp(s.UiSoundsVolume, 0f, 1f);
        lock (_lock)
        {
            EnsureBuiltLocked(vol);
            if (_nativeWavs.TryGetValue(sound, out var entry) && entry.Ptr != 0 && entry.Len >= 44)
                PlaySoundW((byte*)entry.Ptr, 0, SND_MEMORY | SND_ASYNC | SND_NODEFAULT | SND_NOWAIT);
        }
    }

    private static byte[] BuildNotes((float Freq, float StartSec, float DurSec, float Amp)[] notes, float masterVol)
    {
        const int sr = 48000;
        float maxSec = 0.05f;
        foreach (var n in notes) maxSec = MathF.Max(maxSec, n.StartSec + n.DurSec + 0.02f);
        int samples = (int)(maxSec * sr);
        var mix = new float[samples];

        foreach (var (freq, startSec, durSec, amp) in notes)
        {
            int s0 = (int)(startSec * sr);
            int len = (int)(durSec * sr);
            for (int i = 0; i < len && s0 + i < samples; i++)
            {
                float t = i / (float)sr;
                float u = i / (float)Math.Max(1, len);
                float attack = MathF.Min(1f, t / 0.008f);
                float env = attack * MathF.Exp(-u * 3.8f) * (1f - u * u);
                float osc = MathF.Sin(2f * MathF.PI * freq * t) * 0.82f
                          + MathF.Sin(2f * MathF.PI * freq * 2f * t) * 0.14f
                          + MathF.Sin(2f * MathF.PI * freq * 0.5f * t) * 0.04f;
                mix[s0 + i] += osc * env * amp * masterVol;
            }
        }

        int dataBytes = samples * 2;
        var wav = new byte[44 + dataBytes];
        WriteAscii(wav, 0, "RIFF");
        WriteU32(wav, 4, (uint)(36 + dataBytes));
        WriteAscii(wav, 8, "WAVE");
        WriteAscii(wav, 12, "fmt ");
        WriteU32(wav, 16, 16);
        WriteU16(wav, 20, 1);
        WriteU16(wav, 22, 1);
        WriteU32(wav, 24, sr);
        WriteU32(wav, 28, sr * 2);
        WriteU16(wav, 32, 2);
        WriteU16(wav, 34, 16);
        WriteAscii(wav, 36, "data");
        WriteU32(wav, 40, (uint)dataBytes);

        for (int i = 0; i < samples; i++)
        {
            float v = Math.Clamp(mix[i], -0.98f, 0.98f);
            short s16 = (short)(v * 32767f);
            wav[44 + i * 2] = (byte)(s16 & 0xFF);
            wav[44 + i * 2 + 1] = (byte)((s16 >> 8) & 0xFF);
        }
        return wav;
    }

    private static void WriteAscii(byte[] b, int o, string s) { for (int i = 0; i < s.Length; i++) b[o + i] = (byte)s[i]; }
    private static void WriteU16(byte[] b, int o, ushort v) { b[o] = (byte)(v & 0xFF); b[o + 1] = (byte)(v >> 8); }
    private static void WriteU32(byte[] b, int o, uint v) { b[o] = (byte)(v & 0xFF); b[o + 1] = (byte)((v >> 8) & 0xFF); b[o + 2] = (byte)((v >> 16) & 0xFF); b[o + 3] = (byte)((v >> 24) & 0xFF); }
}
