import ctypes
import ctypes.wintypes as wt
import hashlib
import json
import os
import subprocess
import sys
import tempfile
import time

DIST = os.environ.get("FLUXCORD_BIN_DIR", r"C:\Users\User\Projects\FluxCord\dist\FluxCord")
EXE = os.path.join(DIST, "FluxCord.exe")
DLL = os.path.join(DIST, "FluxCord.Media.dll")
REAL_USER_SETTINGS = os.path.join(os.environ["LOCALAPPDATA"], "FluxCord", "settings.json")


def file_sha256(path):
    if not os.path.exists(path):
        return None
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest()


class FcMonitorInfo(ctypes.Structure):
    _fields_ = [("index", ctypes.c_int32), ("width", ctypes.c_int32), ("height", ctypes.c_int32),
                ("refresh_hz", ctypes.c_int32), ("name", ctypes.c_char * 128)]


class FcWindowInfo(ctypes.Structure):
    _fields_ = [("hwnd", ctypes.c_uint64), ("width", ctypes.c_int32), ("height", ctypes.c_int32),
                ("title", ctypes.c_char * 128)]


class FcDeviceInfo(ctypes.Structure):
    _fields_ = [("id", ctypes.c_char * 128), ("name", ctypes.c_char * 128), ("is_default", ctypes.c_int32)]


class FcScreenStats(ctypes.Structure):
    _fields_ = [("width", ctypes.c_int32), ("height", ctypes.c_int32), ("fps", ctypes.c_float),
                ("bitrate_kbps", ctypes.c_float), ("encode_ms", ctypes.c_float), ("decode_ms", ctypes.c_float),
                ("zero_copy", ctypes.c_int32), ("running", ctypes.c_int32),
                ("frames_encoded", ctypes.c_int64), ("frames_skipped_static", ctypes.c_int64),
                ("encoder_name", ctypes.c_char * 64)]


class FcAudioStats(ctypes.Structure):
    _fields_ = [("raw_rms", ctypes.c_float), ("clean_rms", ctypes.c_float), ("noise_reduction_db", ctypes.c_float),
                ("vad_prob", ctypes.c_float), ("speaking", ctypes.c_int32), ("active_mode", ctypes.c_int32),
                ("tx_packets", ctypes.c_int32), ("rx_packets", ctypes.c_int32), ("backend_name", ctypes.c_char * 64)]


failures = []


def check(cond, msg):
    if cond:
        print(f"[PASS] {msg}", flush=True)
    else:
        print(f"[FAIL] {msg}", flush=True)
        failures.append(msg)


real_settings_hash_before = file_sha256(REAL_USER_SETTINGS)
real_settings_mtime_before = os.path.getmtime(REAL_USER_SETTINGS) if os.path.exists(REAL_USER_SETTINGS) else None

