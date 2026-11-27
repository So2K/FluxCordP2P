package main

import (
	"bufio"
	"bytes"
	"crypto/aes"
	"crypto/cipher"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha1"
	"crypto/sha256"
	"crypto/tls"
	"encoding/base64"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"html"
	"io"
	"log"
	"net"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// ============================================================================
// FluxP2P Hub for NVIDIA Jetson Orin Nano (Go 1.18+ Zero-Dependency ARM64)
// - HTTP + Native RFC 6455 WebSocket Signaling & Attachment Store (:37400)
// - Zero-Copy RTP/RTCP Media Router + STUN Cone-NAT Keepalive (:37401)
// - Zero-Config AES-256-GCM Encrypted Global Rendezvous (MQTT-over-TLS-WS)
//   for automatic Cone-NAT UDP Hole-Punching without router port-forwarding!
// ============================================================================

type Channel struct {
	Id       string `json:"id"`
	GuildId  string `json:"guild_id"`
	Name     string `json:"name"`
	Topic    string `json:"topic"`
	Type     int    `json:"type"` // 0=text, 2=voice, 4=category
	Position int    `json:"position"`
	ParentId string `json:"parent_id"`
}

type ReactionDto struct {
	Emoji string   `json:"emoji"`
	Users []string `json:"users"`
}

type AttachmentDto struct {
	Url         string `json:"url"`
	Filename    string `json:"filename"`
	Width       int    `json:"width"`
	Height      int    `json:"height"`
	Size        int64  `json:"size"`
	ContentType string `json:"content_type"`
}

type EmbedDto struct {
	Title       string `json:"title,omitempty"`
	Description string `json:"description,omitempty"`
	Url         string `json:"url,omitempty"`
	Author      string `json:"author,omitempty"`
	Footer      string `json:"footer,omitempty"`
	ImageUrl    string `json:"image_url,omitempty"`
	Color       uint32 `json:"color,omitempty"`
}

type MessageDto struct {
	Id          string          `json:"id"`
	ChannelId   string          `json:"channel_id"`
	AuthorId    string          `json:"author_id"`
	AuthorName  string          `json:"author_name"`
	AuthorColor uint32          `json:"author_color"`
	Avatar      string          `json:"avatar"`
	Content     string          `json:"content"`
	Timestamp   string          `json:"timestamp"`
	DateLabel   string          `json:"date_label"`
	CreatedUnix int64           `json:"created_unix"`
	Edited      bool            `json:"edited"`
	Pinned      bool            `json:"pinned"`
	MsgType     int             `json:"msg_type"`
	ReplyToId   string          `json:"reply_to_id,omitempty"`
	ReplyAuthor string          `json:"reply_author,omitempty"`
	ReplyText   string          `json:"reply_text,omitempty"`
	Attachments []AttachmentDto `json:"attachments,omitempty"`
	Embeds      []EmbedDto      `json:"embeds,omitempty"`
	Reactions   []ReactionDto   `json:"reactions,omitempty"`
}

type SavedProfile struct {
	DisplayName  string `json:"display_name"`
	Avatar       string `json:"avatar"`
	ActivityText string `json:"activity_text"`
}

type PersistedState struct {
	ServerName string                  `json:"server_name"`
	Channels   []Channel               `json:"channels"`
	Messages   map[string][]MessageDto `json:"messages"`
	Profiles   map[string]SavedProfile `json:"profiles,omitempty"`
}

type WatchSub struct {
	Slot           int
	StreamerUserId string
	LocalSsrc      uint32
	Addr           *net.UDPAddr
}

type PeerSession struct {
	SessionId    string
	UserId       string
	Username     string
	DisplayName  string
	Avatar       string
	Status       string
	ActivityText string
	Color        uint32

	WsConn      *wsConn
	MqttCid     string
	LanIp       string
	LastSeenMs  int64

	VoiceChannelId string
	SelfMute       bool
	SelfDeaf       bool
	SelfStream     bool
	StreamWidth    int
	StreamHeight   int
	StreamFps      int
	StreamDual     bool
	Speaking       bool

	VoiceSsrc    uint32
	VoiceAddr    *net.UDPAddr
	VoiceExtIp   string
	VoiceExtPort uint16

	PubAudioSsrc uint32
	PubVideoSsrc uint32
	PubAddr      *net.UDPAddr
	PubExtIp     string
	PubExtPort   uint16

	Watches map[int]*WatchSub // slot -> WatchSub
}

type PeerPublicDto struct {
	UserId         string `json:"user_id"`
	Username       string `json:"username"`
	DisplayName    string `json:"display_name"`
	Avatar         string `json:"avatar"`
	Status         string `json:"status"`
	ActivityText   string `json:"activity_text"`
	Color          uint32 `json:"color"`
	VoiceChannelId string `json:"voice_channel_id"`
	SelfMute       bool   `json:"self_mute"`
	SelfDeaf       bool   `json:"self_deaf"`
	SelfStream     bool   `json:"self_stream"`
	StreamWidth    int    `json:"stream_width"`
	StreamHeight   int    `json:"stream_height"`
	StreamFps      int    `json:"stream_fps"`
	StreamDual     bool   `json:"stream_dual"`
	Speaking       bool   `json:"speaking"`
	VoiceSsrc      uint32 `json:"voice_ssrc"`
	VoiceExtIp     string `json:"voice_ext_ip"`
	VoiceExtPort   uint16 `json:"voice_ext_port"`
	LanIp          string `json:"lan_ip"`
	PubAudioSsrc   uint32 `json:"pub_audio_ssrc"`
	PubVideoSsrc   uint32 `json:"pub_video_ssrc"`
	PubExtIp       string `json:"pub_ext_ip"`
	PubExtPort     uint16 `json:"pub_ext_port"`
}

type Hub struct {
	mu          sync.RWMutex
	dataDir     string
	statePath   string
	roomSecret  string
	roomTopic   string
	aeadKey     [32]byte
	mediaKey    [32]byte
	gcm         cipher.AEAD
	httpPort    int
	udpPort     int
	udpConn     *net.UDPConn
	startTime   time.Time

	extMu       sync.RWMutex
	extIp       string
	extUdpPort  uint16
	lanIp       string

	state       PersistedState
	dirty       int32

	peers       map[string]*PeerSession // sessionId -> PeerSession
	ssrcOwner   map[uint32]ssrcBinding  // ssrc -> binding
	nextSsrc    uint32
	msgSeq      uint64

	mqttSendCh  chan mqttOutMsg
}

type ssrcRole int

const (
	roleVoice ssrcRole = 1
	rolePubA  ssrcRole = 2
	rolePubV  ssrcRole = 3
	roleWatch ssrcRole = 4
)

type ssrcBinding struct {
	SessionId string
	Role      ssrcRole
	Slot      int
}

type mqttOutMsg struct {
	Topic   string
	Payload []byte
}

func nowMs() int64 { return time.Now().UnixNano() / 1e6 }

func deriveBytes(tag, secret string) [32]byte {
	return sha256.Sum256([]byte(tag + ":" + secret))
}

func detectLanIP() string {
	conn, err := net.Dial("udp4", "1.1.1.1:80")
	if err != nil {
		return "192.168.100.72"
	}
	defer conn.Close()
	if ua, ok := conn.LocalAddr().(*net.UDPAddr); ok {
		return ua.IP.String()
	}
	return "192.168.100.72"
}

func newHub(dataDir, secret string, httpPort, udpPort int) (*Hub, error) {
	if err := os.MkdirAll(filepath.Join(dataDir, "uploads"), 0755); err != nil {
		return nil, err
	}
	topicHash := deriveBytes("fluxp2p-topic-v1", secret)
	aeadKey := deriveBytes("fluxp2p-aead-v1", secret)
	mediaKey := deriveBytes("fluxp2p-rtp-v1", secret)
	blk, err := aes.NewCipher(aeadKey[:])
	if err != nil {
		return nil, err
	}
	gcm, err := cipher.NewGCM(blk)
	if err != nil {
		return nil, err
	}

	h := &Hub{
		dataDir:    dataDir,
		statePath:  filepath.Join(dataDir, "hub_state.json"),
		roomSecret: secret,
		roomTopic:  hex.EncodeToString(topicHash[:12]),
		aeadKey:    aeadKey,
		mediaKey:   mediaKey,
		gcm:        gcm,
		httpPort:   httpPort,
		udpPort:    udpPort,
		startTime:  time.Now(),
		lanIp:      detectLanIP(),
		peers:      make(map[string]*PeerSession),
		ssrcOwner:  make(map[uint32]ssrcBinding),
		nextSsrc:   10000,
		msgSeq:     uint64(time.Now().UnixNano() / 1e6),
		mqttSendCh: make(chan mqttOutMsg, 256),
	}
	h.loadOrInitState()
	return h, nil
}

func (h *Hub) loadOrInitState() {
	raw, err := os.ReadFile(h.statePath)
	if err == nil && len(raw) > 0 {
		if json.Unmarshal(raw, &h.state) == nil && len(h.state.Channels) > 0 {
			if h.state.Messages == nil {
				h.state.Messages = make(map[string][]MessageDto)
			}
			if h.state.Profiles == nil {
				h.state.Profiles = make(map[string]SavedProfile)
			}
			return
		}
	}
	now := time.Now()
	h.state = PersistedState{
		ServerName: "FluxCord P2P • Jetson Orin",
		Channels: []Channel{
			{Id: "cat_txt", GuildId: "g_p2p", Name: "ЧАТЫ КОМНАТЫ", Type: 4, Position: 0},
			{Id: "c_main", GuildId: "g_p2p", Name: "общий", Topic: "Прямой зашифрованный P2P + Jetson Orin Nano узел (без РКН и лагов Discord)", Type: 0, Position: 1, ParentId: "cat_txt"},
			{Id: "c_media", GuildId: "g_p2p", Name: "стримы-и-файлы", Topic: "Скриншоты, ссылки и файлы", Type: 0, Position: 2, ParentId: "cat_txt"},
			{Id: "cat_vc", GuildId: "g_p2p", Name: "ГОЛОС И 4K60 СТРИМЫ", Type: 4, Position: 3},
			{Id: "v_lapki", GuildId: "g_p2p", Name: "Ламповая (P2P + 4K60)", Type: 2, Position: 4, ParentId: "cat_vc"},
			{Id: "v_dual", GuildId: "g_p2p", Name: "2 Экрана • Игровая", Type: 2, Position: 5, ParentId: "cat_vc"},
			{Id: "v_afk", GuildId: "g_p2p", Name: "АФК", Type: 2, Position: 6, ParentId: "cat_vc"},
		},
		Messages: map[string][]MessageDto{
			"c_main": {
				{
					Id:          "1000001",
					ChannelId:   "c_main",
					AuthorId:    "jetson_sys",
					AuthorName:  "Jetson Orin Nano Hub",
					AuthorColor: 0xFF7BD88F,
					Content:     "Узел FluxCord P2P на Jetson Orin Nano запущен! Голос 48 кГц Opus + все 6 шумодавов + 4K60 / 2-Экрана Zero-Copy NVENC работают напрямую через XChaCha20-Poly1305 UDP.",
					Timestamp:   now.Format("15:04"),
					DateLabel:   russianDateLabel(now),
					CreatedUnix: now.Unix(),
				},
			},
		},
	}
	h.saveStateSync()
}

var ruMonths = []string{"", "января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря"}

func russianDateLabel(t time.Time) string {
	m := int(t.Month())
	mn := "ноября"
	if m >= 1 && m <= 12 {
		mn = ruMonths[m]
	}
	return fmt.Sprintf("%d %s %d г.", t.Day(), mn, t.Year())
}

func (h *Hub) saveStateSync() {
	h.mu.RLock()
	raw, err := json.MarshalIndent(&h.state, "", "  ")
	h.mu.RUnlock()
	if err != nil {
		return
	}
	tmp := h.statePath + ".tmp"
	if os.WriteFile(tmp, raw, 0644) == nil {
		_ = os.Rename(tmp, h.statePath)
	}
}

func (h *Hub) markDirty() {
	atomic.StoreInt32(&h.dirty, 1)
}

func (h *Hub) backgroundFlusher() {
	t := time.NewTicker(2 * time.Second)
	for range t.C {
		if atomic.CompareAndSwapInt32(&h.dirty, 1, 0) {
			h.saveStateSync()
		}
		// Prune ghost MQTT-only sessions idle > 90s
		now := nowMs()
		var stale []string
		h.mu.RLock()
		for sid, p := range h.peers {
			if p.WsConn == nil && now-p.LastSeenMs > 90000 {
				stale = append(stale, sid)
			}
		}
		h.mu.RUnlock()
		for _, sid := range stale {
			h.removePeer(sid)
		}
	}
}

func (h *Hub) encryptAEAD(plain []byte) ([]byte, error) {
	nonce := make([]byte, 12)
	if _, err := io.ReadFull(rand.Reader, nonce); err != nil {
		return nil, err
	}
	out := make([]byte, 12, 12+len(plain)+16)
	copy(out, nonce)
	return h.gcm.Seal(out, nonce, plain, nil), nil
}

func (h *Hub) decryptAEAD(cipherWithNonce []byte) ([]byte, error) {
	if len(cipherWithNonce) < 28 {
		return nil, fmt.Errorf("short aead")
	}
	return h.gcm.Open(nil, cipherWithNonce[:12], cipherWithNonce[12:], nil)
}

func (h *Hub) allocSsrcLocked(sid string, role ssrcRole, slot int) uint32 {
	h.nextSsrc += 4
	s := h.nextSsrc
	h.ssrcOwner[s] = ssrcBinding{SessionId: sid, Role: role, Slot: slot}
	return s
}

func (h *Hub) getExtEndpoint() (string, uint16) {
	h.extMu.RLock()
	defer h.extMu.RUnlock()
	ip := h.extIp
	pt := h.extUdpPort
	if ip == "" {
		ip = h.lanIp
	}
	if pt == 0 {
		pt = uint16(h.udpPort)
	}
	return ip, pt
}

func (h *Hub) peerDtosLocked() []PeerPublicDto {
	out := make([]PeerPublicDto, 0, len(h.peers))
	// Deduplicate by UserId (latest session wins)
	byUid := make(map[string]*PeerSession, len(h.peers))
	for _, p := range h.peers {
		if p.UserId == "" {
			continue
		}
		prev, exists := byUid[p.UserId]
		if !exists || p.LastSeenMs >= prev.LastSeenMs {
			byUid[p.UserId] = p
		}
	}
	for _, p := range byUid {
		out = append(out, PeerPublicDto{
			UserId:         p.UserId,
			Username:       p.Username,
			DisplayName:    p.DisplayName,
			Avatar:         p.Avatar,
			Status:         p.Status,
			ActivityText:   p.ActivityText,
			Color:          p.Color,
			VoiceChannelId: p.VoiceChannelId,
			SelfMute:       p.SelfMute,
			SelfDeaf:       p.SelfDeaf,
			SelfStream:     p.SelfStream,
			StreamWidth:    p.StreamWidth,
			StreamHeight:   p.StreamHeight,
			StreamFps:      p.StreamFps,
			StreamDual:     p.StreamDual,
			Speaking:       p.Speaking,
			VoiceSsrc:      p.VoiceSsrc,
			VoiceExtIp:     p.VoiceExtIp,
			VoiceExtPort:   p.VoiceExtPort,
			LanIp:          p.LanIp,
			PubAudioSsrc:   p.PubAudioSsrc,
			PubVideoSsrc:   p.PubVideoSsrc,
			PubExtIp:       p.PubExtIp,
			PubExtPort:     p.PubExtPort,
		})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].DisplayName < out[j].DisplayName })
	return out
}

