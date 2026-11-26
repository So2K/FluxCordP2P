#include "VoiceEngine.h"
#include "NetTurbo.h"
#include <opus.h>
#include <initguid.h>
#include <audioclient.h>
#include <avrt.h>
#include <mmdeviceapi.h>
#include <propkey.h>
#include <functiondiscoverykeys_devpkey.h>
#include <wrl/client.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>

using Microsoft::WRL::ComPtr;

namespace fc {

namespace {
constexpr int kRate = 48000;
constexpr int kFrame = 960;      // 20 ms @ 48 kHz
constexpr int kMaxOpus = 5760;   // 120 ms max Opus packet size

int64_t steadyMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}

// Safe bit-level finite check under /fp:fast!
inline bool isFiniteFast(float x) {
    uint32_t bits = 0;
    std::memcpy(&bits, &x, sizeof(bits));
    return (bits & 0x7F800000u) != 0x7F800000u;
}

bool isDeviceLostHr(HRESULT hr) {
    return hr == AUDCLNT_E_DEVICE_INVALIDATED ||
           hr == AUDCLNT_E_SERVICE_NOT_RUNNING ||
           hr == AUDCLNT_E_RESOURCES_INVALIDATED ||
           hr == AUDCLNT_E_ENDPOINT_CREATE_FAILED;
}

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

bool openAudioClient(EDataFlow flow, const std::string& deviceId, int channels, ComPtr<IAudioClient>& client, HANDLE ev) {
    client.Reset();
    ComPtr<IMMDeviceEnumerator> en;
    if (FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&en)))) return false;

    ComPtr<IMMDevice> dev;
    if (!deviceId.empty()) {
        std::wstring wid = utf8ToWide(deviceId.c_str());
        if (FAILED(en->GetDevice(wid.c_str(), &dev))) dev.Reset();
    }
    if (!dev) {
        if (FAILED(en->GetDefaultAudioEndpoint(flow, eConsole, &dev))) {
            if (FAILED(en->GetDefaultAudioEndpoint(flow, eCommunications, &dev))) return false;
        }
    }
    if (FAILED(dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, &client))) return false;

    WAVEFORMATEX wf{};
    wf.wFormatTag = WAVE_FORMAT_IEEE_FLOAT;
    wf.nChannels = WORD(channels);
    wf.nSamplesPerSec = kRate;
    wf.wBitsPerSample = 32;
    wf.nBlockAlign = WORD(channels * 4);
    wf.nAvgBytesPerSec = kRate * wf.nBlockAlign;

    const DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
    if (FAILED(client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 200000, 0, &wf, nullptr))) {
        client.Reset();
        return false;
    }
    return SUCCEEDED(client->SetEventHandle(ev));
}
} // namespace

/// Smooth adaptive 40ms jitter buffer with 64-bit seq unwrapping + Opus In-Band FEC/PLC (zero micro-dropouts!)
struct VoiceEngine::RemoteStream {
    OpusDecoder* dec = nullptr;
    std::map<int64_t, std::vector<uint8_t>> jb;
    int64_t last_ext_seq = -1;
    int64_t next_play_seq = 0;
    bool playing = false;
    int consecutive_plc = 0;
    int stale_count = 0;
    int64_t last_rx_ms = 0;
    int64_t last_loud_ms = 0;
    int64_t last_loss_ms = 0;
    std::vector<float> pcm_fifo; // interleaved stereo float32

    RemoteStream() {
        int err = 0;
        dec = opus_decoder_create(kRate, 2, &err);
        if (err != OPUS_OK) {
            if (dec) opus_decoder_destroy(dec);
            dec = nullptr;
        }
        pcm_fifo.reserve(kFrame * 2 * 6);
        last_rx_ms = steadyMs();
    }
    ~RemoteStream() {
        if (dec) opus_decoder_destroy(dec);
    }

    int64_t unwrapSeq(uint16_t s) {
        if (last_ext_seq < 0) {
            last_ext_seq = s;
            return s;
        }
        int64_t cand = (last_ext_seq & ~int64_t(0xFFFF)) | int64_t(s);
        if (cand < last_ext_seq - 32768) cand += 65536;
        else if (cand > last_ext_seq + 32768) cand -= 65536;
        if (cand > last_ext_seq) last_ext_seq = cand;
        return cand;
    }

