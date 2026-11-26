#include "ScreenEngine.h"
#include "NetTurbo.h"
#include <initguid.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <ksmedia.h>
#include <mmdeviceapi.h>
#include <opus.h>
#include <strmif.h>
#include <codecapi.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <dxgi1_5.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mftransform.h>
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <chrono>
#include <cstring>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace fc {

namespace {
std::string wideToUtf8(const wchar_t* w) {
    if (!w || !*w) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
    if (n <= 1) return {};
    std::string s(size_t(n - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w, -1, s.data(), n, nullptr, nullptr);
    return s;
}

std::wstring utf8ToWide(const char* s) {
    if (!s || !*s) return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    if (n <= 1) return {};
    std::wstring w(size_t(n - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s, -1, w.data(), n);
    return w;
}

bool isAudioDeviceLostHr(HRESULT hr) {
    return hr == AUDCLNT_E_DEVICE_INVALIDATED ||
           hr == AUDCLNT_E_SERVICE_NOT_RUNNING ||
           hr == AUDCLNT_E_RESOURCES_INVALIDATED;
}

// Compute aspect-preserving even output dimensions fitting within (maxW, maxH)
void fitAspectEven(UINT srcW, UINT srcH, int32_t maxW, int32_t maxH, UINT& outW, UINT& outH) {
    double scale = std::min(1.0, std::min(double(maxW) / double(std::max<UINT>(1, srcW)),
                                          double(maxH) / double(std::max<UINT>(1, srcH))));
    outW = std::max<UINT>(64, UINT(double(srcW) * scale) & ~1u);
    outH = std::max<UINT>(64, UINT(double(srcH) * scale) & ~1u);
}

// Lightweight COM completion handler for Windows 10/11 Process Loopback (captures game/system audio WITHOUT FluxCord voice echo!)
// Owns AUDIOCLIENT_ACTIVATION_PARAMS & PROPVARIANT on the heap so OS worker thread never reads dead stack memory on timeout!
class ProcessLoopbackHandler final : public IActivateAudioInterfaceCompletionHandler, public IAgileObject {
public:
    HANDLE done_ev = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HRESULT result_hr = E_FAIL;
    ComPtr<IAudioClient> client;
    AUDIOCLIENT_ACTIVATION_PARAMS params{};
    PROPVARIANT pv{};

    ProcessLoopbackHandler(DWORD pid, bool includeTree) {
        params.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
        params.ProcessLoopbackParams.TargetProcessId = pid;
        params.ProcessLoopbackParams.ProcessLoopbackMode = includeTree
            ? PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
            : PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE;
        pv.vt = VT_BLOB;
        pv.blob.cbSize = sizeof(params);
        pv.blob.pBlobData = reinterpret_cast<BYTE*>(&params);
    }

    ~ProcessLoopbackHandler() { if (done_ev) CloseHandle(done_ev); }

    ULONG STDMETHODCALLTYPE AddRef() override { return ++ref_; }
    ULONG STDMETHODCALLTYPE Release() override {
        ULONG r = --ref_;
        if (r == 0) delete this;
        return r;
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IActivateAudioInterfaceCompletionHandler)) {
            *ppv = static_cast<IActivateAudioInterfaceCompletionHandler*>(this);
            AddRef();
            return S_OK;
        }
        if (riid == __uuidof(IAgileObject)) {
            *ppv = static_cast<IAgileObject*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    HRESULT STDMETHODCALLTYPE ActivateCompleted(IActivateAudioInterfaceAsyncOperation* op) override {
        HRESULT hrAct = E_FAIL;
        ComPtr<IUnknown> unk;
        if (op && SUCCEEDED(op->GetActivateResult(&hrAct, &unk)) && SUCCEEDED(hrAct) && unk) {
            result_hr = unk.As(&client);
        } else {
            result_hr = hrAct;
        }
        SetEvent(done_ev);
        return S_OK;
    }
private:
    std::atomic<ULONG> ref_{1};
};

bool tryOpenProcessLoopback(DWORD pid, bool includeTree, ComPtr<IAudioClient>& outClient) {
    if (pid == 0) return false;
    auto* handler = new ProcessLoopbackHandler(pid, includeTree);
    ComPtr<IActivateAudioInterfaceAsyncOperation> op;
    HRESULT hr = ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, __uuidof(IAudioClient), &handler->pv, handler, &op);
    bool ok = false;
    if (SUCCEEDED(hr) && WaitForSingleObject(handler->done_ev, 1500) == WAIT_OBJECT_0 && SUCCEEDED(handler->result_hr) && handler->client) {
        outClient = handler->client;
        ok = true;
    }
    handler->Release();
    return ok;
}

bool createHardwareEncoderMft(int32_t /*codec*/, int32_t w, int32_t h, int32_t fps, int32_t bitrateKbps,
                              IMFDXGIDeviceManager* dxgiMgr, ComPtr<IMFTransform>& outMft, std::string& outName) {
    // Always encode H.264 to match Discord OP 1 + RFC 6184 RTP packetizer + DAVE_CODEC_H264!
    GUID subtype = MFVideoFormat_H264;
    MFT_REGISTER_TYPE_INFO outType{MFMediaType_Video, subtype};
    IMFActivate** activates = nullptr;
    UINT32 count = 0;
    HRESULT hr = MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER,
                           MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
                           nullptr, &outType, &activates, &count);
    if (FAILED(hr) || count == 0) {
        if (activates) CoTaskMemFree(activates);
        return false;
    }

    bool ok = false;
    for (UINT32 i = 0; i < count; ++i) {
        std::string candName;
        LPWSTR friendly = nullptr;
        UINT32 flen = 0;
        if (SUCCEEDED(activates[i]->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, &friendly, &flen)) && friendly) {
            candName = wideToUtf8(friendly);
            CoTaskMemFree(friendly);
        }

        ComPtr<IMFTransform> mft;
        if (SUCCEEDED(activates[i]->ActivateObject(IID_PPV_ARGS(&mft)))) {
            bool matched = false;
            ComPtr<IMFAttributes> attrs;
            if (SUCCEEDED(mft->GetAttributes(&attrs))) {
                UINT32 isAsync = 0;
                if (SUCCEEDED(attrs->GetUINT32(MF_TRANSFORM_ASYNC, &isAsync)) && isAsync) {
                    attrs->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, TRUE);
                }
            }

            // Require D3D manager bind to succeed so multi-GPU (iGPU+dGPU) systems select the exact matching encoder!
            if (dxgiMgr && SUCCEEDED(mft->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, reinterpret_cast<ULONG_PTR>(dxgiMgr)))) {
                ComPtr<ICodecAPI> capi;
                if (SUCCEEDED(mft.As(&capi))) {
                    VARIANT v{};
                    v.vt = VT_BOOL; v.boolVal = VARIANT_TRUE;
                    capi->SetValue(&CODECAPI_AVLowLatencyMode, &v);

                    v.vt = VT_UI4; v.ulVal = eAVEncCommonRateControlMode_CBR;
                    capi->SetValue(&CODECAPI_AVEncCommonRateControlMode, &v);

                    v.vt = VT_UI4; v.ulVal = UINT32(bitrateKbps) * 1000u;
                    capi->SetValue(&CODECAPI_AVEncCommonMeanBitRate, &v);

                    v.vt = VT_UI4; v.ulVal = UINT32(bitrateKbps) * 1250u;
                    capi->SetValue(&CODECAPI_AVEncCommonMaxBitRate, &v);

                    v.vt = VT_UI4; v.ulVal = 15;
                    capi->SetValue(&CODECAPI_AVEncCommonQualityVsSpeed, &v);

                    v.vt = VT_UI4; v.ulVal = 0;
                    capi->SetValue(&CODECAPI_AVEncMPVDefaultBPictureCount, &v);

                    v.vt = VT_UI4; v.ulVal = UINT32(fps * 2);
                    capi->SetValue(&CODECAPI_AVEncMPVGOPSize, &v);
                }

                ComPtr<IMFMediaType> mtOut;
                if (SUCCEEDED(MFCreateMediaType(&mtOut)) && mtOut) {
                    mtOut->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                    mtOut->SetGUID(MF_MT_SUBTYPE, subtype);
                    mtOut->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Main);
                    mtOut->SetUINT32(MF_MT_AVG_BITRATE, UINT32(bitrateKbps) * 1000u);
                    mtOut->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
                    MFSetAttributeSize(mtOut.Get(), MF_MT_FRAME_SIZE, UINT32(w), UINT32(h));
                    MFSetAttributeRatio(mtOut.Get(), MF_MT_FRAME_RATE, UINT32(fps), 1);
                    MFSetAttributeRatio(mtOut.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

                    HRESULT outHr = mft->SetOutputType(0, mtOut.Get(), 0);
                    if (FAILED(outHr)) {
                        mtOut->DeleteItem(MF_MT_MPEG2_PROFILE);
                        outHr = mft->SetOutputType(0, mtOut.Get(), 0);
                    }
                    if (SUCCEEDED(outHr)) {
                        ComPtr<IMFMediaType> mtIn;
                        if (SUCCEEDED(MFCreateMediaType(&mtIn)) && mtIn) {
                            mtIn->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                            mtIn->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
                            mtIn->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
                            MFSetAttributeSize(mtIn.Get(), MF_MT_FRAME_SIZE, UINT32(w), UINT32(h));
                            MFSetAttributeRatio(mtIn.Get(), MF_MT_FRAME_RATE, UINT32(fps), 1);
                            MFSetAttributeRatio(mtIn.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

                            if (SUCCEEDED(mft->SetInputType(0, mtIn.Get(), 0))) {
                                mft->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
                                mft->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
                                outMft = mft;
                                outName = candName.empty() ? "Hardware H.264 NVENC" : candName;
                                ok = true;
                                matched = true;
                            }
                        }
                    }
                }
            }
            if (!matched) {
                ComPtr<IMFShutdown> shut;
                if (SUCCEEDED(mft.As(&shut))) shut->Shutdown();
                activates[i]->ShutdownObject();
            }
            if (matched) break;
        }
    }

    for (UINT32 i = 0; i < count; ++i) activates[i]->Release();
    CoTaskMemFree(activates);
    return ok;
}
} // namespace