func (h *Hub) sendToPeer(p *PeerSession, obj interface{}) {
	raw, err := json.Marshal(obj)
	if err != nil {
		return
	}
	if p.WsConn != nil {
		_ = p.WsConn.WriteText(raw)
		return
	}
	if p.MqttCid != "" {
		if enc, err := h.encryptAEAD(raw); err == nil {
			select {
			case h.mqttSendCh <- mqttOutMsg{Topic: fmt.Sprintf("fluxp2p/%s/s2c/%s", h.roomTopic, p.MqttCid), Payload: enc}:
			default:
			}
		}
	}
}

func (h *Hub) broadcastPeersState() {
	h.mu.RLock()
	peers := h.peerDtosLocked()
	targets := make([]*PeerSession, 0, len(h.peers))
	for _, p := range h.peers {
		if p.UserId != "" {
			targets = append(targets, p)
		}
	}
	h.mu.RUnlock()

	msg := map[string]interface{}{
		"op":    "peers",
		"peers": peers,
	}
	for _, t := range targets {
		h.sendToPeer(t, msg)
	}
}

func (h *Hub) broadcastAll(obj interface{}) {
	h.mu.RLock()
	targets := make([]*PeerSession, 0, len(h.peers))
	for _, p := range h.peers {
		if p.UserId != "" {
			targets = append(targets, p)
		}
	}
	h.mu.RUnlock()
	for _, t := range targets {
		h.sendToPeer(t, obj)
	}
}

func (h *Hub) removePeer(sid string) {
	h.mu.Lock()
	p, ok := h.peers[sid]
	if !ok {
		h.mu.Unlock()
		return
	}
	delete(h.peers, sid)
	for ssrc, b := range h.ssrcOwner {
		if b.SessionId == sid {
			delete(h.ssrcOwner, ssrc)
		}
	}
	hadUser := p.UserId != ""
	h.mu.Unlock()
	if hadUser {
		h.broadcastPeersState()
	}
}

func (h *Hub) punchUdpBurst(ipStr string, basePort int, spread int) {
	if h.udpConn == nil || ipStr == "" || basePort <= 0 {
		return
	}
	ip := net.ParseIP(ipStr)
	if ip == nil {
		return
	}
	pkt := []byte{0xFC, 'P', '2', 'P', 0x05, 0, 0, 0}
	startP := basePort - 2
	if startP < 1024 {
		startP = 1024
	}
	endP := basePort + spread
	if endP > 65535 {
		endP = 65535
	}
	for port := startP; port <= endP; port++ {
		_, _ = h.udpConn.WriteToUDP(pkt, &net.UDPAddr{IP: ip, Port: port})
	}
}

func (h *Hub) ensureDmChannelLocked(dmId, name string) {
	for _, c := range h.state.Channels {
		if c.Id == dmId {
			return
		}
	}
	h.state.Channels = append(h.state.Channels, Channel{
		Id:      dmId,
		GuildId: "@me",
		Name:    name,
		Type:    1,
	})
}

