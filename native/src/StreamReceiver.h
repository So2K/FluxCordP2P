#pragma once
#include "FluxCordMedia.h"
#include "VoiceEngine.h"
#include <dave/dave.h>
#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
#include <winsock2.h>
#include <ws2tcpip.h>

namespace fc {

/// Dedicated UDP + DAVE E2EE + Zero-Copy NVDEC/DXVA H.264 decoder & DXGI FlipDiscard presenter
/// for watching Discord Go Live streams in real time.
class StreamReceiver {
public:
    StreamReceiver(VoiceEngine* voice, int32_t slot_index = 0);
    ~StreamReceiver();

    bool connectAndDiscover(const char* ip, uint16_t port, uint32_t ssrc, HWND hwnd, std::string& out_ext_ip, uint16_t& out_ext_port);
    void setKey(const uint8_t key[32], bool use_xchacha_rtpsize);
    void setP2pPeer(const char* ip, uint16_t port, bool allow_direct_tx);
    void setCodec(int32_t codec, int32_t video_pt);
    void setRemoteStreams(uint32_t video_ssrc, uint32_t audio_ssrc, const char* streamer_user_id);
    void setVolume(float vol);
    void resize(int32_t w, int32_t h);
    void stop();

    // DAVE E2EE for stream session (daveProcessCommit returns: 1=applied, 2=ignored, 0=failed)
    bool daveInit(uint16_t proto_ver, uint64_t channel_id, const char* self_user_id, uint32_t self_ssrc);
    void daveAddUser(const char* user_id);
    void daveRemoveUser(const char* user_id);
    int32_t daveGetKeyPackage(uint8_t* out_buf, int32_t cap);
    void daveSetExternalSender(const uint8_t* data, int32_t len);
    int32_t daveProcessProposals(const uint8_t* data, int32_t len, uint8_t* out_cw, int32_t cap);
    int32_t daveProcessCommit(const uint8_t* data, int32_t len);
    bool daveProcessWelcome(const uint8_t* data, int32_t len);
    void daveExecuteTransition(uint16_t proto_ver);

    void getStats(FcScreenStats* out) const;

private:
    void rxDecodeLoop();
    void closeTransportLocked();
    void stopLocked();
    void daveDestroyLocked();
    uint32_t makeVoiceAudioKey(uint32_t audio_ssrc) const {
        return 0x80000000u | (uint32_t((slot_index_ & 0xF) + 1) << 24) | (audio_ssrc & 0x00FFFFFFu);
    }

    VoiceEngine* voice_;
    int32_t slot_index_ = 0;
    bool wsa_ok_ = false;
    std::mutex lifecycle_mtx_;

    SOCKET sock_ = INVALID_SOCKET;
    ULONG qos_flow_id_ = 0;
    sockaddr_in remote_{};
    std::mutex p2p_mtx_;
    sockaddr_in p2p_peer_{};
    std::atomic<bool> has_p2p_peer_{false};
    std::atomic<bool> allow_p2p_tx_{false};
    std::atomic<int64_t> last_p2p_rx_ms_{0};
    HWND hwnd_ = nullptr;

    std::mutex key_mtx_;
    uint8_t key_[32]{};
    bool use_xchacha_ = true;
    std::atomic<uint32_t> local_ssrc_{0};
    uint32_t rtcp_nonce_ = 1;
    std::atomic<bool> has_key_{false};
    std::atomic<bool> running_{false};
    std::atomic<uint32_t> video_ssrc_{0};
    std::atomic<uint32_t> audio_ssrc_{0};
    std::atomic<int32_t> video_codec_{FC_CODEC_H264};
    std::atomic<int32_t> video_pt_{101};
    std::atomic<bool> reset_depack_{false};
    std::atomic<float> stream_vol_{1.0f};
    std::atomic<int32_t> pending_w_{0};
    std::atomic<int32_t> pending_h_{0};

    std::mutex dave_mtx_;
    DAVESessionHandle dave_session_ = nullptr;
    DAVEDecryptorHandle dave_dec_ = nullptr;
    std::string streamer_uid_;
    uint16_t dave_proto_ver_ = 0;
    bool dave_has_ratchet_ = false;

    mutable std::mutex stats_mtx_;
    FcScreenStats stats_{};

    HANDLE worker_done_ev_ = nullptr;
    std::thread worker_;
};

} // namespace fc
