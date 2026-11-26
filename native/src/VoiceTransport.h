#pragma once
#include <dave/dave.h>
#include <atomic>
#include <cstdint>
#include <functional>
#include <map>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
#include <winsock2.h>
#include <ws2tcpip.h>

namespace fc {

using AudioRxCallback = std::function<void(uint32_t ssrc, uint16_t seq, const uint8_t* opus, int len)>;

class VoiceTransport {
public:
    VoiceTransport();
    ~VoiceTransport();

    bool connectAndDiscover(const char* ip, uint16_t port, uint32_t ssrc, std::string& out_ext_ip, uint16_t& out_ext_port);
    void setKey(const uint8_t key[32], bool use_xchacha_rtpsize);
    void setP2pPeer(const char* ip, uint16_t port, bool allow_direct_tx);
    bool isP2pDirect() const;
    void disconnect();

    bool sendOpus(const uint8_t* opus, int len, uint32_t timestamp);
    void sendSilenceFrames();
    bool sendH264Nalu(const uint8_t* nalu, int len, uint32_t video_ssrc, uint32_t timestamp, bool marker);
    bool sendH264AccessUnit(const uint8_t* annexb, int len, uint32_t video_ssrc, uint32_t timestamp);

    // DAVE E2EE (MLS) lifecycle (daveProcessCommit returns: 1=applied, 2=ignored, 0=failed)
    bool daveInit(uint16_t protocol_version, uint64_t channel_id, const char* self_user_id, uint32_t self_ssrc);
    void daveSetVideoSsrc(uint32_t video_ssrc);
    void daveAddUser(const char* user_id);
    void daveRemoveUser(const char* user_id);
    void daveMapSsrc(uint32_t ssrc, const char* user_id);
    int32_t daveGetKeyPackage(uint8_t* out_buf, int32_t cap);
    void daveSetExternalSender(const uint8_t* data, int32_t len);
    int32_t daveProcessProposals(const uint8_t* data, int32_t len, uint8_t* out_commit_welcome, int32_t cap);
    int32_t daveProcessCommit(const uint8_t* data, int32_t len);
    bool daveProcessWelcome(const uint8_t* data, int32_t len);
    void daveExecuteTransition(uint16_t protocol_version);

    // Authenticated compound RTCP decryption & PLI/FIR detection (exposed for deterministic unit tests)
    static bool decryptAndCheckRtcpPli(const uint8_t* pkt, int n, const uint8_t key[32], bool use_xchacha);

    void setAudioRxCallback(AudioRxCallback cb) {
        std::lock_guard lk(cb_mtx_);
        rx_cb_ = std::move(cb);
    }
    void setPliCallback(std::function<void()> cb) {
        std::lock_guard lk(cb_mtx_);
        pli_cb_ = std::move(cb);
    }
    int32_t txPackets() const { return tx_pkts_.load(); }
    int32_t audioTxPackets() const { return audio_tx_pkts_.load(); }
    int32_t videoTxPackets() const { return video_tx_pkts_.load(); }
    int32_t rxPackets() const { return rx_pkts_.load(); }
    int32_t pliCount() const { return pli_rx_.load(); }
    int32_t pingMs() const { return ping_ms_.load(); }
    bool isReady() const { return connected_.load() && has_key_.load(); }
    bool isDaveEncrypting() const { return dave_enc_active_.load(); }

private:
    void rxLoop();
    void closeTransportLocked();
    void disconnectLocked();
    void daveDestroyLocked();
    void daveRefreshRatchetsLocked(uint16_t protocol_version);
    bool sendEncryptedRtpLocked(uint8_t payload_type, uint16_t seq, uint32_t ts, uint32_t ssrc, bool marker, const uint8_t* payload, int len);

    bool wsa_ok_ = false;
    std::mutex lifecycle_mtx_;
    SOCKET sock_ = INVALID_SOCKET;
    ULONG qos_flow_id_ = 0;
    sockaddr_in remote_{};
    sockaddr_in p2p_peer_{};
    std::atomic<bool> has_p2p_peer_{false};
    std::atomic<bool> allow_p2p_tx_{false};
    std::atomic<int64_t> last_p2p_rx_ms_{0};
    std::atomic<uint32_t> ssrc_{0};
    uint16_t audio_seq_ = 0;
    uint16_t video_seq_ = 0;
    uint32_t nonce_counter_ = 1;
    uint32_t last_audio_ts_ = 0;
    bool has_audio_ts_ = false;

    std::mutex tx_mtx_;
    std::mutex key_mtx_;
    uint8_t key_[32]{};
    bool use_xchacha_ = true;
    std::atomic<bool> connected_{false};
    std::atomic<bool> has_key_{false};
    std::atomic<bool> running_{false};
    std::atomic<int32_t> tx_pkts_{0};
    std::atomic<int32_t> audio_tx_pkts_{0};
    std::atomic<int32_t> video_tx_pkts_{0};
    std::atomic<int32_t> rx_pkts_{0};
    std::atomic<int32_t> pli_rx_{0};
    std::atomic<int32_t> ping_ms_{0};
    std::atomic<bool> dave_enc_active_{false};

    // DAVE E2EE handles
    std::mutex dave_mtx_;
    DAVESessionHandle dave_session_ = nullptr;
    DAVEEncryptorHandle dave_enc_ = nullptr;
    std::string self_user_id_;
    uint16_t dave_proto_ver_ = 0;
    uint32_t video_ssrc_ = 0;
    std::map<uint32_t, std::string> ssrc_to_user_;
    std::map<std::string, DAVEDecryptorHandle> user_decryptors_;
    std::map<std::string, bool> user_has_ratchet_;

    std::mutex video_mtx_;
    std::vector<uint8_t> cached_sps_;
    std::vector<uint8_t> cached_pps_;

    std::thread rx_thread_;
    std::mutex cb_mtx_;
    AudioRxCallback rx_cb_;
    std::function<void()> pli_cb_;
};

} // namespace fc