func (h *Hub) handleRpc(p *PeerSession, raw []byte, isLanClient bool) {
	var req map[string]interface{}
	if json.Unmarshal(raw, &req) != nil {
		return
	}
	p.LastSeenMs = nowMs()
	op, _ := req["op"].(string)

	switch op {
	case "auth":
		// Verify HMAC or secret proof
		proof, _ := req["proof"].(string)
		mac := hmac.New(sha256.New, h.aeadKey[:])
		mac.Write([]byte("fluxp2p-auth-v1"))
		expected := hex.EncodeToString(mac.Sum(nil))
		if !hmac.Equal([]byte(strings.ToLower(proof)), []byte(expected)) {
			h.sendToPeer(p, map[string]interface{}{"op": "auth_err", "error": "Неверный ключ комнаты (Room Secret)"})
			return
		}
		uid, _ := req["user_id"].(string)
		uname, _ := req["username"].(string)
		dname, _ := req["display_name"].(string)
		av, _ := req["avatar"].(string)
		lanIp, _ := req["lan_ip"].(string)
		st, _ := req["status"].(string)
		act, _ := req["activity"].(string)
		if uid == "" {
			uid = "u_" + p.SessionId[:8]
		}
		if dname == "" {
			dname = uname
		}
		if dname == "" {
			dname = "Player"
		}
		if st == "" {
			st = "online"
		}
		extIp, extPort := h.getExtEndpoint()
		mediaHost := extIp
		mediaPort := int(extPort)
		if isLanClient {
			mediaHost = h.lanIp
			mediaPort = h.udpPort
		}

		h.mu.Lock()
		if h.state.Profiles == nil {
			h.state.Profiles = make(map[string]SavedProfile)
		}
		if savedProf, hasProf := h.state.Profiles[uid]; hasProf {
			if av == "" && savedProf.Avatar != "" {
				av = savedProf.Avatar
			}
			if act == "" && savedProf.ActivityText != "" {
				act = savedProf.ActivityText
			}
			if (dname == "" || dname == "Player") && savedProf.DisplayName != "" {
				dname = savedProf.DisplayName
			}
		}
		if av != "" || dname != "" {
			h.state.Profiles[uid] = SavedProfile{DisplayName: dname, Avatar: av, ActivityText: act}
			h.markDirty()
		}
		// Evict any old stale session of same user_id
		for oldSid, oldP := range h.peers {
			if oldSid != p.SessionId && oldP.UserId == uid {
				delete(h.peers, oldSid)
			}
		}
		p.UserId = uid
		p.Username = uname
		p.DisplayName = dname
		p.Avatar = av
		p.LanIp = lanIp
		p.Status = st
		p.ActivityText = act
		if colF, ok := req["color"].(float64); ok && colF > 0 {
			p.Color = uint32(colF)
		} else {
			p.Color = 0xFF7BD88F
		}
		if p.VoiceSsrc == 0 {
			p.VoiceSsrc = h.allocSsrcLocked(p.SessionId, roleVoice, 0)
			p.PubAudioSsrc = h.allocSsrcLocked(p.SessionId, rolePubA, 0)
			p.PubVideoSsrc = p.PubAudioSsrc + 1
			h.ssrcOwner[p.PubVideoSsrc] = ssrcBinding{SessionId: p.SessionId, Role: rolePubV, Slot: 0}
		}
		myAv := p.Avatar
		myDn := p.DisplayName
		myAct := p.ActivityText
		channelsCopy := append([]Channel(nil), h.state.Channels...)
		msgsCopy := make(map[string][]MessageDto, len(h.state.Messages))
		for cid, list := range h.state.Messages {
			start := 0
			if len(list) > 80 {
				start = len(list) - 80
			}
			msgsCopy[cid] = append([]MessageDto(nil), list[start:]...)
		}
		peersCopy := h.peerDtosLocked()
		srvName := h.state.ServerName
		h.mu.Unlock()

		h.sendToPeer(p, map[string]interface{}{
			"op":              "welcome",
			"server_name":     srvName,
			"my_avatar":       myAv,
			"my_display_name": myDn,
			"my_activity":     myAct,
			"media_host":      mediaHost,
			"media_port":     mediaPort,
			"jetson_lan_ip":  h.lanIp,
			"jetson_udp_port": h.udpPort,
			"jetson_ext_ip":  extIp,
			"jetson_ext_port": extPort,
			"http_port":      h.httpPort,
			"media_key_b64":  base64.StdEncoding.EncodeToString(h.mediaKey[:]),
			"voice_ssrc":     p.VoiceSsrc,
			"pub_audio_ssrc": p.PubAudioSsrc,
			"pub_video_ssrc": p.PubVideoSsrc,
			"channels":       channelsCopy,
			"messages":       msgsCopy,
			"peers":          peersCopy,
		})
		h.broadcastPeersState()

	case "ping":
		t0, _ := req["t"].(float64)
		extIp, extPort := h.getExtEndpoint()
		h.sendToPeer(p, map[string]interface{}{
			"op":              "pong",
			"t":               int64(t0),
			"jetson_ext_ip":   extIp,
			"jetson_ext_port": extPort,
		})

	case "punch_media":
		ipS, _ := req["ip"].(string)
		ptF, _ := req["port"].(float64)
		roleS, _ := req["role"].(string)
		if ipS != "" && ptF > 0 {
			h.punchUdpBurst(ipS, int(ptF), 10)
			h.mu.Lock()
			if roleS == "voice" {
				p.VoiceExtIp = ipS
				p.VoiceExtPort = uint16(ptF)
			} else if roleS == "pub" {
				p.PubExtIp = ipS
				p.PubExtPort = uint16(ptF)
			}
			h.mu.Unlock()
			if roleS == "voice" || roleS == "pub" {
				h.broadcastPeersState()
			}
		}

	case "presence":
		profChanged := false
		h.mu.Lock()
		if st, ok := req["status"].(string); ok && st != "" {
			p.Status = st
		}
		if act, ok := req["activity"].(string); ok {
			p.ActivityText = act
			profChanged = true
		}
		if dn, ok := req["display_name"].(string); ok && strings.TrimSpace(dn) != "" {
			p.DisplayName = strings.TrimSpace(dn)
			profChanged = true
		}
		if av, ok := req["avatar"].(string); ok {
			p.Avatar = strings.TrimSpace(av)
			profChanged = true
		}
		if profChanged && p.UserId != "" {
			if h.state.Profiles == nil {
				h.state.Profiles = make(map[string]SavedProfile)
			}
			h.state.Profiles[p.UserId] = SavedProfile{
				DisplayName:  p.DisplayName,
				Avatar:       p.Avatar,
				ActivityText: p.ActivityText,
			}
			for cid := range h.state.Messages {
				list := h.state.Messages[cid]
				for i := range list {
					if list[i].AuthorId == p.UserId {
						if p.DisplayName != "" {
							list[i].AuthorName = p.DisplayName
						}
						if p.Avatar != "" {
							list[i].Avatar = p.Avatar
						}
					}
				}
			}
		}
		h.mu.Unlock()
		if profChanged {
			h.markDirty()
		}
		h.broadcastPeersState()

	case "voice_state":
		vcid, _ := req["channel_id"].(string)
		sm, _ := req["self_mute"].(bool)
		sd, _ := req["self_deaf"].(bool)
		h.mu.Lock()
		p.VoiceChannelId = vcid
		p.SelfMute = sm
		p.SelfDeaf = sd
		if vcid == "" {
			p.SelfStream = false
			p.Speaking = false
			p.Watches = make(map[int]*WatchSub)
		}
		vssrc := p.VoiceSsrc
		h.mu.Unlock()

		extIp, extPort := h.getExtEndpoint()
		mHost := extIp
		mPort := int(extPort)
		if isLanClient {
			mHost = h.lanIp
			mPort = h.udpPort
		}
		if vcid != "" {
			h.sendToPeer(p, map[string]interface{}{
				"op":         "voice_ready",
				"channel_id": vcid,
				"media_host": mHost,
				"media_port": mPort,
				"ssrc":       vssrc,
			})
		}
		h.broadcastPeersState()

	case "speaking":
		spk, _ := req["speaking"].(bool)
		h.mu.Lock()
		changed := p.Speaking != spk
		p.Speaking = spk
		uid := p.UserId
		ssrc := p.VoiceSsrc
		h.mu.Unlock()
		if changed && uid != "" {
			h.broadcastAll(map[string]interface{}{
				"op":       "speaking",
				"user_id":  uid,
				"ssrc":     ssrc,
				"speaking": spk,
			})
		}

	case "stream_start":
		wF, _ := req["width"].(float64)
		hF, _ := req["height"].(float64)
		fpsF, _ := req["fps"].(float64)
		dual, _ := req["dual"].(bool)
		h.mu.Lock()
		p.SelfStream = true
		p.StreamWidth = int(wF)
		p.StreamHeight = int(hF)
		p.StreamFps = int(fpsF)
		p.StreamDual = dual
		pa := p.PubAudioSsrc
		pv := p.PubVideoSsrc
		h.mu.Unlock()

		extIp, extPort := h.getExtEndpoint()
		mHost := extIp
		mPort := int(extPort)
		if isLanClient {
			mHost = h.lanIp
			mPort = h.udpPort
		}
		h.sendToPeer(p, map[string]interface{}{
			"op":         "pub_ready",
			"media_host": mHost,
			"media_port": mPort,
			"audio_ssrc": pa,
			"video_ssrc": pv,
		})
		h.broadcastPeersState()

	case "stream_stop":
		h.mu.Lock()
		p.SelfStream = false
		uid := p.UserId
		h.mu.Unlock()
		h.broadcastAll(map[string]interface{}{"op": "stream_stopped", "streamer_user_id": uid})
		h.broadcastPeersState()

	case "watch_start":
		slotF, _ := req["slot"].(float64)
		slot := int(slotF)
		if slot < 0 || slot >= 4 {
			slot = 0
		}
		streamerUid, _ := req["streamer_user_id"].(string)
		h.mu.Lock()
		if p.Watches == nil {
			p.Watches = make(map[int]*WatchSub)
		}
		ws, exists := p.Watches[slot]
		if !exists {
			ws = &WatchSub{
				Slot:      slot,
				LocalSsrc: h.allocSsrcLocked(p.SessionId, roleWatch, slot),
			}
			p.Watches[slot] = ws
		}
		ws.StreamerUserId = streamerUid
		var stPubA, stPubV uint32
		var stExtIp, stLanIp string
		var stExtPort uint16
		var streamerPeer *PeerSession
		for _, sp := range h.peers {
			if sp.UserId == streamerUid {
				streamerPeer = sp
				stPubA = sp.PubAudioSsrc
				stPubV = sp.PubVideoSsrc
				stExtIp = sp.PubExtIp
				stExtPort = sp.PubExtPort
				stLanIp = sp.LanIp
				break
			}
		}
		localSsrc := ws.LocalSsrc
		h.mu.Unlock()

		extIp, extPort := h.getExtEndpoint()
		mHost := extIp
		mPort := int(extPort)
		if isLanClient {
			mHost = h.lanIp
			mPort = h.udpPort
		}
		h.sendToPeer(p, map[string]interface{}{
			"op":               "watch_ready",
			"slot":             slot,
			"streamer_user_id": streamerUid,
			"media_host":       mHost,
			"media_port":       mPort,
			"local_ssrc":       localSsrc,
			"video_ssrc":       stPubV,
			"audio_ssrc":       stPubA,
			"peer_ext_ip":      stExtIp,
			"peer_ext_port":    stExtPort,
			"peer_lan_ip":      stLanIp,
		})
		if streamerPeer != nil {
			h.sendToPeer(streamerPeer, map[string]interface{}{"op": "request_keyframe"})
		}

	case "watch_stop":
		slotF, _ := req["slot"].(float64)
		h.mu.Lock()
		if p.Watches != nil {
			delete(p.Watches, int(slotF))
		}
		h.mu.Unlock()

	case "request_keyframe":
		streamerUid, _ := req["streamer_user_id"].(string)
		h.mu.RLock()
		var target *PeerSession
		for _, sp := range h.peers {
			if sp.UserId == streamerUid {
				target = sp
				break
			}
		}
		h.mu.RUnlock()
		if target != nil {
			h.sendToPeer(target, map[string]interface{}{"op": "request_keyframe"})
		}

	case "send_msg":
		if p.UserId == "" {
			return
		}
		cid, _ := req["channel_id"].(string)
		content, _ := req["content"].(string)
		replyId, _ := req["reply_to_id"].(string)
		dmName, _ := req["dm_name"].(string)
		if cid == "" {
			return
		}
		var atts []AttachmentDto
		if rawAtts, ok := req["attachments"].([]interface{}); ok {
			for _, ra := range rawAtts {
				if m, ok := ra.(map[string]interface{}); ok {
					u, _ := m["url"].(string)
					fn, _ := m["filename"].(string)
					ct, _ := m["content_type"].(string)
					wf, _ := m["width"].(float64)
					hf, _ := m["height"].(float64)
					sf, _ := m["size"].(float64)
					if u != "" {
						atts = append(atts, AttachmentDto{Url: u, Filename: fn, ContentType: ct, Width: int(wf), Height: int(hf), Size: int64(sf)})
					}
				}
			}
		}
		if strings.TrimSpace(content) == "" && len(atts) == 0 {
			return
		}
		now := time.Now()
		idNum := atomic.AddUint64(&h.msgSeq, 1)
		msg := MessageDto{
			Id:          fmt.Sprintf("%d", idNum),
			ChannelId:   cid,
			AuthorId:    p.UserId,
			AuthorName:  p.DisplayName,
			AuthorColor: p.Color,
			Avatar:      p.Avatar,
			Content:     content,
			Timestamp:   now.Format("15:04"),
			DateLabel:   russianDateLabel(now),
			CreatedUnix: now.Unix(),
			Attachments: atts,
		}
		h.mu.Lock()
		if strings.HasPrefix(cid, "dm_") && dmName != "" {
			h.ensureDmChannelLocked(cid, dmName)
		}
		if replyId != "" {
			for _, m := range h.state.Messages[cid] {
				if m.Id == replyId {
					msg.ReplyToId = replyId
					msg.ReplyAuthor = m.AuthorName
					msg.ReplyText = m.Content
					break
				}
			}
		}
		list := append(h.state.Messages[cid], msg)
		if len(list) > 500 {
			list = list[len(list)-500:]
		}
		h.state.Messages[cid] = list
		h.mu.Unlock()
		h.markDirty()
		h.broadcastAll(map[string]interface{}{"op": "msg_create", "message": msg})
		if firstUrl := extractFirstHttpUrl(content); firstUrl != "" {
			go h.fetchAndAttachOgEmbed(cid, msg.Id, firstUrl)
		}

	case "edit_msg":
		cid, _ := req["channel_id"].(string)
		mid, _ := req["message_id"].(string)
		content, _ := req["content"].(string)
		var updated *MessageDto
		h.mu.Lock()
		list := h.state.Messages[cid]
		for i := range list {
			if list[i].Id == mid && list[i].AuthorId == p.UserId {
				list[i].Content = content
				list[i].Edited = true
				cp := list[i]
				updated = &cp
				break
			}
		}
		h.mu.Unlock()
		if updated != nil {
			h.markDirty()
			h.broadcastAll(map[string]interface{}{"op": "msg_update", "message": updated})
		}

	case "delete_msg":
		cid, _ := req["channel_id"].(string)
		mid, _ := req["message_id"].(string)
		removed := false
		h.mu.Lock()
		list := h.state.Messages[cid]
		for i := range list {
			if list[i].Id == mid && list[i].AuthorId == p.UserId {
				h.state.Messages[cid] = append(list[:i], list[i+1:]...)
				removed = true
				break
			}
		}
		h.mu.Unlock()
		if removed {
			h.markDirty()
			h.broadcastAll(map[string]interface{}{"op": "msg_delete", "channel_id": cid, "message_id": mid})
		}

	case "pin_msg":
		cid, _ := req["channel_id"].(string)
		mid, _ := req["message_id"].(string)
		var updated *MessageDto
		h.mu.Lock()
		list := h.state.Messages[cid]
		for i := range list {
			if list[i].Id == mid {
				list[i].Pinned = !list[i].Pinned
				cp := list[i]
				updated = &cp
				break
			}
		}
		h.mu.Unlock()
		if updated != nil {
			h.markDirty()
			h.broadcastAll(map[string]interface{}{"op": "msg_update", "message": updated})
		}

	case "react_msg":
		cid, _ := req["channel_id"].(string)
		mid, _ := req["message_id"].(string)
		emoji, _ := req["emoji"].(string)
		if emoji == "" || p.UserId == "" {
			return
		}
		var updated *MessageDto
		h.mu.Lock()
		list := h.state.Messages[cid]
		for i := range list {
			if list[i].Id == mid {
				foundIdx := -1
				for ri := range list[i].Reactions {
					if list[i].Reactions[ri].Emoji == emoji {
						foundIdx = ri
						break
					}
				}
				if foundIdx < 0 {
					list[i].Reactions = append(list[i].Reactions, ReactionDto{Emoji: emoji, Users: []string{p.UserId}})
				} else {
					users := list[i].Reactions[foundIdx].Users
					uIdx := -1
					for ui, u := range users {
						if u == p.UserId {
							uIdx = ui
							break
						}
					}
					if uIdx >= 0 {
						users = append(users[:uIdx], users[uIdx+1:]...)
					} else {
						users = append(users, p.UserId)
					}
					if len(users) == 0 {
						list[i].Reactions = append(list[i].Reactions[:foundIdx], list[i].Reactions[foundIdx+1:]...)
					} else {
						list[i].Reactions[foundIdx].Users = users
					}
				}
				cp := list[i]
				updated = &cp
				break
			}
		}
		h.mu.Unlock()
		if updated != nil {
			h.markDirty()
			h.broadcastAll(map[string]interface{}{"op": "msg_update", "message": updated})
		}

	case "typing":
		cid, _ := req["channel_id"].(string)
		if cid != "" && p.UserId != "" {
			h.broadcastAll(map[string]interface{}{
				"op":          "typing",
				"channel_id":  cid,
				"user_id":     p.UserId,
				"author_name": p.DisplayName,
			})
		}

	case "upload_b64":
		reqId, _ := req["req_id"].(string)
		origName, _ := req["filename"].(string)
		b64, _ := req["data_b64"].(string)
		if b64 == "" || len(b64) > 8*1024*1024 {
			h.sendToPeer(p, map[string]interface{}{"op": "upload_res", "req_id": reqId, "ok": false})
			return
		}
		rawBytes, err := base64.StdEncoding.DecodeString(b64)
		if err != nil || len(rawBytes) == 0 {
			h.sendToPeer(p, map[string]interface{}{"op": "upload_res", "req_id": reqId, "ok": false})
			return
		}
		origName = filepath.Base(origName)
		if origName == "" || origName == "." || origName == "/" {
			origName = "image.png"
		}
		var safe strings.Builder
		for _, ch := range origName {
			if (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '.' || ch == '_' || ch == '-' {
				safe.WriteRune(ch)
			} else {
				safe.WriteByte('_')
			}
		}
		var rnd [6]byte
		_, _ = rand.Read(rnd[:])
		stored := hex.EncodeToString(rnd[:]) + "_" + safe.String()
		dstPath := filepath.Join(h.dataDir, "uploads", stored)
		if os.WriteFile(dstPath, rawBytes, 0644) != nil {
			h.sendToPeer(p, map[string]interface{}{"op": "upload_res", "req_id": reqId, "ok": false})
			return
		}
		h.sendToPeer(p, map[string]interface{}{
			"op":     "upload_res",
			"req_id": reqId,
			"ok":     true,
			"path":   "/files/" + stored,
			"size":   len(rawBytes),
		})

	case "fetch_file":
		reqPath, _ := req["path"].(string)
		base := filepath.Base(reqPath)
		if base == "" || base == "." || base == "/" {
			return
		}
		full := filepath.Join(h.dataDir, "uploads", base)
		rawBytes, err := os.ReadFile(full)
		if err != nil || len(rawBytes) > 6*1024*1024 {
			h.sendToPeer(p, map[string]interface{}{"op": "file_data", "path": reqPath, "ok": false})
			return
		}
		h.sendToPeer(p, map[string]interface{}{
			"op":       "file_data",
			"path":     reqPath,
			"ok":       true,
			"data_b64": base64.StdEncoding.EncodeToString(rawBytes),
		})

	case "load_older":
		cid, _ := req["channel_id"].(string)
		beforeId, _ := req["before_id"].(string)
		limitF, _ := req["limit"].(float64)
		limit := int(limitF)
		if limit <= 0 || limit > 100 {
			limit = 50
		}
		if cid == "" {
			return
		}
		h.mu.RLock()
		list := h.state.Messages[cid]
		endIdx := len(list)
		if beforeId != "" {
			for i := len(list) - 1; i >= 0; i-- {
				if list[i].Id == beforeId {
					endIdx = i
					break
				}
			}
		}
		startIdx := endIdx - limit
		if startIdx < 0 {
			startIdx = 0
		}
		page := append([]MessageDto(nil), list[startIdx:endIdx]...)
		hasMore := startIdx > 0
		h.mu.RUnlock()
		h.sendToPeer(p, map[string]interface{}{
			"op":         "history_page",
			"channel_id": cid,
			"messages":   page,
			"has_more":   hasMore,
		})

	case "soundboard":
		if p.UserId == "" {
			return
		}
		sid, _ := req["sound_id"].(string)
		lbl, _ := req["label"].(string)
		surl, _ := req["url"].(string)
		if sid == "" && surl == "" {
			return
		}
		h.broadcastAll(map[string]interface{}{
			"op":          "soundboard",
			"user_id":     p.UserId,
			"author_name": p.DisplayName,
			"channel_id":  p.VoiceChannelId,
			"sound_id":    sid,
			"label":       lbl,
			"url":         surl,
		})

	case "create_channel":
		cname, _ := req["name"].(string)
		ctypeF, _ := req["type"].(float64)
		ctype := int(ctypeF)
		if strings.TrimSpace(cname) == "" {
			return
		}
		parent := "cat_txt"
		if ctype == 2 {
			parent = "cat_vc"
		}
		h.mu.Lock()
		ch := Channel{
			Id:       fmt.Sprintf("c_%d", nowMs()),
			GuildId:  "g_p2p",
			Name:     strings.TrimSpace(cname),
			Type:     ctype,
			Position: len(h.state.Channels) + 1,
			ParentId: parent,
		}
		h.state.Channels = append(h.state.Channels, ch)
		chs := append([]Channel(nil), h.state.Channels...)
		h.mu.Unlock()
		h.markDirty()
		h.broadcastAll(map[string]interface{}{"op": "channels", "channels": chs})
	}
}

// ============================================================================
// UDP Media Router + STUN Cone-NAT Keepalive (:37401)
// ============================================================================

func (h *Hub) runUdpServer() error {
	addr := &net.UDPAddr{IP: net.IPv4zero, Port: h.udpPort}
	conn, err := net.ListenUDP("udp4", addr)
	if err != nil {
		return err
	}
	_ = conn.SetReadBuffer(8 * 1024 * 1024)
	_ = conn.SetWriteBuffer(8 * 1024 * 1024)
	h.udpConn = conn

	// STUN Cone-NAT Keepalive loop (every 15s)
	go func() {
		stunServers := []string{"stun.cloudflare.com:3478", "stun.l.google.com:19302"}
		for {
			for _, srv := range stunServers {
				raddr, err := net.ResolveUDPAddr("udp4", srv)
				if err != nil {
					continue
				}
				var req [20]byte
				binary.BigEndian.PutUint16(req[0:2], 0x0001)
				binary.BigEndian.PutUint16(req[2:4], 0)
				binary.BigEndian.PutUint32(req[4:8], 0x2112A442)
				_, _ = rand.Read(req[8:20])
				_, _ = conn.WriteToUDP(req[:], raddr)
			}
			time.Sleep(15 * time.Second)
		}
	}()

	log.Printf("[UDP] Media + STUN Cone-NAT router listening on :%d", h.udpPort)
	buf := make([]byte, 2048)
	targets := make([]*net.UDPAddr, 0, 16)

	for {
		n, from, err := conn.ReadFromUDP(buf)
		if err != nil {
			continue
		}
		if n < 8 {
			continue
		}

		// 1. STUN Binding Response (0x0101 + magic cookie 0x2112A442)
		if n >= 20 && binary.BigEndian.Uint16(buf[0:2]) == 0x0101 && binary.BigEndian.Uint32(buf[4:8]) == 0x2112A442 {
			pos := 20
			for pos+4 <= n {
				at := binary.BigEndian.Uint16(buf[pos : pos+2])
				al := int(binary.BigEndian.Uint16(buf[pos+2 : pos+4]))
				if pos+4+al > n {
					break
				}
				val := buf[pos+4 : pos+4+al]
				if at == 0x0020 && al >= 8 {
					xport := binary.BigEndian.Uint16(val[2:4]) ^ 0x2112
					xip := binary.BigEndian.Uint32(val[4:8]) ^ 0x2112A442
					var ipb [4]byte
					binary.BigEndian.PutUint32(ipb[:], xip)
					extIpStr := net.IPv4(ipb[0], ipb[1], ipb[2], ipb[3]).String()
					h.extMu.Lock()
					changed := h.extIp != extIpStr || h.extUdpPort != xport
					h.extIp = extIpStr
					h.extUdpPort = xport
					h.extMu.Unlock()
					if changed {
						log.Printf("[STUN] Cone-NAT external UDP mapped: %s:%d (LAN %s:%d)", extIpStr, xport, h.lanIp, h.udpPort)
					}
					break
				}
				pos += 4 + ((al + 3) & ^3)
			}
			continue
		}

		// 2. FluxCord.Media.dll 74-byte UDP IP Discovery (0x0001, len=70, ssrc)
		if n >= 74 && binary.BigEndian.Uint16(buf[0:2]) == 0x0001 && binary.BigEndian.Uint16(buf[2:4]) == 70 {
			ssrc := binary.BigEndian.Uint32(buf[4:8])
			addrCopy := &net.UDPAddr{IP: append(net.IP(nil), from.IP...), Port: from.Port}

			var notifyVoiceOrPub bool
			var notifyStreamer *PeerSession
			h.mu.Lock()
			if b, ok := h.ssrcOwner[ssrc]; ok {
				if p, pok := h.peers[b.SessionId]; pok {
					switch b.Role {
					case roleVoice:
						p.VoiceAddr = addrCopy
						p.VoiceExtIp = from.IP.String()
						p.VoiceExtPort = uint16(from.Port)
						notifyVoiceOrPub = true
					case rolePubA, rolePubV:
						p.PubAddr = addrCopy
						p.PubExtIp = from.IP.String()
						p.PubExtPort = uint16(from.Port)
						notifyVoiceOrPub = true
					case roleWatch:
						if ws, wok := p.Watches[b.Slot]; wok {
							ws.Addr = addrCopy
							for _, sp := range h.peers {
								if sp.UserId == ws.StreamerUserId {
									notifyStreamer = sp
									break
								}
							}
						}
					}
				}
			}
			h.mu.Unlock()

			var resp [74]byte
			binary.BigEndian.PutUint16(resp[0:2], 0x0002)
			binary.BigEndian.PutUint16(resp[2:4], 70)
			binary.BigEndian.PutUint32(resp[4:8], ssrc)
			copy(resp[8:72], []byte(from.IP.String()))
			binary.BigEndian.PutUint16(resp[72:74], uint16(from.Port))
			_, _ = conn.WriteToUDP(resp[:], from)

			if notifyVoiceOrPub {
				go h.broadcastPeersState()
			}
			if notifyStreamer != nil {
				go h.sendToPeer(notifyStreamer, map[string]interface{}{"op": "request_keyframe"})
			}
			continue
		}

		// 3. FluxP2P Keepalive / Probe (0x13 0x37 or 0xFC 'P' '2' 'P')
		if buf[0] == 0x13 && buf[1] == 0x37 {
			continue
		}
		if n >= 5 && buf[0] == 0xFC && buf[1] == 'P' && buf[2] == '2' && buf[3] == 'P' {
			if buf[4] == 0x01 { // UDP ping -> pong
				var pong [12]byte
				copy(pong[0:4], buf[0:4])
				pong[4] = 0x02
				if n >= 12 {
					copy(pong[5:12], buf[5:12])
				}
				_, _ = conn.WriteToUDP(pong[:], from)
			}
			continue
		}

		// 4. Zero-Copy RTP & RTCP Media Forwarding
		if n < 28 || (buf[0]&0xC0) != 0x80 {
			continue
		}
		ptFull := buf[1]
		targets = targets[:0]

		// 4a. Encrypted RTCP (200..207, e.g. PT=206 PLI keyframe demand from viewer -> streamer PubAddr!)
		if ptFull >= 200 && ptFull <= 207 {
			senderSsrc := binary.BigEndian.Uint32(buf[4:8])
			h.mu.RLock()
			if b, ok := h.ssrcOwner[senderSsrc]; ok && b.Role == roleWatch {
				if viewer, vok := h.peers[b.SessionId]; vok {
					if ws, wok := viewer.Watches[b.Slot]; wok {
						for _, sp := range h.peers {
							if sp.UserId == ws.StreamerUserId && sp.PubAddr != nil {
								targets = append(targets, sp.PubAddr)
								break
							}
						}
					}
				}
			}
			h.mu.RUnlock()
			for _, dst := range targets {
				_, _ = conn.WriteToUDP(buf[:n], dst)
			}
			continue
		}

		// 4b. Media RTP (PT 120 Opus Audio, PT 101 H.264 Video, PT 103 HEVC Video)
		pt := ptFull & 0x7F
		if pt != 120 && pt != 101 && pt != 103 {
			continue
		}
		ssrc := binary.BigEndian.Uint32(buf[8:12])

		h.mu.RLock()
		b, ok := h.ssrcOwner[ssrc]
		if ok {
			sender, pok := h.peers[b.SessionId]
			if pok {
				switch b.Role {
				case roleVoice:
					// Seamless NAT rebind learning
					if sender.VoiceAddr == nil || sender.VoiceAddr.Port != from.Port || !sender.VoiceAddr.IP.Equal(from.IP) {
						h.mu.RUnlock()
						h.mu.Lock()
						sender.VoiceAddr = &net.UDPAddr{IP: append(net.IP(nil), from.IP...), Port: from.Port}
						h.mu.Unlock()
						h.mu.RLock()
					}
					vcid := sender.VoiceChannelId
					if vcid != "" && !sender.SelfMute {
						for sid, peer := range h.peers {
							if sid != b.SessionId && peer.VoiceChannelId == vcid && !peer.SelfDeaf && peer.VoiceAddr != nil {
								targets = append(targets, peer.VoiceAddr)
							}
						}
					}
				case rolePubA, rolePubV:
					if sender.PubAddr == nil || sender.PubAddr.Port != from.Port || !sender.PubAddr.IP.Equal(from.IP) {
						h.mu.RUnlock()
						h.mu.Lock()
						sender.PubAddr = &net.UDPAddr{IP: append(net.IP(nil), from.IP...), Port: from.Port}
						h.mu.Unlock()
						h.mu.RLock()
					}
					suid := sender.UserId
					if suid != "" && sender.SelfStream {
						for sid, peer := range h.peers {
							if sid == b.SessionId {
								continue
							}
							for _, ws := range peer.Watches {
								if ws.StreamerUserId == suid && ws.Addr != nil {
									targets = append(targets, ws.Addr)
								}
							}
						}
					}
				}
			}
		}
		h.mu.RUnlock()

		for _, dst := range targets {
			_, _ = conn.WriteToUDP(buf[:n], dst)
		}
	}
}

// ============================================================================
// Pure-Stdlib RFC 6455 WebSocket + HTTP Attachment Server (:37400)
// ============================================================================

type wsConn struct {
	c   net.Conn
	br  *bufio.Reader
	wmu sync.Mutex
}

func upgradeWebSocket(w http.ResponseWriter, r *http.Request) (*wsConn, error) {
	key := r.Header.Get("Sec-WebSocket-Key")
	if key == "" {
		http.Error(w, "missing ws key", http.StatusBadRequest)
		return nil, fmt.Errorf("no key")
	}
	hj, ok := w.(http.Hijacker)
	if !ok {
		return nil, fmt.Errorf("no hijacker")
	}
	conn, rw, err := hj.Hijack()
	if err != nil {
		return nil, err
	}
	h := sha1.New()
	h.Write([]byte(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"))
	accept := base64.StdEncoding.EncodeToString(h.Sum(nil))
	resp := "HTTP/1.1 101 Switching Protocols\r\n" +
		"Upgrade: websocket\r\n" +
		"Connection: Upgrade\r\n" +
		"Sec-WebSocket-Accept: " + accept + "\r\n\r\n"
	if _, err := rw.WriteString(resp); err != nil {
		conn.Close()
		return nil, err
	}
	if err := rw.Flush(); err != nil {
		conn.Close()
		return nil, err
	}
	return &wsConn{c: conn, br: rw.Reader}, nil
}

func (ws *wsConn) WriteFrame(opcode byte, payload []byte, maskClient bool) error {
	ws.wmu.Lock()
	defer ws.wmu.Unlock()
	var hdr [14]byte
	hdr[0] = 0x80 | (opcode & 0x0F)
	hlen := 2
	maskBit := byte(0)
	if maskClient {
		maskBit = 0x80
	}
	n := len(payload)
	if n < 126 {
		hdr[1] = maskBit | byte(n)
	} else if n <= 65535 {
		hdr[1] = maskBit | 126
		binary.BigEndian.PutUint16(hdr[2:4], uint16(n))
		hlen = 4
	} else {
		hdr[1] = maskBit | 127
		binary.BigEndian.PutUint64(hdr[2:10], uint64(n))
		hlen = 10
	}
	var maskKey [4]byte
	if maskClient {
		_, _ = rand.Read(maskKey[:])
		copy(hdr[hlen:hlen+4], maskKey[:])
		hlen += 4
	}
	_ = ws.c.SetWriteDeadline(time.Now().Add(8 * time.Second))
	if _, err := ws.c.Write(hdr[:hlen]); err != nil {
		return err
	}
	if n > 0 {
		if maskClient {
			masked := make([]byte, n)
			for i := 0; i < n; i++ {
				masked[i] = payload[i] ^ maskKey[i&3]
			}
			_, err := ws.c.Write(masked)
			return err
		}
		_, err := ws.c.Write(payload)
		return err
	}
	return nil
}

func (ws *wsConn) WriteText(b []byte) error { return ws.WriteFrame(0x01, b, false) }

func (ws *wsConn) ReadMessage() (byte, []byte, error) {
	var msgBuf bytes.Buffer
	var msgOp byte
	for {
		var h2 [2]byte
		if _, err := io.ReadFull(ws.br, h2[:]); err != nil {
			return 0, nil, err
		}
		fin := (h2[0] & 0x80) != 0
		op := h2[0] & 0x0F
		masked := (h2[1] & 0x80) != 0
		plen := int64(h2[1] & 0x7F)
		if plen == 126 {
			var ext [2]byte
			if _, err := io.ReadFull(ws.br, ext[:]); err != nil {
				return 0, nil, err
			}
			plen = int64(binary.BigEndian.Uint16(ext[:]))
		} else if plen == 127 {
			var ext [8]byte
			if _, err := io.ReadFull(ws.br, ext[:]); err != nil {
				return 0, nil, err
			}
			plen = int64(binary.BigEndian.Uint64(ext[:]))
		}
		if plen > 8*1024*1024 {
			return 0, nil, fmt.Errorf("frame too large")
		}
		var maskKey [4]byte
		if masked {
			if _, err := io.ReadFull(ws.br, maskKey[:]); err != nil {
				return 0, nil, err
			}
		}
		data := make([]byte, plen)
		if plen > 0 {
			if _, err := io.ReadFull(ws.br, data); err != nil {
				return 0, nil, err
			}
			if masked {
				for i := range data {
					data[i] ^= maskKey[i&3]
				}
			}
		}
		if op == 0x08 { // Close
			return op, nil, io.EOF
		}
		if op == 0x09 { // Ping -> Pong
			_ = ws.WriteFrame(0x0A, data, false)
			continue
		}
		if op == 0x0A { // Pong
			continue
		}
		if op != 0 {
			msgOp = op
		}
		msgBuf.Write(data)
		if fin {
			return msgOp, msgBuf.Bytes(), nil
		}
	}
}

func isPrivateIP(hostPort string) bool {
	host, _, err := net.SplitHostPort(hostPort)
	if err != nil {
		host = hostPort
	}
	ip := net.ParseIP(host)
	if ip == nil {
		return false
	}
	if ip.IsLoopback() {
		return true
	}
	ip4 := ip.To4()
	if ip4 == nil {
		return false
	}
	return ip4[0] == 10 || (ip4[0] == 172 && ip4[1] >= 16 && ip4[1] <= 31) || (ip4[0] == 192 && ip4[1] == 168)
}

func (h *Hub) runHttpServer() error {
	mux := http.NewServeMux()

	mux.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		extIp, extPort := h.getExtEndpoint()
		h.mu.RLock()
		pc := len(h.peers)
		h.mu.RUnlock()
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(map[string]interface{}{
			"ok":            true,
			"peers":         pc,
			"lan_ip":        h.lanIp,
			"udp_port":      h.udpPort,
			"stun_ext_ip":   extIp,
			"stun_ext_port": extPort,
			"room_topic":    h.roomTopic,
			"uptime_sec":    int(time.Since(h.startTime).Seconds()),
		})
	})

	mux.HandleFunc("/ws", func(w http.ResponseWriter, r *http.Request) {
		ws, err := upgradeWebSocket(w, r)
		if err != nil {
			return
		}
		var sidRaw [8]byte
		_, _ = rand.Read(sidRaw[:])
		sid := hex.EncodeToString(sidRaw[:])
		isLan := isPrivateIP(r.RemoteAddr)
		p := &PeerSession{
			SessionId:  sid,
			WsConn:     ws,
			LastSeenMs: nowMs(),
			Watches:    make(map[int]*WatchSub),
		}
		h.mu.Lock()
		h.peers[sid] = p
		h.mu.Unlock()

		defer func() {
			ws.c.Close()
			h.removePeer(sid)
		}()

		for {
			_ = ws.c.SetReadDeadline(time.Now().Add(75 * time.Second))
			_, payload, err := ws.ReadMessage()
			if err != nil {
				return
			}
			h.handleRpc(p, payload, isLan)
		}
	})

	mux.HandleFunc("/upload", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			http.Error(w, "method", http.StatusMethodNotAllowed)
			return
		}
		auth := r.Header.Get("X-Room-Auth")
		mac := hmac.New(sha256.New, h.aeadKey[:])
		mac.Write([]byte("fluxp2p-upload-v1"))
		expected := hex.EncodeToString(mac.Sum(nil))
		if !hmac.Equal([]byte(strings.ToLower(auth)), []byte(expected)) {
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			return
		}
		origName := filepath.Base(r.URL.Query().Get("name"))
		if origName == "" || origName == "." || origName == "/" {
			origName = "file.bin"
		}
		var safe strings.Builder
		for _, ch := range origName {
			if (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '.' || ch == '_' || ch == '-' {
				safe.WriteRune(ch)
			} else {
				safe.WriteByte('_')
			}
		}
		var rnd [6]byte
		_, _ = rand.Read(rnd[:])
		stored := hex.EncodeToString(rnd[:]) + "_" + safe.String()
		dstPath := filepath.Join(h.dataDir, "uploads", stored)
		f, err := os.Create(dstPath)
		if err != nil {
			http.Error(w, "create", http.StatusInternalServerError)
			return
		}
		written, err := io.Copy(f, io.LimitReader(r.Body, 30*1024*1024))
		f.Close()
		if err != nil {
			http.Error(w, "write", http.StatusInternalServerError)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(map[string]interface{}{
			"ok":   true,
			"path": "/files/" + stored,
			"size": written,
		})
	})

	fileSrv := http.StripPrefix("/files/", http.FileServer(http.Dir(filepath.Join(h.dataDir, "uploads"))))
	mux.Handle("/files/", fileSrv)

	log.Printf("[HTTP/WS] Signaling + Attachments listening on :%d", h.httpPort)
	return http.ListenAndServe(fmt.Sprintf("0.0.0.0:%d", h.httpPort), mux)
}

// ============================================================================
// Zero-Config Encrypted Rendezvous Client (MQTT v3.1.1 over TLS WebSocket)
// Lets Lapki connect from ANY ISP/city without router port-forwarding!
// ============================================================================

func encodeMqttVarLen(n int) []byte {
	var out []byte
	for {
		b := byte(n % 128)
		n /= 128
		if n > 0 {
			b |= 0x80
		}
		out = append(out, b)
		if n == 0 {
			break
		}
	}
	return out
}

func encodeMqttStr(s string) []byte {
	b := make([]byte, 2+len(s))
	binary.BigEndian.PutUint16(b[0:2], uint16(len(s)))
	copy(b[2:], s)
	return b
}

func dialWsTls(hostPort, path, subproto string) (*wsConn, error) {
	host, _, _ := net.SplitHostPort(hostPort)
	dialer := &net.Dialer{Timeout: 6 * time.Second}
	tc, err := tls.DialWithDialer(dialer, "tcp", hostPort, &tls.Config{ServerName: host})
	if err != nil {
		return nil, err
	}
	var keyRaw [16]byte
	_, _ = rand.Read(keyRaw[:])
	wsKey := base64.StdEncoding.EncodeToString(keyRaw[:])
	req := fmt.Sprintf("GET %s HTTP/1.1\r\nHost: %s\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: %s\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Protocol: %s\r\n\r\n",
		path, host, wsKey, subproto)
	if _, err := tc.Write([]byte(req)); err != nil {
		tc.Close()
		return nil, err
	}
	br := bufio.NewReaderSize(tc, 65536)
	statusLine, err := br.ReadString('\n')
	if err != nil || !strings.Contains(statusLine, "101") {
		tc.Close()
		return nil, fmt.Errorf("ws upgrade failed: %v %s", err, statusLine)
	}
	for {
		line, err := br.ReadString('\n')
		if err != nil {
			tc.Close()
			return nil, err
		}
		if line == "\r\n" || line == "\n" {
			break
		}
	}
	return &wsConn{c: tc, br: br}, nil
}

func (h *Hub) runRendezvousBridge() {
	brokers := []struct {
		HostPort string
		Path     string
	}{
		{"broker.emqx.io:8084", "/mqtt"},
		{"broker.hivemq.com:8884", "/mqtt"},
	}
	idx := 0
	for {
		b := brokers[idx%len(brokers)]
		idx++
		err := h.serveSingleBroker(b.HostPort, b.Path)
		if err != nil {
			log.Printf("[Rendezvous] %s disconnected (%v), retrying...", b.HostPort, err)
		}
		time.Sleep(3 * time.Second)
	}
}

func (h *Hub) serveSingleBroker(hostPort, path string) error {
	ws, err := dialWsTls(hostPort, path, "mqtt")
	if err != nil {
		return err
	}
	defer ws.c.Close()

	var cidRnd [5]byte
	_, _ = rand.Read(cidRnd[:])
	mqttClientId := "fphub_" + hex.EncodeToString(cidRnd[:])

	// 1. MQTT CONNECT (v3.1.1, CleanSession=1, KeepAlive=30s)
	var body bytes.Buffer
	body.Write(encodeMqttStr("MQTT"))
	body.WriteByte(4)    // Protocol Level 4 (3.1.1)
	body.WriteByte(0x02) // Clean Session
	body.Write([]byte{0, 30})
	body.Write(encodeMqttStr(mqttClientId))

	var pkt bytes.Buffer
	pkt.WriteByte(0x10)
	pkt.Write(encodeMqttVarLen(body.Len()))
	pkt.Write(body.Bytes())
	if err := ws.WriteFrame(0x02, pkt.Bytes(), true); err != nil {
		return err
	}

	// 2. MQTT SUBSCRIBE to fluxp2p/<roomTopic>/c2s
	subTopic := fmt.Sprintf("fluxp2p/%s/c2s", h.roomTopic)
	var subBody bytes.Buffer
	subBody.Write([]byte{0, 1}) // Packet ID = 1
	subBody.Write(encodeMqttStr(subTopic))
	subBody.WriteByte(0) // QoS 0
	var subPkt bytes.Buffer
	subPkt.WriteByte(0x82)
	subPkt.Write(encodeMqttVarLen(subBody.Len()))
	subPkt.Write(subBody.Bytes())
	if err := ws.WriteFrame(0x02, subPkt.Bytes(), true); err != nil {
		return err
	}

	log.Printf("[Rendezvous] Connected to %s (encrypted topic fluxp2p/%s/*)", hostPort, h.roomTopic)
	done := make(chan struct{})
	defer close(done)

	// Outbound MQTT publisher + 20s PINGREQ loop
	go func() {
		pingTicker := time.NewTicker(20 * time.Second)
		defer pingTicker.Stop()
		for {
			select {
			case <-done:
				return
			case out := <-h.mqttSendCh:
				var pb bytes.Buffer
				pb.Write(encodeMqttStr(out.Topic))
				pb.Write(out.Payload)
				var mp bytes.Buffer
				mp.WriteByte(0x30) // PUBLISH QoS 0
				mp.Write(encodeMqttVarLen(pb.Len()))
				mp.Write(pb.Bytes())
				if ws.WriteFrame(0x02, mp.Bytes(), true) != nil {
					ws.c.Close()
					return
				}
			case <-pingTicker.C:
				if ws.WriteFrame(0x02, []byte{0xC0, 0x00}, true) != nil {
					ws.c.Close()
					return
				}
			}
		}
	}()

	// Inbound MQTT packet parser over WebSocket binary frames
	var streamBuf bytes.Buffer
	for {
		_ = ws.c.SetReadDeadline(time.Now().Add(50 * time.Second))
		_, frame, err := ws.ReadMessage()
		if err != nil {
			return err
		}
		streamBuf.Write(frame)

		for streamBuf.Len() >= 2 {
			raw := streamBuf.Bytes()
			hdrByte := raw[0]
			mult := 1
			remLen := 0
			pos := 1
			completeHdr := false
			for pos < len(raw) && pos <= 4 {
				b := raw[pos]
				remLen += int(b&0x7F) * mult
				mult *= 128
				pos++
				if (b & 0x80) == 0 {
					completeHdr = true
					break
				}
			}
			if !completeHdr {
				break
			}
			totalLen := pos + remLen
			if len(raw) < totalLen {
				break
			}
			pktBytes := make([]byte, totalLen)
			_, _ = streamBuf.Read(pktBytes)

			pktType := hdrByte >> 4
			if pktType == 3 { // PUBLISH
				qos := (hdrByte >> 1) & 0x03
				pOff := pos
				if pOff+2 > totalLen {
					continue
				}
				tLen := int(binary.BigEndian.Uint16(pktBytes[pOff : pOff+2]))
				pOff += 2 + tLen
				if qos > 0 {
					pOff += 2
				}
				if pOff <= totalLen {
					h.handleRendezvousPacket(pktBytes[pOff:totalLen])
				}
			}
		}
	}
}

func (h *Hub) handleRendezvousPacket(cipherPayload []byte) {
	plain, err := h.decryptAEAD(cipherPayload)
	if err != nil {
		return
	}
	var env map[string]interface{}
	if json.Unmarshal(plain, &env) != nil {
		return
	}
	cid, _ := env["cid"].(string)
	if cid == "" {
		return
	}
	kind, _ := env["kind"].(string)
	if kind == "punch" {
		stunIp, _ := env["stun_ip"].(string)
		if ports, ok := env["ports"].([]interface{}); ok {
			for _, pf := range ports {
				if pnum, ok := pf.(float64); ok && pnum > 0 {
					h.punchUdpBurst(stunIp, int(pnum), 12)
				}
			}
		}
		extIp, extPort := h.getExtEndpoint()
		ack, _ := json.Marshal(map[string]interface{}{
			"op":              "punch_ack",
			"jetson_ext_ip":   extIp,
			"jetson_ext_port": extPort,
			"jetson_lan_ip":   h.lanIp,
			"jetson_ws_port":  h.httpPort,
			"jetson_udp_port": h.udpPort,
		})
		if enc, err := h.encryptAEAD(ack); err == nil {
			select {
			case h.mqttSendCh <- mqttOutMsg{Topic: fmt.Sprintf("fluxp2p/%s/s2c/%s", h.roomTopic, cid), Payload: enc}:
			default:
			}
		}
		return
	}

	// Full encrypted RPC over Rendezvous!
	sid := "mq_" + cid
	h.mu.Lock()
	p, exists := h.peers[sid]
	if !exists {
		p = &PeerSession{
			SessionId:  sid,
			MqttCid:    cid,
			LastSeenMs: nowMs(),
			Watches:    make(map[int]*WatchSub),
		}
		h.peers[sid] = p
	}
	p.MqttCid = cid
	p.LastSeenMs = nowMs()
	h.mu.Unlock()

	if rpcRaw, ok := env["rpc"].(map[string]interface{}); ok {
		b, _ := json.Marshal(rpcRaw)
		h.handleRpc(p, b, false)
	} else {
		h.handleRpc(p, plain, false)
	}
}

var (
	reHttpUrl   = regexp.MustCompile(`https?://[^\s<>"]+`)
	reMetaTag   = regexp.MustCompile(`(?is)<meta\s+[^>]+>`)
	reTitleTag  = regexp.MustCompile(`(?is)<title[^>]*>(.*?)</title>`)
	reAttrProp  = regexp.MustCompile(`(?is)(?:property|name)\s*=\s*["']([^"']+)["']`)
	reAttrCont  = regexp.MustCompile(`(?is)content\s*=\s*["']([^"']+)["']`)
)

func extractFirstHttpUrl(s string) string {
	m := reHttpUrl.FindString(s)
	return strings.TrimRight(m, ".,!?;:)]}")
}

func isPrivateHost(host string) bool {
	h := host
	if idx := strings.IndexByte(h, ':'); idx >= 0 {
		h = h[:idx]
	}
	low := strings.ToLower(h)
	if low == "localhost" || strings.HasSuffix(low, ".local") {
		return true
	}
	ips, err := net.LookupIP(h)
	if err != nil || len(ips) == 0 {
		return true
	}
	for _, ip := range ips {
		if ip.IsLoopback() || ip.IsPrivate() || ip.IsLinkLocalUnicast() || ip.IsLinkLocalMulticast() || ip.IsUnspecified() {
			return true
		}
	}
	return false
}

func (h *Hub) fetchAndAttachOgEmbed(cid, msgId, rawUrl string) {
	u, err := url.Parse(rawUrl)
	if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" {
		return
	}
	if isPrivateHost(u.Host) {
		return
	}
	client := &http.Client{Timeout: 3 * time.Second}
	req, err := http.NewRequest("GET", rawUrl, nil)
	if err != nil {
		return
	}
	req.Header.Set("User-Agent", "Mozilla/5.0 (compatible; FluxCordP2P/2.3; +https://github.com/So2K/FluxCordP2P)")
	req.Header.Set("Accept", "text/html,application/xhtml+xml;q=0.9,*/*;q=0.8")
	resp, err := client.Do(req)
	if err != nil {
		return
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return
	}
	ct := strings.ToLower(resp.Header.Get("Content-Type"))
	if !strings.Contains(ct, "text/html") && !strings.Contains(ct, "xhtml") {
		return
	}
	bodyBytes, err := io.ReadAll(io.LimitReader(resp.Body, 256*1024))
	if err != nil || len(bodyBytes) == 0 {
		return
	}
	doc := string(bodyBytes)
	var ogTitle, ogDesc, ogImg, ogSite string
	for _, tag := range reMetaTag.FindAllString(doc, 80) {
		pm := reAttrProp.FindStringSubmatch(tag)
		cm := reAttrCont.FindStringSubmatch(tag)
		if len(pm) < 2 || len(cm) < 2 {
			continue
		}
		key := strings.ToLower(strings.TrimSpace(pm[1]))
		val := strings.TrimSpace(html.UnescapeString(cm[1]))
		switch key {
		case "og:title", "twitter:title":
			if ogTitle == "" {
				ogTitle = val
			}
		case "og:description", "twitter:description", "description":
			if ogDesc == "" {
				ogDesc = val
			}
		case "og:image", "twitter:image":
			if ogImg == "" && (strings.HasPrefix(val, "http://") || strings.HasPrefix(val, "https://")) {
				ogImg = val
			}
		case "og:site_name":
			if ogSite == "" {
				ogSite = val
			}
		}
	}
	if ogTitle == "" {
		if tm := reTitleTag.FindStringSubmatch(doc); len(tm) >= 2 {
			ogTitle = strings.TrimSpace(html.UnescapeString(tm[1]))
		}
	}
	if ogTitle == "" && ogDesc == "" && ogImg == "" {
		return
	}
	if len(ogTitle) > 140 {
		ogTitle = ogTitle[:140] + "…"
	}
	if len(ogDesc) > 260 {
		ogDesc = ogDesc[:260] + "…"
	}
	if ogSite == "" {
		ogSite = u.Host
	}
	emb := EmbedDto{
		Title:       ogTitle,
		Description: ogDesc,
		Url:         rawUrl,
		Author:      ogSite,
		ImageUrl:    ogImg,
		Color:       0xFF5865F2,
	}

	var updated *MessageDto
	h.mu.Lock()
	list := h.state.Messages[cid]
	for i := range list {
		if list[i].Id == msgId {
			list[i].Embeds = []EmbedDto{emb}
			cp := list[i]
			updated = &cp
			break
		}
	}
	h.mu.Unlock()
	if updated != nil {
		h.markDirty()
		h.broadcastAll(map[string]interface{}{"op": "msg_update", "message": updated})
	}
}