    void pushPacket(uint16_t seq, const uint8_t* opus, int len) {
        last_rx_ms = steadyMs();
        int64_t ext = unwrapSeq(seq);
        bool recentStream = playing || (next_play_seq > 0 && (last_rx_ms - last_loss_ms) < 500);
        if (recentStream && ext < next_play_seq) {
            // Recover cleanly if remote sender restarted its sequence counter while playing!
            if (++stale_count > 50) {
                jb.clear();
                playing = false;
                last_ext_seq = seq;
                next_play_seq = 0;
                ext = seq;
                stale_count = 0;
            } else {
                return;
            }
        } else {
            stale_count = 0;
        }
        jb[ext] = std::vector<uint8_t>(opus, opus + len);
        // Cap at 12 packets (240 ms) to bound max latency
        while (jb.size() > 12) {
            jb.erase(jb.begin());
            if (playing && !jb.empty() && next_play_seq < jb.begin()->first)
                next_play_seq = jb.begin()->first;
        }
    }

    void decodeIntoFifo(size_t needed_samples) {
        if (!dec) return;
        std::vector<float> decBuf(kMaxOpus * 2);
        while (pcm_fifo.size() < needed_samples) {
            if (!playing) {
                // Smooth 40ms (2-packet) prebuffer prevents jitter micro-dropouts; expands to 3 packets briefly after loss!
                size_t targetPrebuf = (steadyMs() - last_loss_ms < 10000) ? 3 : 2;
                if (jb.size() < targetPrebuf) break;
                next_play_seq = jb.begin()->first;
                playing = true;
                consecutive_plc = 0;
            }

            auto it = jb.find(next_play_seq);
            int samples = 0;
            if (it != jb.end()) {
                const auto& p = it->second;
                if (p.size() == 3 && p[0] == 0xF8 && p[1] == 0xFF && p[2] == 0xFE) {
                    std::memset(decBuf.data(), 0, sizeof(float) * kFrame * 2);
                    samples = kFrame;
                    jb.erase(it);
                    next_play_seq++;
                    consecutive_plc = 0;
                    if (jb.empty()) playing = false; // explicit end-of-talkspurt
                } else {
                    samples = opus_decode_float(dec, p.data(), int(p.size()), decBuf.data(), kMaxOpus, 0);
                    jb.erase(it);
                    next_play_seq++;
                    consecutive_plc = 0;
                }
            } else if (!jb.empty() && jb.begin()->first > next_play_seq && consecutive_plc < 4) {
                // True packet loss: invoke Opus In-Band FEC matching next packet's exact frame duration, else Opus PLC!
                auto itNext = jb.find(next_play_seq + 1);
                if (itNext != jb.end() && itNext->second.size() > 3) {
                    int fecFrames = opus_packet_get_nb_samples(itNext->second.data(), int(itNext->second.size()), kRate);
                    if (fecFrames <= 0 || fecFrames > kMaxOpus) fecFrames = kFrame;
                    samples = opus_decode_float(dec, itNext->second.data(), int(itNext->second.size()), decBuf.data(), fecFrames, 1);
                } else {
                    samples = opus_decode_float(dec, nullptr, 0, decBuf.data(), kFrame, 0);
                }
                next_play_seq++;
                consecutive_plc++;
                last_loss_ms = steadyMs();
            } else if (!jb.empty() && consecutive_plc >= 4) {
                next_play_seq = jb.begin()->first;
                consecutive_plc = 0;
                continue;
            } else {
                // Buffer briefly empty: bridge 1 jitter tick with clean PLC before fading out on genuine silence!
                if (consecutive_plc == 0) {
                    samples = opus_decode_float(dec, nullptr, 0, decBuf.data(), kFrame, 0);
                    next_play_seq++;
                    consecutive_plc = 1;
                    last_loss_ms = steadyMs();
                } else if (consecutive_plc == 1) {
                    samples = opus_decode_float(dec, nullptr, 0, decBuf.data(), kFrame, 0);
                    for (int i = 0; i < samples * 2; ++i) {
                        float fade = 1.0f - float(i) / float(std::max(1, samples * 2));
                        decBuf[size_t(i)] *= fade;
                    }
                    next_play_seq++;
                    consecutive_plc = 2;
                    playing = false;
                } else {
                    playing = false;
                    break;
                }
                if (samples <= 0) break;
            }

            if (samples > 0) {
                double energy = 0.0;
                for (int i = 0; i < samples * 2; ++i) energy += double(decBuf[size_t(i)]) * double(decBuf[size_t(i)]);
                float rms = float(std::sqrt(energy / double(samples * 2)));
                if (rms > 0.004f) last_loud_ms = steadyMs();
                pcm_fifo.insert(pcm_fifo.end(), decBuf.data(), decBuf.data() + samples * 2);
            } else {
                break;
            }
        }
    }
};

