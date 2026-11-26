#pragma once
#include <winsock2.h>
#include <ws2tcpip.h>
#include <mswsock.h>
#include <mstcpip.h>
#include <windows.h>
#include <avrt.h>
#include <mmsystem.h>
#include <atomic>
#include <cstdint>
#include <mutex>

#ifndef SIO_UDP_CONNRESET
#define SIO_UDP_CONNRESET _WSAIOW(IOC_VENDOR, 12)
#endif

namespace fc {

inline void enableHighResTimer(bool enable) {
    static std::atomic<bool> g_timerActive{false};
    if (enable) {
        if (!g_timerActive.exchange(true)) timeBeginPeriod(1);
    } else {
        if (g_timerActive.exchange(false)) timeEndPeriod(1);
    }
}

inline HANDLE boostRealtimeThread(const wchar_t* taskName = L"Pro Audio") {
    DWORD idx = 0;
    HANDLE mm = AvSetMmThreadCharacteristicsW(taskName, &idx);
    if (mm) {
        AvSetMmThreadPriority(mm, AVRT_PRIORITY_HIGH);
    } else {
        // Only fall back to manual thread priority when MMCSS is unavailable (avoids MMCSS priority conflict)
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    }
    return mm;
}

inline void revertRealtimeThread(HANDLE mm) {
    if (mm) {
        AvRevertMmThreadCharacteristics(mm);
    } else {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_NORMAL);
    }
}

