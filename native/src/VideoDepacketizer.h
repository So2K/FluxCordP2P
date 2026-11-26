#pragma once
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <map>
#include <set>
#include <vector>

namespace fc {

/// Production-grade RFC 6184 H.264 RTP Reorder Buffer & Access-Unit Depacketizer.
/// Handles out-of-order UDP bursts, 0-byte Discord BWE padding probe packets,
/// SFU temporal-layer frame skipping, Single NALU / STAP-A / FU-A, packet-loss detection,
/// and strict IDR/SPS keyframe gating.
class VideoDepacketizer {
public:
    struct FrameOut {
        uint32_t timestamp = 0;
        std::vector<uint8_t> annexb;
        bool is_keyframe = false;
    };

    void setHevc(bool hevc) {
        if (is_hevc_ != hevc) {
            is_hevc_ = hevc;
            reset();
        }
    }
    bool isHevc() const { return is_hevc_; }

    void reset() {
        frames_.clear();
        empty_probe_seqs_.clear();
        has_seq_ = false;
        last_ext_seq_ = -1;
        last_emitted_max_seq_ = -1;
        need_keyframe_ = true;
        frames_ok_ = 0;
        frames_dropped_ = 0;
    }

    /// Record a 0-byte RTP padding/BWE probe packet sequence number so it never looks like a lost video packet!
    void noteEmptyProbeSeq(uint16_t seq) {
        int64_t ext = unwrapSeq(seq);
        if (last_emitted_max_seq_ >= 0 && ext <= last_emitted_max_seq_) return;
        empty_probe_seqs_.insert(ext);
        while (empty_probe_seqs_.size() > 256) empty_probe_seqs_.erase(empty_probe_seqs_.begin());
    }

    bool pushPacket(uint16_t seq, uint32_t rtp_ts, bool marker, const uint8_t* payload, int len,
                    int64_t now_ms, FrameOut& out, bool& want_pli) {
        want_pli = false;
        if (!payload || len < 1) {
            noteEmptyProbeSeq(seq);
            return tryEmit(now_ms, out, want_pli);
        }

        int64_t extSeq = unwrapSeq(seq);
        if (last_emitted_max_seq_ >= 0 && extSeq <= last_emitted_max_seq_) {
            return false;
        }

        auto& fb = frames_[rtp_ts];
        if (fb.packets.empty()) fb.first_ms = now_ms;
        fb.packets[extSeq] = std::vector<uint8_t>(payload, payload + len);
        if (marker) {
            fb.has_marker = true;
            fb.marker_seq = extSeq;
            fb.marker_ms = now_ms;
        }

        // Evict oldest frame by minimum extended sequence (safe across 32-bit RTP timestamp wraparound!)
        bool overflowPli = false;
        while (frames_.size() > 16) {
            auto oldIt = findOldestFrame();
            if (!oldIt->second.packets.empty()) {
                advanceLastEmitted(oldIt->second.packets.rbegin()->first);
            }
            frames_.erase(oldIt);
            frames_dropped_++;
            need_keyframe_ = true;
            overflowPli = true;
        }

        bool emitPli = false;
        bool ok = tryEmit(now_ms, out, emitPli);
        want_pli = overflowPli || emitPli;
        return ok;
    }

