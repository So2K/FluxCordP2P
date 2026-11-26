#pragma once
#include "FluxCordMedia.h"
#include "NoiseSuppressor.h"
#include "VoiceTransport.h"
#include <atomic>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace fc {

class VoiceEngine {
public:
    explicit VoiceEngine(VoiceTransport* transport);
    ~VoiceEngine();

    static int32_t listDevices(bool capture, FcDeviceInfo* out_list, int32_t max_count);

    bool start(const char* capture_device_id, const char* render_device_id);
    void stop();

    void setMuted(bool m) { muted_.store(m); }
    void setDeafened(bool d) { deafened_.store(d); }
    void setLoopback(bool lb) { loopback_.store(lb); }
    void setInputGain(float g);
    void setOutputVolume(float v);
    void setUserVolume(uint32_t ssrc, float vol);
    void setNoiseConfig(int32_t mode, float strength, bool hpf, bool agc, float gate_db);

    void onRemoteOpus(uint32_t ssrc, uint16_t seq, const uint8_t* opus, int len);
    bool isSsrcSpeaking(uint32_t ssrc) const;
    void getStats(FcAudioStats* out) const;
    int32_t availableBackends() const { return suppressor_.availableBackendsMask(); }

    // Exposed for deterministic unit tests (jitter buffer + PLC verification)
    size_t pullMixedStereo(float* dst, size_t stereo_frames);

private:
    struct RemoteStream;
    void stopLocked();
    void captureLoop();
    void renderLoop();

    VoiceTransport* transport_;
    NoiseSuppressor suppressor_;

    std::mutex lifecycle_mtx_;
    std::string cap_id_;
    std::string ren_id_;

    std::atomic<bool> running_{false};
    std::atomic<bool> muted_{false};
    std::atomic<bool> deafened_{false};
    std::atomic<bool> loopback_{false};
    std::atomic<bool> speaking_{false};
    std::atomic<float> input_gain_{1.0f};
    std::atomic<float> output_vol_{1.0f};

    std::thread cap_thread_;
    std::thread ren_thread_;

    mutable std::mutex mtx_;
    std::map<uint32_t, std::unique_ptr<RemoteStream>> streams_;
    std::map<uint32_t, float> user_volumes_;
    std::vector<float> loopback_fifo_;
    int64_t last_evict_ms_ = 0;
};

} // namespace fc