namespace detail {
struct QosVer { USHORT Major; USHORT Minor; };
using QosCreateFn = BOOL (WINAPI*)(QosVer*, PHANDLE);
using QosAddFn = BOOL (WINAPI*)(HANDLE, SOCKET, PSOCKADDR, int, DWORD, PULONG);
using QosRemoveFn = BOOL (WINAPI*)(HANDLE, SOCKET, ULONG, DWORD);

struct QwaveApi {
    HANDLE hQos = nullptr;
    QosAddFn pAdd = nullptr;
    QosRemoveFn pRemove = nullptr;
};

inline QwaveApi& getQwave() {
    static std::once_flag once;
    static QwaveApi api{};
    std::call_once(once, []() {
        HMODULE hMod = LoadLibraryExW(L"qwave.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!hMod) return;
        auto pCreate = reinterpret_cast<QosCreateFn>(GetProcAddress(hMod, "QOSCreateHandle"));
        api.pAdd = reinterpret_cast<QosAddFn>(GetProcAddress(hMod, "QOSAddSocketToFlow"));
        api.pRemove = reinterpret_cast<QosRemoveFn>(GetProcAddress(hMod, "QOSRemoveSocketFromFlow"));
        if (pCreate) {
            QosVer ver{ 1, 0 };
            pCreate(&ver, &api.hQos);
        }
    });
    return api;
}
} // namespace detail

inline void removeUdpQosFlow(SOCKET s, ULONG& flowId) {
    if (s == INVALID_SOCKET || flowId == 0) return;
    auto& q = detail::getQwave();
    if (q.hQos && q.pRemove) {
        q.pRemove(q.hQos, s, flowId, 0);
    }
    flowId = 0;
}

// Maximize UDP throughput & minimize ping jitter via OS buffer tuning + DSCP QoS (EF46 Voice / AF41 Video) + Windows qWAVE!
inline void optimizeUdpSocket(SOCKET s, bool isVideo, const sockaddr_in* remote = nullptr, ULONG* inOutFlowId = nullptr) {
    if (s == INVALID_SOCKET) return;

    // 1. Disable Windows SIO_UDP_CONNRESET (prevents ICMP Port Unreachable from stalling recvfrom!)
    BOOL newBehavior = FALSE;
    DWORD bytesRet = 0;
    WSAIoctl(s, SIO_UDP_CONNRESET, &newBehavior, sizeof(newBehavior), nullptr, 0, &bytesRet, nullptr, nullptr);

    // 2. Large kernel socket buffers -> ZERO drops on 4K60/120fps IDR bursts!
    int sndBuf = isVideo ? (4 * 1024 * 1024) : (2 * 1024 * 1024);
    int rcvBuf = isVideo ? (8 * 1024 * 1024) : (4 * 1024 * 1024);
    setsockopt(s, SOL_SOCKET, SO_SNDBUF, reinterpret_cast<const char*>(&sndBuf), sizeof(sndBuf));
    setsockopt(s, SOL_SOCKET, SO_RCVBUF, reinterpret_cast<const char*>(&rcvBuf), sizeof(rcvBuf));

    // 3. IP DSCP Traffic Class: EF (46 -> 0xB8) for Voice, AF41 (34 -> 0x88) for Interactive Video
    int tos = isVideo ? 0x88 : 0xB8;
    setsockopt(s, IPPROTO_IP, IP_TOS, reinterpret_cast<const char*>(&tos), sizeof(tos));

    // 4. Windows qWAVE QoS Flow (thread-safe init + clean flow replacement so Voice->Video upgrade works without leaking!)
    if (remote) {
        auto& q = detail::getQwave();
        if (q.hQos && q.pAdd) {
            if (inOutFlowId && *inOutFlowId != 0 && q.pRemove) {
                q.pRemove(q.hQos, s, *inOutFlowId, 0);
                *inOutFlowId = 0;
            }
            ULONG flowId = 0;
            // QOSTrafficTypeVoice = 4, QOSTrafficTypeAudioVideo = 3, QOS_NON_ADAPTIVE_FLOW = 0x00000002
            if (q.pAdd(q.hQos, s, const_cast<sockaddr*>(reinterpret_cast<const sockaddr*>(remote)),
                       isVideo ? 3 : 4, 0x00000002, &flowId)) {
                if (inOutFlowId) *inOutFlowId = flowId;
            }
        }
    }
}

namespace p2p {
struct PendingStun {
    std::mutex mtx;
    char ip[64]{};
    uint16_t port = 0;
    bool ready = false;
};

inline PendingStun& stunState() {
    static PendingStun st{};
    return st;
}

inline void publishStunEndpoint(const char* ip, uint16_t port) {
    if (!ip || !*ip || port == 0) return;
    auto& s = stunState();
    std::lock_guard lk(s.mtx);
    std::memset(s.ip, 0, sizeof(s.ip));
    std::strncpy(s.ip, ip, sizeof(s.ip) - 1);
    s.port = port;
    s.ready = true;
}

inline bool consumeStunEndpoint(char* out_ip, int32_t cap, uint16_t* out_port) {
    auto& s = stunState();
    std::lock_guard lk(s.mtx);
    if (!s.ready) return false;
    s.ready = false;
    if (out_ip && cap > 0) {
        std::memset(out_ip, 0, size_t(cap));
        std::strncpy(out_ip, s.ip, size_t(cap - 1));
    }
    if (out_port) *out_port = s.port;
    return true;
}

inline bool isPrivateIpv4Addr(const sockaddr_in& a) {
    uint32_t h = ntohl(a.sin_addr.s_addr);
    uint8_t b0 = uint8_t(h >> 24), b1 = uint8_t(h >> 16);
    return b0 == 127 || b0 == 10 || (b0 == 172 && b1 >= 16 && b1 <= 31) || (b0 == 192 && b1 == 168);
}

inline void sendCloudflareStunReq(SOCKET s) {
    sockaddr_in stunAddr{};
    stunAddr.sin_family = AF_INET;
    stunAddr.sin_port = htons(3478);
    inet_pton(AF_INET, "162.159.207.0", &stunAddr.sin_addr); // stun.cloudflare.com Anycast IP
    uint8_t req[20]{};
    req[0] = 0x00; req[1] = 0x01; // Binding Request
    req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42; // Magic Cookie
    uint64_t t = GetTickCount64();
    std::memcpy(req + 8, &t, 8);
    req[16] = 0xFC; req[17] = 0x50; req[18] = 0x32; req[19] = 0x50;
    sendto(s, reinterpret_cast<const char*>(req), sizeof(req), 0, reinterpret_cast<sockaddr*>(&stunAddr), sizeof(stunAddr));
}

inline bool tryParseStunXorMapped(const uint8_t* pkt, int n) {
    if (!pkt || n < 20) return false;
    if (pkt[0] != 0x01 || pkt[1] != 0x01) return false;
    if (pkt[4] != 0x21 || pkt[5] != 0x12 || pkt[6] != 0xA4 || pkt[7] != 0x42) return false;
    int pos = 20;
    while (pos + 4 <= n) {
        uint16_t at = uint16_t((uint16_t(pkt[pos]) << 8) | pkt[pos + 1]);
        uint16_t al = uint16_t((uint16_t(pkt[pos + 2]) << 8) | pkt[pos + 3]);
        if (pos + 4 + int(al) > n) break;
        const uint8_t* val = pkt + pos + 4;
        if (at == 0x0020 && al >= 8) {
            uint16_t xport = uint16_t(((uint16_t(val[2]) << 8) | val[3]) ^ 0x2112u);
            uint32_t xip = ((uint32_t(val[4]) << 24) | (uint32_t(val[5]) << 16) | (uint32_t(val[6]) << 8) | val[7]) ^ 0x2112A442u;
            in_addr ia{};
            ia.s_addr = htonl(xip);
            char ipStr[64]{};
            if (inet_ntop(AF_INET, &ia, ipStr, sizeof(ipStr))) {
                publishStunEndpoint(ipStr, xport);
                return true;
            }
        }
        pos += 4 + ((int(al) + 3) & ~3);
    }
    return false;
}

inline void buildProbe(uint8_t out[16], uint8_t cmd, uint32_t tsMs, const uint8_t key[32]) {
    out[0] = 0xFC; out[1] = 'P'; out[2] = '2'; out[3] = 'P';
    out[4] = cmd;
    out[5] = uint8_t(tsMs >> 24); out[6] = uint8_t(tsMs >> 16);
    out[7] = uint8_t(tsMs >> 8);  out[8] = uint8_t(tsMs);
    for (int i = 0; i < 7; ++i) {
        out[9 + i] = uint8_t(key[i] ^ key[16 + i] ^ out[4 + (i % 5)] ^ uint8_t(0xA5 + i * 17));
    }
}

inline bool verifyProbe(const uint8_t* pkt, int n, const uint8_t key[32], uint8_t& outCmd, uint32_t& outTsMs) {
    if (!pkt || n != 16) return false;
    if (pkt[0] != 0xFC || pkt[1] != 'P' || pkt[2] != '2' || pkt[3] != 'P') return false;
    if (pkt[4] != 0x55 && pkt[4] != 0x56) return false;
    for (int i = 0; i < 7; ++i) {
        uint8_t exp = uint8_t(key[i] ^ key[16 + i] ^ pkt[4 + (i % 5)] ^ uint8_t(0xA5 + i * 17));
        if (pkt[9 + i] != exp) return false;
    }
    outCmd = pkt[4];
    outTsMs = (uint32_t(pkt[5]) << 24) | (uint32_t(pkt[6]) << 16) | (uint32_t(pkt[7]) << 8) | pkt[8];
    return true;
}
} // namespace p2p

} // namespace fc