    bool tryEmit(int64_t now_ms, FrameOut& out, bool& want_pli) {
        want_pli = false;
        while (!frames_.empty()) {
            // Advance last_emitted_max_seq_ over any contiguous BWE probe packets
            while (last_emitted_max_seq_ >= 0 && empty_probe_seqs_.count(last_emitted_max_seq_ + 1)) {
                empty_probe_seqs_.erase(last_emitted_max_seq_ + 1);
                last_emitted_max_seq_++;
            }

            auto bestIt = findOldestFrame();
            auto& fb = bestIt->second;
            if (fb.packets.empty()) {
                frames_.erase(bestIt);
                continue;
            }
            int64_t minSeq = fb.packets.begin()->first;
            int64_t maxSeq = fb.packets.rbegin()->first;

            // Check internal contiguity (allowing BWE probe packets if any fell inside the range)
            bool contiguousInternal = true;
            for (int64_t s = minSeq; s <= maxSeq; ++s) {
                if (!fb.packets.count(s) && !empty_probe_seqs_.count(s)) {
                    contiguousInternal = false;
                    break;
                }
            }

            // Check that the first packet is NOT a mid-FU continuation (guards out-of-order initial IDR!)
            bool startsClean = true;
            {
                const auto& fp = fb.packets.begin()->second;
                if (!is_hevc_) {
                    if (fp.size() >= 2 && (fp[0] & 0x1F) == 28 && (fp[1] & 0x80) == 0) startsClean = false;
                } else {
                    if (fp.size() >= 3 && ((fp[0] >> 1) & 0x3F) == 49 && (fp[2] & 0x80) == 0) startsClean = false;
                }
            }

            // Check contiguity with previous emitted frame (allowing up to 256 BWE probes in between)
            bool contiguousWithPrev = (last_emitted_max_seq_ < 0) ? startsClean : (minSeq == last_emitted_max_seq_ + 1);
            if (!contiguousWithPrev && last_emitted_max_seq_ >= 0 && minSeq > last_emitted_max_seq_ + 1 && (minSeq - last_emitted_max_seq_) <= 256) {
                bool allProbes = true;
                for (int64_t s = last_emitted_max_seq_ + 1; s < minSeq; ++s) {
                    if (!empty_probe_seqs_.count(s)) { allProbes = false; break; }
                }
                contiguousWithPrev = allProbes;
            }

            bool frameComplete = fb.has_marker && (maxSeq == fb.marker_seq) && contiguousInternal && startsClean;

            // Fast-path: complete frame that is either contiguous with previous (when NOT awaiting keyframe) OR is a keyframe
            if (frameComplete && (contiguousWithPrev || need_keyframe_)) {
                bool assembled = is_hevc_ ? assembleAnnexBHevc(fb, out.annexb) : assembleAnnexB(fb, out.annexb);
                out.timestamp = bestIt->first;
                out.is_keyframe = assembled && (is_hevc_ ? hasHevcKeyOrConfig(out.annexb.data(), out.annexb.size()) : hasIdrOrSps(out.annexb.data(), out.annexb.size()));
                if ((!need_keyframe_ && contiguousWithPrev) || out.is_keyframe) {
                    advanceLastEmitted(maxSeq);
                    frames_.erase(bestIt);
                    if (assembled && !out.annexb.empty()) {
                        if (is_hevc_ ? hasHevcIdr(out.annexb.data(), out.annexb.size()) : hasIdr(out.annexb.data(), out.annexb.size())) need_keyframe_ = false;
                        frames_ok_++;
                        return true;
                    }
                    frames_dropped_++;
                    need_keyframe_ = true;
                    want_pli = true;
                    continue;
                }
                // Waiting for keyframe, and this complete frame is NOT a keyframe: discard immediately so it NEVER head-of-line blocks an IDR behind it!
                if (need_keyframe_) {
                    advanceLastEmitted(maxSeq);
                    frames_.erase(bestIt);
                    continue;
                }
            }

            // If marker packet was lost, don't stall for full 250ms once a newer frame has already been waiting >=40ms!
            bool newerFrameWaiting = false;
            if (!fb.has_marker && frames_.size() > 1) {
                for (auto it = frames_.begin(); it != frames_.end(); ++it) {
                    if (it != bestIt && !it->second.packets.empty() &&
                        it->second.packets.begin()->first > maxSeq &&
                        (now_ms - it->second.first_ms >= 40)) {
                        newerFrameWaiting = true;
                        break;
                    }
                }
            }

            // Timeouts: wait 40ms after marker (or newer frame arrival on lost marker), or 250ms total for WebRTC-paced 1080p/1440p IDR keyframes!
            bool timedOut = (fb.has_marker && (now_ms - fb.marker_ms >= 40)) || newerFrameWaiting || (now_ms - fb.first_ms >= 250);
            if (!timedOut) break;

            if (frameComplete) {
                bool assembled = is_hevc_ ? assembleAnnexBHevc(fb, out.annexb) : assembleAnnexB(fb, out.annexb);
                out.timestamp = bestIt->first;
                out.is_keyframe = assembled && (is_hevc_ ? hasHevcKeyOrConfig(out.annexb.data(), out.annexb.size()) : hasIdrOrSps(out.annexb.data(), out.annexb.size()));
                int64_t gap = (last_emitted_max_seq_ >= 0) ? (minSeq - last_emitted_max_seq_ - 1) : 0;
                advanceLastEmitted(maxSeq);
                frames_.erase(bestIt);

                if (assembled && !out.annexb.empty()) {
                    if (out.is_keyframe) {
                        if (is_hevc_ ? hasHevcIdr(out.annexb.data(), out.annexb.size()) : hasIdr(out.annexb.data(), out.annexb.size())) need_keyframe_ = false;
                        frames_ok_++;
                        return true;
                    }
                    // Discord SFU frequently drops disposable temporal-layer P-frames between clean frame boundaries
                    // (small sequence gap <= 24 packets while the arriving frame is 100% internally intact!).
                    // Emit it smoothly instead of locking the entire stream at 1fps waiting for an IDR!
                    if (!need_keyframe_ && gap > 0 && gap <= 24) {
                        frames_ok_++;
                        want_pli = true; // gently ask for a refresh without stalling playback
                        return true;
                    }
                }
                frames_dropped_++;
                need_keyframe_ = true;
                want_pli = true;
                continue;
            }

            // Incomplete / corrupt frame: drop and strictly demand an IDR keyframe
            advanceLastEmitted(maxSeq);
            frames_.erase(bestIt);
            frames_dropped_++;
            need_keyframe_ = true;
            want_pli = true;
        }
        return false;
    }