VoiceEngine::VoiceEngine(VoiceTransport* transport) : transport_(transport) {
    if (transport_) {
        transport_->setAudioRxCallback([this](uint32_t ssrc, uint16_t seq, const uint8_t* opus, int len) {
            onRemoteOpus(ssrc, seq, opus, len);
        });
    }
}

VoiceEngine::~VoiceEngine() {
    if (transport_) transport_->setAudioRxCallback(nullptr);
    stop();
}

int32_t VoiceEngine::listDevices(bool capture, FcDeviceInfo* out_list, int32_t max_count) {
    if (!out_list || max_count <= 0) return 0;
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    bool uninit = SUCCEEDED(hr);

    int32_t count = 0;
    ComPtr<IMMDeviceEnumerator> en;
    if (SUCCEEDED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&en)))) {
        EDataFlow flow = capture ? eCapture : eRender;
        std::wstring defId;
        ComPtr<IMMDevice> defDev;
        if (SUCCEEDED(en->GetDefaultAudioEndpoint(flow, eConsole, &defDev))) {
            LPWSTR pid = nullptr;
            if (SUCCEEDED(defDev->GetId(&pid)) && pid) {
                defId = pid;
                CoTaskMemFree(pid);
            }
        }
        ComPtr<IMMDeviceCollection> col;
        if (SUCCEEDED(en->EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, &col))) {
            UINT n = 0;
            col->GetCount(&n);
            for (UINT i = 0; i < n && count < max_count; ++i) {
                ComPtr<IMMDevice> d;
                if (FAILED(col->Item(i, &d))) continue;
                LPWSTR pid = nullptr;
                if (FAILED(d->GetId(&pid)) || !pid) continue;
                std::wstring wid = pid;
                CoTaskMemFree(pid);

                std::string name = "Audio Device";
                ComPtr<IPropertyStore> ps;
                if (SUCCEEDED(d->OpenPropertyStore(STGM_READ, &ps))) {
                    PROPVARIANT pv;
                    PropVariantInit(&pv);
                    if (SUCCEEDED(ps->GetValue(PKEY_Device_FriendlyName, &pv)) && pv.vt == VT_LPWSTR && pv.pwszVal) {
                        name = wideToUtf8(pv.pwszVal);
                    }
                    PropVariantClear(&pv);
                }
                std::string id = wideToUtf8(wid.c_str());
                std::memset(&out_list[count], 0, sizeof(FcDeviceInfo));
                std::strncpy(out_list[count].id, id.c_str(), sizeof(out_list[count].id) - 1);
                std::strncpy(out_list[count].name, name.c_str(), sizeof(out_list[count].name) - 1);
                out_list[count].is_default = (wid == defId) ? 1 : 0;
                count++;
            }
        }
    }
    if (uninit) CoUninitialize();
    return count;
}

bool VoiceEngine::start(const char* capture_device_id, const char* render_device_id) {
    std::lock_guard life(lifecycle_mtx_);
    stopLocked();
    cap_id_ = capture_device_id ? capture_device_id : "";
    ren_id_ = render_device_id ? render_device_id : "";
    suppressor_.reset();
    running_.store(true);
    cap_thread_ = std::thread([this] { captureLoop(); });
    ren_thread_ = std::thread([this] { renderLoop(); });
    return true;
}

void VoiceEngine::stop() {
    std::lock_guard life(lifecycle_mtx_);
    stopLocked();
}

void VoiceEngine::stopLocked() {
    running_.store(false);
    if (cap_thread_.joinable()) cap_thread_.join();
    if (ren_thread_.joinable()) ren_thread_.join();
    std::lock_guard lk(mtx_);
    streams_.clear();
    loopback_fifo_.clear();
    speaking_.store(false);
    suppressor_.clearTelemetry();
}

