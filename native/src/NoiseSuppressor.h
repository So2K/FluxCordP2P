#pragma once
#include "FluxCordMedia.h"
#include <atomic>
#include <string>
#include <vector>
#include <windows.h>

namespace fc {

class NoiseSuppressor {
public:
    NoiseSuppressor();
    ~NoiseSuppressor();

    void configure(int32_t mode, float strength, bool hpf, bool agc, float gate_db);
    void reset();
    void clearTelemetry();
    // Process arbitrary frame size (480 or 960 mono float32 samples @ 48 kHz) in-place with zero boundary discontinuities
    void process(float* pcm, int32_t count);

    int32_t availableBackendsMask() const;
    int32_t activeMode() const { return active_mode_.load(); }
    float lastRawRms() const { return raw_rms_.load(); }
    float lastCleanRms() const { return clean_rms_.load(); }
    float lastNoiseReductionDb() const { return nr_db_.load(); }
    float lastVadProb() const { return vad_prob_.load(); }
    bool isVoiceActive() const { return voice_active_.load(); }
    std::string activeBackendName() const;

private:
    void probeExternalPlugins();
    void fft512(float* re, float* im, bool inverse) const;
    void processSpectralMmse(float* pcm, int32_t count, float strength, bool keyboard_killer);
    void processStudioDsp(float* pcm, int32_t count);

    std::atomic<int32_t> req_mode_{FC_NOISE_AUTO_BEST};
    std::atomic<int32_t> active_mode_{FC_NOISE_DEEPFILTERNET3};
    std::atomic<float> strength_{0.75f};
    std::atomic<bool> hpf_{true};
    std::atomic<bool> agc_{true};
    std::atomic<float> gate_db_{-50.0f};

    std::atomic<float> raw_rms_{0.0f};
    std::atomic<float> clean_rms_{0.0f};
    std::atomic<float> nr_db_{0.0f};
    std::atomic<float> vad_prob_{0.0f};
    std::atomic<bool> voice_active_{false};

    HMODULE h_rnnoise_ = nullptr;
    using RnnoiseCreateFn = void* (*)(void*);
    using RnnoiseDestroyFn = void (*)(void*);
    using RnnoiseProcessFn = float (*)(void*, float*, const float*);
    RnnoiseCreateFn rnnoise_create_ = nullptr;
    RnnoiseDestroyFn rnnoise_destroy_ = nullptr;
    RnnoiseProcessFn rnnoise_process_ = nullptr;
    void* rnnoise_state_ = nullptr;
    int32_t prev_proc_mode_ = -1;

    // 80 Hz 2nd-order Butterworth HPF state (48 kHz)
    float hpf_x1_ = 0, hpf_x2_ = 0, hpf_y1_ = 0, hpf_y2_ = 0;

    // Exact COLA STFT 512-pt overlap-add (hop = 256, periodic Hann analysis window -> sum == 1.000000!)
    static constexpr int kFft = 512;
    static constexpr int kHop = 256;
    static constexpr int kBins = kFft / 2 + 1;

    float win_[kFft]{};
    float tw_cos_[kFft / 2]{};
    float tw_sin_[kFft / 2]{};
    float in_ring_[kFft]{};
    float out_ola_[kFft]{};
    int ring_fill_ = kHop; // starts half-filled with zeros for exact 256-sample COLA alignment
    std::vector<float> out_fifo_;

    float noise_psd_[kBins]{};
    float min_psd_[kBins]{};
    float prev_post_snr_[kBins]{};
    float prev_gain_[kBins]{};
    float smooth_gain_[kBins]{};
    float prev_mag_[kBins]{};
    bool noise_init_ = false;
    int init_frames_ = 0;
    int min_track_frames_ = 0;

    // Smooth AGC & Gate state
    float agc_env_ = 0.08f;
    float agc_gain_ = 1.0f;
    float gate_gain_ = 1.0f;
    int gate_hang_ = 0;
};

} // namespace fc