    static bool hasIdr(const uint8_t* data, size_t len) {
        for (size_t i = 0; i + 3 < len; ++i) {
            if (data[i] == 0 && data[i + 1] == 0) {
                size_t sc = 0;
                if (data[i + 2] == 1) sc = 3;
                else if (i + 4 <= len && data[i + 2] == 0 && data[i + 3] == 1) sc = 4;
                if (sc > 0 && i + sc < len) {
                    uint8_t nt = data[i + sc] & 0x1F;
                    if (nt == 5) return true;
                    i += sc;
                }
            }
        }
        return false;
    }

    static bool hasIdrOrSps(const uint8_t* data, size_t len) {
        for (size_t i = 0; i + 3 < len; ++i) {
            if (data[i] == 0 && data[i + 1] == 0) {
                size_t sc = 0;
                if (data[i + 2] == 1) sc = 3;
                else if (i + 4 <= len && data[i + 2] == 0 && data[i + 3] == 1) sc = 4;
                if (sc > 0 && i + sc < len) {
                    uint8_t nt = data[i + sc] & 0x1F;
                    if (nt == 5 || nt == 7) return true;
                    i += sc;
                }
            }
        }
        return false;
    }

    static bool hasHevcIdr(const uint8_t* data, size_t len) {
        for (size_t i = 0; i + 4 < len; ++i) {
            if (data[i] == 0 && data[i + 1] == 0) {
                size_t sc = (data[i + 2] == 1) ? 3 : ((i + 5 <= len && data[i + 2] == 0 && data[i + 3] == 1) ? 4 : 0);
                if (sc > 0 && i + sc < len) {
                    uint8_t nt = (data[i + sc] >> 1) & 0x3F;
                    if (nt >= 16 && nt <= 21) return true;
                    i += sc;
                }
            }
        }
        return false;
    }

