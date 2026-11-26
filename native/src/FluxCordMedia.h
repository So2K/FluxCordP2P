#pragma once
#include <stdint.h>

#ifdef FLUXCORD_MEDIA_EXPORTS
#define FC_API __declspec(dllexport)
#else
#define FC_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

enum FcNoiseMode : int32_t {
    FC_NOISE_OFF = 0,
    FC_NOISE_STUDIO_DSP = 1,
    FC_NOISE_RNNOISE = 2,
    FC_NOISE_DEEPFILTERNET3 = 3,
    FC_NOISE_NVIDIA_BROADCAST = 4,
    FC_NOISE_AUTO_BEST = 5
};

enum FcVideoCodec : int32_t {
    FC_CODEC_H264 = 0,
    FC_CODEC_HEVC = 1,
    FC_CODEC_AV1 = 2
};

struct FcAudioStats {
    float raw_rms;
    float clean_rms;
    float noise_reduction_db;
    float vad_prob;
    int32_t speaking;
    int32_t active_mode;
    int32_t tx_packets;
    int32_t rx_packets;
    char backend_name[64];
};

struct FcScreenStats {
    int32_t width;
    int32_t height;
    float fps;
    float bitrate_kbps;
    float encode_ms;
    float decode_ms;
    int32_t zero_copy;
    int32_t running;
    int64_t frames_encoded;
    int64_t frames_skipped_static;
    char encoder_name[64];
};

struct FcDeviceInfo {
    char id[128];
    char name[128];
    int32_t is_default;
};

struct FcMonitorInfo {
    int32_t index;
    int32_t width;
    int32_t height;
    int32_t refresh_hz;
    char name[128];
};

struct FcWindowInfo {
    uint64_t hwnd;
    int32_t width;
    int32_t height;
    char title[128];
};

FC_API int32_t fc_init();
FC_API void fc_shutdown();

// Audio enumeration & voice engine
FC_API int32_t fc_list_audio_devices(int32_t capture, FcDeviceInfo* out_list, int32_t max_count);
FC_API int32_t fc_voice_start(const char* capture_device_id, const char* render_device_id);
FC_API void fc_voice_stop();
FC_API void fc_voice_set_muted(int32_t muted);
FC_API void fc_voice_set_deafened(int32_t deafened);
FC_API void fc_voice_set_loopback(int32_t enabled);
FC_API void fc_voice_set_noise_config(int32_t mode, float strength, int32_t hpf_enabled, int32_t agc_enabled, float gate_db);
FC_API void fc_voice_set_input_gain(float gain);
FC_API void fc_voice_set_output_volume(float volume);
FC_API void fc_voice_set_user_volume(uint32_t ssrc, float volume);
FC_API int32_t fc_voice_is_ssrc_speaking(uint32_t ssrc);
FC_API void fc_voice_get_stats(FcAudioStats* out_stats);
FC_API int32_t fc_voice_get_available_backends(); // bitmask: 1=StudioDSP, 2=RNNoiseDLL, 4=DFNet3DLL, 8=NvAFXDLL

// Discord Voice UDP transport (IP discovery + AEAD XChaCha20-Poly1305 / XSalsa20 + DAVE E2EE MLS)
FC_API int32_t fc_transport_connect(const char* ip, uint16_t port, uint32_t ssrc, char* out_ext_ip, int32_t ext_ip_cap, uint16_t* out_ext_port);
FC_API int32_t fc_transport_set_key(const uint8_t* secret_key_32, int32_t use_xchacha_rtpsize);
FC_API void fc_transport_set_p2p_peer(const char* ip, uint16_t port, int32_t allow_direct_tx);
FC_API int32_t fc_transport_is_p2p_direct();
FC_API int32_t fc_get_pending_stun_endpoint(char* out_ip, int32_t ip_cap, uint16_t* out_port);
FC_API void fc_transport_disconnect();
FC_API int32_t fc_transport_get_ping_ms();
FC_API void fc_transport_send_silence();

