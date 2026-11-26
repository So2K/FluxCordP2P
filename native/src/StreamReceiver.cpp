#include "StreamReceiver.h"
#include "NetTurbo.h"
#include "VideoDepacketizer.h"
#include <initguid.h>
#include <codecapi.h>
#include <sodium.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <dxgi1_2.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mftransform.h>
#include <wmcodecdsp.h>
#include <wrl/client.h>
#include <algorithm>
#include <chrono>
#include <cstring>

using Microsoft::WRL::ComPtr;

namespace fc {

namespace {
inline void putBE16(uint8_t* p, uint16_t v) { p[0] = uint8_t(v >> 8); p[1] = uint8_t(v & 0xFF); }
inline void putBE32(uint8_t* p, uint32_t v) {
    p[0] = uint8_t(v >> 24); p[1] = uint8_t((v >> 16) & 0xFF);
    p[2] = uint8_t((v >> 8) & 0xFF); p[3] = uint8_t(v & 0xFF);
}
inline uint16_t getBE16(const uint8_t* p) { return uint16_t((uint16_t(p[0]) << 8) | p[1]); }
inline uint32_t getBE32(const uint8_t* p) {
    return (uint32_t(p[0]) << 24) | (uint32_t(p[1]) << 16) | (uint32_t(p[2]) << 8) | p[3];
}
inline bool isSilenceFrame(const uint8_t* p, int len) {
    return p && len == 3 && p[0] == 0xF8 && p[1] == 0xFF && p[2] == 0xFE;
}
int64_t steadyMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}
} // namespace

StreamReceiver::StreamReceiver(VoiceEngine* voice, int32_t slot_index) : voice_(voice), slot_index_(slot_index) {
    WSADATA wsa{};
    wsa_ok_ = (WSAStartup(MAKEWORD(2, 2), &wsa) == 0);
    sodium_init();
    worker_done_ev_ = CreateEventW(nullptr, TRUE, TRUE, nullptr);
}

StreamReceiver::~StreamReceiver() {
    stop();
    if (worker_done_ev_) { CloseHandle(worker_done_ev_); worker_done_ev_ = nullptr; }
    if (wsa_ok_) WSACleanup();
}

void StreamReceiver::daveDestroyLocked() {
    if (dave_dec_) { daveDecryptorDestroy(dave_dec_); dave_dec_ = nullptr; }
    if (dave_session_) { daveSessionDestroy(dave_session_); dave_session_ = nullptr; }
    streamer_uid_.clear();
    dave_proto_ver_ = 0;
    dave_has_ratchet_ = false;
}

bool StreamReceiver::daveInit(uint16_t proto_ver, uint64_t channel_id, const char* self_user_id, uint32_t self_ssrc) {
    if (self_ssrc != 0) local_ssrc_.store(self_ssrc);
    bool alreadyDecoding = false;
    {
        std::lock_guard slk(stats_mtx_);
        alreadyDecoding = (stats_.frames_encoded > 0);
    }
    std::lock_guard lk(dave_mtx_);
    dave_proto_ver_ = proto_ver;
    if (!dave_session_) dave_session_ = daveSessionCreate(nullptr, nullptr, nullptr, nullptr);
    if (!dave_dec_) dave_dec_ = daveDecryptorCreate();
    if (!dave_session_ || !dave_dec_) return false;
    if (proto_ver > 0) {
        daveSessionInit(dave_session_, proto_ver, channel_id, self_user_id ? self_user_id : "");
        if (!alreadyDecoding || dave_has_ratchet_) {
            daveDecryptorTransitionToPassthroughMode(dave_dec_, false);
        }
    } else {
        daveSessionReset(dave_session_);
        daveDecryptorTransitionToPassthroughMode(dave_dec_, true);
        dave_has_ratchet_ = false;
    }
    return true;
}

void StreamReceiver::daveAddUser(const char* user_id) {
    if (!user_id || !*user_id) return;
    std::lock_guard lk(dave_mtx_);
    if (!dave_session_) dave_session_ = daveSessionCreate(nullptr, nullptr, nullptr, nullptr);
    if (dave_session_) daveSessionAddRecognizedUser(dave_session_, user_id);
    if (dave_session_ && dave_dec_ && dave_proto_ver_ > 0 && !streamer_uid_.empty() && streamer_uid_ == user_id) {
        DAVEKeyRatchetHandle kr = daveSessionGetKeyRatchet(dave_session_, user_id);
        if (kr) {
            daveDecryptorTransitionToPassthroughMode(dave_dec_, false);
            daveDecryptorTransitionToKeyRatchet(dave_dec_, kr);
            daveKeyRatchetDestroy(kr);
            dave_has_ratchet_ = true;
        }
    }
}

void StreamReceiver::daveRemoveUser(const char* user_id) {
    if (!user_id || !*user_id) return;
    std::lock_guard lk(dave_mtx_);
    if (dave_session_) daveSessionRemoveRecognizedUser(dave_session_, user_id);
}

int32_t StreamReceiver::daveGetKeyPackage(uint8_t* out_buf, int32_t cap) {
    std::lock_guard lk(dave_mtx_);
    if (!dave_session_ || !out_buf || cap <= 0) return 0;
    uint8_t* kp = nullptr;
    size_t len = 0;
    daveSessionGetMarshalledKeyPackage(dave_session_, &kp, &len);
    if (!kp || len == 0) return 0;
    int32_t n = 0;
    if (len <= size_t(cap)) {
        n = int32_t(len);
        std::memcpy(out_buf, kp, len);
    }
    daveFree(kp);
    return n;
}

void StreamReceiver::daveSetExternalSender(const uint8_t* data, int32_t len) {
    std::lock_guard lk(dave_mtx_);
    if (dave_session_ && data && len > 0) daveSessionSetExternalSender(dave_session_, data, size_t(len));
}

int32_t StreamReceiver::daveProcessProposals(const uint8_t* data, int32_t len, uint8_t* out_cw, int32_t cap) {
    std::lock_guard lk(dave_mtx_);
    if (!dave_session_ || !data || len <= 0) return 0;
    uint8_t* cw = nullptr;
    size_t cwLen = 0;
    daveSessionProcessProposals(dave_session_, data, size_t(len), &cw, &cwLen);
    if (!cw || cwLen == 0) return 0;
    int32_t n = 0;
    if (out_cw && cap > 0 && cwLen <= size_t(cap)) {
        n = int32_t(cwLen);
        std::memcpy(out_cw, cw, cwLen);
    }
    daveFree(cw);
    return n;
}

int32_t StreamReceiver::daveProcessCommit(const uint8_t* data, int32_t len) {
    std::lock_guard lk(dave_mtx_);
    if (!dave_session_ || !data || len <= 0) return 0;
    DAVECommitResultHandle res = daveSessionProcessCommit(dave_session_, data, size_t(len));
    if (!res) return 0;
    bool ignored = daveCommitResultIsIgnored(res);
    bool failed = daveCommitResultIsFailed(res);
    daveCommitResultDestroy(res);
    if (ignored) return 2;
    return failed ? 0 : 1;
}