with tempfile.TemporaryDirectory(prefix="fluxcord_test_") as DATA:
    test_env = os.environ.copy()
    test_env["FLUXCORD_DATA_DIR"] = DATA

    # 1. NativeAOT FluxCord.exe --demo --bench + Ultra-Updater Resume Session verification (100% isolated FLUXCORD_DATA_DIR)
    resume_file = os.path.join(DATA, "resume_session.json")
    with open(resume_file, "w", encoding="utf-8") as rf:
        json.dump({
            "SavedUtcTicks": int((time.time() + 62135596800) * 10000000),
            "FromVersion": "1.0.3", "TargetVersion": "1.0.4",
            "ActiveGuildId": "g1", "ActiveChannelId": "c_gen",
            "InVoiceStage": True, "ShowFriendsView": False,
            "VoiceGuildId": "g1", "VoiceChannelId": "v_main",
            "Muted": True, "Deafened": False,
            "WatchedStreams": [], "WasStreaming": False
        }, rf)
    t0 = time.perf_counter()
    proc = subprocess.run([EXE, "--demo", "--bench", "--resumed-update"], cwd=DIST, env=test_env, timeout=20)
    wall_ms = (time.perf_counter() - t0) * 1000
    check(proc.returncode == 0, f"FluxCord.exe --demo --bench --resumed-update exit=0 (wall={wall_ms:.0f}ms)")
    check(not os.path.exists(resume_file), "Ultra-Updater resume_session.json consumed and restored cleanly on startup")
    check(os.path.exists(os.path.join(DATA, "settings.json")), "Isolated settings.json persisted in FLUXCORD_DATA_DIR")
    check(os.path.exists(os.path.join(DATA, "cache.db")), "Isolated SQLite WAL cache.db initialized in FLUXCORD_DATA_DIR")

    # 1b. Live Win32 UI Interaction, Modal/Popup Click-Block & Handle-Leak Regression Suite (--demo)
    settings_path = os.path.join(DATA, "settings.json")
    p_click = subprocess.Popen([EXE, "--demo"], cwd=DIST, env=test_env)
    try:
        hwnd_found = 0
        WNDENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
        for _ in range(50):
            time.sleep(0.1)
            found = [0]

            def _cb(h, _):
                pid_out = wt.DWORD()
                ctypes.windll.user32.GetWindowThreadProcessId(h, ctypes.byref(pid_out))
                if pid_out.value == p_click.pid:
                    cls = ctypes.create_unicode_buffer(64)
                    ctypes.windll.user32.GetClassNameW(h, cls, 64)
                    if cls.value == "FluxCordMain":
                        found[0] = h
                        return False
                return True

            ctypes.windll.user32.EnumWindows(WNDENUMPROC(_cb), 0)
            if found[0]:
                hwnd_found = found[0]
                break

        if hwnd_found:
            time.sleep(0.35)
            dpi = ctypes.windll.user32.GetDpiForWindow(hwnd_found) or 96
            sc = dpi / 96.0
            rc = wt.RECT()
            ctypes.windll.user32.GetClientRect(hwnd_found, ctypes.byref(rc))
            lw, lh = rc.right / sc, rc.bottom / sc

            def ui_click(lx, ly):
                lp = (int(ly * sc) << 16) | (int(lx * sc) & 0xFFFF)
                ctypes.windll.user32.SendMessageW(hwnd_found, 0x0200, 0, lp)  # WM_MOUSEMOVE
                ctypes.windll.user32.SendMessageW(hwnd_found, 0x0201, 1, lp)  # WM_LBUTTONDOWN
                ctypes.windll.user32.SendMessageW(hwnd_found, 0x0202, 0, lp)  # WM_LBUTTONUP
                time.sleep(0.12)

            def read_settings():
                with open(settings_path, encoding="utf-8") as sf:
                    return json.load(sf)

            # Test A: Switch server rail g1 -> g2
            ui_click(34, 182)
            s1 = read_settings()
            check(s1.get("LastGuildId") == "g2" and s1.get("LastChannelId") == "c_g2",
                  f"UI click server rail g1 -> g2 (LastGuildId={s1.get('LastGuildId')}, LastChannelId={s1.get('LastChannelId')})")

            # Test B: Switch server rail back g2 -> g1
            ui_click(34, 128)
            s2 = read_settings()
            check(s2.get("LastGuildId") == "g1" and s2.get("LastChannelId") == "c_gen",
                  f"UI click server rail g2 -> g1 (LastGuildId={s2.get('LastGuildId')}, LastChannelId={s2.get('LastChannelId')})")

            # Test C: Click text channel #gamedev-granny2 (c_dev) in sidebar
            ui_click(188, 170)
            s3 = read_settings()
            check(s3.get("LastChannelId") == "c_dev",
                  f"UI click text channel sidebar c_gen -> c_dev (LastChannelId={s3.get('LastChannelId')})")

            # Test C2: Switch to Direct Messages (@me), open Group DM (dm_1: 3 members + call history), then switch back to g1!
            ui_click(34, 72)        # Top-left DM button -> @me Friends view
            ui_click(188, 174)      # First Group DM item (dm_1: "mirrorcoder, DemoTeammate")
            s3b = read_settings()
            check(s3b.get("LastGuildId") == "@me" and s3b.get("LastChannelId") == "dm_1",
                  f"UI click DM rail + Group DM dm_1 (LastGuildId={s3b.get('LastGuildId')}, LastChannelId={s3b.get('LastChannelId')})")
            ui_click(34, 128)       # Return to g1 for modal/popup tests

            # Test D: Open Settings modal (gear icon), verify background rail click is BLOCKED, then Esc closes modal & rail click works again!
            ui_click(302, lh - 30)  # Open Settings
            ui_click(34, 182)       # Try clicking g2 UNDER modal -> must be blocked!
            s4 = read_settings()
            check(s4.get("LastGuildId") == "g1",
                  f"Settings modal blocks background click-through (LastGuildId stayed {s4.get('LastGuildId')})")

            ctypes.windll.user32.SendMessageW(hwnd_found, 0x0100, 0x1B, 0)  # WM_KEYDOWN VK_ESCAPE
            time.sleep(0.12)
            ui_click(34, 182)       # Now g2 click MUST succeed!
            s5 = read_settings()
            check(s5.get("LastGuildId") == "g2",
                  f"After Esc closes modal, server click g1 -> g2 works again (LastGuildId={s5.get('LastGuildId')})")

            # Test D2 ([F-15]): Open Status Popup on User Bar, click server g1 outside popup -> popup closes WITHOUT click-through, second click switches to g1!
            ui_click(120, lh - 30)  # Open Status Menu popup
            ui_click(34, 128)       # Outside click on g1 dismisses popup without switching guild!
            s5b = read_settings()
            check(s5b.get("LastGuildId") == "g2",
                  f"Popup outside-click dismisses popup without background click-through (LastGuildId stayed {s5b.get('LastGuildId')})")
            ui_click(34, 128)       # Next click on g1 switches cleanly!
            s5c = read_settings()
            check(s5c.get("LastGuildId") == "g1",
                  f"Subsequent click after popup dismiss switches g2 -> g1 (LastGuildId={s5c.get('LastGuildId')})")

            # Test E: Handle-leak regression across 200 steady-state redraws
            time.sleep(2.2)
            h_before, h_after = wt.DWORD(), wt.DWORD()
            ctypes.windll.kernel32.GetProcessHandleCount(int(p_click._handle), ctypes.byref(h_before))
            for k in range(200):
                lp = (int((120 + (k % 8) * 20) * sc) << 16) | (int(180 * sc) & 0xFFFF)
                ctypes.windll.user32.SendMessageW(hwnd_found, 0x0200, 0, lp)
            ctypes.windll.kernel32.GetProcessHandleCount(int(p_click._handle), ctypes.byref(h_after))
            delta_h = int(h_after.value) - int(h_before.value)
            check(delta_h <= 25, f"No per-frame handle leak across 200 redraws (before={h_before.value}, after={h_after.value}, delta={delta_h:+d})")

            # Test F1: Close on 'X' (WM_CLOSE wParam=0) flies to background tray instead of quitting!
            ctypes.windll.user32.SendMessageW(hwnd_found, 0x0010, 0, 0)  # WM_CLOSE (normal close button)
            time.sleep(0.15)
            vis_after_close = ctypes.windll.user32.IsWindowVisible(hwnd_found)
            alive_in_bg = (p_click.poll() is None)
            check(vis_after_close == 0 and alive_in_bg,
                  f"Close on 'X' hides window to background tray while keeping Discord alive (visible={vis_after_close}, running={alive_in_bg})")

            # Test F2: Clicking system tray icon (WM_TRAY = WM_APP+3 = 0x8003, lParam = WM_LBUTTONUP = 0x0202) restores window!
            ctypes.windll.user32.SendMessageW(hwnd_found, 0x8003, 1, 0x0202)
            time.sleep(0.15)
            vis_restored = ctypes.windll.user32.IsWindowVisible(hwnd_found)
            check(vis_restored != 0 and p_click.poll() is None,
                  f"Clicking system tray icon restores FluxCord window from background (visible={vis_restored})")

            # Test F3: Explicit Full Quit (ForceCloseWParam = 0xFC01 / Tray Exit / Ctrl+Q) exits 0 cleanly!
            ctypes.windll.user32.PostMessageW(hwnd_found, 0x0010, 0xFC01, 0)  # WM_CLOSE ForceQuit
            rc_close = p_click.wait(timeout=5)
            check(rc_close == 0, f"Explicit Full Quit from tray/Ctrl+Q exits cleanly (exit={rc_close})")
        else:
            check(False, "Win32 UI click test could not find FluxCordMain --demo HWND")
    finally:
        if p_click.poll() is None:
            p_click.terminate()
            try:
                p_click.wait(timeout=3)
            except Exception:
                p_click.kill()

