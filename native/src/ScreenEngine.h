#pragma once
#include "FluxCordMedia.h"
#include "VoiceTransport.h"
#include <atomic>
#include <mutex>
#include <string>
#include <thread>
#include <windows.h>

namespace fc {

class ScreenEngine {
public:
    explicit ScreenEngine(VoiceTransport* transport);
    ~ScreenEngine();

    static int32_t listMonitors(FcMonitorInfo* out_list, int32_t max_count);

    bool start(int32_t monitor_index, int32_t target_w, int32_t target_h, int32_t fps,
               int32_t bitrate_kbps, int32_t codec, bool loopback_preview, HWND preview_hwnd,
               HWND target_hwnd = nullptr, bool capture_system_audio = false, const char* render_device_id = nullptr);
    void setVideoSsrc(uint32_t ssrc) { video_ssrc_.store(ssrc); requestKeyframe(); }
    void requestKeyframe() { force_keyframe_.store(true); }
    bool isCapturingAudio() const { return running_.load() && capture_audio_; }
    void stop();
    void resizePreview(int32_t width, int32_t height);
    void getStats(FcScreenStats* out) const;

private:
    void stopLocked();
    void captureEncodeLoop();
    void systemAudioLoop();

    VoiceTransport* transport_;
    std::mutex lifecycle_mtx_;
    std::atomic<bool> running_{false};
    std::atomic<uint32_t> video_ssrc_{0};
    std::atomic<bool> force_keyframe_{true};
    HANDLE worker_done_ev_ = nullptr;
    std::thread worker_;
    std::thread audio_worker_;

    int32_t monitor_idx_ = 0;
    int32_t target_w_ = 3840;
    int32_t target_h_ = 2160;
    int32_t target_fps_ = 60;
    int32_t bitrate_kbps_ = 10000;
    int32_t codec_ = FC_CODEC_H264;
    bool loopback_preview_ = true;
    bool capture_audio_ = false;
    std::string render_device_id_;
    HWND preview_hwnd_ = nullptr;
    HWND target_hwnd_ = nullptr;

    std::atomic<int32_t> pending_w_{0};
    std::atomic<int32_t> pending_h_{0};

    mutable std::mutex stats_mtx_;
    FcScreenStats stats_{};
};

} // namespace fc
