#include "FluxCordMedia.h"
#include "NetTurbo.h"
#include "ScreenEngine.h"
#include "StreamReceiver.h"
#include "VoiceEngine.h"
#include "VoiceTransport.h"
#include <dwmapi.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <array>
#include <cstring>
#include <memory>
#include <mutex>
#include <shared_mutex>
#include <vector>

#pragma comment(lib, "dwmapi.lib")

namespace {
constexpr int kMaxWatchSlots = 4;
std::shared_mutex g_smtx;
std::unique_ptr<fc::VoiceTransport> g_transport;
std::unique_ptr<fc::VoiceTransport> g_pub_transport;
std::unique_ptr<fc::VoiceEngine> g_voice;
std::unique_ptr<fc::ScreenEngine> g_screen;
std::array<std::unique_ptr<fc::StreamReceiver>, kMaxWatchSlots> g_watch;

fc::StreamReceiver* getWatchUnlocked(int32_t slot) {
    if (slot < 0 || slot >= kMaxWatchSlots) return nullptr;
    return g_watch[size_t(slot)].get();
}

void ensureInitLocked() {
    fc::enableHighResTimer(true);
    if (!g_transport) g_transport = std::make_unique<fc::VoiceTransport>();
    if (!g_pub_transport) g_pub_transport = std::make_unique<fc::VoiceTransport>();
    if (!g_voice) g_voice = std::make_unique<fc::VoiceEngine>(g_transport.get());
    if (!g_screen) g_screen = std::make_unique<fc::ScreenEngine>(g_pub_transport.get());
    for (int i = 0; i < kMaxWatchSlots; ++i) {
        if (!g_watch[size_t(i)]) g_watch[size_t(i)] = std::make_unique<fc::StreamReceiver>(g_voice.get(), i);
    }
}

std::string wideToUtf8(const wchar_t* w) {
    if (!w || !*w) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
    if (n <= 1) return {};
    std::string s(size_t(n - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w, -1, s.data(), n, nullptr, nullptr);
    return s;
}

bool captureRectToBgra(HDC srcDc, int sx, int sy, int sw, int sh, uint8_t* outBgra, int tw, int th) {
    if (!srcDc || sw <= 0 || sh <= 0 || !outBgra || tw <= 0 || th <= 0 || tw > 4096 || th > 4096) return false;
    HDC memDc = CreateCompatibleDC(srcDc);
    if (!memDc) return false;
    BITMAPINFO bi{};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = tw;
    bi.bmiHeader.biHeight = -th;
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;
    void* bits = nullptr;
    HBITMAP dib = CreateDIBSection(memDc, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
    bool ok = false;
    if (dib && bits) {
        HGDIOBJ old = SelectObject(memDc, dib);
        SetStretchBltMode(memDc, HALFTONE);
        SetBrushOrgEx(memDc, 0, 0, nullptr);
        if (StretchBlt(memDc, 0, 0, tw, th, srcDc, sx, sy, sw, sh, SRCCOPY | CAPTUREBLT)) {
            const size_t pxCount = size_t(tw) * size_t(th);
            std::memcpy(outBgra, bits, pxCount * 4);
            for (size_t i = 0; i < pxCount; ++i) outBgra[i * 4 + 3] = 255;
            ok = true;
        }
        SelectObject(memDc, old);
    }
    if (dib) DeleteObject(dib);
    DeleteDC(memDc);
    return ok;
}

template <typename Fn>
auto runOnInputDesktop(Fn&& fn) {
    using Ret = decltype(fn());
    Ret result{};
    std::thread t([&]() {
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        HDESK origDesk = GetThreadDesktop(GetCurrentThreadId());
        HDESK desk = OpenInputDesktop(0, FALSE, GENERIC_ALL);
        if (!desk) desk = OpenInputDesktop(0, FALSE, DESKTOP_READOBJECTS | DESKTOP_ENUMERATE);
        if (desk) SetThreadDesktop(desk);
        result = fn();
        // MUST restore original thread desktop BEFORE CloseDesktop() or Windows leaks the HDESK handle (ERROR_BUSY)!
        if (desk) {
            if (origDesk) SetThreadDesktop(origDesk);
            CloseDesktop(desk);
        }
    });
    t.join();
    return result;
}
} // namespace

extern "C" {

FC_API int32_t fc_init() {
    std::unique_lock lk(g_smtx);
    ensureInitLocked();
    return 1;
}

FC_API void fc_shutdown() {
    std::unique_lock lk(g_smtx);
    // CRITICAL shutdown order: stop watchers & disconnect transports (joining rx_thread_ & clearing callbacks)
    // BEFORE destroying g_screen and g_voice to eliminate shutdown UAF crashes!
    for (int i = 0; i < kMaxWatchSlots; ++i) {
        if (g_watch[size_t(i)]) { g_watch[size_t(i)]->stop(); g_watch[size_t(i)].reset(); }
    }
    if (g_pub_transport) {
        g_pub_transport->setPliCallback(nullptr);
        g_pub_transport->setAudioRxCallback(nullptr);
        g_pub_transport->disconnect();
    }
    if (g_transport) {
        g_transport->setPliCallback(nullptr);
        g_transport->setAudioRxCallback(nullptr);
        g_transport->disconnect();
    }
    if (g_screen) { g_screen->stop(); g_screen.reset(); }
    if (g_voice) { g_voice->stop(); g_voice.reset(); }
    g_pub_transport.reset();
    g_transport.reset();
    fc::enableHighResTimer(false);
}

FC_API int32_t fc_list_audio_devices(int32_t capture, FcDeviceInfo* out_list, int32_t max_count) {
    return fc::VoiceEngine::listDevices(capture != 0, out_list, max_count);
}

FC_API int32_t fc_voice_start(const char* capture_device_id, const char* render_device_id) {
    {
        std::unique_lock ulk(g_smtx);
        if (!g_voice) ensureInitLocked();
    }
    std::shared_lock lk(g_smtx);
    return (g_voice && g_voice->start(capture_device_id, render_device_id)) ? 1 : 0;
}

FC_API void fc_voice_stop() { std::shared_lock lk(g_smtx); if (g_voice) g_voice->stop(); }
FC_API void fc_voice_set_muted(int32_t muted) { std::shared_lock lk(g_smtx); if (g_voice) g_voice->setMuted(muted != 0); }
FC_API void fc_voice_set_deafened(int32_t deafened) { std::shared_lock lk(g_smtx); if (g_voice) g_voice->setDeafened(deafened != 0); }
FC_API void fc_voice_set_loopback(int32_t enabled) { std::shared_lock lk(g_smtx); if (g_voice) g_voice->setLoopback(enabled != 0); }
FC_API void fc_voice_set_noise_config(int32_t mode, float strength, int32_t hpf_enabled, int32_t agc_enabled, float gate_db) {
    std::shared_lock lk(g_smtx);
    if (g_voice) g_voice->setNoiseConfig(mode, strength, hpf_enabled != 0, agc_enabled != 0, gate_db);
}
FC_API void fc_voice_set_input_gain(float gain) { std::shared_lock lk(g_smtx); if (g_voice) g_voice->setInputGain(gain); }
FC_API void fc_voice_set_output_volume(float volume) { std::shared_lock lk(g_smtx); if (g_voice) g_voice->setOutputVolume(volume); }
FC_API void fc_voice_set_user_volume(uint32_t ssrc, float volume) { std::shared_lock lk(g_smtx); if (g_voice) g_voice->setUserVolume(ssrc, volume); }
FC_API int32_t fc_voice_is_ssrc_speaking(uint32_t ssrc) { std::shared_lock lk(g_smtx); return (g_voice && g_voice->isSsrcSpeaking(ssrc)) ? 1 : 0; }
FC_API void fc_voice_get_stats(FcAudioStats* out_stats) {
    if (!out_stats) return;
    std::memset(out_stats, 0, sizeof(*out_stats));
    std::shared_lock lk(g_smtx);
    if (g_voice) g_voice->getStats(out_stats);
}
FC_API int32_t fc_voice_get_available_backends() {
    {
        std::unique_lock ulk(g_smtx);
        if (!g_voice) ensureInitLocked();
    }
    std::shared_lock lk(g_smtx);
    return g_voice ? g_voice->availableBackends() : 1;
}

FC_API int32_t fc_transport_connect(const char* ip, uint16_t port, uint32_t ssrc, char* out_ext_ip, int32_t ext_ip_cap, uint16_t* out_ext_port) {
    {
        std::unique_lock ulk(g_smtx);
        if (!g_transport) ensureInitLocked();
    }
    std::shared_lock lk(g_smtx);
    if (!g_transport) return 0;
    std::string extIp;
    uint16_t extPort = 0;
    if (!g_transport->connectAndDiscover(ip, port, ssrc, extIp, extPort)) return 0;
    if (out_ext_ip && ext_ip_cap > 0) {
        std::memset(out_ext_ip, 0, size_t(ext_ip_cap));
        std::strncpy(out_ext_ip, extIp.c_str(), size_t(ext_ip_cap - 1));
    }
    if (out_ext_port) *out_ext_port = extPort;
    return 1;
}

FC_API int32_t fc_transport_set_key(const uint8_t* secret_key_32, int32_t use_xchacha_rtpsize) {
    std::shared_lock lk(g_smtx);
    if (!g_transport || !secret_key_32) return 0;
    g_transport->setKey(secret_key_32, use_xchacha_rtpsize != 0);
    return 1;
}
FC_API void fc_transport_set_p2p_peer(const char* ip, uint16_t port, int32_t allow_direct_tx) {
    std::shared_lock lk(g_smtx);
    if (g_transport) g_transport->setP2pPeer(ip, port, allow_direct_tx != 0);
}
FC_API int32_t fc_transport_is_p2p_direct() {
    std::shared_lock lk(g_smtx);
    return (g_transport && g_transport->isP2pDirect()) ? 1 : 0;
}
FC_API int32_t fc_get_pending_stun_endpoint(char* out_ip, int32_t ip_cap, uint16_t* out_port) {
    return fc::p2p::consumeStunEndpoint(out_ip, ip_cap, out_port) ? 1 : 0;
}

FC_API void fc_transport_disconnect() { std::shared_lock lk(g_smtx); if (g_transport) g_transport->disconnect(); }
FC_API int32_t fc_transport_get_ping_ms() { std::shared_lock lk(g_smtx); return g_transport ? g_transport->pingMs() : 0; }
FC_API void fc_transport_send_silence() { std::shared_lock lk(g_smtx); if (g_transport) g_transport->sendSilenceFrames(); }

FC_API int32_t fc_dave_init(uint16_t protocol_version, uint64_t channel_id, const char* self_user_id, uint32_t self_ssrc) {
    {
        std::unique_lock ulk(g_smtx);
        if (!g_transport) ensureInitLocked();
    }
    std::shared_lock lk(g_smtx);
    return (g_transport && g_transport->daveInit(protocol_version, channel_id, self_user_id, self_ssrc)) ? 1 : 0;
}
FC_API void fc_dave_add_user(const char* user_id) { std::shared_lock lk(g_smtx); if (g_transport) g_transport->daveAddUser(user_id); }
FC_API void fc_dave_remove_user(const char* user_id) { std::shared_lock lk(g_smtx); if (g_transport) g_transport->daveRemoveUser(user_id); }
FC_API void fc_dave_map_ssrc(uint32_t ssrc, const char* user_id) { std::shared_lock lk(g_smtx); if (g_transport) g_transport->daveMapSsrc(ssrc, user_id); }
FC_API int32_t fc_dave_get_key_package(uint8_t* out_buf, int32_t cap) { std::shared_lock lk(g_smtx); return g_transport ? g_transport->daveGetKeyPackage(out_buf, cap) : 0; }
FC_API void fc_dave_set_external_sender(const uint8_t* data, int32_t len) { std::shared_lock lk(g_smtx); if (g_transport) g_transport->daveSetExternalSender(data, len); }
FC_API int32_t fc_dave_process_proposals(const uint8_t* data, int32_t len, uint8_t* out_commit_welcome, int32_t cap) {
    std::shared_lock lk(g_smtx);
    return g_transport ? g_transport->daveProcessProposals(data, len, out_commit_welcome, cap) : 0;
}
FC_API int32_t fc_dave_process_commit(const uint8_t* data, int32_t len) { std::shared_lock lk(g_smtx); return g_transport ? g_transport->daveProcessCommit(data, len) : 0; }
FC_API int32_t fc_dave_process_welcome(const uint8_t* data, int32_t len) { std::shared_lock lk(g_smtx); return (g_transport && g_transport->daveProcessWelcome(data, len)) ? 1 : 0; }
FC_API void fc_dave_execute_transition(uint16_t protocol_version) { std::shared_lock lk(g_smtx); if (g_transport) g_transport->daveExecuteTransition(protocol_version); }

FC_API int32_t fc_list_monitors(FcMonitorInfo* out_list, int32_t max_count) {
    return fc::ScreenEngine::listMonitors(out_list, max_count);
}

FC_API int32_t fc_list_windows(FcWindowInfo* out_list, int32_t max_count) {
    if (!out_list || max_count <= 0) return 0;
    const DWORD selfPid = GetCurrentProcessId();
    return runOnInputDesktop([&]() -> int32_t {
        struct Ctx { FcWindowInfo* out; int32_t max_c; int32_t count; DWORD selfPid; };
        Ctx ctx{ out_list, max_count, 0, selfPid };
        EnumWindows([](HWND hwnd, LPARAM lp) -> BOOL {
            auto* c = reinterpret_cast<Ctx*>(lp);
            if (c->count >= c->max_c) return FALSE;
            DWORD pid = 0;
            GetWindowThreadProcessId(hwnd, &pid);
            if (pid == c->selfPid) return TRUE;
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return TRUE;
            DWORD cloaked = 0;
            DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(cloaked));
            if (cloaked) return TRUE;
            LONG ex = GetWindowLongW(hwnd, GWL_EXSTYLE);
            if (ex & WS_EX_TOOLWINDOW) return TRUE;
            wchar_t title[256]{};
            DWORD_PTR res = 0;
            if (!SendMessageTimeoutW(hwnd, WM_GETTEXT, 255, reinterpret_cast<LPARAM>(title),
                                     SMTO_ABORTIFHUNG | SMTO_BLOCK, 25, &res) || res == 0) {
                return TRUE;
            }
            RECT rc{};
            GetWindowRect(hwnd, &rc);
            int w = rc.right - rc.left, h = rc.bottom - rc.top;
            if (w < 200 || h < 120) return TRUE;
            std::string u8 = wideToUtf8(title);
            std::memset(&c->out[c->count], 0, sizeof(FcWindowInfo));
            c->out[c->count].hwnd = uint64_t(hwnd);
            c->out[c->count].width = w;
            c->out[c->count].height = h;
            std::strncpy(c->out[c->count].title, u8.c_str(), sizeof(c->out[c->count].title) - 1);
            c->count++;
            return TRUE;
        }, reinterpret_cast<LPARAM>(&ctx));
        return ctx.count;
    });
}

FC_API int32_t fc_capture_monitor_thumb(int32_t monitor_index, uint8_t* out_bgra, int32_t thumb_w, int32_t thumb_h) {
    return runOnInputDesktop([&]() -> int32_t {
        // Match exact DXGI monitor ordering used by ScreenEngine::listMonitors & captureEncodeLoop!
        RECT targetRc{};
        bool found = false;
        Microsoft::WRL::ComPtr<IDXGIFactory1> fac;
        if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&fac)))) {
            int cur = 0;
            Microsoft::WRL::ComPtr<IDXGIAdapter1> ad;
            for (UINT a = 0; fac->EnumAdapters1(a, &ad) != DXGI_ERROR_NOT_FOUND && !found; ++a) {
                Microsoft::WRL::ComPtr<IDXGIOutput> out;
                for (UINT o = 0; ad->EnumOutputs(o, &out) != DXGI_ERROR_NOT_FOUND; ++o) {
                    DXGI_OUTPUT_DESC od{};
                    out->GetDesc(&od);
                    if (!od.AttachedToDesktop) continue;
                    if (cur == monitor_index) {
                        targetRc = od.DesktopCoordinates;
                        found = true;
                        break;
                    }
                    cur++;
                }
            }
        }
        if (!found) return 0;
        HDC scr = GetDC(nullptr);
        bool ok = captureRectToBgra(scr, targetRc.left, targetRc.top,
                                    targetRc.right - targetRc.left, targetRc.bottom - targetRc.top,
                                    out_bgra, thumb_w, thumb_h);
        ReleaseDC(nullptr, scr);
        return ok ? 1 : 0;
    });
}

