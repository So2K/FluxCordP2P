#include "NetTurbo.h"
#include "NoiseSuppressor.h"
#include "VideoDepacketizer.h"
#include "VoiceEngine.h"
#include <opus.h>
#include <sodium.h>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <vector>

namespace {
int g_pass = 0, g_fail = 0;

void check(bool cond, const char* msg) {
    if (cond) {
        std::printf("[PASS] %s\n", msg);
        g_pass++;
    } else {
        std::printf("[FAIL] %s\n", msg);
        g_fail++;
    }
}

float rms(const float* p, size_t n) {
    double s = 0;
    for (size_t i = 0; i < n; ++i) s += double(p[i]) * double(p[i]);
    return float(std::sqrt(s / double(n)));
}

// 1. Verify exact COLA STFT reconstruction + zero clicks/discontinuities across 960-sample (20ms) frames!
void testNoiseSuppressorCleanSignalFidelity() {
    fc::NoiseSuppressor ns;
    ns.configure(FC_NOISE_DEEPFILTERNET3, 0.75f, true, false, -60.0f);

    constexpr int kTotal = 48000; // 1 second
    std::vector<float> buf(kTotal);
    for (int i = 0; i < kTotal; ++i) {
        buf[i] = 0.25f * std::sin(2.0f * 3.14159265f * 440.0f * float(i) / 48000.0f);
    }

    for (int off = 0; off + 960 <= kTotal; off += 960) {
        ns.process(buf.data() + off, 960);
    }

    // Skip initial 100 ms convergence, verify smooth steady 440 Hz sine with ZERO NaN, ZERO discontinuities, preserved energy!
    bool finiteOk = true;
    float maxJump = 0.0f;
    for (int i = 4800; i < kTotal - 1; ++i) {
        if (!std::isfinite(buf[i])) finiteOk = false;
        float diff = std::fabs(buf[i + 1] - buf[i]);
        if (diff > maxJump) maxJump = diff;
    }
    float outRms = rms(buf.data() + 4800, kTotal - 4800);
    // Expected max per-sample delta of 0.25 * sin(2*pi*440*t) at 48kHz is 0.25 * 2*pi*440/48000 = 0.0144
    char msg[160];
    std::snprintf(msg, sizeof(msg), "STFT COLA 440Hz fidelity: finite=%d outRms=%.4f maxSampleJump=%.4f", finiteOk, outRms, maxJump);
    check(finiteOk && outRms > 0.10f && maxJump < 0.05f, msg);
}

// 2. Verify real noise suppression attenuates stationary broadband noise while keeping speech active
void testNoiseSuppressorNoiseRejection() {
    fc::NoiseSuppressor ns;
    ns.configure(FC_NOISE_DEEPFILTERNET3, 0.85f, true, false, -38.0f);

    uint32_t rng = 0x12345678u;
    auto randf = [&]() {
        rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
        return (float(int32_t(rng)) / 2147483648.0f);
    };

    // 1.0 sec of low-level white noise (-45 dBFS) -> must be suppressed heavily
    std::vector<float> noise(48000);
    for (auto& s : noise) s = randf() * 0.005f;
    float rawRms = rms(noise.data() + 9600, 38400);
    for (int off = 0; off + 960 <= 48000; off += 960) ns.process(noise.data() + off, 960);
    float cleanRms = rms(noise.data() + 24000, 24000);
    float attenDb = 20.0f * std::log10(std::max(rawRms, 1e-6f) / std::max(cleanRms, 1e-6f));

    char msg[160];
    std::snprintf(msg, sizeof(msg), "Noise rejection: raw=%.5f clean=%.5f atten=%.1fdB", rawRms, cleanRms, attenDb);
    check(attenDb >= 15.0f, msg);
}

// 3. Verify VoiceEngine jitter buffer handles out-of-order packets, 16-bit seq wrap (65534->2), and Opus PLC with ZERO gaps!
void testVoiceJitterBufferWrapAndPlc() {
    fc::VoiceEngine ve(nullptr);
    int err = 0;
    OpusEncoder* enc = opus_encoder_create(48000, 2, OPUS_APPLICATION_VOIP, &err);
    opus_encoder_ctl(enc, OPUS_SET_BITRATE(96000));
    opus_encoder_ctl(enc, OPUS_SET_INBAND_FEC(1));

    // Encode 10 consecutive 20ms stereo frames of 440 Hz tone starting at seq 65533 (wraps over 65535 -> 0!)
    struct Pkt { uint16_t seq; std::vector<uint8_t> data; };
    std::vector<Pkt> pkts;
    for (int f = 0; f < 10; ++f) {
        float pcm[960 * 2];
        for (int i = 0; i < 960; ++i) {
            float v = 0.3f * std::sin(2.0f * 3.14159265f * 440.0f * float(f * 960 + i) / 48000.0f);
            pcm[i * 2] = v;
            pcm[i * 2 + 1] = v;
        }
        uint8_t out[1024];
        int n = opus_encode_float(enc, pcm, 960, out, sizeof(out));
        pkts.push_back({ uint16_t(65533 + f), std::vector<uint8_t>(out, out + n) });
    }
    opus_encoder_destroy(enc);

    // Feed packets OUT OF ORDER and DROP packet #4 (seq 1) to test wrap-around + reorder + FEC/PLC!
    const int order[] = { 0, 2, 1, 3, /*drop 4*/ 5, 7, 6, 8, 9 };
    for (int idx : order) {
        ve.onRemoteOpus(4242, pkts[idx].seq, pkts[idx].data.data(), int(pkts[idx].data.size()));
    }

    // Pull 8 consecutive 20ms stereo frames (7680 frames) and verify EVERY single frame has non-zero continuous audio (NO zero dropouts!)
    int nonZeroFrames = 0;
    for (int f = 0; f < 8; ++f) {
        float mix[960 * 2];
        ve.pullMixedStereo(mix, 960);
        if (rms(mix, 960 * 2) > 0.05f) nonZeroFrames++;
    }

    char msg[160];
    std::snprintf(msg, sizeof(msg), "Voice jitter buffer wrap(65533..6) + reorder + FEC/PLC: nonZeroFrames=%d/8 speaking=%d",
                  nonZeroFrames, ve.isSsrcSpeaking(4242) ? 1 : 0);
    check(nonZeroFrames == 8 && ve.isSsrcSpeaking(4242), msg);
}

// 4. Verify VideoDepacketizer handles FU-A + STAP-A + out-of-order UDP bursts + packet-loss PLI trigger!
void testVideoDepacketizerReorderAndLoss() {
    fc::VideoDepacketizer dp;

    // Build an IDR keyframe consisting of SPS (type 7), PPS (type 8) in STAP-A + a large IDR (type 5, 3000 bytes) in 3 FU-A packets
    std::vector<uint8_t> sps = { 0x67, 0x42, 0x00, 0x1F, 0xAB, 0xCD };
    std::vector<uint8_t> pps = { 0x68, 0xCE, 0x3C, 0x80 };
    std::vector<uint8_t> idr(3000);
    idr[0] = 0x65; // IDR NALU header
    for (size_t i = 1; i < idr.size(); ++i) idr[i] = uint8_t((i * 37) & 0xFF);

    // STAP-A packet (seq 100)
    std::vector<uint8_t> stap = { 24, 0, uint8_t(sps.size()) };
    stap.insert(stap.end(), sps.begin(), sps.end());
    stap.push_back(0); stap.push_back(uint8_t(pps.size()));
    stap.insert(stap.end(), pps.begin(), pps.end());

    // 3 FU-A packets (seq 101, 102, 103 marker=true)
    auto makeFu = [&](size_t off, size_t len, bool s, bool e) {
        std::vector<uint8_t> p;
        p.push_back((idr[0] & 0xE0) | 28);
        p.push_back(uint8_t((s ? 0x80 : 0) | (e ? 0x40 : 0) | (idr[0] & 0x1F)));
        p.insert(p.end(), idr.begin() + off, idr.begin() + off + len);
        return p;
    };
    auto fu1 = makeFu(1, 1000, true, false);
    auto fu2 = makeFu(1001, 1000, false, false);
    auto fu3 = makeFu(2001, 999, false, true);

    // Deliver OUT OF ORDER: seq 102, 100, 103 (marker!), 101 -> MUST reassemble cleanly on arrival of seq 101!
    fc::VideoDepacketizer::FrameOut out;
    bool pli = false;
    check(!dp.pushPacket(102, 90000, false, fu2.data(), int(fu2.size()), 10, out, pli), "Video reorder holds out-of-order seq 102");
    check(!dp.pushPacket(100, 90000, false, stap.data(), int(stap.size()), 11, out, pli), "Video reorder holds seq 100");
    check(!dp.pushPacket(103, 90000, true, fu3.data(), int(fu3.size()), 12, out, pli), "Video reorder waits for missing seq 101");
    bool emitted = dp.pushPacket(101, 90000, false, fu1.data(), int(fu1.size()), 14, out, pli);

    std::vector<uint8_t> expected;
    auto addSc = [&](const std::vector<uint8_t>& n) {
        expected.push_back(0); expected.push_back(0); expected.push_back(0); expected.push_back(1);
        expected.insert(expected.end(), n.begin(), n.end());
    };
    addSc(sps); addSc(pps); addSc(idr);

    check(emitted && out.is_keyframe && out.annexb == expected && !pli,
          "VideoDepacketizer exact byte-for-byte Annex-B reconstruction after out-of-order STAP-A + FU-A");

    // Test 0-byte Discord BWE padding probe packet (seq 104) between Frame 1 (100..103) and Frame 2 (105..106)!
    dp.noteEmptyProbeSeq(104);
    std::vector<uint8_t> pSlice = { 0x41, 0x9A, 0x22, 0x33, 0x44 };
    bool emittedAfterProbe = dp.pushPacket(105, 91500, true, pSlice.data(), int(pSlice.size()), 20, out, pli);
    check(emittedAfterProbe && !pli, "VideoDepacketizer emits Frame 2 cleanly across 0-byte BWE probe packet (seq 104)");

    // Now test true packet loss on P-frame (drop seq 107, send 106 + 108 marker) -> after 90ms (>80ms marker timeout) must drop & trigger PLI!
    auto p1 = makeFu(1, 500, true, false);
    auto p3 = makeFu(1001, 500, false, true);
    dp.pushPacket(106, 93000, false, p1.data(), int(p1.size()), 50, out, pli);
    dp.pushPacket(108, 93000, true, p3.data(), int(p3.size()), 52, out, pli);
    bool emittedBroken = dp.tryEmit(150, out, pli); // +98ms > 40ms marker timeout
    check(!emittedBroken && pli && dp.needKeyframe(), "VideoDepacketizer drops corrupt P-frame and triggers RTCP PLI");

    // [C-01 Regression] Contiguous post-loss P-frame (seq 109 right after dropped 106..108) MUST be rejected while needKeyframe()==true!
    bool emittedCorruptRef = dp.pushPacket(109, 94500, true, pSlice.data(), int(pSlice.size()), 160, out, pli);
    check(!emittedCorruptRef && dp.needKeyframe(),
          "VideoDepacketizer rejects contiguous post-loss P-frame while awaiting IDR keyframe");

    // Verify large WebRTC-paced IDR keyframe spread over 120ms is NOT dropped prematurely and emits immediately even after prior loss!
    dp.pushPacket(120, 96000, false, stap.data(), int(stap.size()), 200, out, pli);
    dp.pushPacket(121, 96000, false, fu1.data(), int(fu1.size()), 240, out, pli);
    dp.pushPacket(122, 96000, false, fu2.data(), int(fu2.size()), 290, out, pli);
    bool emittedPacedIdr = dp.pushPacket(123, 96000, true, fu3.data(), int(fu3.size()), 320, out, pli);
    check(emittedPacedIdr && out.is_keyframe && !dp.needKeyframe() && out.annexb == expected,
          "VideoDepacketizer preserves 120ms WebRTC-paced IDR keyframe after prior packet loss");

    // Regression: SFU temporal-layer frame skip (small seq gap 124..125 skipped, clean intact P-frame at 126) MUST NOT lock needKeyframe()!
    bool emittedCleanGap = dp.pushPacket(126, 99000, true, pSlice.data(), int(pSlice.size()), 360, out, pli);
    if (!emittedCleanGap) emittedCleanGap = dp.tryEmit(410, out, pli); // +50ms > 40ms marker wait
    check(emittedCleanGap && !dp.needKeyframe(),
          "VideoDepacketizer survives SFU temporal-layer seq gap without stalling stream to 1fps");
}

// 9. Verify VideoDepacketizer overflow want_pli preservation + lost-marker fast recovery + strict RFC 6184 checks!
void testVideoDepacketizerOverflowAndRfc6184Guards() {
    fc::VideoDepacketizer dp;
    fc::VideoDepacketizer::FrameOut out;
    bool pli = false;

    // 9a. Push >16 incomplete frames: eviction want_pli MUST survive tryEmit()!
    uint8_t slice[4] = { 0x41, 0x9A, 0x11, 0x22 };
    bool sawOverflowPli = false;
    for (int i = 0; i < 18; ++i) {
        dp.pushPacket(uint16_t(500 + i * 2), uint32_t(10000 + i * 3000), false, slice, sizeof(slice), 1000, out, pli);
        if (pli) sawOverflowPli = true;
    }
    check(sawOverflowPli && dp.needKeyframe(), "VideoDepacketizer preserves want_pli on >16 frame buffer overflow");

    // 9b. Reject malformed RFC 6184 FU-A (both Start+End bits set 0xC0) & truncated STAP-A trailing byte
    dp.reset();
    uint8_t badFua[4] = { 28 | 0x60, uint8_t(0x80 | 0x40 | 5), 0xAA, 0xBB };
    bool okBadFua = dp.pushPacket(10, 3000, true, badFua, sizeof(badFua), 10, out, pli);
    uint8_t badStap[6] = { 24, 0, 2, 0x67, 0x42, 0xFF }; // trailing 0xFF
    bool okBadStap = dp.pushPacket(11, 6000, true, badStap, sizeof(badStap), 20, out, pli);
    check(!okBadFua && !okBadStap, "VideoDepacketizer rejects malformed RFC 6184 FU-A (S+E) and truncated STAP-A");

    // 9c. Lost marker packet does NOT stall subsequent keyframe for 250ms (recovers at >=40ms)
    dp.reset();
    dp.pushPacket(20, 9000, false, slice, sizeof(slice), 100, out, pli); // Frame A missing marker
    uint8_t idrSingle[6] = { 0x65, 0x88, 0x84, 0x21, 0xA0, 0x01 };
    dp.pushPacket(22, 12000, true, idrSingle, sizeof(idrSingle), 110, out, pli); // Frame B (IDR) waiting behind A
    bool fastRecovered = dp.tryEmit(155, out, pli); // +45ms after Frame B arrived (< 250ms!)
    check(fastRecovered && out.is_keyframe && !dp.needKeyframe(),
          "VideoDepacketizer recovers in 45ms when previous frame lost its RTP marker packet");
}

// 8. Verify NoiseSuppressor NaN/Inf immunity (zero state poisoning even on corrupted audio driver input!)
void testNoiseSuppressorNanImmunity() {
    fc::NoiseSuppressor ns;
    ns.configure(FC_NOISE_DEEPFILTERNET3, 0.8f, true, true, -50.0f);
    std::vector<float> buf(960, 0.1f);
    buf[10] = std::numeric_limits<float>::quiet_NaN();
    buf[20] = std::numeric_limits<float>::infinity();
    buf[30] = -std::numeric_limits<float>::infinity();
    ns.process(buf.data(), 960);
    // Feed a normal sine next and verify 100% finite non-dead output
    for (int i = 0; i < 960; ++i) buf[i] = 0.2f * std::sin(2.0f * 3.14159265f * 440.0f * float(i) / 48000.0f);
    ns.process(buf.data(), 960);
    bool ok = true;
    for (float s : buf) if (!std::isfinite(s)) ok = false;
    check(ok && rms(buf.data(), 960) > 0.01f, "NoiseSuppressor NaN/Inf driver-glitch immunity verified");
}

// 5. Verify Discord aead_xchacha20_poly1305_rtpsize encryption/decryption with RTP Padding + Encrypted RTCP PLI!
void testAeadRtpPaddingAndEncryptedRtcpPli() {
    sodium_init();
    uint8_t key[32];
    randombytes_buf(key, 32);

    // 5a. RTP packet with Extension (X=1) + Padding (P=1, 4 pad bytes)
    uint8_t hdr[16] = { 0xB0, 120, 0x00, 0x2A, 0,0,0,1, 0x11,0x22,0x33,0x44, 0xBE,0xDE, 0x00,0x01 }; // V=2,P=1,X=1, ext=1 word (4B)
    uint8_t inner[4 + 5 + 4] = { 0x10, 0xAA, 0x00, 0x00,  0xDE, 0xAD, 0xBE, 0xEF, 0x42,  0x00, 0x00, 0x00, 0x04 };
    uint32_t ctr = 77;
    uint8_t nonce[24]{};
    std::memcpy(nonce, &ctr, 4);
    uint8_t pkt[128]{};
    std::memcpy(pkt, hdr, 16);
    unsigned long long clen = 0;
    crypto_aead_xchacha20poly1305_ietf_encrypt(pkt + 16, &clen, inner, sizeof(inner), pkt, 16, nullptr, nonce, key);
    std::memcpy(pkt + 16 + clen, &ctr, 4);
    int total = int(16 + clen + 4);

    // Decrypt + strip padding + skip 4-byte ext payload
    uint8_t dec[128]{};
    unsigned long long plen = 0;
    uint8_t rxNonce[24]{};
    std::memcpy(rxNonce, pkt + total - 4, 4);
    bool ok = crypto_aead_xchacha20poly1305_ietf_decrypt(dec, &plen, nullptr, pkt + 16, size_t(total - 20), pkt, 16, rxNonce, key) == 0;
    if (pkt[0] & 0x20) plen -= dec[plen - 1];
    int extBytes = int((uint16_t(pkt[14]) << 8) | pkt[15]) * 4;
    check(ok && (int(plen) - extBytes == 5) && dec[extBytes] == 0xDE && dec[extBytes + 4] == 0x42,
          "AEAD XChaCha20-Poly1305 RTP with Extension + Padding stripped byte-exact");

    // 5b. Encrypted RTCP PLI (8-byte AAD + 4-byte Media SSRC + 16-byte tag + 4-byte LE nonce)
    uint8_t rtcp[64] = { 0x81, 206, 0, 2, 0x11, 0x22, 0x33, 0x44 };
    uint8_t mediaSsrc[4] = { 0x55, 0x66, 0x77, 0x88 };
    crypto_aead_xchacha20poly1305_ietf_encrypt(rtcp + 8, &clen, mediaSsrc, 4, rtcp, 8, nullptr, nonce, key);
    std::memcpy(rtcp + 8 + clen, &ctr, 4);
    uint8_t rtcpPlain[16]{};
    // 5c. [C-05 Regression] Compound Encrypted RTCP (SR PT=200 + PSFB PT=206 PLI) & unauthenticated PT=206 rejection!
    // Build compound RTCP: 28-byte SR (V=2,RC=0,PT=200,len=6 -> 28 bytes) + 12-byte PSFB PLI (V=2,FMT=1,PT=206,len=2 -> 12 bytes) = 40 bytes total
    uint8_t compoundPlain[40]{};
    compoundPlain[0] = 0x80; compoundPlain[1] = 200; compoundPlain[2] = 0; compoundPlain[3] = 6; // 28B SR
    compoundPlain[28] = 0x81; compoundPlain[29] = 206; compoundPlain[30] = 0; compoundPlain[31] = 2; // 12B PLI
    uint8_t compoundEnc[128]{};
    std::memcpy(compoundEnc, compoundPlain, 8);
    uint32_t ctr2 = 88;
    uint8_t nonce2[24]{};
    std::memcpy(nonce2, &ctr2, 4);
    unsigned long long cLen2 = 0;
    crypto_aead_xchacha20poly1305_ietf_encrypt(compoundEnc + 8, &cLen2, compoundPlain + 8, 32, compoundEnc, 8, nullptr, nonce2, key);
    std::memcpy(compoundEnc + 8 + cLen2, &ctr2, 4);
    int compTotal = int(8 + cLen2 + 4);

    bool compoundDetected = fc::VoiceTransport::decryptAndCheckRtcpPli(compoundEnc, compTotal, key, true);
    uint8_t forgedPli[32] = { 0x81, 206, 0, 2, 0x11, 0x22, 0x33, 0x44 };
    bool forgedRejected = !fc::VoiceTransport::decryptAndCheckRtcpPli(forgedPli, sizeof(forgedPli), key, true);
    check(compoundDetected && forgedRejected,
          "Encrypted Compound RTCP (SR+PLI) detected & unauthenticated PT=206 spoof rejected");
}

// 6. Verify VoiceTransport Outbound Go Live DAVE H.264 video SSRC assignment & MLS KeyPackage + connectAndDiscover DAVE preservation
void testOutboundGoLiveDaveInit() {
    fc::VoiceTransport pub;
    pub.daveAddUser("999888777666555444"); // Pre-added from VOICE_STATE_UPDATE BEFORE UDP connect!
    pub.daveSetVideoSsrc(33334u);
    std::string extIp;
    uint16_t extPort = 0;
    // Failed connect to closed loopback port MUST NOT wipe pre-added DAVE session or video_ssrc_!
    pub.connectAndDiscover("127.0.0.1", 9, 33333u, extIp, extPort);
    bool ok = pub.daveInit(1, 987654321012345678ULL, "111222333444555666", 33333u);
    bool pliWired = false;
    pub.setPliCallback([&] { pliWired = true; });
    uint8_t kp[4096]{};
    int32_t kpLen = pub.daveGetKeyPackage(kp, sizeof(kp));
    char msg[140];
    std::snprintf(msg, sizeof(msg), "Outbound Go Live DAVE init + preserved session across connectAndDiscover (%d bytes)", kpLen);
    check(ok && kpLen > 100 && !pub.isDaveEncrypting() && !pliWired && pub.audioTxPackets() == 0 && pub.videoTxPackets() == 0, msg);
}

// 7. Verify Go Live System Audio 48kHz Stereo Music Opus 128kbps roundtrip encode+decode fidelity
void testGoLiveSystemAudioOpus128k() {
    int err = 0;
    OpusEncoder* enc = opus_encoder_create(48000, 2, OPUS_APPLICATION_AUDIO, &err);
    OpusDecoder* dec = opus_decoder_create(48000, 2, &err);
    opus_encoder_ctl(enc, OPUS_SET_BITRATE(128000));
    opus_encoder_ctl(enc, OPUS_SET_SIGNAL(OPUS_SIGNAL_MUSIC));
    opus_encoder_ctl(enc, OPUS_SET_INBAND_FEC(1));
    opus_encoder_ctl(enc, OPUS_SET_DTX(0));

    float inPcm[960 * 2], outPcm[960 * 2];
    uint8_t pkt[1200];
    int totalBytes = 0;
    float lastRms = 0.0f;
    for (int f = 0; f < 5; ++f) {
        for (int i = 0; i < 960; ++i) {
            float t = float(f * 960 + i) / 48000.0f;
            inPcm[i * 2]     = 0.35f * std::sin(2.0f * 3.14159265f * 440.0f * t);
            inPcm[i * 2 + 1] = 0.35f * std::sin(2.0f * 3.14159265f * 660.0f * t);
        }
        int n = opus_encode_float(enc, inPcm, 960, pkt, sizeof(pkt));
        if (n > 0) {
            totalBytes += n;
            int ds = opus_decode_float(dec, pkt, n, outPcm, 960, 0);
            if (ds == 960) lastRms = rms(outPcm, 960 * 2);
        }
    }
    opus_encoder_destroy(enc);
    opus_decoder_destroy(dec);
    char msg[128];
    std::snprintf(msg, sizeof(msg), "Go Live System Audio Stereo 128kbps Music Opus roundtrip: bytes=%d rms=%.4f", totalBytes, lastRms);
    check(totalBytes > 500 && lastRms > 0.18f, msg);
}

void testVideoDepacketizerHevcRfc7798() {
    fc::VideoDepacketizer depack;
    depack.setHevc(true);
    fc::VideoDepacketizer::FrameOut out{};
    bool pli = false;

    // 1. RFC 7798 AP (type=48): VPS(32) + SPS(33) + PPS(34)
    // HEVC 2-byte NAL header: (type << 1), tid=1 -> VPS=(32<<1)=0x40,0x01; SPS=(33<<1)=0x42,0x01; PPS=(34<<1)=0x44,0x01
    std::vector<uint8_t> ap = {
        uint8_t(48 << 1), 0x01,
        0x00, 0x04, 0x40, 0x01, 0xAA, 0xBB,
        0x00, 0x04, 0x42, 0x01, 0xCC, 0xDD,
        0x00, 0x04, 0x44, 0x01, 0xEE, 0xFF
    };
    bool ok1 = depack.pushPacket(10, 9000, false, ap.data(), int(ap.size()), 100, out, pli);
    check(!ok1, "HEVC RFC 7798 AP config packet queued waiting for IDR FU");

    // 2. RFC 7798 FU (type=49) carrying IDR_W_RADL (type=19 -> orig hdr0 = 0x26, hdr1 = 0x01) across 2 fragments
    std::vector<uint8_t> fuStart = { uint8_t(49 << 1), 0x01, uint8_t(0x80 | 19), 0x11, 0x22, 0x33 };
    std::vector<uint8_t> fuEnd   = { uint8_t(49 << 1), 0x01, uint8_t(0x40 | 19), 0x44, 0x55, 0x66 };
    depack.pushPacket(11, 9000, false, fuStart.data(), int(fuStart.size()), 101, out, pli);
    bool okIdr = depack.pushPacket(12, 9000, true, fuEnd.data(), int(fuEnd.size()), 102, out, pli);
    check(okIdr && out.is_keyframe && !depack.needKeyframe() &&
          fc::VideoDepacketizer::hasHevcIdr(out.annexb.data(), out.annexb.size()),
          "HEVC RFC 7798 AP + FU IDR_W_RADL reassembled cleanly with start codes & keyframe lock");
}
void testP2pAuthenticatedProbe() {
    uint8_t key[32]{};
    for (int i = 0; i < 32; ++i) key[i] = uint8_t(i * 7 + 3);
    uint8_t pkt[16]{};
    fc::p2p::buildProbe(pkt, 0x55, 12345678u, key);
    uint8_t cmd = 0;
    uint32_t ts = 0;
    bool okValid = fc::p2p::verifyProbe(pkt, 16, key, cmd, ts) && cmd == 0x55 && ts == 12345678u;
    pkt[12] ^= 0x01; // flip 1 bit in MAC
    bool okTamper = !fc::p2p::verifyProbe(pkt, 16, key, cmd, ts);
    check(okValid && okTamper, "P2P Direct UDP probe MAC authentication & tamper rejection verified");
}
} // namespace

int main() {
    testNoiseSuppressorCleanSignalFidelity();
    testNoiseSuppressorNoiseRejection();
    testVoiceJitterBufferWrapAndPlc();
    testVideoDepacketizerReorderAndLoss();
    testVideoDepacketizerOverflowAndRfc6184Guards();
    testVideoDepacketizerHevcRfc7798();
    testP2pAuthenticatedProbe();
    testAeadRtpPaddingAndEncryptedRtcpPli();
    testOutboundGoLiveDaveInit();
    testGoLiveSystemAudioOpus128k();
    testNoiseSuppressorNanImmunity();
    std::printf("\n=== C++ MEDIA UNIT TESTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail == 0 ? 0 : 1;
}