void VoiceEngine::setInputGain(float g) {
    if (!isFiniteFast(g)) g = 1.0f;
    input_gain_.store(std::clamp(g, 0.0f, 4.0f));
}

void VoiceEngine::setOutputVolume(float v) {
    if (!isFiniteFast(v)) v = 1.0f;
    output_vol_.store(std::clamp(v, 0.0f, 2.0f));
}

void VoiceEngine::setUserVolume(uint32_t ssrc, float vol) {
    if (!isFiniteFast(vol)) vol = 1.0f;
    std::lock_guard lk(mtx_);
    user_volumes_[ssrc] = std::clamp(vol, 0.0f, 2.0f);
}

void VoiceEngine::setNoiseConfig(int32_t mode, float strength, bool hpf, bool agc, float gate_db) {
    suppressor_.configure(mode, strength, hpf, agc, gate_db);
}

void VoiceEngine::onRemoteOpus(uint32_t ssrc, uint16_t seq, const uint8_t* opus, int len) {
    if (!opus || len <= 0) return;
    std::lock_guard lk(mtx_);
    auto& sp = streams_[ssrc];
    if (!sp) sp = std::make_unique<RemoteStream>();
    sp->pushPacket(seq, opus, len);
}

bool VoiceEngine::isSsrcSpeaking(uint32_t ssrc) const {
    int64_t now = steadyMs();
    std::lock_guard lk(mtx_);
    auto it = streams_.find(ssrc);
    if (it == streams_.end() || !it->second) return false;
    return (now - it->second->last_loud_ms) < 350;
}

void VoiceEngine::getStats(FcAudioStats* out) const {
    if (!out) return;
    std::memset(out, 0, sizeof(FcAudioStats));
    out->raw_rms = suppressor_.lastRawRms();
    out->clean_rms = suppressor_.lastCleanRms();
    out->noise_reduction_db = suppressor_.lastNoiseReductionDb();
    out->vad_prob = suppressor_.lastVadProb();
    out->speaking = speaking_.load() ? 1 : 0;
    out->active_mode = suppressor_.activeMode();
    out->tx_packets = transport_ ? transport_->txPackets() : 0;
    out->rx_packets = transport_ ? transport_->rxPackets() : 0;
    std::string bname = suppressor_.activeBackendName();
    std::strncpy(out->backend_name, bname.c_str(), sizeof(out->backend_name) - 1);
}

size_t VoiceEngine::pullMixedStereo(float* dst, size_t stereo_frames) {
    size_t samples = stereo_frames * 2;
    std::memset(dst, 0, sizeof(float) * samples);

    std::lock_guard lk(mtx_);
    // When deafened, actively drain all queued packets so undeafening NEVER blasts stale audio!
    if (deafened_.load()) {
        loopback_fifo_.clear();
        for (auto& [ssrc, st] : streams_) {
            st->jb.clear();
            st->pcm_fifo.clear();
            st->playing = false;
        }
        return samples;
    }

    float master = output_vol_.load();
    if (!loopback_fifo_.empty()) {
        size_t take = std::min(samples, loopback_fifo_.size());
        for (size_t i = 0; i < take; ++i) dst[i] += loopback_fifo_[i] * master;
        loopback_fifo_.erase(loopback_fifo_.begin(), loopback_fifo_.begin() + take);
    }

    int64_t now = steadyMs();
    for (auto& [ssrc, st] : streams_) {
        float uvol = master;
        auto itv = user_volumes_.find(ssrc);
        if (itv != user_volumes_.end()) uvol *= itv->second;

        st->decodeIntoFifo(samples);
        size_t take = std::min(samples, st->pcm_fifo.size());
        for (size_t i = 0; i < take; ++i) dst[i] += st->pcm_fifo[i] * uvol;
        if (take > 0) st->pcm_fifo.erase(st->pcm_fifo.begin(), st->pcm_fifo.begin() + take);
    }

    // Periodically evict long-idle remote Opus decoders (>30s inactive) to prevent unbounded memory growth!
    if (now - last_evict_ms_ >= 10000) {
        last_evict_ms_ = now;
        for (auto it = streams_.begin(); it != streams_.end(); ) {
            if (it->second && it->second->jb.empty() && it->second->pcm_fifo.empty() &&
                (now - it->second->last_rx_ms) > 30000) {
                it = streams_.erase(it);
            } else {
                ++it;
            }
        }
    }

    // Soft limiter + /fp:fast NaN guard on mixed master bus so loud speakers never hard-clip!
    for (size_t i = 0; i < samples; ++i) {
        float s = dst[i];
        if (!isFiniteFast(s)) s = 0.0f;
        else if (s > 0.95f) s = 0.95f + 0.049f * std::tanh((s - 0.95f) * 10.0f);
        else if (s < -0.95f) s = -0.95f + 0.049f * std::tanh((s + 0.95f) * 10.0f);
        dst[i] = s;
    }
    return samples;
}