FC_API int32_t fc_capture_window_thumb(uint64_t hwnd_u64, uint8_t* out_bgra, int32_t thumb_w, int32_t thumb_h) {
    const DWORD selfPid = GetCurrentProcessId();
    return runOnInputDesktop([&]() -> int32_t {
        HWND hwnd = reinterpret_cast<HWND>(hwnd_u64);
        if (!IsWindow(hwnd) || IsHungAppWindow(hwnd) || !out_bgra || thumb_w <= 0 || thumb_h <= 0 || thumb_w > 4096 || thumb_h > 4096) return 0;
        DWORD pid = 0;
        GetWindowThreadProcessId(hwnd, &pid);
        if (pid == selfPid) return 0; // never self-deadlock PrintWindow on our own window!

        RECT rc{};
        GetWindowRect(hwnd, &rc);
        int w = std::min<int>(8192, rc.right - rc.left), h = std::min<int>(8192, rc.bottom - rc.top);
        if (w <= 0 || h <= 0) return 0;

        HDC winDc = GetDC(nullptr);
        HDC tmpDc = CreateCompatibleDC(winDc);
        HBITMAP tmpBmp = CreateCompatibleBitmap(winDc, w, h);
        bool ok = false;
        if (tmpDc && tmpBmp) {
            HGDIOBJ old = SelectObject(tmpDc, tmpBmp);
            if (PrintWindow(hwnd, tmpDc, 2)) {
                ok = captureRectToBgra(tmpDc, 0, 0, w, h, out_bgra, thumb_w, thumb_h);
            }
            SelectObject(tmpDc, old);
        }
        if (tmpBmp) DeleteObject(tmpBmp);
        if (tmpDc) DeleteDC(tmpDc);
        ReleaseDC(nullptr, winDc);
        return ok ? 1 : 0;
    });
}