// Official Discord DAVE E2EE (libdave)
FC_API int32_t fc_dave_init(uint16_t protocol_version, uint64_t channel_id, const char* self_user_id, uint32_t self_ssrc);
FC_API void fc_dave_add_user(const char* user_id);
FC_API void fc_dave_remove_user(const char* user_id);
FC_API void fc_dave_map_ssrc(uint32_t ssrc, const char* user_id);
FC_API int32_t fc_dave_get_key_package(uint8_t* out_buf, int32_t cap);
FC_API void fc_dave_set_external_sender(const uint8_t* data, int32_t len);
FC_API int32_t fc_dave_process_proposals(const uint8_t* data, int32_t len, uint8_t* out_commit_welcome, int32_t cap);
FC_API int32_t fc_dave_process_commit(const uint8_t* data, int32_t len);
FC_API int32_t fc_dave_process_welcome(const uint8_t* data, int32_t len);
FC_API void fc_dave_execute_transition(uint16_t protocol_version);

// Zero-Copy 4K60 Screen Capture (DXGI DD -> GPU NV12 -> NVENC) + Screen Share Source Picker Thumbnails
FC_API int32_t fc_list_monitors(FcMonitorInfo* out_list, int32_t max_count);
FC_API int32_t fc_list_windows(FcWindowInfo* out_list, int32_t max_count);
FC_API int32_t fc_capture_monitor_thumb(int32_t monitor_index, uint8_t* out_bgra, int32_t thumb_w, int32_t thumb_h);
FC_API int32_t fc_capture_window_thumb(uint64_t hwnd, uint8_t* out_bgra, int32_t thumb_w, int32_t thumb_h);
FC_API int32_t fc_screen_start(int32_t monitor_index, int32_t target_w, int32_t target_h, int32_t fps, int32_t bitrate_kbps, int32_t codec, int32_t loopback_preview, void* preview_hwnd);
FC_API int32_t fc_screen_start_ex(int32_t monitor_index, uint64_t target_hwnd, int32_t target_w, int32_t target_h, int32_t fps, int32_t bitrate_kbps, int32_t codec, int32_t loopback_preview, void* preview_hwnd, int32_t capture_audio, const char* render_device_id);
FC_API int32_t fc_screen_start_window(uint64_t target_hwnd, int32_t target_w, int32_t target_h, int32_t fps, int32_t bitrate_kbps, int32_t codec, int32_t loopback_preview, void* preview_hwnd);
FC_API void fc_screen_stop();
FC_API void fc_screen_resize_preview(int32_t width, int32_t height);
FC_API void fc_screen_get_stats(FcScreenStats* out_stats);

// Dedicated Outbound Go Live Stream Publisher Transport (UDP + DAVE E2EE Video)
FC_API int32_t fc_pub_connect(const char* ip, uint16_t port, uint32_t ssrc, char* out_ext_ip, int32_t ext_ip_cap, uint16_t* out_ext_port);
FC_API int32_t fc_pub_set_key(const uint8_t* secret_key_32, int32_t use_xchacha_rtpsize);
FC_API void fc_pub_set_p2p_peer(const char* ip, uint16_t port, int32_t allow_direct_tx);
FC_API void fc_pub_set_video_ssrc(uint32_t video_ssrc);
FC_API void fc_pub_request_keyframe();
FC_API void fc_pub_get_stats(int32_t* out_tx_pkts, int32_t* out_pli_rx, int32_t* out_dave_active);
FC_API void fc_pub_get_stats_ex(int32_t* out_video_pkts, int32_t* out_audio_pkts, int32_t* out_pli_rx, int32_t* out_dave_active);
FC_API void fc_pub_disconnect();
FC_API int32_t fc_pub_dave_init(uint16_t protocol_version, uint64_t channel_id, const char* self_user_id, uint32_t self_ssrc, uint32_t video_ssrc);
FC_API void fc_pub_dave_add_user(const char* user_id);
FC_API void fc_pub_dave_remove_user(const char* user_id);
FC_API int32_t fc_pub_dave_get_key_package(uint8_t* out_buf, int32_t cap);
FC_API void fc_pub_dave_set_external_sender(const uint8_t* data, int32_t len);
FC_API int32_t fc_pub_dave_process_proposals(const uint8_t* data, int32_t len, uint8_t* out_commit_welcome, int32_t cap);
FC_API int32_t fc_pub_dave_process_commit(const uint8_t* data, int32_t len);
FC_API int32_t fc_pub_dave_process_welcome(const uint8_t* data, int32_t len);
FC_API void fc_pub_dave_execute_transition(uint16_t protocol_version);