    static bool hasHevcKeyOrConfig(const uint8_t* data, size_t len) {
        for (size_t i = 0; i + 4 < len; ++i) {
            if (data[i] == 0 && data[i + 1] == 0) {
                size_t sc = (data[i + 2] == 1) ? 3 : ((i + 5 <= len && data[i + 2] == 0 && data[i + 3] == 1) ? 4 : 0);
                if (sc > 0 && i + sc < len) {
                    uint8_t nt = (data[i + sc] >> 1) & 0x3F;
                    if ((nt >= 16 && nt <= 21) || nt == 32 || nt == 33) return true;
                    i += sc;
                }
            }
        }
        return false;
    }

    bool needKeyframe() const { return need_keyframe_; }
    void setNeedKeyframe(bool v) { need_keyframe_ = v; }
    int64_t framesOk() const { return frames_ok_; }
    int64_t framesDropped() const { return frames_dropped_; }

private:
    struct FrameBuf {
        std::map<int64_t, std::vector<uint8_t>> packets;
        bool has_marker = false;
        int64_t marker_seq = -1;
        int64_t first_ms = 0;
        int64_t marker_ms = 0;
    };

    std::map<uint32_t, FrameBuf>::iterator findOldestFrame() {
        auto bestIt = frames_.begin();
        int64_t bestMinSeq = bestIt->second.packets.empty() ? INT64_MAX : bestIt->second.packets.begin()->first;
        for (auto it = frames_.begin(); it != frames_.end(); ++it) {
            if (!it->second.packets.empty() && it->second.packets.begin()->first < bestMinSeq) {
                bestMinSeq = it->second.packets.begin()->first;
                bestIt = it;
            }
        }
        return bestIt;
    }

    void advanceLastEmitted(int64_t maxSeq) {
        if (maxSeq > last_emitted_max_seq_) last_emitted_max_seq_ = maxSeq;
        while (!empty_probe_seqs_.empty() && *empty_probe_seqs_.begin() <= last_emitted_max_seq_) {
            empty_probe_seqs_.erase(empty_probe_seqs_.begin());
        }
    }

    int64_t unwrapSeq(uint16_t s) {
        if (!has_seq_) {
            has_seq_ = true;
            last_ext_seq_ = s;
            return s;
        }
        int64_t cand = (last_ext_seq_ & ~int64_t(0xFFFF)) | int64_t(s);
        if (cand < last_ext_seq_ - 32768) cand += 65536;
        else if (cand > last_ext_seq_ + 32768) cand -= 65536;
        if (cand > last_ext_seq_) last_ext_seq_ = cand;
        return cand;
    }

    static void appendStartCode(std::vector<uint8_t>& v) {
        v.push_back(0); v.push_back(0); v.push_back(0); v.push_back(1);
    }