void VoiceEngine::captureLoop() {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    HANDLE ev = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    HANDLE mm = boostRealtimeThread(L"Pro Audio");

    int err = 0;
    OpusEncoder* enc = opus_encoder_create(kRate, 2, OPUS_APPLICATION_VOIP, &err);
    if (!enc || err != OPUS_OK) {
        if (enc) opus_encoder_destroy(enc);
        revertRealtimeThread(mm);
        CloseHandle(ev);
        CoUninitialize();
        return;
    }
    opus_encoder_ctl(enc, OPUS_SET_BITRATE(128000));
    opus_encoder_ctl(enc, OPUS_SET_INBAND_FEC(1));
    opus_encoder_ctl(enc, OPUS_SET_PACKET_LOSS_PERC(5));
    opus_encoder_ctl(enc, OPUS_SET_DTX(0));
    opus_encoder_ctl(enc, OPUS_SET_COMPLEXITY(8));
    opus_encoder_ctl(enc, OPUS_SET_SIGNAL(OPUS_SIGNAL_VOICE));

    std::vector<float> acc;
    acc.reserve(kFrame * 8);
    float stereoFrame[kFrame * 2];
    uint32_t rtpTs = 0;
    bool prevSpeaking = false;
    bool wasMuted = false;

    // Outer device-recovery loop: automatically survives USB mic hot-unplug / Bluetooth switches!
    while (running_.load()) {
        ComPtr<IAudioClient> client;
        ComPtr<IAudioCaptureClient> cap;
        if (!openAudioClient(eCapture, cap_id_, 1, client, ev) || FAILED(client->GetService(IID_PPV_ARGS(&cap)))) {
            std::this_thread::sleep_for(std::chrono::milliseconds(300));
            continue;
        }
        acc.clear();
        if (FAILED(client->Start())) {
            std::this_thread::sleep_for(std::chrono::milliseconds(300));
            continue;
        }

        while (running_.load()) {
            WaitForSingleObject(ev, 50);
            UINT32 pktSize = 0;
            HRESULT hr = cap->GetNextPacketSize(&pktSize);
            if (isDeviceLostHr(hr)) break;
            if (FAILED(hr)) continue;

            bool deviceLost = false;
            while (pktSize > 0) {
                BYTE* data = nullptr;
                UINT32 frames = 0;
                DWORD flags = 0;
                hr = cap->GetBuffer(&data, &frames, &flags, nullptr, nullptr);
                if (isDeviceLostHr(hr)) { deviceLost = true; break; }
                if (FAILED(hr)) break;

                if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) || !data) {
                    acc.insert(acc.end(), frames, 0.0f);
                } else {
                    const float* f = reinterpret_cast<const float*>(data);
                    float gain = input_gain_.load();
                    size_t oldSz = acc.size();
                    acc.resize(oldSz + frames);
                    for (UINT32 i = 0; i < frames; ++i) acc[oldSz + i] = f[i] * gain;
                }
                cap->ReleaseBuffer(frames);
                hr = cap->GetNextPacketSize(&pktSize);
                if (isDeviceLostHr(hr)) { deviceLost = true; break; }
                if (FAILED(hr)) break;
            }
            if (deviceLost) break;

            size_t off = 0;
            while (acc.size() - off >= size_t(kFrame)) {
                float frame[kFrame];
                std::memcpy(frame, acc.data() + off, sizeof(frame));
                off += kFrame;

                bool nowMuted = muted_.load();
                if (nowMuted) {
                    std::memset(frame, 0, sizeof(frame));
                    speaking_.store(false);
                    suppressor_.clearTelemetry();
                } else {
                    if (wasMuted) suppressor_.reset();
                    suppressor_.process(frame, kFrame);
                    speaking_.store(suppressor_.isVoiceActive());
                }
                wasMuted = nowMuted;

                if (loopback_.load() && !deafened_.load()) {
                    std::lock_guard lk(mtx_);
                    if (loopback_fifo_.size() < size_t(kFrame * 6)) {
                        for (int i = 0; i < kFrame; ++i) {
                            loopback_fifo_.push_back(frame[i]);
                            loopback_fifo_.push_back(frame[i]);
                        }
                    }
                }

                bool nowSpeaking = !muted_.load() && speaking_.load();
                if (nowSpeaking && transport_ && transport_->isReady()) {
                    for (int i = 0; i < kFrame; ++i) {
                        stereoFrame[i * 2] = frame[i];
                        stereoFrame[i * 2 + 1] = frame[i];
                    }
                    uint8_t opusOut[1200];
                    int n = opus_encode_float(enc, stereoFrame, kFrame, opusOut, sizeof(opusOut));
                    if (n > 2) {
                        transport_->sendOpus(opusOut, n, rtpTs);
                    }
                } else if (prevSpeaking && !nowSpeaking && transport_ && transport_->isReady()) {
                    transport_->sendSilenceFrames();
                    rtpTs += 5 * kFrame; // Keep monotonic alignment with sendSilenceFrames()!
                }
                prevSpeaking = nowSpeaking;
                rtpTs += kFrame;
            }
            if (off > 0) acc.erase(acc.begin(), acc.begin() + off);
        }

        client->Stop();
        cap.Reset();
        client.Reset();
        if (running_.load()) std::this_thread::sleep_for(std::chrono::milliseconds(200));
    }

    opus_encoder_destroy(enc);
    revertRealtimeThread(mm);
    CloseHandle(ev);
    CoUninitialize();
}