FC_API int32_t fc_screen_start(int32_t monitor_index, int32_t target_w, int32_t target_h, int32_t fps, int32_t bitrate_kbps, int32_t codec, int32_t loopback_preview, void* preview_hwnd) {
    { std::unique_lock ulk(g_smtx); if (!g_screen) ensureInitLocked(); }
    std::shared_lock lk(g_smtx);
    return (g_screen && g_screen->start(monitor_index, target_w, target_h, fps, bitrate_kbps, codec, loopback_preview != 0, reinterpret_cast<HWND>(preview_hwnd), nullptr, false)) ? 1 : 0;
}
FC_API int32_t fc_screen_start_ex(int32_t monitor_index, uint64_t target_hwnd, int32_t target_w, int32_t target_h, int32_t fps, int32_t bitrate_kbps, int32_t codec, int32_t loopback_preview, void* preview_hwnd, int32_t capture_audio, const char* render_device_id) {
    { std::unique_lock ulk(g_smtx); if (!g_screen) ensureInitLocked(); }
    std::shared_lock lk(g_smtx);
    return (g_screen && g_screen->start(monitor_index, target_w, target_h, fps, bitrate_kbps, codec, loopback_preview != 0, reinterpret_cast<HWND>(preview_hwnd), reinterpret_cast<HWND>(target_hwnd), capture_audio != 0, render_device_id)) ? 1 : 0;
}
FC_API int32_t fc_screen_start_window(uint64_t target_hwnd, int32_t target_w, int32_t target_h, int32_t fps, int32_t bitrate_kbps, int32_t codec, int32_t loopback_preview, void* preview_hwnd) {
    { std::unique_lock ulk(g_smtx); if (!g_screen) ensureInitLocked(); }
    std::shared_lock lk(g_smtx);
    return (g_screen && g_screen->start(0, target_w, target_h, fps, bitrate_kbps, codec, loopback_preview != 0, reinterpret_cast<HWND>(preview_hwnd), reinterpret_cast<HWND>(target_hwnd), false)) ? 1 : 0;
}
FC_API void fc_screen_stop() { std::shared_lock lk(g_smtx); if (g_screen) g_screen->stop(); }
FC_API void fc_screen_resize_preview(int32_t width, int32_t height) { std::shared_lock lk(g_smtx); if (g_screen) g_screen->resizePreview(width, height); }
FC_API void fc_screen_get_stats(FcScreenStats* out_stats) {
    if (!out_stats) return;
    std::memset(out_stats, 0, sizeof(*out_stats));
    std::shared_lock lk(g_smtx);
    if (g_screen) g_screen->getStats(out_stats);
}

