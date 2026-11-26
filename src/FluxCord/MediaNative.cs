using System.Runtime.InteropServices;

namespace FluxCord;

public enum NoiseMode : int
{
    Off = 0,
    StudioDsp = 1,
    Rnnoise = 2,
    DeepFilterNet3 = 3,
    NvidiaBroadcast = 4,
    AutoBest = 5
}

public enum VideoCodec : int
{
    H264 = 0,
    Hevc = 1,
    Av1 = 2
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct FcAudioStats
{
    public float RawRms;
    public float CleanRms;
    public float NoiseReductionDb;
    public float VadProb;
    public int Speaking;
    public int ActiveMode;
    public int TxPackets;
    public int RxPackets;
    public fixed byte BackendName[64];

    public string GetBackendName()
    {
        fixed (byte* p = BackendName)
            return Marshal.PtrToStringUTF8((nint)p) ?? "";
    }
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct FcScreenStats
{
    public int Width;
    public int Height;
    public float Fps;
    public float BitrateKbps;
    public float EncodeMs;
    public float DecodeMs;
    public int ZeroCopy;
    public int Running;
    public long FramesEncoded;
    public long FramesSkippedStatic;
    public fixed byte EncoderName[64];

    public string GetEncoderName()
    {
        fixed (byte* p = EncoderName)
            return Marshal.PtrToStringUTF8((nint)p) ?? "";
    }
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct FcDeviceInfo
{
    public fixed byte Id[128];
    public fixed byte Name[128];
    public int IsDefault;

    public string GetId() { fixed (byte* p = Id) return Marshal.PtrToStringUTF8((nint)p) ?? ""; }
    public string GetName() { fixed (byte* p = Name) return Marshal.PtrToStringUTF8((nint)p) ?? ""; }
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct FcMonitorInfo
{
    public int Index;
    public int Width;
    public int Height;
    public int RefreshHz;
    public fixed byte Name[128];

    public string GetName() { fixed (byte* p = Name) return Marshal.PtrToStringUTF8((nint)p) ?? ""; }
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct FcWindowInfo
{
    public ulong Hwnd;
    public int Width;
    public int Height;
    public fixed byte Title[128];

    public string GetTitle() { fixed (byte* p = Title) return Marshal.PtrToStringUTF8((nint)p) ?? ""; }
}

internal static unsafe partial class MediaNative
{
    private const string Dll = "FluxCord.Media.dll";

    [LibraryImport(Dll)] public static partial int fc_init();
    [LibraryImport(Dll)] public static partial void fc_shutdown();

    [LibraryImport(Dll)] public static partial int fc_list_audio_devices(int capture, FcDeviceInfo* outList, int maxCount);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int fc_voice_start(string? captureDeviceId, string? renderDeviceId);
    [LibraryImport(Dll)] public static partial void fc_voice_stop();
    [LibraryImport(Dll)] public static partial void fc_voice_set_muted(int muted);
    [LibraryImport(Dll)] public static partial void fc_voice_set_deafened(int deafened);
    [LibraryImport(Dll)] public static partial void fc_voice_set_loopback(int enabled);
    [LibraryImport(Dll)] public static partial void fc_voice_set_noise_config(int mode, float strength, int hpfEnabled, int agcEnabled, float gateDb);
    [LibraryImport(Dll)] public static partial void fc_voice_set_input_gain(float gain);
    [LibraryImport(Dll)] public static partial void fc_voice_set_output_volume(float volume);
    [LibraryImport(Dll)] public static partial void fc_voice_set_user_volume(uint ssrc, float volume);
    [LibraryImport(Dll)] public static partial int fc_voice_is_ssrc_speaking(uint ssrc);
    [LibraryImport(Dll)] public static partial void fc_voice_get_stats(FcAudioStats* outStats);
    [LibraryImport(Dll)] public static partial int fc_voice_get_available_backends();

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int fc_transport_connect(string ip, ushort port, uint ssrc, byte* outExtIp, int extIpCap, ushort* outExtPort);
    [LibraryImport(Dll)] public static partial int fc_transport_set_key(byte* secretKey32, int useXchachaRtpsize);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void fc_transport_set_p2p_peer(string? ip, ushort port, int allowDirectTx);
    [LibraryImport(Dll)] public static partial int fc_transport_is_p2p_direct();
    [LibraryImport(Dll)] public static partial int fc_get_pending_stun_endpoint(byte* outIp, int ipCap, ushort* outPort);
    [LibraryImport(Dll)] public static partial void fc_transport_disconnect();
    [LibraryImport(Dll)] public static partial int fc_transport_get_ping_ms();
    [LibraryImport(Dll)] public static partial void fc_transport_send_silence();

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int fc_dave_init(ushort protocolVersion, ulong channelId, string selfUserId, uint selfSsrc);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void fc_dave_add_user(string userId);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void fc_dave_remove_user(string userId);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void fc_dave_map_ssrc(uint ssrc, string userId);
    [LibraryImport(Dll)] public static partial int fc_dave_get_key_package(byte* outBuf, int cap);
    [LibraryImport(Dll)] public static partial void fc_dave_set_external_sender(byte* data, int len);
    [LibraryImport(Dll)] public static partial int fc_dave_process_proposals(byte* data, int len, byte* outCommitWelcome, int cap);
    [LibraryImport(Dll)] public static partial int fc_dave_process_commit(byte* data, int len);
    [LibraryImport(Dll)] public static partial int fc_dave_process_welcome(byte* data, int len);
    [LibraryImport(Dll)] public static partial void fc_dave_execute_transition(ushort protocolVersion);

    // Screen Share Picker + Capture (Monitor Zero-Copy & Window Capture)
    [LibraryImport(Dll)] public static partial int fc_list_monitors(FcMonitorInfo* outList, int maxCount);
    [LibraryImport(Dll)] public static partial int fc_list_windows(FcWindowInfo* outList, int maxCount);
    [LibraryImport(Dll)] public static partial int fc_capture_monitor_thumb(int monitorIndex, byte* outBgra, int thumbW, int thumbH);
    [LibraryImport(Dll)] public static partial int fc_capture_window_thumb(ulong hwnd, byte* outBgra, int thumbW, int thumbH);
    [LibraryImport(Dll)] public static partial int fc_screen_start(int monitorIndex, int targetW, int targetH, int fps, int bitrateKbps, int codec, int loopbackPreview, nint previewHwnd);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int fc_screen_start_ex(int monitorIndex, ulong targetHwnd, int targetW, int targetH, int fps, int bitrateKbps, int codec, int loopbackPreview, nint previewHwnd, int captureAudio, string? renderDeviceId);
    [LibraryImport(Dll)] public static partial int fc_screen_start_window(ulong targetHwnd, int targetW, int targetH, int fps, int bitrateKbps, int codec, int loopbackPreview, nint previewHwnd);
    [LibraryImport(Dll)] public static partial void fc_screen_stop();
    [LibraryImport(Dll)] public static partial void fc_screen_resize_preview(int width, int height);
    [LibraryImport(Dll)] public static partial void fc_screen_get_stats(FcScreenStats* outStats);

    // Dedicated Outbound Go Live Stream Publisher Transport
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int fc_pub_connect(string ip, ushort port, uint ssrc, byte* outExtIp, int extIpCap, ushort* outExtPort);
    [LibraryImport(Dll)] public static partial int fc_pub_set_key(byte* secretKey32, int useXchachaRtpsize);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void fc_pub_set_p2p_peer(string? ip, ushort port, int allowDirectTx);
    [LibraryImport(Dll)] public static partial void fc_pub_set_video_ssrc(uint videoSsrc);
    [LibraryImport(Dll)] public static partial void fc_pub_request_keyframe();
    [LibraryImport(Dll)] public static partial void fc_pub_get_stats(int* outTxPkts, int* outPliRx, int* outDaveActive);
    [LibraryImport(Dll)] public static partial void fc_pub_get_stats_ex(int* outVideoPkts, int* outAudioPkts, int* outPliRx, int* outDaveActive);
    [LibraryImport(Dll)] public static partial void fc_pub_disconnect();
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int fc_pub_dave_init(ushort protocolVersion, ulong channelId, string selfUserId, uint selfSsrc, uint videoSsrc);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void fc_pub_dave_add_user(string userId);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void fc_pub_dave_remove_user(string userId);
    [LibraryImport(Dll)] public static partial int fc_pub_dave_get_key_package(byte* outBuf, int cap);
    [LibraryImport(Dll)] public static partial void fc_pub_dave_set_external_sender(byte* data, int len);
    [LibraryImport(Dll)] public static partial int fc_pub_dave_process_proposals(byte* data, int len, byte* outCommitWelcome, int cap);
    [LibraryImport(Dll)] public static partial int fc_pub_dave_process_commit(byte* data, int len);
    [LibraryImport(Dll)] public static partial int fc_pub_dave_process_welcome(byte* data, int len);
    [LibraryImport(Dll)] public static partial void fc_pub_dave_execute_transition(ushort protocolVersion);

    // 4-Slot Multi-Stream Go Live Receivers (slots 0..3)
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int fc_watch_connect_slot(int slot, string ip, ushort port, uint ssrc, nint hwnd, byte* outExtIp, int extIpCap, ushort* outExtPort);
    [LibraryImport(Dll)] public static partial void fc_watch_set_key_slot(int slot, byte* secretKey32, int useXchachaRtpsize);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void fc_watch_set_p2p_peer_slot(int slot, string? ip, ushort port, int allowDirectTx);
    [LibraryImport(Dll)] public static partial void fc_watch_set_codec_slot(int slot, int codec, int videoPt);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void fc_watch_set_remote_slot(int slot, uint videoSsrc, uint audioSsrc, string? streamerUserId);
    [LibraryImport(Dll)] public static partial void fc_watch_set_volume_slot(int slot, float volume);
    [LibraryImport(Dll)] public static partial void fc_watch_resize_slot(int slot, int width, int height);
    [LibraryImport(Dll)] public static partial void fc_watch_stop_slot(int slot);
    [LibraryImport(Dll)] public static partial void fc_watch_get_stats_slot(int slot, FcScreenStats* outStats);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int fc_watch_dave_init_slot(int slot, ushort protoVer, ulong channelId, string selfUserId, uint selfSsrc);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void fc_watch_dave_add_user_slot(int slot, string userId);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void fc_watch_dave_remove_user_slot(int slot, string userId);
    [LibraryImport(Dll)] public static partial int fc_watch_dave_get_key_package_slot(int slot, byte* outBuf, int cap);
    [LibraryImport(Dll)] public static partial void fc_watch_dave_set_external_sender_slot(int slot, byte* data, int len);
    [LibraryImport(Dll)] public static partial int fc_watch_dave_process_proposals_slot(int slot, byte* data, int len, byte* outCw, int cap);
    [LibraryImport(Dll)] public static partial int fc_watch_dave_process_commit_slot(int slot, byte* data, int len);
    [LibraryImport(Dll)] public static partial int fc_watch_dave_process_welcome_slot(int slot, byte* data, int len);
    [LibraryImport(Dll)] public static partial void fc_watch_dave_execute_transition_slot(int slot, ushort protoVer);

    public static List<(string Id, string Name, bool Default)> ListAudioDevices(bool capture)
    {
        var arr = new FcDeviceInfo[32];
        var res = new List<(string, string, bool)>();
        fixed (FcDeviceInfo* p = arr)
        {
            int n = fc_list_audio_devices(capture ? 1 : 0, p, arr.Length);
            for (int i = 0; i < n; i++) res.Add((arr[i].GetId(), arr[i].GetName(), arr[i].IsDefault != 0));
        }
        return res;
    }

    public static List<(int Index, int W, int H, int Hz, string Name)> ListMonitors()
    {
        var arr = new FcMonitorInfo[16];
        var res = new List<(int, int, int, int, string)>();
        fixed (FcMonitorInfo* p = arr)
        {
            int n = fc_list_monitors(p, arr.Length);
            for (int i = 0; i < n; i++) res.Add((arr[i].Index, arr[i].Width, arr[i].Height, arr[i].RefreshHz, arr[i].GetName()));
        }
        return res;
    }

    public static List<(ulong Hwnd, int W, int H, string Title)> ListWindows(nint excludeHwnd = 0)
    {
        var arr = new FcWindowInfo[24];
        var res = new List<(ulong, int, int, string)>();
        fixed (FcWindowInfo* p = arr)
        {
            int n = fc_list_windows(p, arr.Length);
            for (int i = 0; i < n; i++)
            {
                if ((nint)arr[i].Hwnd == excludeHwnd) continue;
                var title = arr[i].GetTitle();
                if (string.IsNullOrWhiteSpace(title) || title == "Program Manager") continue;
                res.Add((arr[i].Hwnd, arr[i].Width, arr[i].Height, title));
            }
        }
        return res;
    }
}