ScreenEngine::ScreenEngine(VoiceTransport* transport) : transport_(transport) {
    worker_done_ev_ = CreateEventW(nullptr, TRUE, TRUE, nullptr);
    if (transport_) {
        transport_->setPliCallback([this] { requestKeyframe(); });
    }
}

ScreenEngine::~ScreenEngine() {
    if (transport_) transport_->setPliCallback(nullptr);
    stop();
    if (worker_done_ev_) { CloseHandle(worker_done_ev_); worker_done_ev_ = nullptr; }
}

int32_t ScreenEngine::listMonitors(FcMonitorInfo* out_list, int32_t max_count) {
    if (!out_list || max_count <= 0) return 0;
    ComPtr<IDXGIFactory1> fac;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&fac)))) return 0;

    int32_t count = 0;
    ComPtr<IDXGIAdapter1> ad;
    for (UINT a = 0; fac->EnumAdapters1(a, &ad) != DXGI_ERROR_NOT_FOUND && count < max_count; ++a) {
        ComPtr<IDXGIOutput> out;
        for (UINT o = 0; ad->EnumOutputs(o, &out) != DXGI_ERROR_NOT_FOUND && count < max_count; ++o) {
            DXGI_OUTPUT_DESC od{};
            out->GetDesc(&od);
            if (!od.AttachedToDesktop) continue;
            std::memset(&out_list[count], 0, sizeof(FcMonitorInfo));
            out_list[count].index = count;
            out_list[count].width = od.DesktopCoordinates.right - od.DesktopCoordinates.left;
            out_list[count].height = od.DesktopCoordinates.bottom - od.DesktopCoordinates.top;
            out_list[count].refresh_hz = 60;
            DEVMODEW dm{};
            dm.dmSize = sizeof(dm);
            if (EnumDisplaySettingsW(od.DeviceName, ENUM_CURRENT_SETTINGS, &dm) && dm.dmDisplayFrequency > 0) {
                out_list[count].refresh_hz = int32_t(dm.dmDisplayFrequency);
            }
            std::string name = wideToUtf8(od.DeviceName);
            std::strncpy(out_list[count].name, name.c_str(), sizeof(out_list[count].name) - 1);
            count++;
        }
    }
    return count;
}

bool ScreenEngine::start(int32_t monitor_index, int32_t target_w, int32_t target_h, int32_t fps,
                         int32_t bitrate_kbps, int32_t codec, bool loopback_preview, HWND preview_hwnd,
                         HWND target_hwnd, bool capture_system_audio, const char* render_device_id) {
    std::lock_guard life(lifecycle_mtx_);
    stopLocked();
    monitor_idx_ = monitor_index;
    target_w_ = std::clamp(target_w, 640, 3840) & ~7;
    target_h_ = std::clamp(target_h, 360, 2160) & ~7;
    target_fps_ = std::clamp(fps, 15, 120);
    bitrate_kbps_ = std::clamp(bitrate_kbps, 1000, 50000);
    codec_ = FC_CODEC_H264; // Discord WebRTC Go Live requires H.264 RTP packetizer & DAVE_CODEC_H264
    loopback_preview_ = loopback_preview;
    capture_audio_ = capture_system_audio;
    render_device_id_ = render_device_id ? render_device_id : "";
    preview_hwnd_ = preview_hwnd;
    target_hwnd_ = (target_hwnd && IsWindow(target_hwnd)) ? target_hwnd : nullptr;

    {
        std::lock_guard lk(stats_mtx_);
        std::memset(&stats_, 0, sizeof(stats_));
        stats_.width = target_w_;
        stats_.height = target_h_;
        stats_.running = 1;
    }

    if (worker_done_ev_) ResetEvent(worker_done_ev_);
    running_.store(true);
    worker_ = std::thread([this] { captureEncodeLoop(); });
    if (capture_audio_ && transport_) {
        audio_worker_ = std::thread([this] { systemAudioLoop(); });
    }
    return true;
}

void ScreenEngine::stop() {
    std::lock_guard life(lifecycle_mtx_);
    stopLocked();
}

void ScreenEngine::stopLocked() {
    running_.store(false);
    if (worker_.joinable()) {
        // Prevent cross-thread DXGI SwapChain HWND SendMessage deadlock when stop() is called from UI thread!
        if (preview_hwnd_ && worker_done_ev_ && GetWindowThreadProcessId(preview_hwnd_, nullptr) == GetCurrentThreadId()) {
            while (WaitForSingleObject(worker_done_ev_, 0) != WAIT_OBJECT_0) {
                DWORD wr = MsgWaitForMultipleObjectsEx(1, &worker_done_ev_, 50, QS_SENDMESSAGE, 0);
                if (wr == WAIT_OBJECT_0) break;
                MSG msg{};
                PeekMessageW(&msg, nullptr, 0, 0, PM_NOREMOVE | PM_QS_SENDMESSAGE);
            }
        }
        worker_.join();
    }
    if (audio_worker_.joinable()) audio_worker_.join();
    std::lock_guard lk(stats_mtx_);
    stats_.running = 0;
}

void ScreenEngine::resizePreview(int32_t width, int32_t height) {
    if (width > 16 && height > 16) {
        pending_w_.store(width);
        pending_h_.store(height);
    }
}

void ScreenEngine::getStats(FcScreenStats* out) const {
    if (!out) return;
    std::lock_guard lk(stats_mtx_);
    *out = stats_;
}