// Outbound Go Live Stream Publisher Transport (UDP + DAVE E2EE)
FC_API int32_t fc_pub_connect(const char* ip, uint16_t port, uint32_t ssrc, char* out_ext_ip, int32_t ext_ip_cap, uint16_t* out_ext_port) {
    { std::unique_lock ulk(g_smtx); if (!g_pub_transport) ensureInitLocked(); }
    std::shared_lock lk(g_smtx);
    if (!g_pub_transport) return 0;
    std::string extIp;
    uint16_t extPort = 0;
    if (!g_pub_transport->connectAndDiscover(ip, port, ssrc, extIp, extPort)) return 0;
    if (out_ext_ip && ext_ip_cap > 0) {
        std::memset(out_ext_ip, 0, size_t(ext_ip_cap));
        std::strncpy(out_ext_ip, extIp.c_str(), size_t(ext_ip_cap - 1));
    }
    if (out_ext_port) *out_ext_port = extPort;
    return 1;
}
FC_API int32_t fc_pub_set_key(const uint8_t* secret_key_32, int32_t use_xchacha_rtpsize) {
    std::shared_lock lk(g_smtx);
    if (!g_pub_transport || !secret_key_32) return 0;
    g_pub_transport->setKey(secret_key_32, use_xchacha_rtpsize != 0);
    return 1;
}
FC_API void fc_pub_set_p2p_peer(const char* ip, uint16_t port, int32_t allow_direct_tx) {
    std::shared_lock lk(g_smtx);
    if (g_pub_transport) g_pub_transport->setP2pPeer(ip, port, allow_direct_tx != 0);
}
FC_API void fc_pub_set_video_ssrc(uint32_t video_ssrc) {
    std::shared_lock lk(g_smtx);
    if (g_screen) g_screen->setVideoSsrc(video_ssrc);
    if (g_pub_transport) g_pub_transport->daveSetVideoSsrc(video_ssrc);
}
FC_API void fc_pub_request_keyframe() {
    std::shared_lock lk(g_smtx);
    if (g_screen) g_screen->requestKeyframe();
    // Only emit initial silence frames if system audio isn't actively streaming (prevents 100ms mute & RTP TS jump in live audio!)
    if (g_pub_transport && g_pub_transport->isReady() &&
        g_pub_transport->audioTxPackets() == 0 && (!g_screen || !g_screen->isCapturingAudio())) {
        g_pub_transport->sendSilenceFrames();
    }
}
FC_API void fc_pub_get_stats(int32_t* out_tx_pkts, int32_t* out_pli_rx, int32_t* out_dave_active) {
    std::shared_lock lk(g_smtx);
    if (out_tx_pkts) *out_tx_pkts = g_pub_transport ? g_pub_transport->txPackets() : 0;
    if (out_pli_rx) *out_pli_rx = g_pub_transport ? g_pub_transport->pliCount() : 0;
    if (out_dave_active) *out_dave_active = (g_pub_transport && g_pub_transport->isDaveEncrypting()) ? 1 : 0;
}
FC_API void fc_pub_get_stats_ex(int32_t* out_video_pkts, int32_t* out_audio_pkts, int32_t* out_pli_rx, int32_t* out_dave_active) {
    std::shared_lock lk(g_smtx);
    if (out_video_pkts) *out_video_pkts = g_pub_transport ? g_pub_transport->videoTxPackets() : 0;
    if (out_audio_pkts) *out_audio_pkts = g_pub_transport ? g_pub_transport->audioTxPackets() : 0;
    if (out_pli_rx) *out_pli_rx = g_pub_transport ? g_pub_transport->pliCount() : 0;
    if (out_dave_active) *out_dave_active = (g_pub_transport && g_pub_transport->isDaveEncrypting()) ? 1 : 0;
}
FC_API void fc_pub_disconnect() {
    std::shared_lock lk(g_smtx);
    if (g_screen) g_screen->setVideoSsrc(0);
    if (g_pub_transport) g_pub_transport->disconnect();
}
FC_API int32_t fc_pub_dave_init(uint16_t protocol_version, uint64_t channel_id, const char* self_user_id, uint32_t self_ssrc, uint32_t video_ssrc) {
    { std::unique_lock ulk(g_smtx); if (!g_pub_transport) ensureInitLocked(); }
    std::shared_lock lk(g_smtx);
    bool ok = g_pub_transport && g_pub_transport->daveInit(protocol_version, channel_id, self_user_id, self_ssrc);
    if (ok && video_ssrc != 0) g_pub_transport->daveSetVideoSsrc(video_ssrc);
    return ok ? 1 : 0;
}
FC_API void fc_pub_dave_add_user(const char* user_id) { std::shared_lock lk(g_smtx); if (g_pub_transport) g_pub_transport->daveAddUser(user_id); }
FC_API void fc_pub_dave_remove_user(const char* user_id) { std::shared_lock lk(g_smtx); if (g_pub_transport) g_pub_transport->daveRemoveUser(user_id); }
FC_API int32_t fc_pub_dave_get_key_package(uint8_t* out_buf, int32_t cap) { std::shared_lock lk(g_smtx); return g_pub_transport ? g_pub_transport->daveGetKeyPackage(out_buf, cap) : 0; }
FC_API void fc_pub_dave_set_external_sender(const uint8_t* data, int32_t len) { std::shared_lock lk(g_smtx); if (g_pub_transport) g_pub_transport->daveSetExternalSender(data, len); }
FC_API int32_t fc_pub_dave_process_proposals(const uint8_t* data, int32_t len, uint8_t* out_cw, int32_t cap) {
    std::shared_lock lk(g_smtx);
    return g_pub_transport ? g_pub_transport->daveProcessProposals(data, len, out_cw, cap) : 0;
}
FC_API int32_t fc_pub_dave_process_commit(const uint8_t* data, int32_t len) { std::shared_lock lk(g_smtx); return g_pub_transport ? g_pub_transport->daveProcessCommit(data, len) : 0; }
FC_API int32_t fc_pub_dave_process_welcome(const uint8_t* data, int32_t len) { std::shared_lock lk(g_smtx); return (g_pub_transport && g_pub_transport->daveProcessWelcome(data, len)) ? 1 : 0; }
FC_API void fc_pub_dave_execute_transition(uint16_t protocol_version) { std::shared_lock lk(g_smtx); if (g_pub_transport) g_pub_transport->daveExecuteTransition(protocol_version); }