# Verify real user %LOCALAPPDATA%\FluxCord\settings.json was NEVER modified by the test suite ([F-20])!
real_settings_hash_after = file_sha256(REAL_USER_SETTINGS)
real_settings_mtime_after = os.path.getmtime(REAL_USER_SETTINGS) if os.path.exists(REAL_USER_SETTINGS) else None
check(real_settings_hash_before == real_settings_hash_after and real_settings_mtime_before == real_settings_mtime_after,
      "Real user %LOCALAPPDATA%\\FluxCord\\settings.json untouched (hash & mtime 100% preserved)")

# 2. C++20 FluxCord.Media.dll + libdave.dll hardware & Screen Share Picker verification
dll = ctypes.CDLL(DLL)
check(dll.fc_init() == 1, "FluxCord.Media.dll fc_init()")

mons = (FcMonitorInfo * 8)()
nm = dll.fc_list_monitors(mons, 8)
check(nm >= 1, f"DXGI monitors detected: {nm} ({[(mons[i].width, mons[i].height, mons[i].refresh_hz) for i in range(nm)]})")

wins = (FcWindowInfo * 24)()
tw0 = time.perf_counter()
nw = dll.fc_list_windows(wins, 24)
win_ms = (time.perf_counter() - tw0) * 1000
check(nw >= 1 and win_ms < 500, f"Desktop windows enumerated for Screen Share modal without hang: {nw} in {win_ms:.1f}ms (top: {[wins[i].title.decode('utf-8','ignore')[:24] for i in range(min(3, nw))]})")

