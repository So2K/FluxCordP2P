#include "NoiseSuppressor.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>

namespace fc {

namespace {
constexpr float kPi = 3.14159265358979323846f;

// Safe bit-level finite check that works even under /fp:fast!
inline bool isFiniteFast(float x) {
    uint32_t bits = 0;
    std::memcpy(&bits, &x, sizeof(bits));
    return (bits & 0x7F800000u) != 0x7F800000u;
}

float calcRms(const float* p, int n) {
    if (n <= 0) return 0.0f;
    double s = 0.0;
    for (int i = 0; i < n; ++i) s += double(p[i]) * double(p[i]);
    return float(std::sqrt(s / double(n)));
}

HMODULE tryLoadFromAppDir(const wchar_t* relSubpath) {
    wchar_t modPath[MAX_PATH]{};
    HMODULE self = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(&tryLoadFromAppDir), &self)) {
        return nullptr;
    }
    DWORD n = GetModuleFileNameW(self, modPath, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return nullptr;
    std::wstring dir(modPath, n);
    size_t slash = dir.find_last_of(L"\\/");
    if (slash == std::wstring::npos) return nullptr;
    std::wstring full = dir.substr(0, slash + 1) + relSubpath;
    return LoadLibraryExW(full.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
}
} // namespace

NoiseSuppressor::NoiseSuppressor() {
    // Periodic Hann window: w[n] + w[n + 256] == 1.000000 EXACTLY (zero COLA ripple!)
    for (int i = 0; i < kFft; ++i) {
        win_[i] = 0.5f * (1.0f - std::cos(2.0f * kPi * float(i) / float(kFft)));
    }
    // Precompute 512-pt radix-2 twiddle table once (zero cos/sin calls on audio thread!)
    for (int k = 0; k < kFft / 2; ++k) {
        float ang = -2.0f * kPi * float(k) / float(kFft);
        tw_cos_[k] = std::cos(ang);
        tw_sin_[k] = std::sin(ang);
    }
    out_fifo_.reserve(4096);
    reset();
    probeExternalPlugins();
}

NoiseSuppressor::~NoiseSuppressor() {
    if (rnnoise_state_ && rnnoise_destroy_) rnnoise_destroy_(rnnoise_state_);
    if (h_rnnoise_) FreeLibrary(h_rnnoise_);
}

void NoiseSuppressor::fft512(float* re, float* im, bool inverse) const {
    constexpr int N = kFft;
    for (int i = 1, j = 0; i < N; ++i) {
        int bit = N >> 1;
        for (; j & bit; bit >>= 1) j ^= bit;
        j ^= bit;
        if (i < j) {
            std::swap(re[i], re[j]);
            std::swap(im[i], im[j]);
        }
    }
    const float invSign = inverse ? -1.0f : 1.0f;
    for (int len = 2; len <= N; len <<= 1) {
        int half = len >> 1;
        int step = N / len;
        for (int i = 0; i < N; i += len) {
            for (int j = 0; j < half; ++j) {
                int twIdx = j * step;
                float wr = tw_cos_[twIdx];
                float wi = tw_sin_[twIdx] * invSign;
                int u = i + j;
                int v = u + half;
                float vr = re[v] * wr - im[v] * wi;
                float vi = re[v] * wi + im[v] * wr;
                re[v] = re[u] - vr;
                im[v] = im[u] - vi;
                re[u] += vr;
                im[u] += vi;
            }
        }
    }
    if (inverse) {
        constexpr float invN = 1.0f / float(N);
        for (int i = 0; i < N; ++i) {
            re[i] *= invN;
            im[i] *= invN;
        }
    }
}

void NoiseSuppressor::reset() {
    hpf_x1_ = hpf_x2_ = hpf_y1_ = hpf_y2_ = 0.0f;
    if (rnnoise_state_ && rnnoise_destroy_ && rnnoise_create_) {
        rnnoise_destroy_(rnnoise_state_);
        rnnoise_state_ = rnnoise_create_(nullptr);
    }
    std::memset(in_ring_, 0, sizeof(in_ring_));
    std::memset(out_ola_, 0, sizeof(out_ola_));
    ring_fill_ = kHop; // 256-sample prefill so first 256 inputs trigger frame 0 immediately
    // Pre-seed 256 zeros (5.33 ms constant lookahead latency) so ANY block size (480, 960, etc.) NEVER underflows!
    out_fifo_.assign(kHop, 0.0f);
    for (int k = 0; k < kBins; ++k) {
        noise_psd_[k] = 1e-6f;
        min_psd_[k] = 1e3f;
        prev_post_snr_[k] = 1.0f;
        prev_gain_[k] = 1.0f;
        smooth_gain_[k] = 1.0f;
        prev_mag_[k] = 0.0f;
    }
    noise_init_ = false;
    init_frames_ = 0;
    min_track_frames_ = 0;
    agc_env_ = 0.08f;
    agc_gain_ = 1.0f;
    gate_gain_ = 1.0f;
    gate_hang_ = 0;
}

void NoiseSuppressor::clearTelemetry() {
    raw_rms_.store(0.0f);
    clean_rms_.store(0.0f);
    nr_db_.store(0.0f);
    vad_prob_.store(0.0f);
    voice_active_.store(false);
}

void NoiseSuppressor::probeExternalPlugins() {
    // Only load verified external RNNoise DLL from FluxCord's own directory (prevents DLL search-order hijacking
    // and avoids fake-reporting unimplemented DFNet3/NvAFX DLLs).
    static const wchar_t* kRnnoiseRel[] = {
        L"plugins\\audio\\rnnoise.dll",
        L"rnnoise.dll",
        L"librnnoise.dll"
    };
    for (const wchar_t* rel : kRnnoiseRel) {
        h_rnnoise_ = tryLoadFromAppDir(rel);
        if (h_rnnoise_) break;
    }
    if (h_rnnoise_) {
        rnnoise_create_ = reinterpret_cast<RnnoiseCreateFn>(GetProcAddress(h_rnnoise_, "rnnoise_create"));
        rnnoise_destroy_ = reinterpret_cast<RnnoiseDestroyFn>(GetProcAddress(h_rnnoise_, "rnnoise_destroy"));
        rnnoise_process_ = reinterpret_cast<RnnoiseProcessFn>(GetProcAddress(h_rnnoise_, "rnnoise_process_frame"));
        if (rnnoise_create_ && rnnoise_destroy_ && rnnoise_process_) {
            rnnoise_state_ = rnnoise_create_(nullptr);
        }
        if (!rnnoise_state_) {
            FreeLibrary(h_rnnoise_);
            h_rnnoise_ = nullptr;
            rnnoise_create_ = nullptr;
            rnnoise_destroy_ = nullptr;
            rnnoise_process_ = nullptr;
        }
    }
}

int32_t NoiseSuppressor::availableBackendsMask() const {
    int32_t mask = 1; // Built-in COLA MMSE-LSA + KeyKill + Studio DSP always available
    if (rnnoise_state_) mask |= 2;
    return mask;
}

std::string NoiseSuppressor::activeBackendName() const {
    int32_t m = active_mode_.load();
    switch (m) {
        case FC_NOISE_OFF: return "Off (Bypass)";
        case FC_NOISE_STUDIO_DSP: return "Studio DSP (HPF+Gate+AGC)";
        case FC_NOISE_RNNOISE: return rnnoise_state_ ? "RNNoise Neural (DLL)" : "RNNoise-Lite COLA (48kHz)";
        case FC_NOISE_DEEPFILTERNET3: return "DeepFilter-COLA MMSE + KeyKill";
        case FC_NOISE_NVIDIA_BROADCAST: return "DeepFilter-COLA MMSE + KeyKill";
        default: return "Auto (COLA MMSE + Studio DSP)";
    }
}

void NoiseSuppressor::configure(int32_t mode, float strength, bool hpf, bool agc, float gate_db) {
    req_mode_.store(mode);
    strength_.store(std::clamp(strength, 0.0f, 1.0f));
    hpf_.store(hpf);
    agc_.store(agc);
    gate_db_.store(std::clamp(gate_db, -80.0f, -10.0f));
}

void NoiseSuppressor::process(float* pcm, int32_t count) {
    if (!pcm || count <= 0) return;

    // Sanitize incoming PCM against NaN/Inf from buggy virtual audio drivers so IIR/AGC state can NEVER poison!
    for (int i = 0; i < count; ++i) {
        if (!isFiniteFast(pcm[i])) pcm[i] = 0.0f;
        else pcm[i] = std::clamp(pcm[i], -4.0f, 4.0f);
    }

    float raw = calcRms(pcm, count);
    raw_rms_.store(raw);

    int32_t mode = req_mode_.load();
    if (mode == FC_NOISE_AUTO_BEST) {
        mode = rnnoise_state_ ? FC_NOISE_RNNOISE : FC_NOISE_DEEPFILTERNET3;
    }
    if (mode != prev_proc_mode_) {
        prev_proc_mode_ = mode;
        reset();
    }
    active_mode_.store(mode);

    if (mode == FC_NOISE_OFF) {
        clean_rms_.store(raw);
        nr_db_.store(0.0f);
        float rawDb = 20.0f * std::log10(std::max(raw, 1e-6f));
        bool act = rawDb > gate_db_.load();
        if (act) gate_hang_ = 25;
        else if (gate_hang_ > 0) gate_hang_--;
        voice_active_.store(gate_hang_ > 0);
        vad_prob_.store(act ? 1.0f : 0.0f);
        return;
    }

    float str = strength_.load();

    // 1. High-Pass Filter 80 Hz (2nd-order Butterworth @ 48 kHz)
    if (hpf_.load()) {
        constexpr float b0 = 0.9926236f, b1 = -1.9852472f, b2 = 0.9926236f;
        constexpr float a1 = -1.9851927f, a2 = 0.9853017f;
        for (int i = 0; i < count; ++i) {
            float x = pcm[i];
            float y = b0 * x + b1 * hpf_x1_ + b2 * hpf_x2_ - a1 * hpf_y1_ - a2 * hpf_y2_;
            if (!isFiniteFast(y)) { y = 0.0f; hpf_x1_ = hpf_x2_ = hpf_y1_ = hpf_y2_ = 0.0f; }
            hpf_x2_ = hpf_x1_; hpf_x1_ = x;
            hpf_y2_ = hpf_y1_; hpf_y1_ = y;
            pcm[i] = y;
        }
    }

    // 2. Neural / COLA Spectral Denoiser
    if (mode == FC_NOISE_RNNOISE && rnnoise_state_ && rnnoise_process_) {
        float tmp[480];
        float vadSum = 0.0f;
        int frames = 0;
        for (int off = 0; off + 480 <= count; off += 480) {
            for (int i = 0; i < 480; ++i) tmp[i] = pcm[off + i] * 32768.0f;
            vadSum += rnnoise_process_(rnnoise_state_, tmp, tmp);
            // Output pure wet signal (avoids 10ms dry/wet comb filtering!)
            for (int i = 0; i < 480; ++i) {
                float wet = tmp[i] * (1.0f / 32768.0f);
                pcm[off + i] = isFiniteFast(wet) ? wet : 0.0f;
            }
            frames++;
        }
        if (frames > 0) vad_prob_.store(vadSum / float(frames));
    } else if (mode == FC_NOISE_DEEPFILTERNET3 || mode == FC_NOISE_NVIDIA_BROADCAST) {
        processSpectralMmse(pcm, count, str, true);
    } else if (mode == FC_NOISE_RNNOISE) {
        processSpectralMmse(pcm, count, str * 0.75f, false);
    } else {
        float rmsDb = 20.0f * std::log10(std::max(calcRms(pcm, count), 1e-6f));
        vad_prob_.store(std::clamp((rmsDb - gate_db_.load()) / 15.0f, 0.0f, 1.0f));
    }

    // 3. Studio DSP: Single Smooth Noise Gate (500ms hang) + Gentle AGC + Brickwall Limiter
    processStudioDsp(pcm, count);

    float clean = calcRms(pcm, count);
    clean_rms_.store(clean);
    float nr = (raw > 1e-6f && clean > 1e-7f)
        ? std::max(0.0f, 20.0f * std::log10(raw / clean))
        : (raw > 1e-6f ? 30.0f : 0.0f);
    nr_db_.store(nr);
}

void NoiseSuppressor::processSpectralMmse(float* pcm, int32_t count, float strength, bool keyboard_killer) {
    float re[kFft], im[kFft];
    float targetG[kBins];
    float vadAcc = 0.0f;
    int hops = 0;
    constexpr float kPowerNorm = 1.0f / float(kFft); // Normalize unscaled FFT power so loud ambient mics never saturate cap!

    for (int i = 0; i < count; ++i) {
        in_ring_[ring_fill_++] = pcm[i];
        if (ring_fill_ == kFft) {
            for (int n = 0; n < kFft; ++n) {
                re[n] = in_ring_[n] * win_[n];
                im[n] = 0.0f;
            }
            fft512(re, im, false);

            float speechSnrSum = 0.0f;
            float highFlux = 0.0f;
            float speechFlux = 0.0f;
            min_track_frames_++;
            for (int k = 1; k < kBins; ++k) {
                float mag2 = (re[k] * re[k] + im[k] * im[k]) * kPowerNorm + 1e-12f;
                float mag = std::sqrt(mag2);
                if (mag2 < min_psd_[k]) min_psd_[k] = mag2;
                if (!noise_init_) {
                    // With kPowerNorm = 1/512, 1e-4f ~= -40 dBFS per bin: tracks loud ambient hiss without learning voice!
                    float capped = std::min(mag2, 1e-4f);
                    noise_psd_[k] = (noise_psd_[k] * float(init_frames_) + capped) / float(init_frames_ + 1);
                }
                float post = mag2 / std::max(noise_psd_[k], 1e-12f);
                if (k >= 2 && k <= 45) {
                    speechSnrSum += std::min(post, 25.0f);
                    speechFlux += std::max(0.0f, mag - prev_mag_[k]);
                } else if (k > 60) {
                    highFlux += std::max(0.0f, mag - prev_mag_[k]);
                }
            }
            if (!noise_init_) {
                init_frames_++;
                if (init_frames_ >= 8) noise_init_ = true;
            }

            // Minimum-statistics periodic anchor (~1.5s = 280 hops) prevents loud ambient noise from locking vad=1.0 forever!
            if (min_track_frames_ >= 280) {
                for (int k = 1; k < kBins; ++k) {
                    if (min_psd_[k] < 100.0f) {
                        noise_psd_[k] = 0.70f * noise_psd_[k] + 0.30f * (min_psd_[k] * 1.15f);
                    }
                    min_psd_[k] = 1e3f;
                }
                min_track_frames_ = 0;
            }

            float avgSpeechSnr = speechSnrSum / 44.0f;
            float vad = std::clamp((avgSpeechSnr - 1.3f) / 3.0f, 0.0f, 1.0f);
            bool isClick = keyboard_killer && (highFlux > speechFlux * 1.8f) && (vad < 0.35f);
            if (isClick) vad *= 0.25f;
            vadAcc += vad;
            hops++;

            float gMin = std::max(0.08f, 1.0f - strength * 0.90f);
            constexpr float alphaDd = 0.96f;

            for (int k = 0; k < kBins; ++k) {
                float mag2 = (re[k] * re[k] + im[k] * im[k]) * kPowerNorm + 1e-12f;
                float mag = std::sqrt(mag2);

                if (mag2 < noise_psd_[k]) {
                    noise_psd_[k] = 0.88f * noise_psd_[k] + 0.12f * mag2;
                } else if (vad < 0.20f) {
                    noise_psd_[k] = 0.985f * noise_psd_[k] + 0.015f * mag2;
                }

                float postSnr = mag2 / std::max(noise_psd_[k], 1e-12f);
                float priorSnr = alphaDd * (prev_gain_[k] * prev_gain_[k] * prev_post_snr_[k])
                               + (1.0f - alphaDd) * std::max(postSnr - 1.0f, 0.0f);

                float wiener = priorSnr / (1.0f + priorSnr);
                float extraAtten = (isClick && k > 50) ? 0.20f : 1.0f;
                float g = std::clamp(wiener * extraAtten, gMin, 1.0f);
                targetG[k] = (1.0f - strength) + strength * g;

                prev_post_snr_[k] = postSnr;
                prev_gain_[k] = g;
                prev_mag_[k] = mag;
            }

            for (int k = 0; k < kBins; ++k) {
                float fSmooth = (k > 0 && k < kBins - 1)
                    ? (0.20f * targetG[k - 1] + 0.60f * targetG[k] + 0.20f * targetG[k + 1])
                    : targetG[k];
                float alphaT = (fSmooth > smooth_gain_[k]) ? 0.65f : 0.28f;
                smooth_gain_[k] = smooth_gain_[k] * (1.0f - alphaT) + fSmooth * alphaT;

                float g = smooth_gain_[k];
                re[k] *= g;
                im[k] *= g;
                if (k > 0 && k < kBins - 1) {
                    re[kFft - k] *= g;
                    im[kFft - k] *= g;
                }
            }
            if (hpf_.load()) {
                re[0] = 0.0f;
                im[0] = 0.0f;
            }

            fft512(re, im, true);
            for (int n = 0; n < kFft; ++n) {
                out_ola_[n] += re[n];
            }
            out_fifo_.insert(out_fifo_.end(), out_ola_, out_ola_ + kHop);
            std::memmove(out_ola_, out_ola_ + kHop, sizeof(float) * kHop);
            std::memset(out_ola_ + kHop, 0, sizeof(float) * kHop);

            std::memmove(in_ring_, in_ring_ + kHop, sizeof(float) * kHop);
            ring_fill_ = kHop;
        }
    }

    if (hops > 0) vad_prob_.store(vadAcc / float(hops));

    if (int32_t(out_fifo_.size()) >= count) {
        std::memcpy(pcm, out_fifo_.data(), sizeof(float) * size_t(count));
        out_fifo_.erase(out_fifo_.begin(), out_fifo_.begin() + count);
    }
}

void NoiseSuppressor::processStudioDsp(float* pcm, int32_t count) {
    float rms = calcRms(pcm, count);
    float rmsDb = 20.0f * std::log10(std::max(rms, 1e-6f));
    float gateThr = gate_db_.load();

    bool open = (rmsDb > gateThr) || (vad_prob_.load() > 0.45f && rmsDb > gateThr - 6.0f);
    if (open) gate_hang_ = 25;
    else if (gate_hang_ > 0) gate_hang_--;

    voice_active_.store(gate_hang_ > 0);
    float targetGate = (gate_hang_ > 0) ? 1.0f : 0.0f;

    float targetAgc = agc_gain_;
    if (agc_.load() && gate_hang_ > 0 && rms > 0.006f) {
        agc_env_ = agc_env_ * 0.94f + rms * 0.06f;
        float desired = std::clamp(0.125f / std::max(agc_env_, 0.015f), 0.6f, 2.5f);
        targetAgc = agc_gain_ * 0.92f + desired * 0.08f;
    } else if (!agc_.load()) {
        targetAgc = 1.0f;
    }

    for (int i = 0; i < count; ++i) {
        gate_gain_ += (targetGate - gate_gain_) * (targetGate > gate_gain_ ? 0.012f : 0.0015f);
        agc_gain_ += (targetAgc - agc_gain_) * 0.001f;
        float s = pcm[i] * gate_gain_ * agc_gain_;
        if (!isFiniteFast(s)) s = 0.0f;
        else if (s > 0.94f) s = 0.94f + 0.05f * std::tanh((s - 0.94f) * 10.0f);
        else if (s < -0.94f) s = -0.94f + 0.05f * std::tanh((s + 0.94f) * 10.0f);
        pcm[i] = s;
    }
}

} // namespace fc