void VoiceEngine::renderLoop() {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    HANDLE ev = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    HANDLE mm = boostRealtimeThread(L"Pro Audio");

    // Outer device-recovery loop: automatically survives DAC/headset hot-unplug!
    while (running_.load()) {
        ComPtr<IAudioClient> client;
        ComPtr<IAudioRenderClient> ren;
        if (!openAudioClient(eRender, ren_id_, 2, client, ev) || FAILED(client->GetService(IID_PPV_ARGS(&ren)))) {
            std::this_thread::sleep_for(std::chrono::milliseconds(300));
            continue;
        }

        UINT32 bufFrames = 0;
        if (FAILED(client->GetBufferSize(&bufFrames)) || bufFrames == 0) {
            std::this_thread::sleep_for(std::chrono::milliseconds(300));
            continue;
        }

        // Pre-fill entire render buffer with silence BEFORE Start() per MSDN event-driven WASAPI requirement
        BYTE* initDst = nullptr;
        if (SUCCEEDED(ren->GetBuffer(bufFrames, &initDst))) {
            ren->ReleaseBuffer(bufFrames, AUDCLNT_BUFFERFLAGS_SILENT);
        }
        if (FAILED(client->Start())) {
            std::this_thread::sleep_for(std::chrono::milliseconds(300));
            continue;
        }

        while (running_.load()) {
            WaitForSingleObject(ev, 50);
            UINT32 pad = 0;
            HRESULT hr = client->GetCurrentPadding(&pad);
            if (isDeviceLostHr(hr)) break;
            if (FAILED(hr)) continue;

            UINT32 avail = bufFrames > pad ? bufFrames - pad : 0;
            if (avail == 0) continue;

            BYTE* dst = nullptr;
            hr = ren->GetBuffer(avail, &dst);
            if (isDeviceLostHr(hr)) break;
            if (FAILED(hr) || !dst) continue;

            pullMixedStereo(reinterpret_cast<float*>(dst), avail);
            hr = ren->ReleaseBuffer(avail, 0);
            if (isDeviceLostHr(hr)) break;
        }

        client->Stop();
        ren.Reset();
        client.Reset();
        if (running_.load()) std::this_thread::sleep_for(std::chrono::milliseconds(200));
    }

    revertRealtimeThread(mm);
    CloseHandle(ev);
    CoUninitialize();
}

} // namespace fc