void ScreenEngine::captureEncodeLoop() {
    struct DoneGuard {
        HANDLE ev;
        ~DoneGuard() { if (ev) SetEvent(ev); }
    } doneGuard{ worker_done_ev_ };

    HANDLE mm = boostRealtimeThread(L"Games");
    SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    HDESK origDesk = GetThreadDesktop(GetCurrentThreadId());
    HDESK inputDesk = OpenInputDesktop(0, FALSE, GENERIC_ALL);
    if (!inputDesk) inputDesk = OpenInputDesktop(0, FALSE, DESKTOP_READOBJECTS | DESKTOP_ENUMERATE);
    if (inputDesk) SetThreadDesktop(inputDesk);

    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    MFStartup(MF_VERSION);

    { // Inner COM scope so ALL DXGI/D3D11/MF objects release BEFORE MFShutdown & CoUninitialize!
    auto setErr = [&](const char* msg) {
        std::lock_guard lk(stats_mtx_);
        std::strncpy(stats_.encoder_name, msg, sizeof(stats_.encoder_name) - 1);
    };

    ComPtr<IDXGIFactory2> fac;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&fac)))) {
        setErr("ERR: CreateDXGIFactory1");
        goto cleanup;
    }

    const bool dualMode = (monitor_idx_ == -2) && (target_hwnd_ == nullptr);
    ComPtr<IDXGIAdapter1> chosenAdapter;
    ComPtr<IDXGIOutput1> chosenOutput;
    ComPtr<IDXGIOutput1> chosenOutput2;
    int32_t idx = 0;
    ComPtr<IDXGIAdapter1> ad;
    for (UINT a = 0; fac->EnumAdapters1(a, &ad) != DXGI_ERROR_NOT_FOUND; ++a) {
        ComPtr<IDXGIOutput> out;
        for (UINT o = 0; ad->EnumOutputs(o, &out) != DXGI_ERROR_NOT_FOUND; ++o) {
            DXGI_OUTPUT_DESC od{};
            out->GetDesc(&od);
            if (!od.AttachedToDesktop) continue;
            if (dualMode) {
                if (!chosenOutput) {
                    chosenAdapter = ad;
                    out.As(&chosenOutput);
                } else if (!chosenOutput2 && ad.Get() == chosenAdapter.Get()) {
                    out.As(&chosenOutput2);
                }
            } else if (idx == monitor_idx_) {
                chosenAdapter = ad;
                out.As(&chosenOutput);
                break;
            }
            idx++;
        }
        if (!dualMode && chosenOutput) break;
        if (dualMode && chosenOutput && chosenOutput2) break;
    }
    const bool isDualActive = dualMode && chosenOutput && chosenOutput2;
    if (!chosenOutput) {
        setErr("ERR: NoMonitor");
        goto cleanup;
    }

    ComPtr<ID3D11Device> dev;
    ComPtr<ID3D11DeviceContext> ctx;
    D3D_FEATURE_LEVEL fl{};
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
    if (FAILED(D3D11CreateDevice(chosenAdapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, flags,
                                 nullptr, 0, D3D11_SDK_VERSION, &dev, &fl, &ctx))) {
        setErr("ERR: D3D11CreateDevice");
        goto cleanup;
    }

    ComPtr<ID3D11Multithread> mt;
    if (SUCCEEDED(dev.As(&mt))) mt->SetMultithreadProtected(TRUE);

    ComPtr<IDXGIOutputDuplication> dup;
    ComPtr<IDXGIOutputDuplication> dup2;
    ComPtr<ID3D11Texture2D> dualCache0;
    ComPtr<ID3D11Texture2D> dualCache1;
    UINT srcW = UINT(target_w_), srcH = UINT(target_h_);
    UINT srcW2 = 1920, srcH2 = 1080;
    const bool windowMode = (target_hwnd_ != nullptr && IsWindow(target_hwnd_));

    HDC winSrcDc = nullptr, winMemDc = nullptr;
    HBITMAP winDib = nullptr;
    HGDIOBJ winOldBmp = nullptr;
    void* winBits = nullptr;
    ComPtr<ID3D11Texture2D> winUploadTex;
    UINT winTexW = 0, winTexH = 0;

    auto openDupOnOutput = [&](ComPtr<IDXGIOutput1>& output, ComPtr<IDXGIOutputDuplication>& outDup, UINT& ow, UINT& oh) -> bool {
        outDup.Reset();
        if (!output) return false;
        HRESULT dupHr = E_FAIL;
        ComPtr<IDXGIOutput5> out5;
        if (SUCCEEDED(output.As(&out5))) {
            const DXGI_FORMAT fmts[] = { DXGI_FORMAT_B8G8R8A8_UNORM };
            dupHr = out5->DuplicateOutput1(dev.Get(), 0, 1, fmts, &outDup);
        }
        if (FAILED(dupHr)) dupHr = output->DuplicateOutput(dev.Get(), &outDup);
        if (FAILED(dupHr) || !outDup) return false;
        DXGI_OUTDUPL_DESC dd{};
        outDup->GetDesc(&dd);
        ow = std::max<UINT>(64, dd.ModeDesc.Width);
        oh = std::max<UINT>(64, dd.ModeDesc.Height);
        return true;
    };

    auto initDesktopDup = [&]() -> bool {
        bool ok1 = openDupOnOutput(chosenOutput, dup, srcW, srcH);
        if (isDualActive) {
            openDupOnOutput(chosenOutput2, dup2, srcW2, srcH2);
            dualCache0.Reset();
            dualCache1.Reset();
        }
        return ok1;
    };

    auto allocWindowBuffers = [&](UINT w, UINT h) {
        if (winMemDc && winOldBmp) { SelectObject(winMemDc, winOldBmp); winOldBmp = nullptr; }
        if (winDib) { DeleteObject(winDib); winDib = nullptr; winBits = nullptr; }
        winUploadTex.Reset();
        if (!winSrcDc) winSrcDc = GetDC(nullptr);
        if (!winMemDc) winMemDc = CreateCompatibleDC(winSrcDc);
        BITMAPINFO bi{};
        bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bi.bmiHeader.biWidth = LONG(w);
        bi.bmiHeader.biHeight = -LONG(h);
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        bi.bmiHeader.biCompression = BI_RGB;
        winDib = CreateDIBSection(winMemDc, &bi, DIB_RGB_COLORS, &winBits, nullptr, 0);
        if (winDib) winOldBmp = SelectObject(winMemDc, winDib);
        D3D11_TEXTURE2D_DESC wd{};
        wd.Width = w; wd.Height = h;
        wd.MipLevels = 1; wd.ArraySize = 1;
        wd.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        wd.SampleDesc.Count = 1;
        wd.Usage = D3D11_USAGE_DEFAULT;
        wd.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        dev->CreateTexture2D(&wd, nullptr, &winUploadTex);
        winTexW = w; winTexH = h;
    };

    if (!windowMode) {
        if (!initDesktopDup()) {
            setErr("ERR: DuplicateOutput");
            goto cleanup;
        }
    } else {
        RECT wrc{};
        GetWindowRect(target_hwnd_, &wrc);
        srcW = UINT(std::max<LONG>(64, wrc.right - wrc.left)) & ~1u;
        srcH = UINT(std::max<LONG>(64, wrc.bottom - wrc.top)) & ~1u;
        allocWindowBuffers(srcW, srcH);
    }

    UINT dstW = 1920, dstH = 1080;
    if (isDualActive) {
        // 32:9 Dual-Monitor Side-by-Side (e.g. 3840x1080 = two native 1080p screens side-by-side, zero wasted black bars!)
        dstW = (target_w_ >= 2560) ? 3840u : 2560u;
        dstH = (dstW == 3840u) ? 1080u : 720u;
    } else {
        fitAspectEven(srcW, srcH, target_w_, target_h_, dstW, dstH);
    }

    // D3D11 Video Processor for ZERO-COPY BGRA -> NV12 BT.709 scaling in VRAM
    ComPtr<ID3D11VideoDevice> vdev;
    ComPtr<ID3D11VideoContext> vctx;
    ComPtr<ID3D11VideoContext1> vctx1;
    ComPtr<ID3D11VideoProcessorEnumerator> vpenum;
    ComPtr<ID3D11VideoProcessor> vp;
    bool hasVp = false;
    bool canDualSingleBlt = false;

    // 4-surface NV12 encoder ring + 1 dedicated preview scratch slot (prevents GPU write-after-read race when !canSubmit!)
    constexpr int kEncRing = 4;
    constexpr int kPreviewScratchIdx = 4;
    constexpr int kNv12Total = 5;
    std::array<ComPtr<ID3D11Texture2D>, kNv12Total> nv12Ring;
    std::array<ComPtr<ID3D11VideoProcessorOutputView>, kNv12Total> vpOutRing;
    int nv12CurIdx = 0;
    int nv12LastIdx = 0;

    auto fitLetterboxRect = [](UINT inW, UINT inH, RECT box) -> RECT {
        LONG bw = std::max<LONG>(1, box.right - box.left);
        LONG bh = std::max<LONG>(1, box.bottom - box.top);
        double sa = double(std::max<UINT>(1, inW)) / double(std::max<UINT>(1, inH));
        double da = double(bw) / double(bh);
        RECT r = box;
        if (sa > da + 0.01) {
            LONG nh = std::max<LONG>(2, LONG(double(bw) / sa) & ~1L);
            r.top = box.top + (bh - nh) / 2;
            r.bottom = r.top + nh;
        } else if (sa < da - 0.01) {
            LONG nw = std::max<LONG>(2, LONG(double(bh) * sa) & ~1L);
            r.left = box.left + (bw - nw) / 2;
            r.right = r.left + nw;
        }
        return r;
    };

    auto rebuildVideoProcessor = [&]() {
        hasVp = false;
        canDualSingleBlt = false;
        vp.Reset();
        vpenum.Reset();
        for (int i = 0; i < kNv12Total; ++i) vpOutRing[i].Reset();
        if (!vdev || !vctx) return;
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC vdesc{};
        vdesc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        vdesc.InputWidth = isDualActive ? std::max(srcW, srcW2) : srcW;
        vdesc.InputHeight = isDualActive ? std::max(srcH, srcH2) : srcH;
        vdesc.OutputWidth = dstW; vdesc.OutputHeight = dstH;
        vdesc.Usage = D3D11_VIDEO_USAGE_OPTIMAL_SPEED;
        if (SUCCEEDED(vdev->CreateVideoProcessorEnumerator(&vdesc, &vpenum)) &&
            SUCCEEDED(vdev->CreateVideoProcessor(vpenum.Get(), 0, &vp))) {
            D3D11_VIDEO_PROCESSOR_CAPS vpCaps{};
            if (SUCCEEDED(vpenum->GetVideoProcessorCaps(&vpCaps))) {
                canDualSingleBlt = (vpCaps.MaxInputStreams >= 2 && vpCaps.MaxStreamStates >= 2);
            }
            if (vctx1) {
                vctx1->VideoProcessorSetStreamColorSpace1(vp.Get(), 0, DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);
                if (canDualSingleBlt) {
                    vctx1->VideoProcessorSetStreamColorSpace1(vp.Get(), 1, DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);
                }
                vctx1->VideoProcessorSetOutputColorSpace1(vp.Get(), DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709);
            }
            D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC ovd{};
            ovd.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
            bool allOk = true;
            for (int i = 0; i < kNv12Total; ++i) {
                if (!nv12Ring[i] || FAILED(vdev->CreateVideoProcessorOutputView(nv12Ring[i].Get(), vpenum.Get(), &ovd, &vpOutRing[i]))) {
                    allOk = false;
                }
            }
            hasVp = allOk;
        }
    };

    dev.As(&vdev);
    ctx.As(&vctx);
    if (vctx) vctx.As(&vctx1);

    D3D11_TEXTURE2D_DESC nv12Desc{};
    nv12Desc.Width = dstW;
    nv12Desc.Height = dstH;
    nv12Desc.MipLevels = 1;
    nv12Desc.ArraySize = 1;
    nv12Desc.Format = DXGI_FORMAT_NV12;
    nv12Desc.SampleDesc.Count = 1;
    nv12Desc.Usage = D3D11_USAGE_DEFAULT;
    nv12Desc.BindFlags = D3D11_BIND_RENDER_TARGET;
    for (int i = 0; i < kNv12Total; ++i) {
        dev->CreateTexture2D(&nv12Desc, nullptr, &nv12Ring[i]);
    }
    rebuildVideoProcessor();

    UINT resetToken = 0;
    ComPtr<IMFDXGIDeviceManager> dxgiMgr;
    if (SUCCEEDED(MFCreateDXGIDeviceManager(&resetToken, &dxgiMgr)) && dxgiMgr) {
        dxgiMgr->ResetDevice(dev.Get(), resetToken);
    }

    ComPtr<IMFTransform> encMft;
    ComPtr<ICodecAPI> encCodecApi;
    std::string encName = "No HW Encoder (Preview Only)";
    bool hasHwEnc = dxgiMgr && createHardwareEncoderMft(codec_, int32_t(dstW), int32_t(dstH), target_fps_, bitrate_kbps_,
                                                        dxgiMgr.Get(), encMft, encName);
    if (hasHwEnc && encMft) encMft.As(&encCodecApi);
    ComPtr<IMFMediaEventGenerator> encEvGen;
    bool isAsyncMft = false;
    if (hasHwEnc && encMft) {
        ComPtr<IMFAttributes> ea;
        UINT32 asyncVal = 0;
        if (SUCCEEDED(encMft->GetAttributes(&ea)) && SUCCEEDED(ea->GetUINT32(MF_TRANSFORM_ASYNC, &asyncVal)) && asyncVal) {
            isAsyncMft = SUCCEEDED(encMft.As(&encEvGen));
        }
    }
    auto lastKfTime = std::chrono::steady_clock::now() - std::chrono::seconds(5);
    bool hasLastNv12Frame = false;

    // Preview SwapChain + helper to recreate preview VP on resize
    ComPtr<IDXGISwapChain1> previewSwap;
    ComPtr<ID3D11VideoProcessorEnumerator> prevEnum;
    ComPtr<ID3D11VideoProcessor> prevVp;
    auto rebuildPreviewVp = [&](UINT pw, UINT ph) {
        prevVp.Reset();
        prevEnum.Reset();
        if (!vdev || pw < 16 || ph < 16) return;
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC pcd{};
        pcd.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        pcd.InputWidth = dstW; pcd.InputHeight = dstH;
        pcd.OutputWidth = pw; pcd.OutputHeight = ph;
        pcd.Usage = D3D11_VIDEO_USAGE_OPTIMAL_SPEED;
        if (SUCCEEDED(vdev->CreateVideoProcessorEnumerator(&pcd, &prevEnum))) {
            vdev->CreateVideoProcessor(prevEnum.Get(), 0, &prevVp);
        }
    };
    if (loopback_preview_ && preview_hwnd_ && IsWindow(preview_hwnd_)) {
        RECT rc{};
        GetClientRect(preview_hwnd_, &rc);
        UINT pw = std::max<UINT>(16, UINT(rc.right - rc.left));
        UINT ph = std::max<UINT>(16, UINT(rc.bottom - rc.top));
        DXGI_SWAP_CHAIN_DESC1 scd{};
        scd.Width = pw; scd.Height = ph;
        scd.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        scd.SampleDesc.Count = 1;
        scd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        scd.BufferCount = 2;
        scd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        scd.Scaling = DXGI_SCALING_STRETCH;
        if (SUCCEEDED(fac->CreateSwapChainForHwnd(dev.Get(), preview_hwnd_, &scd, nullptr, nullptr, &previewSwap))) {
            rebuildPreviewVp(pw, ph);
        }
    }

    {
        std::lock_guard lk(stats_mtx_);
        stats_.width = int32_t(dstW);
        stats_.height = int32_t(dstH);
        stats_.zero_copy = (hasVp && nv12Ring[0]) ? 1 : 0;
        std::strncpy(stats_.encoder_name, encName.c_str(), sizeof(stats_.encoder_name) - 1);
    }

    const int64_t framePeriodUs = 1000000 / target_fps_;
    int64_t frameCount = 0;
    int64_t skippedStatic = 0;
    int64_t bytesWindow = 0;
    int64_t framesWindow = 0;
    auto windowStart = std::chrono::steady_clock::now();
    auto streamEpoch = windowStart;
    const int64_t sampleDurHns = 10000000 / target_fps_;
    int asyncNeedInput = 0;
    DWORD encOutBufCap = 4 * 1024 * 1024; // 4 MB handles 4K60 50Mbps IDR keyframes cleanly!
    ComPtr<IMFSample> reusableOutSample;
    ComPtr<IMFMediaBuffer> reusableOutBuf;
    DWORD reusableAllocCap = 0;

    auto pullMftOutput = [&](int64_t fallbackHns) {
        if (!encMft) return;
        MFT_OUTPUT_STREAM_INFO osi{};
        encMft->GetOutputStreamInfo(0, &osi);
        for (int attempt = 0; attempt < 3; ++attempt) {
            MFT_OUTPUT_DATA_BUFFER outBuf{};
            if (!(osi.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES)) {
                DWORD wantCap = std::max<DWORD>(osi.cbSize, encOutBufCap);
                if (!reusableOutSample || !reusableOutBuf || reusableAllocCap < wantCap) {
                    reusableOutSample.Reset();
                    reusableOutBuf.Reset();
                    if (FAILED(MFCreateSample(&reusableOutSample)) ||
                        FAILED(MFCreateMemoryBuffer(wantCap, &reusableOutBuf))) {
                        return;
                    }
                    reusableOutSample->AddBuffer(reusableOutBuf.Get());
                    reusableAllocCap = wantCap;
                }
                reusableOutBuf->SetCurrentLength(0);
                outBuf.pSample = reusableOutSample.Get();
            }
            DWORD status = 0;
            HRESULT ohr = encMft->ProcessOutput(0, 1, &outBuf, &status);
            if (outBuf.pEvents) { outBuf.pEvents->Release(); outBuf.pEvents = nullptr; }
            if (ohr == MF_E_BUFFERTOOSMALL) {
                // MUST retry ProcessOutput immediately in Async MFT mode after dequeuing METransformHaveOutput!
                encOutBufCap = std::min<DWORD>(std::max<DWORD>(osi.cbSize, encOutBufCap * 2), 32 * 1024 * 1024);
                encMft->GetOutputStreamInfo(0, &osi);
                continue;
            }
            if (SUCCEEDED(ohr) && outBuf.pSample) {
                ComPtr<IMFMediaBuffer> cb;
                if (SUCCEEDED(outBuf.pSample->ConvertToContiguousBuffer(&cb))) {
                    BYTE* data = nullptr;
                    DWORD maxLen = 0, curLen = 0;
                    if (SUCCEEDED(cb->Lock(&data, &maxLen, &curLen)) && curLen > 0) {
                        bytesWindow += curLen;
                        uint32_t vssrc = video_ssrc_.load();
                        if (transport_ && transport_->isReady() && vssrc != 0) {
                            LONGLONG outHns = fallbackHns;
                            outBuf.pSample->GetSampleTime(&outHns);
                            uint32_t rtpTs = uint32_t((outHns * 9) / 1000);
                            transport_->sendH264AccessUnit(data, int(curLen), vssrc, rtpTs);
                        }
                        cb->Unlock();
                    }
                }
                if (osi.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) outBuf.pSample->Release();
            }
            break;
        }
    };

    auto drainAsyncEvents = [&]() {
        if (!isAsyncMft || !encEvGen) return;
        int64_t fallbackHns = std::chrono::duration_cast<std::chrono::microseconds>(
            std::chrono::steady_clock::now() - streamEpoch).count() * 10;
        for (int spin = 0; spin < 16; ++spin) {
            ComPtr<IMFMediaEvent> ev;
            HRESULT ehr = encEvGen->GetEvent(MF_EVENT_FLAG_NO_WAIT, &ev);
            if (ehr == MF_E_NO_EVENTS_AVAILABLE || FAILED(ehr)) break;
            MediaEventType met = MEUnknown;
            ev->GetType(&met);
            if (met == METransformNeedInput) {
                asyncNeedInput++;
            } else if (met == METransformHaveOutput) {
                pullMftOutput(fallbackHns);
            }
        }
    };

    while (running_.load()) {
        auto tStart = std::chrono::steady_clock::now();

        // Always drain async NVENC outputs even during static desktop timeouts (eliminates 2s static stall!)
        drainAsyncEvents();

        int32_t rw = pending_w_.exchange(0);
        int32_t rh = pending_h_.exchange(0);
        if (previewSwap && rw > 16 && rh > 16) {
            previewSwap->ResizeBuffers(2, UINT(rw), UINT(rh), DXGI_FORMAT_B8G8R8A8_UNORM, 0);
            rebuildPreviewVp(UINT(rw), UINT(rh));
        }

        ComPtr<ID3D11Texture2D> deskTex;
        ComPtr<ID3D11Texture2D> deskTex2;
        bool acquiredDup = false;
        bool acquiredDup2 = false;
        bool reencodeStaticKf = false;
        if (!windowMode) {
            if (!dup) {
                if (initDesktopDup()) {
                    rebuildVideoProcessor();
                    force_keyframe_.store(true);
                } else {
                    std::this_thread::sleep_for(std::chrono::milliseconds(150));
                    continue;
                }
            }
            if (isDualActive && !dup2) {
                openDupOnOutput(chosenOutput2, dup2, srcW2, srcH2);
            }

            // In Dual-Screen mode, non-blocking poll Monitor 2 first so Monitor 1 + Monitor 2 stay in lockstep with ZERO extra latency!
            if (isDualActive && dup2) {
                DXGI_OUTDUPL_FRAME_INFO fi2{};
                ComPtr<IDXGIResource> res2;
                HRESULT hr2 = dup2->AcquireNextFrame(0, &fi2, &res2);
                if (SUCCEEDED(hr2) && res2) {
                    res2.As(&deskTex2);
                    acquiredDup2 = true;
                    if (deskTex2) {
                        D3D11_TEXTURE2D_DESC td2{};
                        deskTex2->GetDesc(&td2);
                        if (td2.Width != srcW2 || td2.Height != srcH2) {
                            srcW2 = td2.Width; srcH2 = td2.Height;
                            rebuildVideoProcessor();
                        }
                        if (!dualCache1) {
                            D3D11_TEXTURE2D_DESC cd = td2;
                            cd.BindFlags = D3D11_BIND_SHADER_RESOURCE;
                            cd.MiscFlags = 0;
                            dev->CreateTexture2D(&cd, nullptr, &dualCache1);
                        } else {
                            D3D11_TEXTURE2D_DESC cd{};
                            dualCache1->GetDesc(&cd);
                            if (cd.Width != td2.Width || cd.Height != td2.Height || cd.Format != td2.Format) {
                                dualCache1.Reset();
                                D3D11_TEXTURE2D_DESC ncd = td2;
                                ncd.BindFlags = D3D11_BIND_SHADER_RESOURCE;
                                ncd.MiscFlags = 0;
                                dev->CreateTexture2D(&ncd, nullptr, &dualCache1);
                            }
                        }
                        if (dualCache1) ctx->CopyResource(dualCache1.Get(), deskTex2.Get());
                    }
                    dup2->ReleaseFrame();
                    acquiredDup2 = false;
                } else if (hr2 == DXGI_ERROR_ACCESS_LOST || hr2 == DXGI_ERROR_INVALID_CALL) {
                    dup2.Reset();
                }
            }

            DXGI_OUTDUPL_FRAME_INFO fi{};
            ComPtr<IDXGIResource> res;
            HRESULT hr = dup->AcquireNextFrame(uint32_t(std::max<int64_t>(4, framePeriodUs / 1000)), &fi, &res);
            if (hr == DXGI_ERROR_WAIT_TIMEOUT) {
                if (isDualActive && deskTex2 && dualCache0) {
                    // Monitor 2 updated while Monitor 1 was static -> composite fresh Monitor 2 with cached Monitor 1!
                    deskTex = dualCache0;
                } else {
                    skippedStatic++;
                    auto nowKf = std::chrono::steady_clock::now();
                    float stSec = std::chrono::duration<float>(nowKf - windowStart).count();
                    {
                        std::lock_guard lk(stats_mtx_);
                        stats_.frames_skipped_static = skippedStatic;
                        if (stSec >= 1.0f) {
                            stats_.fps = float(framesWindow) / stSec;
                            stats_.bitrate_kbps = (float(bytesWindow) * 8.0f / 1000.0f) / stSec;
                            framesWindow = 0;
                            bytesWindow = 0;
                            windowStart = nowKf;
                        }
                    }
                    bool readyForPliKf = force_keyframe_.load() && (nowKf - lastKfTime >= std::chrono::milliseconds(500));
                    bool periodicKf = (nowKf - lastKfTime >= std::chrono::seconds(2));
                    if (hasLastNv12Frame && (readyForPliKf || periodicKf)) {
                        reencodeStaticKf = true;
                    } else {
                        continue;
                    }
                }
            } else if (hr == DXGI_ERROR_ACCESS_LOST || hr == DXGI_ERROR_INVALID_CALL) {
                dup.Reset();
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
                continue;
            } else if (FAILED(hr)) {
                std::this_thread::sleep_for(std::chrono::milliseconds(16));
                continue;
            } else {
                res.As(&deskTex);
                acquiredDup = true;
                if (deskTex) {
                    D3D11_TEXTURE2D_DESC td{};
                    deskTex->GetDesc(&td);
                    if (td.Width != srcW || td.Height != srcH) {
                        srcW = td.Width; srcH = td.Height;
                        rebuildVideoProcessor();
                    }
                    if (isDualActive) {
                        if (!dualCache0) {
                            D3D11_TEXTURE2D_DESC cd = td;
                            cd.BindFlags = D3D11_BIND_SHADER_RESOURCE;
                            cd.MiscFlags = 0;
                            dev->CreateTexture2D(&cd, nullptr, &dualCache0);
                        } else {
                            D3D11_TEXTURE2D_DESC cd{};
                            dualCache0->GetDesc(&cd);
                            if (cd.Width != td.Width || cd.Height != td.Height || cd.Format != td.Format) {
                                dualCache0.Reset();
                                D3D11_TEXTURE2D_DESC ncd = td;
                                ncd.BindFlags = D3D11_BIND_SHADER_RESOURCE;
                                ncd.MiscFlags = 0;
                                dev->CreateTexture2D(&ncd, nullptr, &dualCache0);
                            }
                        }
                        if (dualCache0) ctx->CopyResource(dualCache0.Get(), deskTex.Get());
                    }
                }
            }
        } else {
            if (!IsWindow(target_hwnd_)) {
                std::this_thread::sleep_for(std::chrono::milliseconds(30));
                continue;
            }
            RECT wrc{};
            GetWindowRect(target_hwnd_, &wrc);
            UINT curW = UINT(std::max<LONG>(64, wrc.right - wrc.left)) & ~1u;
            UINT curH = UINT(std::max<LONG>(64, wrc.bottom - wrc.top)) & ~1u;
            if (curW != winTexW || curH != winTexH || !winUploadTex) {
                srcW = curW; srcH = curH;
                allocWindowBuffers(curW, curH);
                rebuildVideoProcessor();
            }
            if (winMemDc && winBits && winUploadTex && !IsHungAppWindow(target_hwnd_) && PrintWindow(target_hwnd_, winMemDc, 2)) {
                ctx->UpdateSubresource(winUploadTex.Get(), 0, nullptr, winBits, winTexW * 4, 0);
                deskTex = winUploadTex;
            } else {
                std::this_thread::sleep_for(std::chrono::milliseconds(16));
                continue;
            }
        }

        float encMs = 0.0f;
        float decMs = 0.0f;

        if ((deskTex || reencodeStaticKf) && hasVp) {
            auto tEnc0 = std::chrono::steady_clock::now();

            drainAsyncEvents();
            const bool canSubmit = hasHwEnc && encMft && ((!isAsyncMft) || (asyncNeedInput > 0));
            int surfIdx = nv12LastIdx;
            if (deskTex) {
                // Only write/advance NV12 encoder ring when NVENC is ready for input; otherwise write dedicated scratch slot 4!
                surfIdx = canSubmit ? nv12CurIdx : kPreviewScratchIdx;
                D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC ivd{};
                ivd.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
                if (isDualActive) {
                    // ZERO-COPY Dual-Screen GPU Composite: Left half = Monitor 1, Right half = Monitor 2 (2px dark divider, aspect-preserving)!
                    ID3D11Texture2D* texL = dualCache0 ? dualCache0.Get() : deskTex.Get();
                    ID3D11Texture2D* texR = dualCache1 ? dualCache1.Get() : texL;
                    ComPtr<ID3D11VideoProcessorInputView> vpInL, vpInR;
                    if (SUCCEEDED(vdev->CreateVideoProcessorInputView(texL, vpenum.Get(), &ivd, &vpInL)) &&
                        SUCCEEDED(vdev->CreateVideoProcessorInputView(texR, vpenum.Get(), &ivd, &vpInR))) {
                        D3D11_TEXTURE2D_DESC tdL{}, tdR{};
                        texL->GetDesc(&tdL);
                        texR->GetDesc(&tdR);
                        RECT srcL{ 0, 0, LONG(tdL.Width), LONG(tdL.Height) };
                        RECT srcR{ 0, 0, LONG(tdR.Width), LONG(tdR.Height) };
                        RECT boxL{ 0, 0, LONG(dstW / 2 - 2), LONG(dstH) };
                        RECT boxR{ LONG(dstW / 2 + 2), 0, LONG(dstW), LONG(dstH) };
                        RECT rcL = fitLetterboxRect(tdL.Width, tdL.Height, boxL);
                        RECT rcR = fitLetterboxRect(tdR.Width, tdR.Height, boxR);
                        D3D11_VIDEO_COLOR bg{};
                        bg.RGBA = { 0.04f, 0.04f, 0.06f, 1.0f };
                        vctx->VideoProcessorSetOutputBackgroundColor(vp.Get(), FALSE, &bg);
                        if (canDualSingleBlt) {
                            vctx->VideoProcessorSetStreamSourceRect(vp.Get(), 0, TRUE, &srcL);
                            vctx->VideoProcessorSetStreamDestRect(vp.Get(), 0, TRUE, &rcL);
                            vctx->VideoProcessorSetStreamSourceRect(vp.Get(), 1, TRUE, &srcR);
                            vctx->VideoProcessorSetStreamDestRect(vp.Get(), 1, TRUE, &rcR);
                            D3D11_VIDEO_PROCESSOR_STREAM sts[2]{};
                            sts[0].Enable = TRUE; sts[0].pInputSurface = vpInL.Get();
                            sts[1].Enable = TRUE; sts[1].pInputSurface = vpInR.Get();
                            vctx->VideoProcessorBlt(vp.Get(), vpOutRing[surfIdx].Get(), 0, 2, sts);
                        } else {
                            // Safe 2-pass fallback for GPUs with MaxInputStreams == 1
                            D3D11_VIDEO_PROCESSOR_STREAM st{};
                            st.Enable = TRUE;
                            vctx->VideoProcessorSetOutputTargetRect(vp.Get(), TRUE, &boxL);
                            vctx->VideoProcessorSetStreamSourceRect(vp.Get(), 0, TRUE, &srcL);
                            vctx->VideoProcessorSetStreamDestRect(vp.Get(), 0, TRUE, &rcL);
                            st.pInputSurface = vpInL.Get();
                            vctx->VideoProcessorBlt(vp.Get(), vpOutRing[surfIdx].Get(), 0, 1, &st);

                            vctx->VideoProcessorSetOutputTargetRect(vp.Get(), TRUE, &boxR);
                            vctx->VideoProcessorSetStreamSourceRect(vp.Get(), 0, TRUE, &srcR);
                            vctx->VideoProcessorSetStreamDestRect(vp.Get(), 0, TRUE, &rcR);
                            st.pInputSurface = vpInR.Get();
                            vctx->VideoProcessorBlt(vp.Get(), vpOutRing[surfIdx].Get(), 0, 1, &st);
                            vctx->VideoProcessorSetOutputTargetRect(vp.Get(), FALSE, nullptr);
                        }
                        if (canSubmit) {
                            nv12LastIdx = surfIdx;
                            nv12CurIdx = (nv12CurIdx + 1) % kEncRing;
                            hasLastNv12Frame = true;
                        }
                    }
                } else {
                    ComPtr<ID3D11VideoProcessorInputView> vpIn;
                    if (SUCCEEDED(vdev->CreateVideoProcessorInputView(deskTex.Get(), vpenum.Get(), &ivd, &vpIn))) {
                        D3D11_VIDEO_PROCESSOR_STREAM st{};
                        st.Enable = TRUE;
                        st.pInputSurface = vpIn.Get();
                        vctx->VideoProcessorBlt(vp.Get(), vpOutRing[surfIdx].Get(), 0, 1, &st);
                        if (canSubmit) {
                            nv12LastIdx = surfIdx;
                            nv12CurIdx = (nv12CurIdx + 1) % kEncRing;
                            hasLastNv12Frame = true;
                        }
                    }
                }
            }

            // Release DXGI frame immediately after VRAM blit so GPU desktop composition is NEVER held during network send!
            if (acquiredDup && dup) {
                dup->ReleaseFrame();
                acquiredDup = false;
            }

            // Feed VRAM NV12 surface directly to Hardware NVENC MFT
            if (canSubmit && nv12Ring[surfIdx]) {
                {
                    auto nowKf2 = std::chrono::steady_clock::now();
                    bool wantKf = force_keyframe_.load() && (nowKf2 - lastKfTime >= std::chrono::milliseconds(500));
                    bool periodicKf2 = (nowKf2 - lastKfTime >= std::chrono::seconds(2));
                    if (encCodecApi && (wantKf || periodicKf2)) {
                        force_keyframe_.store(false);
                        VARIANT kv{};
                        kv.vt = VT_UI4;
                        kv.ulVal = 1;
                        encCodecApi->SetValue(&CODECAPI_AVEncVideoForceKeyFrame, &kv);
                        lastKfTime = nowKf2;
                    }

                    ComPtr<IMFSample> inSample;
                    ComPtr<IMFMediaBuffer> dxBuf;
                    if (SUCCEEDED(MFCreateSample(&inSample)) &&
                        SUCCEEDED(MFCreateDXGISurfaceBuffer(__uuidof(ID3D11Texture2D), nv12Ring[surfIdx].Get(), 0, FALSE, &dxBuf))) {
                        int64_t elapsedUs = std::chrono::duration_cast<std::chrono::microseconds>(
                            std::chrono::steady_clock::now() - streamEpoch).count();
                        int64_t sampleTimeHns = elapsedUs * 10;
                        inSample->AddBuffer(dxBuf.Get());
                        inSample->SetSampleTime(sampleTimeHns);
                        inSample->SetSampleDuration(sampleDurHns);

                        if (SUCCEEDED(encMft->ProcessInput(0, inSample.Get(), 0))) {
                            if (isAsyncMft) {
                                asyncNeedInput--;
                                drainAsyncEvents();
                            } else {
                                pullMftOutput(sampleTimeHns);
                            }
                        }
                    }
                }
            }
            auto tEnc1 = std::chrono::steady_clock::now();
            encMs = std::chrono::duration<float, std::milli>(tEnc1 - tEnc0).count();

            // Zero-Copy Preview Present directly from VRAM NV12 -> DXGI FlipDiscard SwapChain
            if (previewSwap && prevVp && prevEnum && nv12Ring[surfIdx]) {
                auto tDec0 = std::chrono::steady_clock::now();
                ComPtr<ID3D11Texture2D> backBuf;
                if (SUCCEEDED(previewSwap->GetBuffer(0, IID_PPV_ARGS(&backBuf)))) {
                    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC piv{};
                    piv.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
                    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC pov{};
                    pov.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
                    ComPtr<ID3D11VideoProcessorInputView> pin;
                    ComPtr<ID3D11VideoProcessorOutputView> pout;
                    if (SUCCEEDED(vdev->CreateVideoProcessorInputView(nv12Ring[surfIdx].Get(), prevEnum.Get(), &piv, &pin)) &&
                        SUCCEEDED(vdev->CreateVideoProcessorOutputView(backBuf.Get(), prevEnum.Get(), &pov, &pout))) {
                        D3D11_VIDEO_PROCESSOR_STREAM pst{};
                        pst.Enable = TRUE;
                        pst.pInputSurface = pin.Get();
                        vctx->VideoProcessorBlt(prevVp.Get(), pout.Get(), 0, 1, &pst);
                        previewSwap->Present(0, 0);
                    }
                }
                auto tDec1 = std::chrono::steady_clock::now();
                decMs = std::chrono::duration<float, std::milli>(tDec1 - tDec0).count();
            }
        }

        if (acquiredDup && dup) dup->ReleaseFrame();
        if (acquiredDup2 && dup2) dup2->ReleaseFrame();
        frameCount++;
        framesWindow++;

        auto now = std::chrono::steady_clock::now();
        float elapsedSec = std::chrono::duration<float>(now - windowStart).count();
        if (elapsedSec >= 0.5f) {
            std::lock_guard lk(stats_mtx_);
            stats_.fps = float(framesWindow) / elapsedSec;
            stats_.bitrate_kbps = (float(bytesWindow) * 8.0f / 1000.0f) / elapsedSec;
            stats_.encode_ms = encMs;
            stats_.decode_ms = decMs;
            stats_.frames_encoded = frameCount;
            stats_.frames_skipped_static = skippedStatic;
            bytesWindow = 0;
            framesWindow = 0;
            windowStart = now;
        }

        auto spentUs = std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - tStart).count();
        if (spentUs < framePeriodUs) {
            std::this_thread::sleep_for(std::chrono::microseconds(framePeriodUs - spentUs));
        }
    }

    if (encMft) {
        encMft->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        encMft->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
        ComPtr<IMFShutdown> shut;
        if (SUCCEEDED(encMft.As(&shut))) shut->Shutdown();
    }
    reusableOutBuf.Reset();
    reusableOutSample.Reset();
    encEvGen.Reset();
    encCodecApi.Reset();
    encMft.Reset();
    dxgiMgr.Reset();
    previewSwap.Reset();
    prevVp.Reset();
    prevEnum.Reset();
    for (int i = 0; i < kNv12Total; ++i) { vpOutRing[i].Reset(); nv12Ring[i].Reset(); }
    vp.Reset();
    vpenum.Reset();
    dup.Reset();
    dup2.Reset();
    dualCache0.Reset();
    dualCache1.Reset();
    winUploadTex.Reset();
    if (winMemDc && winOldBmp) SelectObject(winMemDc, winOldBmp);
    if (winDib) DeleteObject(winDib);
    if (winMemDc) DeleteDC(winMemDc);
    if (winSrcDc) ReleaseDC(nullptr, winSrcDc);
    } // End inner COM scope