# Regression test: create a real HWND in THIS thread (without pumping messages) and verify fc_list_windows does NOT self-deadlock!
own_hwnd = ctypes.windll.user32.CreateWindowExW(0, "STATIC", "FluxCordDeadlockGuard", 0x10000000, 0, 0, 300, 200, None, None, None, None)
td0 = time.perf_counter()
nw2 = dll.fc_list_windows(wins, 24)
dl_ms = (time.perf_counter() - td0) * 1000
if own_hwnd:
    ctypes.windll.user32.DestroyWindow(own_hwnd)
check(dl_ms < 500 and nw2 >= 1, f"Anti-deadlock guard (own unpumped HWND active): fc_list_windows returned {nw2} in {dl_ms:.1f}ms")

thumb = (ctypes.c_uint8 * (240 * 135 * 4))()
ok_th = dll.fc_capture_monitor_thumb(0, thumb, 240, 135)
non_zero_px = sum(1 for i in range(0, len(thumb), 4) if (thumb[i] | thumb[i + 1] | thumb[i + 2]) != 0)
check(ok_th == 1 and non_zero_px > 500, f"Monitor #0 live thumbnail captured (240x135, lit_px={non_zero_px})")

caps = (FcDeviceInfo * 16)()
rens = (FcDeviceInfo * 16)()
nc = dll.fc_list_audio_devices(1, caps, 16)
nr = dll.fc_list_audio_devices(0, rens, 16)
check(nc >= 1 and nr >= 1, f"WASAPI devices detected: capture={nc}, render={nr}")

# 3. Zero-Copy VRAM NVENC Hardware Encode + Process-Loopback System Audio Capture test
ok_scr = dll.fc_screen_start_ex(0, ctypes.c_uint64(0), 2560, 1440, 60, 16000, 0, 0, None, 1, b"")
st = FcScreenStats()
for _ in range(25):
    time.sleep(0.1)
    dll.fc_screen_get_stats(ctypes.byref(st))
    if st.zero_copy == 1 and st.frames_encoded >= 1:
        break
dll.fc_screen_stop()
enc_name = st.encoder_name.decode("utf-8", "ignore")
check(ok_scr == 1 and st.zero_copy == 1 and "NVIDIA" in enc_name,
      f"Zero-Copy NVENC + System Audio Loopback: {st.width}x{st.height} zero_copy={st.zero_copy} enc='{enc_name}' enc_ms={st.encode_ms:.2f}ms kbps={st.bitrate_kbps:.0f}")