bool StreamReceiver::daveProcessWelcome(const uint8_t* data, int32_t len) {
    std::lock_guard lk(dave_mtx_);
    if (!dave_session_ || !data || len <= 0) return false;
    DAVEWelcomeResultHandle res = daveSessionProcessWelcome(dave_session_, data, size_t(len));
    if (!res) return false;
    daveWelcomeResultDestroy(res);
    return true;
}

void StreamReceiver::daveExecuteTransition(uint16_t proto_ver) {
    std::lock_guard lk(dave_mtx_);
    dave_proto_ver_ = proto_ver;
    if (!dave_dec_) dave_dec_ = daveDecryptorCreate();
    if (!dave_dec_) return;
    if (proto_ver == 0) {
        daveDecryptorTransitionToPassthroughMode(dave_dec_, true);
        dave_has_ratchet_ = false;
        return;
    }
    if (dave_session_ && !streamer_uid_.empty()) {
        DAVEKeyRatchetHandle kr = daveSessionGetKeyRatchet(dave_session_, streamer_uid_.c_str());
        if (kr) {
            daveDecryptorTransitionToPassthroughMode(dave_dec_, false);
            daveDecryptorTransitionToKeyRatchet(dave_dec_, kr);
            daveKeyRatchetDestroy(kr);
            dave_has_ratchet_ = true;
        }
    }
}

void StreamReceiver::setRemoteStreams(uint32_t video_ssrc, uint32_t audio_ssrc, const char* streamer_user_id) {
    if (video_ssrc != 0) {
        uint32_t prev = video_ssrc_.exchange(video_ssrc);
        if (prev != 0 && prev != video_ssrc) {
            reset_depack_.store(true);
        }
    }
    if (audio_ssrc != 0) {
        audio_ssrc_.store(audio_ssrc);
        if (voice_) voice_->setUserVolume(makeVoiceAudioKey(audio_ssrc), stream_vol_.load());
    }
    std::lock_guard lk(dave_mtx_);
    if (streamer_user_id && *streamer_user_id) {
        streamer_uid_ = streamer_user_id;
        if (dave_session_) daveSessionAddRecognizedUser(dave_session_, streamer_user_id);
        if (dave_session_ && dave_dec_ && dave_proto_ver_ > 0) {
            DAVEKeyRatchetHandle kr = daveSessionGetKeyRatchet(dave_session_, streamer_user_id);
            if (kr) {
                daveDecryptorTransitionToPassthroughMode(dave_dec_, false);
                daveDecryptorTransitionToKeyRatchet(dave_dec_, kr);
                daveKeyRatchetDestroy(kr);
                dave_has_ratchet_ = true;
            }
        }
    }
}

void StreamReceiver::setVolume(float vol) {
    vol = std::clamp(vol, 0.0f, 2.0f);
    stream_vol_.store(vol);
    uint32_t as = audio_ssrc_.load();
    if (voice_ && as != 0) voice_->setUserVolume(makeVoiceAudioKey(as), vol);
}

bool StreamReceiver::connectAndDiscover(const char* ip, uint16_t port, uint32_t ssrc, HWND hwnd, std::string& out_ext_ip, uint16_t& out_ext_port) {
    if (!ip || !*ip) return false;
    std::lock_guard life(lifecycle_mtx_);
    // Close ONLY transport & worker thread — preserve pre-populated DAVE session / streamer_uid_!
    closeTransportLocked();
    hwnd_ = hwnd;
    local_ssrc_.store(ssrc);
    rtcp_nonce_ = 1;

    SOCKET s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (s == INVALID_SOCKET) return false;

    int rcvBuf = 8 * 1024 * 1024;
    setsockopt(s, SOL_SOCKET, SO_RCVBUF, reinterpret_cast<const char*>(&rcvBuf), sizeof(rcvBuf));
    DWORD timeoutMs = 500;
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeoutMs), sizeof(timeoutMs));

    sockaddr_in rem{};
    rem.sin_family = AF_INET;
    rem.sin_port = htons(port);
    if (inet_pton(AF_INET, ip, &rem.sin_addr) != 1) {
        closesocket(s);
        return false;
    }

    uint8_t req[74]{};
    putBE16(req + 0, 0x0001);
    putBE16(req + 2, 70);
    putBE32(req + 4, ssrc);
    const bool isWan = !p2p::isPrivateIpv4Addr(rem);
    if (isWan) p2p::sendCloudflareStunReq(s);
    if (sendto(s, reinterpret_cast<const char*>(req), sizeof(req), 0,
               reinterpret_cast<sockaddr*>(&rem), sizeof(rem)) != sizeof(req)) {
        closesocket(s);
        return false;
    }

    uint8_t resp[128]{};
    bool discOk = false;
    int64_t deadlineMs = steadyMs() + (isWan ? 2800 : 1500);
    while (steadyMs() < deadlineMs) {
        sockaddr_in from{};
        int fromLen = sizeof(from);
        int n = recvfrom(s, reinterpret_cast<char*>(resp), sizeof(resp), 0,
                         reinterpret_cast<sockaddr*>(&from), &fromLen);
        if (n < 0) {
            if (isWan) p2p::sendCloudflareStunReq(s);
            sendto(s, reinterpret_cast<const char*>(req), sizeof(req), 0,
                   reinterpret_cast<sockaddr*>(&rem), sizeof(rem));
            continue;
        }
        if (isWan && p2p::tryParseStunXorMapped(resp, n)) {
            sendto(s, reinterpret_cast<const char*>(req), sizeof(req), 0,
                   reinterpret_cast<sockaddr*>(&rem), sizeof(rem));
            continue;
        }
        if (from.sin_addr.s_addr == rem.sin_addr.s_addr &&
            from.sin_port == rem.sin_port &&
            n >= 74 && getBE16(resp) == 0x0002 && getBE32(resp + 4) == ssrc) {
            discOk = true;
            break;
        }
    }
    if (!discOk) {
        closesocket(s);
        return false;
    }
    char ipBuf[65]{};
    std::memcpy(ipBuf, resp + 8, 64);
    out_ext_ip = ipBuf;
    out_ext_port = getBE16(resp + 72);

    DWORD rxTimeout = 4;
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&rxTimeout), sizeof(rxTimeout));
    ULONG flowId = 0;
    optimizeUdpSocket(s, true, &rem, &flowId);

    sock_ = s;
    qos_flow_id_ = flowId;
    remote_ = rem;

    {
        std::lock_guard lk(stats_mtx_);
        std::memset(&stats_, 0, sizeof(stats_));
        stats_.running = 1;
        stats_.zero_copy = 1;
        std::strncpy(stats_.encoder_name, "DXVA / NVDEC Hardware H.264", sizeof(stats_.encoder_name) - 1);
    }

    if (worker_done_ev_) ResetEvent(worker_done_ev_);
    running_.store(true);
    worker_ = std::thread([this] { rxDecodeLoop(); });
    return true;
}

void StreamReceiver::setKey(const uint8_t key[32], bool use_xchacha_rtpsize) {
    if (!key) return;
    std::lock_guard lk(key_mtx_);
    std::memcpy(key_, key, 32);
    use_xchacha_ = use_xchacha_rtpsize;
    has_key_.store(true);
}