    static bool assembleAnnexB(const FrameBuf& fb, std::vector<uint8_t>& out) {
        out.clear();
        std::vector<uint8_t> fu;
        bool inFu = false;

        for (const auto& [seq, pkt] : fb.packets) {
            if (pkt.empty()) continue;
            const uint8_t* pay = pkt.data();
            int payLen = int(pkt.size());
            uint8_t naluType = pay[0] & 0x1F;

            if (naluType >= 1 && naluType <= 23) {
                if (inFu) return false; // truncated preceding FU-A
                appendStartCode(out);
                out.insert(out.end(), pay, pay + payLen);
            } else if (naluType == 24) {
                if (inFu || payLen <= 3) return false;
                int off = 1;
                while (off + 2 <= payLen) {
                    uint16_t sz = uint16_t((uint16_t(pay[off]) << 8) | pay[off + 1]);
                    off += 2;
                    if (sz == 0 || off + sz > payLen) return false;
                    uint8_t subType = pay[off] & 0x1F;
                    if (subType == 0 || subType >= 24) return false;
                    appendStartCode(out);
                    out.insert(out.end(), pay + off, pay + off + sz);
                    off += sz;
                }
                if (off != payLen) return false; // trailing truncated bytes in STAP-A
            } else if (naluType == 28) {
                if (payLen <= 2) return false; // RFC 6184 §5.8: FU-A requires >=1 payload byte
                uint8_t fuHdr = pay[1];
                uint8_t origType = fuHdr & 0x1F;
                if (origType == 0 || origType >= 24) return false;
                bool startBit = (fuHdr & 0x80) != 0;
                bool endBit = (fuHdr & 0x40) != 0;
                if (startBit && endBit) return false; // RFC 6184 §5.8: S and E MUST NOT both be set
                if (startBit) {
                    if (inFu) return false; // nested FU-A start without endBit -> lost tail
                    fu.clear();
                    fu.push_back((pay[0] & 0xE0) | origType);
                    fu.insert(fu.end(), pay + 2, pay + payLen);
                    inFu = true;
                } else if (inFu) {
                    fu.insert(fu.end(), pay + 2, pay + payLen);
                } else {
                    return false;
                }
                if (endBit && inFu) {
                    appendStartCode(out);
                    out.insert(out.end(), fu.begin(), fu.end());
                    fu.clear();
                    inFu = false;
                }
            } else {
                // Unsupported / reserved NALU packetization type (0, 25..27 STAP-B/MTAP, 29 FU-B, 30..31)
                return false;
            }
        }
        return !inFu && !out.empty();
    }

    static bool assembleAnnexBHevc(const FrameBuf& fb, std::vector<uint8_t>& out) {
        out.clear();
        std::vector<uint8_t> fu;
        bool inFu = false;

        for (const auto& [seq, pkt] : fb.packets) {
            if (pkt.size() < 2) return false;
            const uint8_t* pay = pkt.data();
            int payLen = int(pkt.size());
            uint8_t nalType = (pay[0] >> 1) & 0x3F;

            if (nalType <= 47) {
                if (inFu) return false;
                appendStartCode(out);
                out.insert(out.end(), pay, pay + payLen);
            } else if (nalType == 48) { // RFC 7798 AP
                if (inFu || payLen <= 4) return false;
                int off = 2;
                while (off + 2 <= payLen) {
                    uint16_t sz = uint16_t((uint16_t(pay[off]) << 8) | pay[off + 1]);
                    off += 2;
                    if (sz < 2 || off + sz > payLen) return false;
                    appendStartCode(out);
                    out.insert(out.end(), pay + off, pay + off + sz);
                    off += sz;
                }
                if (off != payLen) return false;
            } else if (nalType == 49) { // RFC 7798 FU
                if (payLen <= 3) return false;
                uint8_t fuHdr = pay[2];
                uint8_t origType = fuHdr & 0x3F;
                if (origType > 47) return false;
                bool startBit = (fuHdr & 0x80) != 0;
                bool endBit = (fuHdr & 0x40) != 0;
                if (startBit && endBit) return false;
                if (startBit) {
                    if (inFu) return false;
                    fu.clear();
                    fu.push_back(uint8_t((pay[0] & 0x81) | (origType << 1)));
                    fu.push_back(pay[1]);
                    fu.insert(fu.end(), pay + 3, pay + payLen);
                    inFu = true;
                } else if (inFu) {
                    fu.insert(fu.end(), pay + 3, pay + payLen);
                } else {
                    return false;
                }
                if (endBit && inFu) {
                    appendStartCode(out);
                    out.insert(out.end(), fu.begin(), fu.end());
                    fu.clear();
                    inFu = false;
                }
            } else {
                return false;
            }
        }
        return !inFu && !out.empty();
    }

    std::map<uint32_t, FrameBuf> frames_;
    std::set<int64_t> empty_probe_seqs_;
    bool is_hevc_ = false;
    bool has_seq_ = false;
    int64_t last_ext_seq_ = -1;
    int64_t last_emitted_max_seq_ = -1;
    bool need_keyframe_ = true;
    int64_t frames_ok_ = 0;
    int64_t frames_dropped_ = 0;
};

} // namespace fc