func main() {
	dataDir := flag.String("data", "/mnt/ai/share/TerraMaster/SSD1/Files/FluxCordP2P", "Data directory")
	secretFile := flag.String("secret-file", "/home/user/fluxp2p-hub/room.secret", "Path to room secret file")
	httpPort := flag.Int("http-port", 37400, "HTTP/WS signaling port")
	udpPort := flag.Int("udp-port", 37401, "UDP media + STUN port")
	flag.Parse()

	secretBytes, err := os.ReadFile(*secretFile)
	secret := strings.TrimSpace(string(secretBytes))
	if err != nil || len(secret) < 16 {
		var rnd [18]byte
		_, _ = rand.Read(rnd[:])
		secret = "FLXP2P-" + strings.ToUpper(hex.EncodeToString(rnd[:]))
		_ = os.MkdirAll(filepath.Dir(*secretFile), 0700)
		_ = os.WriteFile(*secretFile, []byte(secret+"\n"), 0600)
		log.Printf("[Init] Generated fresh 288-bit Room Secret in %s", *secretFile)
	}

	hub, err := newHub(*dataDir, secret, *httpPort, *udpPort)
	if err != nil {
		log.Fatalf("Failed to init hub: %v", err)
	}

	go hub.backgroundFlusher()
	go func() {
		if err := hub.runUdpServer(); err != nil {
			log.Fatalf("UDP server error: %v", err)
		}
	}()
	go hub.runRendezvousBridge()

	if err := hub.runHttpServer(); err != nil {
		log.Fatalf("HTTP server error: %v", err)
	}
}