cleanup:
    MFShutdown();
    CoUninitialize();
    if (inputDesk) {
        if (origDesk) SetThreadDesktop(origDesk);
        CloseDesktop(inputDesk);
    }
    revertRealtimeThread(mm);
}

// Dedicated WASAPI Process-Loopback (zero voice echo!) + Device-Loopback fallback with hot-unplug auto-recovery
void ScreenEngine::systemAudioLoop() {
    HANDLE mm = boostRealtimeThread(L"Pro Audio");
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    {
        int opErr = 0;
        OpusEncoder* enc = opus_encoder_create(48000, 2, OPUS_APPLICATION_AUDIO, &opErr);
        if (!enc || opErr != OPUS_OK) {
            if (enc) opus_encoder_destroy(enc);
            CoUninitialize();
            revertRealtimeThread(mm);
            return;
        }
        opus_encoder_ctl(enc, OPUS_SET_BITRATE(128000));
        opus_encoder_ctl(enc, OPUS_SET_SIGNAL(OPUS_SIGNAL_MUSIC));
        opus_encoder_ctl(enc, OPUS_SET_INBAND_FEC(1));
        opus_encoder_ctl(enc, OPUS_SET_PACKET_LOSS_PERC(5));
        opus_encoder_ctl(enc, OPUS_SET_DTX(0));
        opus_encoder_ctl(enc, OPUS_SET_COMPLEXITY(8));

        uint32_t rtpTs = 0;
        bool announcedInitial = false;

        // Outer recovery loop survives audio device unplug/switch without killing stream audio!
        while (running_.load()) {
            ComPtr<IAudioClient> ac;
            ComPtr<IAudioCaptureClient> cap;
            WAVEFORMATEX* mixWf = nullptr;
            WAVEFORMATEX procWf{};
            bool started = false;
            uint32_t srcRate = 48000;
            uint32_t srcCh = 2;
            int sampleFmt = 0; // 0=float32, 1=int16, 2=int32, 3=int24

            // Priority 1: Windows 10 20348+ / Win11 Process Loopback (captures target app OR all audio EXCLUDING FluxCord -> ZERO ECHO!)
            {
                DWORD targetPid = 0;
                bool includeTree = false;
                if (target_hwnd_ && IsWindow(target_hwnd_)) {
                    GetWindowThreadProcessId(target_hwnd_, &targetPid);
                    if (targetPid != 0 && targetPid != GetCurrentProcessId()) includeTree = true;
                }
                if (!includeTree) targetPid = GetCurrentProcessId();

                ComPtr<IAudioClient> procAc;
                if (tryOpenProcessLoopback(targetPid, includeTree, procAc) && procAc) {
                    procWf.wFormatTag = WAVE_FORMAT_IEEE_FLOAT;
                    procWf.nChannels = 2;
                    procWf.nSamplesPerSec = 48000;
                    procWf.wBitsPerSample = 32;
                    procWf.nBlockAlign = 8;
                    procWf.nAvgBytesPerSec = 48000 * 8;
                    // Virtual Process Loopback uses LOOPBACK flag directly (NO AUTOCONVERTPCM!)
                    if (SUCCEEDED(procAc->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 200000, 0, &procWf, nullptr)) &&
                        SUCCEEDED(procAc->GetService(IID_PPV_ARGS(&cap))) &&
                        SUCCEEDED(procAc->Start())) {
                        ac = procAc;
                        srcRate = 48000; srcCh = 2; sampleFmt = 0;
                        started = true;
                    } else {
                        procAc.Reset();
                        if (tryOpenProcessLoopback(targetPid, includeTree, procAc) && procAc) {
                            procWf.wFormatTag = WAVE_FORMAT_PCM;
                            procWf.wBitsPerSample = 16;
                            procWf.nBlockAlign = 4;
                            procWf.nAvgBytesPerSec = 48000 * 4;
                            if (SUCCEEDED(procAc->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 200000, 0, &procWf, nullptr)) &&
                                SUCCEEDED(procAc->GetService(IID_PPV_ARGS(&cap))) &&
                                SUCCEEDED(procAc->Start())) {
                                ac = procAc;
                                srcRate = 48000; srcCh = 2; sampleFmt = 1;
                                started = true;
                            }
                        }
                    }
                }
            }

            // Priority 2: Classic WASAPI Render Loopback (try OS AUTOCONVERTPCM 48kHz float32 first, fallback to native mix format incl. 24-bit DAC!)
            if (!started) {
                ComPtr<IMMDeviceEnumerator> en;
                ComPtr<IMMDevice> dev;
                if (SUCCEEDED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&en)))) {
                    if (!render_device_id_.empty()) {
                        std::wstring wid = utf8ToWide(render_device_id_.c_str());
                        if (FAILED(en->GetDevice(wid.c_str(), &dev))) dev.Reset();
                    }
                    if (!dev) en->GetDefaultAudioEndpoint(eRender, eConsole, &dev);
                    if (dev) {
                        WAVEFORMATEX wantWf{};
                        wantWf.wFormatTag = WAVE_FORMAT_IEEE_FLOAT;
                        wantWf.nChannels = 2;
                        wantWf.nSamplesPerSec = 48000;
                        wantWf.wBitsPerSample = 32;
                        wantWf.nBlockAlign = 8;
                        wantWf.nAvgBytesPerSec = 48000 * 8;
                        if (SUCCEEDED(dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(ac.GetAddressOf()))) &&
                            SUCCEEDED(ac->Initialize(AUDCLNT_SHAREMODE_SHARED,
                                                     AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                                                     200000, 0, &wantWf, nullptr)) &&
                            SUCCEEDED(ac->GetService(IID_PPV_ARGS(&cap))) &&
                            SUCCEEDED(ac->Start())) {
                            srcRate = 48000; srcCh = 2; sampleFmt = 0;
                            started = true;
                        } else {
                            ac.Reset();
                            if (SUCCEEDED(dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(ac.GetAddressOf()))) &&
                                SUCCEEDED(ac->GetMixFormat(&mixWf)) && mixWf &&
                                SUCCEEDED(ac->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 200000, 0, mixWf, nullptr)) &&
                                SUCCEEDED(ac->GetService(IID_PPV_ARGS(&cap))) &&
                                SUCCEEDED(ac->Start())) {
                                srcRate = std::max<uint32_t>(8000, mixWf->nSamplesPerSec);
                                srcCh = std::max<uint32_t>(1, mixWf->nChannels);
                                bool isFloat = (mixWf->wFormatTag == WAVE_FORMAT_IEEE_FLOAT);
                                if (mixWf->wFormatTag == WAVE_FORMAT_EXTENSIBLE && mixWf->cbSize >= 22) {
                                    const auto* ext = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(mixWf);
                                    isFloat = (ext->SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
                                }
                                if (isFloat && mixWf->wBitsPerSample == 32) { sampleFmt = 0; started = true; }
                                else if (!isFloat && mixWf->wBitsPerSample == 16) { sampleFmt = 1; started = true; }
                                else if (!isFloat && mixWf->wBitsPerSample == 24) { sampleFmt = 3; started = true; } // 24-bit signed LE (USB DAC safe!)
                                else if (!isFloat && mixWf->wBitsPerSample == 32) { sampleFmt = 2; started = true; } // 32-bit int PCM
                                else { ac->Stop(); cap.Reset(); ac.Reset(); }
                            }
                        }
                    }
                }
            }

            if (!started || !ac || !cap) {
                if (mixWf) { CoTaskMemFree(mixWf); mixWf = nullptr; }
                std::this_thread::sleep_for(std::chrono::milliseconds(300));
                continue;
            }

            std::vector<float> fifo;
            fifo.reserve(4096 * 2);
            uint8_t opusBuf[1200];
            int activeHangFrames = 0;
            auto lastPktTime = std::chrono::steady_clock::now();
            // Stateful fractional phase across WASAPI packets eliminates 100Hz boundary clicks at 44.1kHz / 96kHz!
            double srcPhase = 0.0;
            float prevL = 0.0f, prevR = 0.0f;
            bool hasPrev = false;

            auto readSample = [&](const BYTE* base, uint32_t frameIdx, uint32_t chIdx) -> float {
                uint32_t idx = frameIdx * srcCh + std::min(chIdx, srcCh - 1);
                if (sampleFmt == 0) return reinterpret_cast<const float*>(base)[idx];
                if (sampleFmt == 1) return float(reinterpret_cast<const int16_t*>(base)[idx]) * (1.0f / 32768.0f);
                if (sampleFmt == 3) {
                    const uint8_t* p = base + size_t(idx) * 3;
                    int32_t v = int32_t(p[0]) | (int32_t(p[1]) << 8) | (int32_t(p[2]) << 16);
                    if (v & 0x800000) v |= ~0xFFFFFF;
                    return float(v) * (1.0f / 8388608.0f);
                }
                return float(reinterpret_cast<const int32_t*>(base)[idx]) * (1.0f / 2147483648.0f);
            };

            while (running_.load()) {
                if (!announcedInitial && transport_ && transport_->isReady()) {
                    transport_->sendSilenceFrames();
                    rtpTs += 5 * 960;
                    announcedInitial = true;
                }

                UINT32 pktLen = 0;
                HRESULT hr = cap->GetNextPacketSize(&pktLen);
                if (isAudioDeviceLostHr(hr)) break;
                if (FAILED(hr) || pktLen == 0) {
                    // If classic render loopback goes idle while silent, cleanly finish hang tail and emit silence frames!
                    if (activeHangFrames > 0 && std::chrono::steady_clock::now() - lastPktTime >= std::chrono::milliseconds(600)) {
                        activeHangFrames = 0;
                        if (transport_ && transport_->isReady()) {
                            transport_->sendSilenceFrames();
                            rtpTs += 5 * 960;
                        }
                    }
                    std::this_thread::sleep_for(std::chrono::milliseconds(3));
                    continue;
                }

                BYTE* data = nullptr;
                UINT32 frames = 0;
                DWORD flags = 0;
                hr = cap->GetBuffer(&data, &frames, &flags, nullptr, nullptr);
                if (isAudioDeviceLostHr(hr)) break;
                if (SUCCEEDED(hr)) {
                    lastPktTime = std::chrono::steady_clock::now();
                    if (frames > 0) {
                        const double step = double(srcRate) / 48000.0;
                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) || !data) {
                            while (srcPhase < double(frames) - 1.0 + (srcRate == 48000 ? 1.0 : 0.0)) {
                                fifo.push_back(0.0f);
                                fifo.push_back(0.0f);
                                srcPhase += step;
                            }
                            srcPhase -= double(frames);
                            prevL = 0.0f; prevR = 0.0f; hasPrev = true;
                        } else if (srcRate == 48000 && sampleFmt == 0 && srcCh == 2) {
                            const float* fp = reinterpret_cast<const float*>(data);
                            fifo.insert(fifo.end(), fp, fp + size_t(frames) * 2);
                        } else if (srcRate == 48000) {
                            for (uint32_t i = 0; i < frames; ++i) {
                                float l = readSample(data, i, 0);
                                float r = (srcCh >= 2) ? readSample(data, i, 1) : l;
                                fifo.push_back(l);
                                fifo.push_back(r);
                            }
                        } else {
                            auto getChan = [&](int32_t idx, uint32_t ch) -> float {
                                if (idx < 0) return hasPrev ? (ch == 0 ? prevL : prevR) : readSample(data, 0, ch);
                                return readSample(data, std::min<uint32_t>(uint32_t(idx), frames - 1), ch);
                            };
                            while (srcPhase < double(frames) - 1.0) {
                                int32_t i0 = int32_t(std::floor(srcPhase));
                                float frac = float(srcPhase - double(i0));
                                float l = getChan(i0, 0) * (1.0f - frac) + getChan(i0 + 1, 0) * frac;
                                float r = (srcCh >= 2)
                                    ? (getChan(i0, 1) * (1.0f - frac) + getChan(i0 + 1, 1) * frac)
                                    : l;
                                fifo.push_back(l);
                                fifo.push_back(r);
                                srcPhase += step;
                            }
                            prevL = readSample(data, frames - 1, 0);
                            prevR = (srcCh >= 2) ? readSample(data, frames - 1, 1) : prevL;
                            hasPrev = true;
                            srcPhase -= double(frames);
                        }
                    }
                    cap->ReleaseBuffer(frames);
                }

                while (fifo.size() >= 960 * 2) {
                    float energy = 0.0f;
                    for (int i = 0; i < 960 * 2; ++i) energy += fifo[size_t(i)] * fifo[size_t(i)];
                    bool hasAudio = (energy > 1e-8f);
                    if (hasAudio) activeHangFrames = 25;
                    else if (activeHangFrames > 0) activeHangFrames--;

                    if (transport_ && transport_->isReady()) {
                        if (activeHangFrames > 0) {
                            int n = opus_encode_float(enc, fifo.data(), 960, opusBuf, sizeof(opusBuf));
                            if (n > 2) transport_->sendOpus(opusBuf, n, rtpTs);
                            if (activeHangFrames == 1 && !hasAudio) {
                                transport_->sendSilenceFrames();
                                rtpTs += 5 * 960;
                            }
                        }
                    }
                    rtpTs += 960;
                    fifo.erase(fifo.begin(), fifo.begin() + 960 * 2);
                }
            }

            ac->Stop();
            cap.Reset();
            ac.Reset();
            if (mixWf) { CoTaskMemFree(mixWf); mixWf = nullptr; }
            if (running_.load()) std::this_thread::sleep_for(std::chrono::milliseconds(250));
        }

        opus_encoder_destroy(enc);
    }
    CoUninitialize();
    revertRealtimeThread(mm);
}

} // namespace fc
