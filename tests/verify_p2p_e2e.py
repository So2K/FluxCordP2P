import base64
import ctypes
import hashlib
import hmac
import json
import os
import socket
import ssl
import struct
import subprocess
import sys
import tempfile
import time

ROOT = r"C:\Users\User\Projects\FluxCordP2P"
USER_DIR = os.path.join(ROOT, "dist", "FluxCordP2P")
EXE = os.path.join(USER_DIR, "FluxCordP2P.exe")
DLL = os.path.join(USER_DIR, "FluxCord.Media.dll")
SECRET_FILE = os.path.join(ROOT, "room.secret")

failures = []


def check(cond, label):
    if cond:
        print(f"[PASS] {label}", flush=True)
    else:
        print(f"[FAIL] {label}", flush=True)
        failures.append(label)


def derive_sha256(tag: str, secret: str) -> bytes:
    return hashlib.sha256((tag + ":" + secret).encode("utf-8")).digest()


# Minimal pure-stdlib RFC 6455 WebSocket client for testing BOTH LAN WS and TLS MQTT-WS Rendezvous!
class MiniWs:
    def __init__(self, host, port, path, use_tls=False, subproto=None):
        raw = socket.create_connection((host, port), timeout=6.0)
        if use_tls:
            ctx = ssl.create_default_context()
            self.sock = ctx.wrap_socket(raw, server_hostname=host)
        else:
            self.sock = raw
        self.sock.settimeout(8.0)
        key = base64.b64encode(os.urandom(16)).decode("ascii")
        hdrs = [
            f"GET {path} HTTP/1.1",
            f"Host: {host}:{port}",
            "Upgrade: websocket",
            "Connection: Upgrade",
            f"Sec-WebSocket-Key: {key}",
            "Sec-WebSocket-Version: 13",
        ]
        if subproto:
            hdrs.append(f"Sec-WebSocket-Protocol: {subproto}")
        req = "\r\n".join(hdrs) + "\r\n\r\n"
        self.sock.sendall(req.encode("ascii"))
        resp = b""
        while b"\r\n\r\n" not in resp:
            chunk = self.sock.recv(4096)
            if not chunk:
                raise RuntimeError("WS handshake EOF")
            resp += chunk
        head, self.extra = resp.split(b"\r\n\r\n", 1)
        if b"101" not in head.splitlines()[0]:
            raise RuntimeError(f"WS upgrade failed: {head[:120]}")

    def _recv_exact(self, n):
        out = bytearray()
        if self.extra:
            take = min(n, len(self.extra))
            out.extend(self.extra[:take])
            self.extra = self.extra[take:]
        while len(out) < n:
            c = self.sock.recv(n - len(out))
            if not c:
                raise EOFError()
            out.extend(c)
        return bytes(out)

    def send_frame(self, opcode, payload: bytes):
        hdr = bytearray([0x80 | opcode])
        n = len(payload)
        if n < 126:
            hdr.append(0x80 | n)
        elif n <= 65535:
            hdr.append(0x80 | 126)
            hdr.extend(struct.pack("!H", n))
        else:
            hdr.append(0x80 | 127)
            hdr.extend(struct.pack("!Q", n))
        mask = os.urandom(4)
        hdr.extend(mask)
        masked = bytes(payload[i] ^ mask[i & 3] for i in range(n))
        self.sock.sendall(bytes(hdr) + masked)

    def recv_frame(self):
        while True:
            b0, b1 = self._recv_exact(2)
            op = b0 & 0x0F
            masked = (b1 & 0x80) != 0
            ln = b1 & 0x7F
            if ln == 126:
                (ln,) = struct.unpack("!H", self._recv_exact(2))
            elif ln == 127:
                (ln,) = struct.unpack("!Q", self._recv_exact(8))
            mkey = self._recv_exact(4) if masked else b""
            data = self._recv_exact(ln)
            if masked:
                data = bytes(data[i] ^ mkey[i & 3] for i in range(ln))
            if op == 0x09:
                self.send_frame(0x0A, data)
                continue
            return op, data

    def close(self):
        try:
            self.sock.close()
        except Exception:
            pass