// Multi-slot Go Live Receivers (slots 0..3 -> watch up to 4 live streams simultaneously in Grid View!)
FC_API int32_t fc_watch_connect_slot(int32_t slot, const char* ip, uint16_t port, uint32_t ssrc, void* hwnd, char* out_ext_ip, int32_t ext_ip_cap, uint16_t* out_ext_port) {
    if (slot < 0 || slot >= kMaxWatchSlots) return 0;
    {
        std::unique_lock ulk(g_smtx);
        if (!getWatchUnlocked(slot)) ensureInitLocked();
    }
    std::shared_lock lk(g_smtx);
    auto* w = getWatchUnlocked(slot);
    if (!w) return 0;
    std::string extIp;
    uint16_t extPort = 0;
    if (!w->connectAndDiscover(ip, port, ssrc, reinterpret_cast<HWND>(hwnd), extIp, extPort)) return 0;
    if (out_ext_ip && ext_ip_cap > 0) {
        std::memset(out_ext_ip, 0, size_t(ext_ip_cap));
        std::strncpy(out_ext_ip, extIp.c_str(), size_t(ext_ip_cap - 1));
    }
    if (out_ext_port) *out_ext_port = extPort;
    return 1;
}
FC_API void fc_watch_set_key_slot(int32_t slot, const uint8_t* secret_key_32, int32_t use_xchacha_rtpsize) {
    std::shared_lock lk(g_smtx);
    if (auto* w = getWatchUnlocked(slot); w && secret_key_32) w->setKey(secret_key_32, use_xchacha_rtpsize != 0);
}
FC_API void fc_watch_set_p2p_peer_slot(int32_t slot, const char* ip, uint16_t port, int32_t allow_direct_tx) {
    std::shared_lock lk(g_smtx);
    if (auto* w = getWatchUnlocked(slot)) w->setP2pPeer(ip, port, allow_direct_tx != 0);
}
FC_API void fc_watch_set_codec_slot(int32_t slot, int32_t codec, int32_t video_pt) {
    std::shared_lock lk(g_smtx);
    if (auto* w = getWatchUnlocked(slot)) w->setCodec(codec, video_pt);
}
FC_API void fc_watch_set_remote_slot(int32_t slot, uint32_t video_ssrc, uint32_t audio_ssrc, const char* streamer_user_id) {
    std::shared_lock lk(g_smtx);
    if (auto* w = getWatchUnlocked(slot)) w->setRemoteStreams(video_ssrc, audio_ssrc, streamer_user_id);
}
FC_API void fc_watch_set_volume_slot(int32_t slot, float volume) { std::shared_lock lk(g_smtx); if (auto* w = getWatchUnlocked(slot)) w->setVolume(volume); }
FC_API void fc_watch_resize_slot(int32_t slot, int32_t width, int32_t height) { std::shared_lock lk(g_smtx); if (auto* w = getWatchUnlocked(slot)) w->resize(width, height); }
FC_API void fc_watch_stop_slot(int32_t slot) { std::shared_lock lk(g_smtx); if (auto* w = getWatchUnlocked(slot)) w->stop(); }
FC_API void fc_watch_get_stats_slot(int32_t slot, FcScreenStats* out_stats) {
    if (!out_stats) return;
    std::memset(out_stats, 0, sizeof(*out_stats));
    std::shared_lock lk(g_smtx);
    if (auto* w = getWatchUnlocked(slot)) w->getStats(out_stats);
}
FC_API int32_t fc_watch_dave_init_slot(int32_t slot, uint16_t proto_ver, uint64_t channel_id, const char* self_user_id, uint32_t self_ssrc) {
    if (slot < 0 || slot >= kMaxWatchSlots) return 0;
    {
        std::unique_lock ulk(g_smtx);
        if (!getWatchUnlocked(slot)) ensureInitLocked();
    }
    std::shared_lock lk(g_smtx);
    auto* w = getWatchUnlocked(slot);
    return (w && w->daveInit(proto_ver, channel_id, self_user_id, self_ssrc)) ? 1 : 0;
}
FC_API void fc_watch_dave_add_user_slot(int32_t slot, const char* user_id) {
    if (slot < 0 || slot >= kMaxWatchSlots) return;
    {
        std::unique_lock ulk(g_smtx);
        if (!getWatchUnlocked(slot)) ensureInitLocked();
    }
    std::shared_lock lk(g_smtx);
    if (auto* w = getWatchUnlocked(slot)) w->daveAddUser(user_id);
}
FC_API void fc_watch_dave_remove_user_slot(int32_t slot, const char* user_id) {
    std::shared_lock lk(g_smtx);
    if (auto* w = getWatchUnlocked(slot)) w->daveRemoveUser(user_id);
}
FC_API int32_t fc_watch_dave_get_key_package_slot(int32_t slot, uint8_t* out_buf, int32_t cap) {
    std::shared_lock lk(g_smtx);
    auto* w = getWatchUnlocked(slot); return w ? w->daveGetKeyPackage(out_buf, cap) : 0;
}
FC_API void fc_watch_dave_set_external_sender_slot(int32_t slot, const uint8_t* data, int32_t len) {
    std::shared_lock lk(g_smtx);
    if (auto* w = getWatchUnlocked(slot)) w->daveSetExternalSender(data, len);
}
FC_API int32_t fc_watch_dave_process_proposals_slot(int32_t slot, const uint8_t* data, int32_t len, uint8_t* out_cw, int32_t cap) {
    std::shared_lock lk(g_smtx);
    auto* w = getWatchUnlocked(slot); return w ? w->daveProcessProposals(data, len, out_cw, cap) : 0;
}
FC_API int32_t fc_watch_dave_process_commit_slot(int32_t slot, const uint8_t* data, int32_t len) {
    std::shared_lock lk(g_smtx);
    auto* w = getWatchUnlocked(slot); return w ? w->daveProcessCommit(data, len) : 0;
}
FC_API int32_t fc_watch_dave_process_welcome_slot(int32_t slot, const uint8_t* data, int32_t len) {
    std::shared_lock lk(g_smtx);
    auto* w = getWatchUnlocked(slot); return (w && w->daveProcessWelcome(data, len)) ? 1 : 0;
}
FC_API void fc_watch_dave_execute_transition_slot(int32_t slot, uint16_t proto_ver) {
    std::shared_lock lk(g_smtx);
    if (auto* w = getWatchUnlocked(slot)) w->daveExecuteTransition(proto_ver);
}