// Multi-Stream (up to 4 simultaneous!) Zero-Copy Go Live Stream Receivers (slot 0..3)
FC_API int32_t fc_watch_connect_slot(int32_t slot, const char* ip, uint16_t port, uint32_t ssrc, void* hwnd, char* out_ext_ip, int32_t ext_ip_cap, uint16_t* out_ext_port);
FC_API void fc_watch_set_key_slot(int32_t slot, const uint8_t* secret_key_32, int32_t use_xchacha_rtpsize);
FC_API void fc_watch_set_p2p_peer_slot(int32_t slot, const char* ip, uint16_t port, int32_t allow_direct_tx);
FC_API void fc_watch_set_codec_slot(int32_t slot, int32_t codec, int32_t video_pt);
FC_API void fc_watch_set_remote_slot(int32_t slot, uint32_t video_ssrc, uint32_t audio_ssrc, const char* streamer_user_id);
FC_API void fc_watch_set_volume_slot(int32_t slot, float volume);
FC_API void fc_watch_resize_slot(int32_t slot, int32_t width, int32_t height);
FC_API void fc_watch_stop_slot(int32_t slot);
FC_API void fc_watch_get_stats_slot(int32_t slot, FcScreenStats* out_stats);
FC_API int32_t fc_watch_dave_init_slot(int32_t slot, uint16_t proto_ver, uint64_t channel_id, const char* self_user_id, uint32_t self_ssrc);
FC_API void fc_watch_dave_add_user_slot(int32_t slot, const char* user_id);
FC_API void fc_watch_dave_remove_user_slot(int32_t slot, const char* user_id);
FC_API int32_t fc_watch_dave_get_key_package_slot(int32_t slot, uint8_t* out_buf, int32_t cap);
FC_API void fc_watch_dave_set_external_sender_slot(int32_t slot, const uint8_t* data, int32_t len);
FC_API int32_t fc_watch_dave_process_proposals_slot(int32_t slot, const uint8_t* data, int32_t len, uint8_t* out_cw, int32_t cap);
FC_API int32_t fc_watch_dave_process_commit_slot(int32_t slot, const uint8_t* data, int32_t len);
FC_API int32_t fc_watch_dave_process_welcome_slot(int32_t slot, const uint8_t* data, int32_t len);
FC_API void fc_watch_dave_execute_transition_slot(int32_t slot, uint16_t proto_ver);

// Backwards-compatible slot-0 aliases
FC_API int32_t fc_watch_connect(const char* ip, uint16_t port, uint32_t ssrc, void* hwnd, char* out_ext_ip, int32_t ext_ip_cap, uint16_t* out_ext_port);
FC_API void fc_watch_set_key(const uint8_t* secret_key_32, int32_t use_xchacha_rtpsize);
FC_API void fc_watch_set_remote(uint32_t video_ssrc, uint32_t audio_ssrc, const char* streamer_user_id);
FC_API void fc_watch_set_volume(float volume);
FC_API void fc_watch_resize(int32_t width, int32_t height);
FC_API void fc_watch_stop();
FC_API void fc_watch_get_stats(FcScreenStats* out_stats);
FC_API int32_t fc_watch_dave_init(uint16_t proto_ver, uint64_t channel_id, const char* self_user_id, uint32_t self_ssrc);
FC_API void fc_watch_dave_add_user(const char* user_id);
FC_API void fc_watch_dave_remove_user(const char* user_id);
FC_API int32_t fc_watch_dave_get_key_package(uint8_t* out_buf, int32_t cap);
FC_API void fc_watch_dave_set_external_sender(const uint8_t* data, int32_t len);
FC_API int32_t fc_watch_dave_process_proposals(const uint8_t* data, int32_t len, uint8_t* out_cw, int32_t cap);
FC_API int32_t fc_watch_dave_process_commit(const uint8_t* data, int32_t len);
FC_API int32_t fc_watch_dave_process_welcome(const uint8_t* data, int32_t len);
FC_API void fc_watch_dave_execute_transition(uint16_t proto_ver);

#ifdef __cplusplus
}
#endif