void StreamReceiver::setCodec(int32_t codec, int32_t video_pt) {
    int32_t prevC = video_codec_.exchange(codec);
    if (video_pt > 0) video_pt_.store(video_pt);
    if (prevC != codec) reset_depack_.store(true);
}

void StreamReceiver::setP2pPeer(const char* ip, uint16_t port, bool allow_direct_tx) {
    std::lock_guard lk(p2p_mtx_);
    allow_p2p_tx_.store(allow_direct_tx);
    if (!ip || !*ip || port == 0) {
        has_p2p_peer_.store(false);
        last_p2p_rx_ms_.store(0);
        return;
    }
    sockaddr_in cand{};
    cand.sin_family = AF_INET;
    cand.sin_port = htons(port);
    if (inet_pton(AF_INET, ip, &cand.sin_addr) == 1) {
        p2p_peer_ = cand;
        has_p2p_peer_.store(true);
    }
}

void StreamReceiver::resize(int32_t w, int32_t h) {
    if (w > 16 && h > 16) {
        pending_w_.store(w);
        pending_h_.store(h);
    }
}

void StreamReceiver::stop() {
    std::lock_guard life(lifecycle_mtx_);
    stopLocked();
}

void StreamReceiver::closeTransportLocked() {
    running_.store(false);
    has_key_.store(false);
    has_p2p_peer_.store(false);
    allow_p2p_tx_.store(false);
    last_p2p_rx_ms_.store(0);
    reset_depack_.store(false);

    if (sock_ != INVALID_SOCKET) shutdown(sock_, SD_BOTH);
    if (worker_.joinable()) {
        // Prevent cross-thread DXGI SwapChain HWND SendMessage deadlock when stop() is called from UI thread!
        if (hwnd_ && worker_done_ev_ && GetWindowThreadProcessId(hwnd_, nullptr) == GetCurrentThreadId()) {
            while (WaitForSingleObject(worker_done_ev_, 0) != WAIT_OBJECT_0) {
                DWORD wr = MsgWaitForMultipleObjectsEx(1, &worker_done_ev_, 50, QS_SENDMESSAGE, 0);
                if (wr == WAIT_OBJECT_0) break;
                MSG msg{};
                PeekMessageW(&msg, nullptr, 0, 0, PM_NOREMOVE | PM_QS_SENDMESSAGE);
            }
        }
        worker_.join();
    }
    if (sock_ != INVALID_SOCKET) {
        removeUdpQosFlow(sock_, qos_flow_id_);
        closesocket(sock_);
        sock_ = INVALID_SOCKET;
    }
    std::lock_guard lk(stats_mtx_);
    stats_.running = 0;
}

void StreamReceiver::stopLocked() {
    video_ssrc_.store(0);
    audio_ssrc_.store(0);
    closeTransportLocked();
    std::lock_guard lk(dave_mtx_);
    daveDestroyLocked();
}

void StreamReceiver::getStats(FcScreenStats* out) const {
    if (!out) return;
    std::lock_guard lk(stats_mtx_);
    *out = stats_;
}