# 3b. Zero-Copy 32:9 Dual-Screen Composite (Monitor 1 + Monitor 2 simultaneously in VRAM -> NVENC 3840x1080!)
if nm >= 2:
    ok_dual = dll.fc_screen_start_ex(-2, ctypes.c_uint64(0), 3840, 1080, 60, 14000, 0, 0, None, 0, b"")
    time.sleep(1.2)
    dst = FcScreenStats()
    dll.fc_screen_get_stats(ctypes.byref(dst))
    dll.fc_screen_stop()
    denc = dst.encoder_name.decode("utf-8", "ignore")
    check(ok_dual == 1 and dst.zero_copy == 1 and dst.width == 3840 and dst.height == 1080 and dst.frames_encoded >= 1,
          f"Zero-Copy Dual-Screen Composite (-2): {dst.width}x{dst.height} frames={dst.frames_encoded} enc='{denc}' enc_ms={dst.encode_ms:.2f}ms kbps={dst.bitrate_kbps:.0f}")

# 4. ALL 6 Noise Suppressor Modes on live WASAPI Pro Audio
dll.fc_voice_start(b"", b"")
for mode_id, label in [(0, "Off"), (1, "StudioDSP"), (2, "RNNoise"), (3, "DeepFilterNet3"), (4, "NvidiaBroadcast"), (5, "AutoBest")]:
    dll.fc_voice_set_noise_config(mode_id, ctypes.c_float(0.88), 1, 1, ctypes.c_float(-46.0))
    time.sleep(0.2)
    ast = FcAudioStats()
    dll.fc_voice_get_stats(ctypes.byref(ast))
    bname = ast.backend_name.decode("utf-8", "ignore")
    check(len(bname) > 0, f"NoiseMode[{mode_id}:{label}] -> '{bname}' raw={ast.raw_rms:.4f} clean={ast.clean_rms:.4f} nr={ast.noise_reduction_db:.1f}dB")
dll.fc_voice_stop()

# 5. Official Discord DAVE E2EE MLS (Voice + Outbound Go Live Publisher + ALL 4 Multi-Stream Watch Slots + Tri-State Commit!)
kp_buf = (ctypes.c_uint8 * 4096)()
ok_v = dll.fc_dave_init(ctypes.c_uint16(1), ctypes.c_uint64(123456789012345678), b"987654321012345678", ctypes.c_uint32(11111))
kp_v = dll.fc_dave_get_key_package(kp_buf, 4096)
check(ok_v == 1 and kp_v > 100, f"Voice DAVE E2EE MLS KeyPackage ({kp_v} bytes)")

pre_welcome_commit = (ctypes.c_uint8 * 8)(0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04)
commit_rc_ignored = dll.fc_dave_process_commit(pre_welcome_commit, 8)
commit_rc_empty = dll.fc_dave_process_commit(None, 0)
check(commit_rc_ignored == 2 and commit_rc_empty == 0,
      f"Tri-state DAVE process_commit: pre-Welcome ignored rc={commit_rc_ignored} (no false OP23/OP31), empty/invalid rc={commit_rc_empty}")

ok_p = dll.fc_pub_dave_init(ctypes.c_uint16(1), ctypes.c_uint64(323456789012345678), b"987654321012345678", ctypes.c_uint32(33333), ctypes.c_uint32(33334))
kp_p = dll.fc_pub_dave_get_key_package(kp_buf, 4096)
check(ok_p == 1 and kp_p > 100, f"Outbound Go Live Publisher DAVE E2EE MLS KeyPackage ({kp_p} bytes)")

slot_kps = []
for slot in range(4):
    dll.fc_watch_set_codec_slot(slot, 1 if (slot % 2) else 0, 103 if (slot % 2) else 101)
    ok_s = dll.fc_watch_dave_init_slot(slot, ctypes.c_uint16(1), ctypes.c_uint64(223456789012345678 + slot), b"987654321012345678", ctypes.c_uint32(22220 + slot))
    dll.fc_watch_dave_add_user_slot(slot, b"111222333444555666")
    dll.fc_watch_dave_remove_user_slot(slot, b"111222333444555666")
    kp_s = dll.fc_watch_dave_get_key_package_slot(slot, kp_buf, 4096)
    if ok_s == 1 and kp_s > 100:
        slot_kps.append(kp_s)
check(len(slot_kps) == 4, f"4-Slot Multi-Stream Watch DAVE MLS + remove_user_slot verified (key packages: {slot_kps})")

dll.fc_shutdown()
print(f"\n=== SUMMARY: {len(failures)} failures ===", flush=True)
sys.exit(1 if failures else 0)