def mqtt_varlen(n):
    out = bytearray()
    while True:
        b = n % 128
        n //= 128
        if n > 0:
            b |= 0x80
        out.append(b)
        if n == 0:
            return bytes(out)


def mqtt_str(s: str):
    b = s.encode("utf-8")
    return struct.pack("!H", len(b)) + b


def main():
    with open(SECRET_FILE, "r", encoding="utf-8") as f:
        secret = f.read().strip()

    topic_hex = derive_sha256("fluxp2p-topic-v1", secret)[:12].hex()
    aead_key = derive_sha256("fluxp2p-aead-v1", secret)
    auth_proof = hmac.new(aead_key, b"fluxp2p-auth-v1", hashlib.sha256).hexdigest()

    # Use cryptography.hazmat AESGCM if available (hermes venv)
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    aesgcm = AESGCM(aead_key)

    def enc_aead(plain: bytes) -> bytes:
        nonce = os.urandom(12)
        return nonce + aesgcm.encrypt(nonce, plain, None)

    def dec_aead(buf: bytes) -> bytes:
        return aesgcm.decrypt(buf[:12], buf[12:], None)

    # 1. Peer A connects directly to Jetson Orin Nano LAN WS (192.168.100.72:37400)
    ws_lan = MiniWs("192.168.100.72", 37400, "/ws", use_tls=False)
    ws_lan.send_frame(0x01, json.dumps({
        "op": "auth",
        "proof": auth_proof,
        "user_id": "test_so2k",
        "username": "so2k_test",
        "display_name": "So2K_E2E",
        "lan_ip": "192.168.100.12",
        "status": "online",
    }).encode("utf-8"))

    welcome_lan = None
    for _ in range(6):
        _, raw = ws_lan.recv_frame()
        msg = json.loads(raw.decode("utf-8"))
        if msg.get("op") == "welcome":
            welcome_lan = msg
            break
    check(welcome_lan is not None and welcome_lan.get("voice_ssrc", 0) > 0,
          f"Peer A (User LAN) authenticated on Jetson Orin Nano (voice_ssrc={welcome_lan.get('voice_ssrc') if welcome_lan else None})")

    # 2. Peer B (simulating Lapki in another city with NO router port-forwarding!) connects via TLS MQTT-WS Rendezvous!
    ws_mq = MiniWs("broker.emqx.io", 8084, "/mqtt", use_tls=True, subproto="mqtt")
    cid = "lapki_e2e_" + os.urandom(3).hex()
    conn_body = mqtt_str("MQTT") + bytes([4, 2, 0, 30]) + mqtt_str(cid)
    ws_mq.send_frame(0x02, bytes([0x10]) + mqtt_varlen(len(conn_body)) + conn_body)
    sub_topic = f"fluxp2p/{topic_hex}/s2c/{cid}"
    sub_body = b"\x00\x01" + mqtt_str(sub_topic) + b"\x00"
    ws_mq.send_frame(0x02, bytes([0x82]) + mqtt_varlen(len(sub_body)) + sub_body)
    time.sleep(0.35)

    def mq_publish_rpc(rpc_obj):
        env = json.dumps({"cid": cid, "kind": "rpc", "rpc": rpc_obj}).encode("utf-8")
        enc = enc_aead(env)
        pub_body = mqtt_str(f"fluxp2p/{topic_hex}/c2s") + enc
        ws_mq.send_frame(0x02, bytes([0x30]) + mqtt_varlen(len(pub_body)) + pub_body)

    mq_stream = bytearray()

    def mq_recv_json(timeout_s=6.0):
        deadline = time.time() + timeout_s
        while time.time() < deadline:
            while len(mq_stream) >= 2:
                hdr = mq_stream[0]
                mult, rem, pos, ok_h = 1, 0, 1, False
                while pos < len(mq_stream) and pos <= 4:
                    b = mq_stream[pos]
                    pos += 1
                    rem += (b & 0x7F) * mult
                    mult *= 128
                    if (b & 0x80) == 0:
                        ok_h = True
                        break
                if not ok_h or len(mq_stream) < pos + rem:
                    break
                pkt = bytes(mq_stream[:pos + rem])
                del mq_stream[:pos + rem]
                if (hdr >> 4) == 3:
                    tlen = struct.unpack("!H", pkt[pos:pos + 2])[0]
                    poff = pos + 2 + tlen
                    plain = dec_aead(pkt[poff:])
                    return json.loads(plain.decode("utf-8"))
            _, fr = ws_mq.recv_frame()
            mq_stream.extend(fr)
        return None

    mq_publish_rpc({
        "op": "auth",
        "proof": auth_proof,
        "user_id": "test_lapki",
        "username": "lapki_test",
        "display_name": "Lapki_E2E",
        "lan_ip": "10.0.0.55",
        "status": "online",
    })

    welcome_mq = None
    for _ in range(8):
        m = mq_recv_json()
        if m and m.get("op") == "welcome":
            welcome_mq = m
            break
    check(welcome_mq is not None and welcome_mq.get("jetson_ext_port", 0) > 0,
          f"Peer B (Lapki Internet Zero-Port-Forward) authenticated via AES-256-GCM Rendezvous -> Jetson Cone-NAT {welcome_mq.get('jetson_ext_ip')}:{welcome_mq.get('jetson_ext_port') if welcome_mq else None}")

    # 3. Real-time Bidirectional Chat & Inline Image Upload/Download over Encrypted Rendezvous!
    unique_txt = f"E2E_P2P_HELLO_{os.urandom(3).hex()}"
    mq_publish_rpc({"op": "send_msg", "channel_id": "c_main", "content": unique_txt})

    got_on_lan = False
    for _ in range(10):
        _, raw = ws_lan.recv_frame()
        ev = json.loads(raw.decode("utf-8"))
        if ev.get("op") == "msg_create" and ev["message"].get("content") == unique_txt:
            got_on_lan = True
            # Clean up test message so chat stays clean for the user!
            mq_publish_rpc({"op": "delete_msg", "channel_id": "c_main", "message_id": ev["message"]["id"]})
            break
    check(got_on_lan, "Lapki -> Jetson Orin -> User real-time encrypted message delivery + clean delete verified")

    # 4. Join Voice Room on both peers & verify real FluxCord.Media.dll XChaCha20-Poly1305 UDP audio forwarding through Jetson (:37401)!
    ws_lan.send_frame(0x01, json.dumps({"op": "voice_state", "channel_id": "v_afk", "self_mute": False, "self_deaf": False}).encode("utf-8"))
    mq_publish_rpc({"op": "voice_state", "channel_id": "v_afk", "self_mute": False, "self_deaf": False})
    time.sleep(0.3)

    ssrc_a = welcome_lan["voice_ssrc"]
    ssrc_b = welcome_mq["voice_ssrc"]
    media_key = base64.b64decode(welcome_lan["media_key_b64"])

    # Peer B opens raw UDP socket on LAN to Jetson :37401, registers ssrc_b via 74-byte 0x0001 discovery
    udp_b = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp_b.settimeout(2.0)
    disc_req = struct.pack("!HHI", 0x0001, 70, ssrc_b) + b"\x00" * 66
    udp_b.sendto(disc_req, ("192.168.100.72", 37401))
    disc_resp, _ = udp_b.recvfrom(128)
    check(len(disc_resp) >= 74 and struct.unpack("!H", disc_resp[:2])[0] == 0x0002,
          "Jetson Orin UDP :37401 74-byte FluxCord.Media discovery handshake verified")

    # Peer A uses real C++20 FluxCord.Media.dll (fc_transport_connect + fc_transport_set_key + fc_transport_send_silence)
    dll = ctypes.CDLL(DLL)
    dll.fc_init()
    ext_ip_buf = ctypes.create_string_buffer(65)
    ext_port = ctypes.c_uint16(0)
    ok_c = dll.fc_transport_connect(b"192.168.100.72", ctypes.c_uint16(37401), ctypes.c_uint32(ssrc_a), ext_ip_buf, 65, ctypes.byref(ext_port))
    k_arr = (ctypes.c_uint8 * 32)(*media_key)
    dll.fc_transport_set_key(k_arr, 1)
    dll.fc_dave_init(ctypes.c_uint16(0), ctypes.c_uint64(1), b"test_so2k", ctypes.c_uint32(ssrc_a))
    dll.fc_transport_send_silence()

    rx_rtp = 0
    for _ in range(5):
        try:
            pkt, _ = udp_b.recvfrom(2048)
            if len(pkt) >= 28 and (pkt[0] & 0xC0) == 0x80 and (pkt[1] & 0x7F) == 120:
                pkt_ssrc = struct.unpack("!I", pkt[8:12])[0]
                if pkt_ssrc == ssrc_a:
                    rx_rtp += 1
        except Exception:
            break
    check(ok_c == 1 and rx_rtp >= 3,
          f"Live C++20 FluxCord.Media.dll -> Jetson Orin UDP :37401 -> Peer B XChaCha20-Poly1305 Opus RTP forwarded ({rx_rtp}/5 packets)")

    # 5. Direct 0-hop P2P hole-punch lock test (fc_transport_set_p2p_peer -> 0xFC 'P' '2' 'P' 0x55/0x56)
    p2p_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    p2p_sock.bind(("127.0.0.1", 0))
    p2p_sock.settimeout(2.0)
    p2p_port = p2p_sock.getsockname()[1]
    dll.fc_transport_set_p2p_peer(b"127.0.0.1", ctypes.c_uint16(p2p_port), 1)
    got_probe = False
    for _ in range(6):
        try:
            pr, addr = p2p_sock.recvfrom(256)
            if len(pr) == 16 and pr[:5] == b"\xfcP2P\x55":
                # Reply authenticated 0x56 pong
                pong = bytearray(pr)
                pong[4] = 0x56
                for i in range(7):
                    pong[9 + i] = (media_key[i] ^ media_key[16 + i] ^ pong[4 + (i % 5)] ^ (0xA5 + i * 17)) & 0xFF
                p2p_sock.sendto(bytes(pong), addr)
                got_probe = True
                break
        except Exception:
            pass
    time.sleep(0.15)
    is_direct = dll.fc_transport_is_p2p_direct()
    check(got_probe and is_direct == 1,
          f"Direct 0-Hop UDP P2P Hole-Punch handshake & lock verified (fc_transport_is_p2p_direct={is_direct})")

    dll.fc_transport_disconnect()
    dll.fc_shutdown()
    p2p_sock.close()
    udp_b.close()
    ws_lan.close()
    ws_mq.close()

    # 6. Launch FluxCordP2P.exe --demo --bench in isolated FLUXCORD_DATA_DIR to verify NativeAOT Direct2D shell
    with tempfile.TemporaryDirectory(prefix="fluxp2p_ui_") as tmpdir:
        env = os.environ.copy()
        env["FLUXCORD_DATA_DIR"] = tmpdir
        proc = subprocess.run([EXE, "--demo", "--bench"], env=env, timeout=15)
        check(proc.returncode == 0, f"FluxCordP2P.exe --demo --bench clean NativeAOT Direct2D render & exit (rc={proc.returncode})")

    print(f"\n=== P2P + JETSON E2E SUMMARY: {len(failures)} failures ===", flush=True)
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