void StreamReceiver::rxDecodeLoop() {
    struct DoneGuard {
        HANDLE ev;
        ~DoneGuard() { if (ev) SetEvent(ev); }
    } doneGuard{ worker_done_ev_ };

    HANDLE mm = boostRealtimeThread(L"Games");
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    MFStartup(MF_VERSION);
    const sockaddr_in rem = remote_;

    { // Inner COM scope so ALL D3D11 / MF objects release cleanly BEFORE MFShutdown!
    ComPtr<ID3D11Device> dev;
    ComPtr<ID3D11DeviceContext> ctx;
    D3D_FEATURE_LEVEL fl{};
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
    D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, nullptr, 0, D3D11_SDK_VERSION, &dev, &fl, &ctx);
    ComPtr<ID3D11Multithread> mt;
    if (dev && SUCCEEDED(dev.As(&mt))) mt->SetMultithreadProtected(TRUE);

    UINT resetToken = 0;
    ComPtr<IMFDXGIDeviceManager> dxgiMgr;
    if (dev && SUCCEEDED(MFCreateDXGIDeviceManager(&resetToken, &dxgiMgr)) && dxgiMgr) {
        dxgiMgr->ResetDevice(dev.Get(), resetToken);
    }

    // Obtain DXGI factory from dev's own adapter (MSDN multi-adapter best practice)
    ComPtr<IDXGIFactory2> fac;
    if (dev) {
        ComPtr<IDXGIDevice> dxgiDev;
        ComPtr<IDXGIAdapter> adapter;
        if (SUCCEEDED(dev.As(&dxgiDev)) && SUCCEEDED(dxgiDev->GetAdapter(&adapter))) {
            adapter->GetParent(IID_PPV_ARGS(&fac));
        }
    }
    if (!fac) CreateDXGIFactory1(IID_PPV_ARGS(&fac));

    ComPtr<IDXGISwapChain1> swap;
    UINT sw = 1280, sh = 720;
    auto clearSwapBlack = [&]() {
        if (!swap || !dev || !ctx) return;
        ComPtr<ID3D11Texture2D> bb;
        ComPtr<ID3D11RenderTargetView> rtv;
        if (SUCCEEDED(swap->GetBuffer(0, IID_PPV_ARGS(&bb))) &&
            SUCCEEDED(dev->CreateRenderTargetView(bb.Get(), nullptr, &rtv))) {
            const float blk[4] = { 0.0f, 0.0f, 0.0f, 1.0f };
            ctx->ClearRenderTargetView(rtv.Get(), blk);
            swap->Present(0, 0);
        }
    };

    if (dev && fac && hwnd_ && IsWindow(hwnd_)) {
        RECT rc{};
        GetClientRect(hwnd_, &rc);
        sw = std::max<UINT>(16, UINT(rc.right - rc.left));
        sh = std::max<UINT>(16, UINT(rc.bottom - rc.top));
        DXGI_SWAP_CHAIN_DESC1 scd{};
        scd.Width = sw; scd.Height = sh;
        scd.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        scd.SampleDesc.Count = 1;
        scd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        scd.BufferCount = 2;
        scd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        scd.Scaling = DXGI_SCALING_STRETCH;
        scd.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        if (SUCCEEDED(fac->CreateSwapChainForHwnd(dev.Get(), hwnd_, &scd, nullptr, nullptr, &swap))) {
            clearSwapBlack();
        }
    }

    ComPtr<IMFTransform> decMft;
    int32_t activeDecCodec = -1;
    auto initDecoderForCodec = [&](int32_t targetCodec) {
        if (decMft && activeDecCodec == targetCodec) return;
        if (decMft) {
            decMft->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
            decMft->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
            ComPtr<IMFShutdown> shut;
            if (SUCCEEDED(decMft.As(&shut))) shut->Shutdown();
            decMft.Reset();
        }
        activeDecCodec = targetCodec;
        const GUID subtype = (targetCodec == FC_CODEC_HEVC) ? MFVideoFormat_HEVC : MFVideoFormat_H264;
        MFT_REGISTER_TYPE_INFO inInfo{ MFMediaType_Video, subtype };

        auto tryInitMft = [&](IMFTransform* mft, bool bindD3d) -> bool {
            if (!mft) return false;
            ComPtr<IMFAttributes> attrs;
            if (SUCCEEDED(mft->GetAttributes(&attrs)) && attrs) {
                UINT32 isAsync = 0;
                if (SUCCEEDED(attrs->GetUINT32(MF_TRANSFORM_ASYNC, &isAsync)) && isAsync)
                    attrs->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, TRUE);
                attrs->SetUINT32(CODECAPI_AVLowLatencyMode, TRUE);
            }
            if (bindD3d && dxgiMgr) {
                if (FAILED(mft->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, reinterpret_cast<ULONG_PTR>(dxgiMgr.Get()))))
                    return false;
            }
            ComPtr<IMFMediaType> mtIn;
            if (FAILED(MFCreateMediaType(&mtIn)) || !mtIn) return false;
            mtIn->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            mtIn->SetGUID(MF_MT_SUBTYPE, subtype);
            if (FAILED(mft->SetInputType(0, mtIn.Get(), 0))) return false;

            bool outSet = false;
            for (DWORD i = 0; ; ++i) {
                ComPtr<IMFMediaType> mtOut;
                if (FAILED(mft->GetOutputAvailableType(0, i, &mtOut))) break;
                GUID sub{};
                if (SUCCEEDED(mtOut->GetGUID(MF_MT_SUBTYPE, &sub)) && sub == MFVideoFormat_NV12) {
                    if (SUCCEEDED(mft->SetOutputType(0, mtOut.Get(), 0))) { outSet = true; break; }
                }
            }
            if (!outSet) return false;
            mft->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
            mft->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
            return true;
        };

        for (UINT32 flagsPass : { UINT32(MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER),
                                  UINT32(MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER) }) {
            IMFActivate** acts = nullptr;
            UINT32 count = 0;
            if (SUCCEEDED(MFTEnumEx(MFT_CATEGORY_VIDEO_DECODER, flagsPass, &inInfo, nullptr, &acts, &count)) && count > 0) {
                for (UINT32 i = 0; i < count; ++i) {
                    ComPtr<IMFTransform> cand;
                    if (!decMft && SUCCEEDED(acts[i]->ActivateObject(IID_PPV_ARGS(&cand)))) {
                        if (tryInitMft(cand.Get(), (flagsPass & MFT_ENUM_FLAG_HARDWARE) != 0) || tryInitMft(cand.Get(), false)) {
                            decMft = cand;
                        } else {
                            ComPtr<IMFShutdown> shut;
                            if (SUCCEEDED(cand.As(&shut))) shut->Shutdown();
                            acts[i]->ShutdownObject();
                        }
                    }
                    acts[i]->Release();
                }
                CoTaskMemFree(acts);
                if (decMft) return;
            } else if (acts) {
                CoTaskMemFree(acts);
            }
        }

        if (!decMft && targetCodec == FC_CODEC_H264) {
            ComPtr<IMFTransform> cand;
            if (SUCCEEDED(CoCreateInstance(CLSID_CMSH264DecoderMFT, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&cand)))) {
                if (tryInitMft(cand.Get(), true) || tryInitMft(cand.Get(), false)) decMft = cand;
            }
        }
    };
    initDecoderForCodec(video_codec_.load());

    ComPtr<ID3D11VideoDevice> vdev;
    ComPtr<ID3D11VideoContext> vctx;
    ComPtr<ID3D11VideoProcessorEnumerator> vpenum;
    ComPtr<ID3D11VideoProcessor> vp;
    if (dev) { dev.As(&vdev); ctx.As(&vctx); }
    UINT curDecW = 0, curDecH = 0;
    RECT dispAperture{ 0, 0, 0, 0 }; // Exact visible crop (fixes 1920x1088 DXVA bottom green bar -> 1920x1080!)

    VideoDepacketizer depack;
    VideoDepacketizer::FrameOut readyFrame;

    uint8_t pkt[2048];
    uint8_t plain[2048];
    std::vector<uint8_t> daveBuf(2 * 1024 * 1024);
    std::vector<uint8_t> cachedVps;
    std::vector<uint8_t> cachedSps;
    std::vector<uint8_t> cachedPps;
    std::vector<uint8_t> prependBuf;

    int64_t framesDecoded = 0;
    int64_t framesWindow = 0;
    int64_t bytesWindow = 0;
    int64_t rxUdpPkts = 0;
    int64_t rxVideoPkts = 0;
    int64_t daveOk = 0;
    int64_t daveFail = 0;
    int64_t kfSkipped = 0;
    int64_t mftFed = 0;
    uint8_t lastNaluType = 0;
    int64_t lastRatchetRetryMs = 0;
    auto windowStart = std::chrono::steady_clock::now();
    int64_t lastPliMs = 0;
    int64_t lastKeepaliveMs = 0;

    auto updateDiagText = [&]() {
        char diag[64]{};
        std::snprintf(diag, sizeof(diag), "rx=%lld v=%lld dv=%lld/%lld kf=%lld dec=%lld nt=%u",
                      (long long)rxUdpPkts, (long long)rxVideoPkts, (long long)daveOk, (long long)daveFail,
                      (long long)kfSkipped, (long long)framesDecoded, unsigned(lastNaluType));
        std::lock_guard lk(stats_mtx_);
        std::strncpy(stats_.encoder_name, diag, sizeof(stats_.encoder_name) - 1);
    };

    auto cacheSpsPpsFromFrame = [&](const uint8_t* data, size_t len, bool isHevc) {
        size_t pos = 0;
        while (pos + 3 < len) {
            size_t sc = 0;
            if (data[pos] == 0 && data[pos + 1] == 0 && data[pos + 2] == 1) sc = 3;
            else if (pos + 4 <= len && data[pos] == 0 && data[pos + 1] == 0 && data[pos + 2] == 0 && data[pos + 3] == 1) sc = 4;
            if (sc == 0) { pos++; continue; }
            size_t start = pos + sc;
            size_t next = start;
            while (next + 3 < len) {
                if (data[next] == 0 && data[next + 1] == 0 &&
                    (data[next + 2] == 1 || (next + 3 < len && data[next + 2] == 0 && data[next + 3] == 1))) break;
                next++;
            }
            if (next + 3 >= len) next = len;
            if (start < next) {
                if (!isHevc) {
                    uint8_t nt = data[start] & 0x1F;
                    if (nt == 7) {
                        cachedSps.assign({0, 0, 0, 1});
                        cachedSps.insert(cachedSps.end(), data + start, data + next);
                    } else if (nt == 8) {
                        cachedPps.assign({0, 0, 0, 1});
                        cachedPps.insert(cachedPps.end(), data + start, data + next);
                    }
                } else {
                    uint8_t nt = (data[start] >> 1) & 0x3F;
                    if (nt == 32) {
                        cachedVps.assign({0, 0, 0, 1});
                        cachedVps.insert(cachedVps.end(), data + start, data + next);
                    } else if (nt == 33) {
                        cachedSps.assign({0, 0, 0, 1});
                        cachedSps.insert(cachedSps.end(), data + start, data + next);
                    } else if (nt == 34) {
                        cachedPps.assign({0, 0, 0, 1});
                        cachedPps.insert(cachedPps.end(), data + start, data + next);
                    }
                }
            }
            pos = next;
        }
    };

    auto frameHasNalu = [](const uint8_t* data, size_t len, uint8_t targetNt, bool isHevc) -> bool {
        for (size_t i = 0; i + 3 < len; ++i) {
            if (data[i] == 0 && data[i + 1] == 0) {
                size_t sc = (data[i + 2] == 1) ? 3 : ((i + 4 <= len && data[i + 2] == 0 && data[i + 3] == 1) ? 4 : 0);
                if (sc > 0 && i + sc < len) {
                    uint8_t nt = isHevc ? uint8_t((data[i + sc] >> 1) & 0x3F) : uint8_t(data[i + sc] & 0x1F);
                    if (nt == targetNt) return true;
                }
            }
        }
        return false;
    };

    auto sendEncryptedRtcpPli = [&](uint32_t mediaSsrc) {
        if (sock_ == INVALID_SOCKET || !has_key_.load() || mediaSsrc == 0) return;
        uint8_t localKey[32];
        bool useXchacha = true;
        {
            std::lock_guard klk(key_mtx_);
            std::memcpy(localKey, key_, 32);
            useXchacha = use_xchacha_;
        }

        uint8_t outPkt[64]{};
        outPkt[0] = 0x81; // V=2, P=0, FMT=1 (PLI)
        outPkt[1] = 206;  // PT=PSFB
        putBE16(outPkt + 2, 2);
        putBE32(outPkt + 4, local_ssrc_.load());

        uint8_t body[4];
        putBE32(body, mediaSsrc);

        uint32_t ctr = rtcp_nonce_++;
        uint8_t nonce[24]{};
        std::memcpy(nonce, &ctr, 4);
        int totalLen = 0;
        if (useXchacha) {
            unsigned long long clen = 0;
            if (crypto_aead_xchacha20poly1305_ietf_encrypt(
                    outPkt + 8, &clen, body, 4, outPkt, 8, nullptr, nonce, localKey) != 0) return;
            std::memcpy(outPkt + 8 + clen, &ctr, 4);
            totalLen = int(8 + clen + 4);
        } else {
            if (crypto_secretbox_easy(outPkt + 8, body, 4, nonce, localKey) != 0) return;
            std::memcpy(outPkt + 8 + 4 + crypto_secretbox_MACBYTES, &ctr, 4);
            totalLen = 8 + 4 + crypto_secretbox_MACBYTES + 4;
        }
        sendto(sock_, reinterpret_cast<const char*>(outPkt), totalLen, 0,
               reinterpret_cast<sockaddr*>(&remote_), sizeof(remote_));
        if (allow_p2p_tx_.load() && has_p2p_peer_.load()) {
            sockaddr_in p2pDst{};
            { std::lock_guard plk(p2p_mtx_); p2pDst = p2p_peer_; }
            sendto(sock_, reinterpret_cast<const char*>(outPkt), totalLen, 0,
                   reinterpret_cast<sockaddr*>(&p2pDst), sizeof(p2pDst));
        }
    };

    auto drainDecoderOutputs = [&]() {
        if (!decMft) return;
        for (int drain = 0; drain < 32; ++drain) {
            MFT_OUTPUT_DATA_BUFFER outBuf{};
            DWORD status = 0;
            HRESULT ohr = decMft->ProcessOutput(0, 1, &outBuf, &status);
            if (outBuf.pEvents) { outBuf.pEvents->Release(); outBuf.pEvents = nullptr; }
            if (ohr == MF_E_TRANSFORM_STREAM_CHANGE) {
                for (DWORD i = 0; ; ++i) {
                    ComPtr<IMFMediaType> mtOut;
                    if (FAILED(decMft->GetOutputAvailableType(0, i, &mtOut))) break;
                    GUID sub{};
                    if (SUCCEEDED(mtOut->GetGUID(MF_MT_SUBTYPE, &sub)) && sub == MFVideoFormat_NV12) {
                        decMft->SetOutputType(0, mtOut.Get(), 0);
                        UINT32 dw = 0, dh = 0;
                        MFGetAttributeSize(mtOut.Get(), MF_MT_FRAME_SIZE, &dw, &dh);
                        curDecW = dw; curDecH = dh;
                        MFVideoArea area{};
                        UINT32 areaSize = 0;
                        if (SUCCEEDED(mtOut->GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE, reinterpret_cast<UINT8*>(&area), sizeof(area), &areaSize)) &&
                            areaSize >= sizeof(area) && area.Area.cx > 0 && area.Area.cy > 0) {
                            dispAperture.left = area.OffsetX.value;
                            dispAperture.top = area.OffsetY.value;
                            dispAperture.right = area.OffsetX.value + area.Area.cx;
                            dispAperture.bottom = area.OffsetY.value + area.Area.cy;
                        } else {
                            dispAperture = { 0, 0, LONG(dw), LONG(dh) };
                        }
                        vp.Reset(); vpenum.Reset();
                        break;
                    }
                }
                continue;
            }
            if (FAILED(ohr) || !outBuf.pSample) break;

            ComPtr<IMFMediaBuffer> ob;
            if (SUCCEEDED(outBuf.pSample->GetBufferByIndex(0, &ob))) {
                ComPtr<IMFDXGIBuffer> dxBuf;
                ComPtr<ID3D11Texture2D> decTex;
                UINT subRes = 0;
                if (SUCCEEDED(ob.As(&dxBuf)) && SUCCEEDED(dxBuf->GetResource(IID_PPV_ARGS(&decTex))) &&
                    SUCCEEDED(dxBuf->GetSubresourceIndex(&subRes)) && swap && vdev && vctx) {
                    D3D11_TEXTURE2D_DESC td{};
                    decTex->GetDesc(&td);
                    if (curDecW != td.Width || curDecH != td.Height || !vp) {
                        curDecW = td.Width; curDecH = td.Height;
                        if (dispAperture.right <= dispAperture.left || dispAperture.right > LONG(curDecW) || dispAperture.bottom > LONG(curDecH)) {
                            dispAperture = { 0, 0, LONG(curDecW), LONG(curDecH) };
                        }
                        vp.Reset(); vpenum.Reset();
                        D3D11_VIDEO_PROCESSOR_CONTENT_DESC vcd{};
                        vcd.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
                        vcd.InputWidth = curDecW; vcd.InputHeight = curDecH;
                        vcd.OutputWidth = sw; vcd.OutputHeight = sh;
                        vcd.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
                        if (SUCCEEDED(vdev->CreateVideoProcessorEnumerator(&vcd, &vpenum)))
                            vdev->CreateVideoProcessor(vpenum.Get(), 0, &vp);
                    }
                    ComPtr<ID3D11Texture2D> backBuf;
                    if (vp && vpenum && SUCCEEDED(swap->GetBuffer(0, IID_PPV_ARGS(&backBuf)))) {
                        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC ivd{};
                        ivd.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
                        ivd.Texture2D.ArraySlice = subRes;
                        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC ovd{};
                        ovd.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
                        ComPtr<ID3D11VideoProcessorInputView> pin;
                        ComPtr<ID3D11VideoProcessorOutputView> pout;
                        if (SUCCEEDED(vdev->CreateVideoProcessorInputView(decTex.Get(), vpenum.Get(), &ivd, &pin)) &&
                            SUCCEEDED(vdev->CreateVideoProcessorOutputView(backBuf.Get(), vpenum.Get(), &ovd, &pout))) {
                            LONG visW = std::max<LONG>(1, dispAperture.right - dispAperture.left);
                            LONG visH = std::max<LONG>(1, dispAperture.bottom - dispAperture.top);
                            float srcAsp = float(visW) / float(visH);
                            float dstAsp = float(sw) / float(std::max<UINT>(1, sh));
                            RECT dstRc{ 0, 0, LONG(sw), LONG(sh) };
                            if (srcAsp > dstAsp) {
                                LONG nh = LONG(float(sw) / srcAsp);
                                dstRc.top = (LONG(sh) - nh) / 2;
                                dstRc.bottom = dstRc.top + nh;
                            } else if (srcAsp < dstAsp) {
                                LONG nw = LONG(float(sh) * srcAsp);
                                dstRc.left = (LONG(sw) - nw) / 2;
                                dstRc.right = dstRc.left + nw;
                            }
                            D3D11_VIDEO_COLOR bg{};
                            bg.RGBA = { 0.0f, 0.0f, 0.0f, 1.0f };
                            vctx->VideoProcessorSetOutputBackgroundColor(vp.Get(), FALSE, &bg);
                            vctx->VideoProcessorSetStreamSourceRect(vp.Get(), 0, TRUE, &dispAperture);
                            vctx->VideoProcessorSetStreamDestRect(vp.Get(), 0, TRUE, &dstRc);
                            D3D11_VIDEO_PROCESSOR_STREAM pst{};
                            pst.Enable = TRUE;
                            pst.pInputSurface = pin.Get();
                            vctx->VideoProcessorBlt(vp.Get(), pout.Get(), 0, 1, &pst);
                            swap->Present(0, 0);
                            framesDecoded++;
                            framesWindow++;
                        }
                    }
                }
            }
            outBuf.pSample->Release();
        }
    };

    bool decNeedsKf = true;
    auto decodeAndPresent = [&](const VideoDepacketizer::FrameOut& fr, uint32_t mediaSsrc) {
        if (fr.annexb.empty() || !decMft) return;
        if (depack.needKeyframe()) decNeedsKf = true;
        const uint8_t* frameData = fr.annexb.data();
        size_t frameLen = fr.annexb.size();

        // DAVE E2EE Video Frame Decryption (dynamic buffer size handles >2MB 4K IDR keyframes cleanly!)
        {
            std::lock_guard lk(dave_mtx_);
            if (dave_dec_ && dave_proto_ver_ > 0) {
                size_t needCap = daveDecryptorGetMaxPlaintextByteSize(dave_dec_, DAVE_MEDIA_TYPE_VIDEO, frameLen);
                if (needCap > daveBuf.size()) daveBuf.resize(std::max(needCap, frameLen + 1024));
                size_t w = 0;
                if (daveDecryptorDecrypt(dave_dec_, DAVE_MEDIA_TYPE_VIDEO, frameData, frameLen,
                                         daveBuf.data(), daveBuf.size(), &w) == DAVE_DECRYPTOR_RESULT_CODE_SUCCESS && w > 0) {
                    frameData = daveBuf.data();
                    frameLen = w;
                    daveOk++;
                } else {
                    daveFail++;
                    int64_t nowMs = steadyMs();
                    if (dave_session_ && !streamer_uid_.empty() && nowMs - lastRatchetRetryMs > 400) {
                        lastRatchetRetryMs = nowMs;
                        DAVEKeyRatchetHandle kr = daveSessionGetKeyRatchet(dave_session_, streamer_uid_.c_str());
                        if (kr) {
                            daveDecryptorTransitionToPassthroughMode(dave_dec_, false);
                            daveDecryptorTransitionToKeyRatchet(dave_dec_, kr);
                            daveKeyRatchetDestroy(kr);
                            dave_has_ratchet_ = true;
                        }
                    }
                    decNeedsKf = true;
                    depack.setNeedKeyframe(true);
                    return;
                }
            }
        }

        // Cache VPS/SPS/PPS separately so NVDEC always has codec config even if IDR omits repeated parameter sets
        const bool isHevc = (video_codec_.load() == FC_CODEC_HEVC);
        cacheSpsPpsFromFrame(frameData, frameLen, isHevc);
        if (frameLen >= 5 && frameData[0] == 0 && frameData[1] == 0) {
            uint8_t hdrByte = (frameData[2] == 1 ? frameData[3] : frameData[4]);
            lastNaluType = isHevc ? uint8_t((hdrByte >> 1) & 0x3F) : uint8_t(hdrByte & 0x1F);
        }

        // Gate strictly on an actual IDR + valid SPS/PPS (and VPS for HEVC) so NVDEC never smears or hangs!
        if (decNeedsKf) {
            bool hasIdr = isHevc ? VideoDepacketizer::hasHevcIdr(frameData, frameLen) : VideoDepacketizer::hasIdr(frameData, frameLen);
            bool hasVps = !isHevc || frameHasNalu(frameData, frameLen, 32, true);
            bool hasSps = frameHasNalu(frameData, frameLen, isHevc ? 33 : 7, isHevc);
            bool hasPps = frameHasNalu(frameData, frameLen, isHevc ? 34 : 8, isHevc);
            bool configReady = (hasVps || !cachedVps.empty()) && (hasSps || !cachedSps.empty()) && (hasPps || !cachedPps.empty());
            if (!hasIdr || !configReady) {
                kfSkipped++;
                depack.setNeedKeyframe(true);
                int64_t nowMs = steadyMs();
                if (nowMs - lastPliMs > 450) {
                    sendEncryptedRtcpPli(mediaSsrc);
                    lastPliMs = nowMs;
                }
                return;
            }
            if ((!hasVps && !cachedVps.empty()) || (!hasSps && !cachedSps.empty()) || (!hasPps && !cachedPps.empty())) {
                prependBuf.clear();
                if (!hasVps) prependBuf.insert(prependBuf.end(), cachedVps.begin(), cachedVps.end());
                if (!hasSps) prependBuf.insert(prependBuf.end(), cachedSps.begin(), cachedSps.end());
                if (!hasPps) prependBuf.insert(prependBuf.end(), cachedPps.begin(), cachedPps.end());
                prependBuf.insert(prependBuf.end(), frameData, frameData + frameLen);
                frameData = prependBuf.data();
                frameLen = prependBuf.size();
            }
            decNeedsKf = false;
            depack.setNeedKeyframe(false);
        }

        auto tDec0 = std::chrono::steady_clock::now();
        mftFed++;
        ComPtr<IMFSample> inSamp;
        ComPtr<IMFMediaBuffer> mb;
        if (SUCCEEDED(MFCreateSample(&inSamp)) && SUCCEEDED(MFCreateMemoryBuffer(DWORD(frameLen), &mb))) {
            BYTE* dst = nullptr;
            if (SUCCEEDED(mb->Lock(&dst, nullptr, nullptr))) {
                std::memcpy(dst, frameData, frameLen);
                mb->Unlock();
                mb->SetCurrentLength(DWORD(frameLen));
                inSamp->AddBuffer(mb.Get());
                HRESULT phr = decMft->ProcessInput(0, inSamp.Get(), 0);
                if (phr == MF_E_NOTACCEPTING) {
                    drainDecoderOutputs();
                    phr = decMft->ProcessInput(0, inSamp.Get(), 0);
                }
                if (FAILED(phr)) {
                    decNeedsKf = true;
                    depack.setNeedKeyframe(true);
                }
            }
        }
        drainDecoderOutputs();

        float decMs = std::chrono::duration<float, std::milli>(std::chrono::steady_clock::now() - tDec0).count();
        auto now = std::chrono::steady_clock::now();
        float sec = std::chrono::duration<float>(now - windowStart).count();
        if (sec >= 0.5f) {
            {
                std::lock_guard lk(stats_mtx_);
                LONG visW = dispAperture.right - dispAperture.left;
                LONG visH = dispAperture.bottom - dispAperture.top;
                stats_.width = int32_t(visW > 0 ? visW : LONG(curDecW));
                stats_.height = int32_t(visH > 0 ? visH : LONG(curDecH));
                stats_.fps = float(framesWindow) / sec;
                stats_.bitrate_kbps = (float(bytesWindow) * 8.0f / 1000.0f) / sec;
                stats_.decode_ms = decMs;
                stats_.frames_encoded = framesDecoded;
                stats_.frames_skipped_static = depack.framesDropped();
            }
            updateDiagText();
            framesWindow = 0;
            bytesWindow = 0;
            windowStart = now;
        }
    };

    int64_t lastDiagMs = steadyMs();

    while (running_.load()) {
        int32_t wantCodec = video_codec_.load();
        if (wantCodec != activeDecCodec) {
            initDecoderForCodec(wantCodec);
            depack.setHevc(wantCodec == FC_CODEC_HEVC);
            decNeedsKf = true;
            cachedVps.clear();
            cachedSps.clear();
            cachedPps.clear();
        }
        if (reset_depack_.exchange(false)) {
            depack.setHevc(wantCodec == FC_CODEC_HEVC);
            depack.reset();
            decNeedsKf = true;
            cachedVps.clear();
            cachedSps.clear();
            cachedPps.clear();
            if (decMft) decMft->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
        }

        int32_t rw = pending_w_.exchange(0);
        int32_t rh = pending_h_.exchange(0);
        if (swap && rw > 16 && rh > 16) {
            sw = UINT(rw); sh = UINT(rh);
            swap->ResizeBuffers(2, sw, sh, DXGI_FORMAT_B8G8R8A8_UNORM, 0);
            vp.Reset(); vpenum.Reset();
            clearSwapBlack();
        }

        int64_t nowMs = steadyMs();
        if (nowMs - lastDiagMs >= 500) {
            lastDiagMs = nowMs;
            auto nowWin = std::chrono::steady_clock::now();
            float idleSec = std::chrono::duration<float>(nowWin - windowStart).count();
            if (idleSec >= 1.0f) {
                std::lock_guard lk(stats_mtx_);
                stats_.fps = float(framesWindow) / idleSec;
                stats_.bitrate_kbps = (float(bytesWindow) * 8.0f / 1000.0f) / idleSec;
                framesWindow = 0;
                bytesWindow = 0;
                windowStart = nowWin;
            }
            updateDiagText();
        }
        if (has_key_.load()) {
            // Periodic 12s UDP NAT keepalive so strict NATs never time out watched streams!
            if (nowMs - lastKeepaliveMs >= 12000) {
                uint8_t ka[8] = { 0x13, 0x37, 0xCA, 0xFE, 0, 0, 0, 1 };
                sendto(sock_, reinterpret_cast<const char*>(ka), sizeof(ka), 0,
                       reinterpret_cast<sockaddr*>(&remote_), sizeof(remote_));
                lastKeepaliveMs = nowMs;
            }
            if (has_p2p_peer_.load() && nowMs - lastPliMs > 500) {
                bool locked = last_p2p_rx_ms_.load() > 0 && (nowMs - last_p2p_rx_ms_.load() < 8000);
                static thread_local int64_t lastWatchProbe = 0;
                if (nowMs - lastWatchProbe >= (locked ? 3500 : 450)) {
                    lastWatchProbe = nowMs;
                    uint8_t localKey[32];
                    { std::lock_guard klk(key_mtx_); std::memcpy(localKey, key_, 32); }
                    sockaddr_in p2pDst{};
                    { std::lock_guard plk(p2p_mtx_); p2pDst = p2p_peer_; }
                    uint8_t probe[16];
                    p2p::buildProbe(probe, 0x55, uint32_t(nowMs), localKey);
                    sendto(sock_, reinterpret_cast<const char*>(probe), sizeof(probe), 0,
                           reinterpret_cast<sockaddr*>(&p2pDst), sizeof(p2pDst));
                }
            }
            uint32_t vs = video_ssrc_.load();
            if (vs != 0 && (depack.needKeyframe() || decNeedsKf) && (nowMs - lastPliMs > 600)) {
                sendEncryptedRtcpPli(vs);
                lastPliMs = nowMs;
            }
        }

        sockaddr_in from{};
        int fromLen = sizeof(from);
        int n = recvfrom(sock_, reinterpret_cast<char*>(pkt), sizeof(pkt), 0,
                         reinterpret_cast<sockaddr*>(&from), &fromLen);
        if (n == 16 && pkt[0] == 0xFC && has_key_.load()) {
            uint8_t localKey[32];
            { std::lock_guard klk(key_mtx_); std::memcpy(localKey, key_, 32); }
            uint8_t pCmd = 0; uint32_t pTs = 0;
            if (p2p::verifyProbe(pkt, n, localKey, pCmd, pTs)) {
                last_p2p_rx_ms_.store(steadyMs());
                { std::lock_guard plk(p2p_mtx_); p2p_peer_ = from; has_p2p_peer_.store(true); }
                if (pCmd == 0x55) {
                    uint8_t pong[16];
                    p2p::buildProbe(pong, 0x56, pTs, localKey);
                    sendto(sock_, reinterpret_cast<const char*>(pong), sizeof(pong), 0,
                           reinterpret_cast<sockaddr*>(&from), sizeof(from));
                }
            }
            continue;
        }
        bool isRelaySrc = (from.sin_addr.s_addr == rem.sin_addr.s_addr && from.sin_port == rem.sin_port);
        if (n < 28 || !has_key_.load() || (!isRelaySrc && !has_p2p_peer_.load())) {
            bool anyPli = false;
            bool stepPli = false;
            while (depack.tryEmit(steadyMs(), readyFrame, stepPli)) {
                anyPli |= stepPli;
                decodeAndPresent(readyFrame, video_ssrc_.load());
            }
            anyPli |= stepPli;
            if (anyPli && (steadyMs() - lastPliMs > 400)) {
                sendEncryptedRtcpPli(video_ssrc_.load());
                lastPliMs = steadyMs();
            }
            continue;
        }

        uint8_t pt = pkt[1] & 0x7F;
        if (pt >= 72 && pt <= 76) continue; // RTCP
        bool marker = (pkt[1] & 0x80) != 0;
        bool hasPad = (pkt[0] & 0x20) != 0;
        uint8_t cc = pkt[0] & 0x0F;
        bool ext = (pkt[0] & 0x10) != 0;
        uint16_t seq = getBE16(pkt + 2);
        uint32_t rtpTs = getBE32(pkt + 4);
        uint32_t ssrc = getBE32(pkt + 8);
        uint8_t nonce[24]{};
        std::memcpy(nonce, pkt + n - 4, 4);

        uint8_t localKey[32];
        bool useXchacha = true;
        {
            std::lock_guard klk(key_mtx_);
            std::memcpy(localKey, key_, 32);
            useXchacha = use_xchacha_;
        }

        unsigned long long plen = 0;
        bool ok = false;
        int payOff = 0;

        if (useXchacha) {
            int hdrLen = 12 + cc * 4;
            uint16_t extWords = 0;
            if (ext && n > hdrLen + 4) {
                extWords = getBE16(pkt + hdrLen + 2);
                hdrLen += 4;
            }
            if (n < hdrLen + 20) continue; // allow exact 0-byte BWE probe packets (n == hdrLen + 20)!
            int cipherLen = n - hdrLen - 4;
            ok = crypto_aead_xchacha20poly1305_ietf_decrypt(
                     plain, &plen, nullptr,
                     pkt + hdrLen, size_t(cipherLen),
                     pkt, size_t(hdrLen),
                     nonce, localKey) == 0;
            payOff = ext ? int(extWords) * 4 : 0;
        } else {
            int hdrLen = 12;
            if (n < hdrLen + int(crypto_secretbox_MACBYTES) + 4) continue;
            int cipherLen = n - hdrLen - 4;
            ok = crypto_secretbox_open_easy(plain, pkt + hdrLen, size_t(cipherLen), nonce, localKey) == 0;
            plen = size_t(cipherLen - crypto_secretbox_MACBYTES);
            if (ok) {
                payOff = cc * 4;
                if (ext && payOff + 4 <= int(plen)) {
                    uint16_t extWords = getBE16(plain + payOff + 2);
                    payOff += 4 + int(extWords) * 4;
                }
            }
        }
        if (!ok) continue;
        if (!isRelaySrc) {
            last_p2p_rx_ms_.store(steadyMs());
            std::lock_guard plk(p2p_mtx_);
            p2p_peer_ = from;
        }
        rxUdpPkts++;

        // Strip RFC 3550 RTP Padding bytes if P bit (0x20) is set!
        if (hasPad && plen > 0) {
            int padLen = int(plain[plen - 1]);
            if (padLen > 0 && padLen <= int(plen)) plen -= size_t(padLen);
        }

        if (payOff >= int(plen)) {
            uint32_t vsCheck = video_ssrc_.load();
            if ((pt == 101 || pt == 103) && (vsCheck == 0 || ssrc == vsCheck)) depack.noteEmptyProbeSeq(seq);
            continue;
        }
        const uint8_t* pay = plain + payOff;
        int payLen = int(plen) - payOff;
        bytesWindow += payLen;

        // Stream audio (PT 120) -> DAVE decrypt + feed into slot-isolated WASAPI mixer key (zero cross-slot collision!)
        if (pt == 120 && voice_) {
            uint32_t expectedAs = audio_ssrc_.load();
            if (expectedAs == 0) {
                audio_ssrc_.store(ssrc);
                expectedAs = ssrc;
                voice_->setUserVolume(makeVoiceAudioKey(ssrc), stream_vol_.load());
            } else if (ssrc != expectedAs) {
                continue;
            }
            const uint8_t* opPtr = pay;
            int opLen = payLen;
            if (!isSilenceFrame(pay, payLen)) {
                std::lock_guard lk(dave_mtx_);
                if (dave_dec_ && dave_proto_ver_ > 0) {
                    size_t w = 0;
                    if (daveDecryptorDecrypt(dave_dec_, DAVE_MEDIA_TYPE_AUDIO, pay, size_t(payLen),
                                             daveBuf.data(), daveBuf.size(), &w) == DAVE_DECRYPTOR_RESULT_CODE_SUCCESS && w > 0) {
                        opPtr = daveBuf.data();
                        opLen = int(w);
                    } else {
                        // NEVER pass failed DAVE ciphertext into Opus decoder!
                        int64_t tNow = steadyMs();
                        if (dave_session_ && !streamer_uid_.empty() && tNow - lastRatchetRetryMs > 400) {
                            lastRatchetRetryMs = tNow;
                            DAVEKeyRatchetHandle kr = daveSessionGetKeyRatchet(dave_session_, streamer_uid_.c_str());
                            if (kr) {
                                daveDecryptorTransitionToPassthroughMode(dave_dec_, false);
                                daveDecryptorTransitionToKeyRatchet(dave_dec_, kr);
                                daveKeyRatchetDestroy(kr);
                            }
                        }
                        continue;
                    }
                }
            }
            voice_->onRemoteOpus(makeVoiceAudioKey(ssrc), seq, opPtr, opLen);
            continue;
        }

        // Stream video H.264 / HEVC (accept negotiated video_pt_ or 101/103; skip even RTX PTs 102/104/106)
        int32_t negPt = video_pt_.load();
        bool isVideoPt = (pt == uint8_t(negPt)) || (pt == 101) || (pt == 103);
        if (!isVideoPt) continue;
        if (pt == 103 && negPt == 103 && video_codec_.load() != FC_CODEC_HEVC) {
            video_codec_.store(FC_CODEC_HEVC);
        }
        uint32_t targetVssrc = video_ssrc_.load();
        if (targetVssrc == 0) {
            video_ssrc_.store(ssrc);
            targetVssrc = ssrc;
        } else if (ssrc != targetVssrc) {
            continue;
        }
        rxVideoPkts++;

        bool anyPli = false;
        bool stepPli = false;
        if (depack.pushPacket(seq, rtpTs, marker, pay, payLen, steadyMs(), readyFrame, stepPli)) {
            anyPli |= stepPli;
            decodeAndPresent(readyFrame, ssrc);
            while (depack.tryEmit(steadyMs(), readyFrame, stepPli)) {
                anyPli |= stepPli;
                decodeAndPresent(readyFrame, ssrc);
            }
        }
        anyPli |= stepPli;
        if (anyPli && (steadyMs() - lastPliMs > 400)) {
            sendEncryptedRtcpPli(ssrc);
            lastPliMs = steadyMs();
        }
    }

    if (decMft) {
        decMft->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        decMft->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
        ComPtr<IMFShutdown> shut;
        if (SUCCEEDED(decMft.As(&shut))) shut->Shutdown();
    }
    decMft.Reset();
    dxgiMgr.Reset();
    swap.Reset();
    vp.Reset();
    vpenum.Reset();
    } // end inner COM scope

    MFShutdown();
    CoUninitialize();
    revertRealtimeThread(mm);
}

} // namespace fc