// Slot-0 aliases
FC_API int32_t fc_watch_connect(const char* ip, uint16_t port, uint32_t ssrc, void* hwnd, char* out_ext_ip, int32_t ext_ip_cap, uint16_t* out_ext_port) {
    return fc_watch_connect_slot(0, ip, port, ssrc, hwnd, out_ext_ip, ext_ip_cap, out_ext_port);
}
FC_API void fc_watch_set_key(const uint8_t* k, int32_t x) { fc_watch_set_key_slot(0, k, x); }
FC_API void fc_watch_set_remote(uint32_t v, uint32_t a, const char* u) { fc_watch_set_remote_slot(0, v, a, u); }
FC_API void fc_watch_set_volume(float v) { fc_watch_set_volume_slot(0, v); }
FC_API void fc_watch_resize(int32_t w, int32_t h) { fc_watch_resize_slot(0, w, h); }
FC_API void fc_watch_stop() { fc_watch_stop_slot(0); }
FC_API void fc_watch_get_stats(FcScreenStats* o) { fc_watch_get_stats_slot(0, o); }
FC_API int32_t fc_watch_dave_init(uint16_t p, uint64_t c, const char* u, uint32_t s) { return fc_watch_dave_init_slot(0, p, c, u, s); }
FC_API void fc_watch_dave_add_user(const char* u) { fc_watch_dave_add_user_slot(0, u); }
FC_API void fc_watch_dave_remove_user(const char* u) { fc_watch_dave_remove_user_slot(0, u); }
FC_API int32_t fc_watch_dave_get_key_package(uint8_t* b, int32_t c) { return fc_watch_dave_get_key_package_slot(0, b, c); }
FC_API void fc_watch_dave_set_external_sender(const uint8_t* d, int32_t l) { fc_watch_dave_set_external_sender_slot(0, d, l); }
FC_API int32_t fc_watch_dave_process_proposals(const uint8_t* d, int32_t l, uint8_t* o, int32_t c) { return fc_watch_dave_process_proposals_slot(0, d, l, o, c); }
FC_API int32_t fc_watch_dave_process_commit(const uint8_t* d, int32_t l) { return fc_watch_dave_process_commit_slot(0, d, l); }
FC_API int32_t fc_watch_dave_process_welcome(const uint8_t* d, int32_t l) { return fc_watch_dave_process_welcome_slot(0, d, l); }
FC_API void fc_watch_dave_execute_transition(uint16_t p) { fc_watch_dave_execute_transition_slot(0, p); }

} // extern "C"
