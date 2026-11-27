using System.Runtime.InteropServices;

namespace FluxCord;

internal static unsafe partial class W
{
    public const uint WM_DESTROY = 0x0002, WM_SIZE = 0x0005, WM_SETFOCUS = 0x0007, WM_KILLFOCUS = 0x0008,
        WM_PAINT = 0x000F, WM_CLOSE = 0x0010, WM_QUERYENDSESSION = 0x0011, WM_ERASEBKGND = 0x0014,
        WM_ENDSESSION = 0x0016, WM_SETCURSOR = 0x0020,
        WM_GETMINMAXINFO = 0x0024, WM_SETFONT = 0x0030, WM_CONTEXTMENU = 0x007B,
        WM_SIZING = 0x0214, WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232,
        WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101,
        WM_CHAR = 0x0102, WM_COMMAND = 0x0111, WM_TIMER = 0x0113, WM_CTLCOLOREDIT = 0x0133,
        WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203,
        WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MOUSEWHEEL = 0x020A,
        WM_CAPTURECHANGED = 0x0215,
        WM_DROPFILES = 0x0233,
        WM_MOUSELEAVE = 0x02A3, WM_DPICHANGED = 0x02E0, WM_HOTKEY = 0x0312, WM_APP = 0x8000,
        WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_SETICON = 0x0080;
    public const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_NOREPEAT = 0x4000;
    public const uint NIM_ADD = 0x00000000, NIM_MODIFY = 0x00000001, NIM_DELETE = 0x00000002,
        NIF_MESSAGE = 0x00000001, NIF_ICON = 0x00000002, NIF_TIP = 0x00000004, NIF_INFO = 0x00000010,
        NIIF_INFO = 0x00000001, NIIF_NOSOUND = 0x00000010;
    public const uint MF_STRING = 0x0000, MF_CHECKED = 0x0008, MF_SEPARATOR = 0x0800;
    public const uint TPM_RIGHTBUTTON = 0x0002, TPM_BOTTOMALIGN = 0x0020, TPM_NONOTIFY = 0x0080, TPM_RETURNCMD = 0x0100;
    public const uint IMAGE_ICON = 1, LR_DEFAULTSIZE = 0x0040;
    public const int SM_CXICON = 11, SM_CYICON = 12, SM_CXSIZEFRAME = 32, SM_CYSIZEFRAME = 33,
        SM_CXSMICON = 49, SM_CYSMICON = 50, SM_CXPADDEDBORDER = 92;

    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_VISIBLE = 0x10000000, WS_CHILD = 0x40000000,
        WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000,
        ES_MULTILINE = 0x0004, ES_AUTOVSCROLL = 0x0040, ES_AUTOHSCROLL = 0x0080, ES_WANTRETURN = 0x1000, ES_PASSWORD = 0x0020;

    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80;
    public const int SW_HIDE = 0, SW_SHOW = 5, SW_SHOWNORMAL = 1, SW_MAXIMIZE = 3, SW_MINIMIZE = 6;
    public const int GWLP_WNDPROC = -4;
    public const int VK_RETURN = 13, VK_ESCAPE = 27, VK_SHIFT = 16, VK_CONTROL = 17, VK_MENU = 18,
        VK_UP = 0x26, VK_DOWN = 0x28, VK_A = 0x41, VK_D = 0x44, VK_F = 0x46, VK_K = 0x4B, VK_M = 0x4D, VK_Q = 0x51, VK_V = 0x56, VK_RCONTROL = 0xA3;
    public const int EN_CHANGE = 0x0300;

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct NCCALCSIZE_PARAMS { public RECT r0, r1, r2; public nint lppos; }
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public POINT pt; public uint priv; }
    [StructLayout(LayoutKind.Sequential)] public struct MINMAXINFO { public POINT reserved, maxSize, maxPosition, minTrackSize, maxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] public struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public nint hwndTrack; public uint dwHoverTime; }
    [StructLayout(LayoutKind.Sequential)] public struct DATA_BLOB { public uint cbData; public byte* pbData; }

    [StructLayout(LayoutKind.Sequential)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uTimeoutOrVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [LibraryImport("user32.dll")] public static partial ushort RegisterClassExW(WNDCLASSEXW* wc);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(uint ex, string cls, string? name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [LibraryImport("user32.dll")] public static partial nint DefWindowProcW(nint h, uint m, nuint w, nint l);
    [LibraryImport("user32.dll")] public static partial int GetMessageW(MSG* msg, nint h, uint min, uint max);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool TranslateMessage(MSG* msg);
    [LibraryImport("user32.dll")] public static partial nint DispatchMessageW(MSG* msg);
    [LibraryImport("user32.dll")] public static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PostMessageW(nint h, uint m, nuint w, nint l);
    [LibraryImport("user32.dll")] public static partial nint SendMessageW(nint h, uint m, nuint w, nint l);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ShowWindow(nint h, int cmd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool InvalidateRect(nint h, RECT* r, int erase);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ValidateRect(nint h, RECT* r);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetClientRect(nint h, RECT* r);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetWindowRect(nint h, RECT* r);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] public static partial uint GetDpiForWindow(nint h);
    [LibraryImport("user32.dll")] public static partial uint GetDpiForSystem();
    [LibraryImport("user32.dll")] public static partial nint LoadCursorW(nint inst, nint id);
    [LibraryImport("user32.dll")] public static partial nint LoadImageW(nint inst, nint name, uint type, int cx, int cy, uint fuLoad);
    [LibraryImport("user32.dll")] public static partial int GetSystemMetrics(int nIndex);
    [LibraryImport("user32.dll")] public static partial int GetSystemMetricsForDpi(int nIndex, uint dpi);
    [LibraryImport("user32.dll")] public static partial nint SetCursor(nint cur);
    [LibraryImport("user32.dll")] public static partial nint SetCapture(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ReleaseCapture();
    [LibraryImport("user32.dll")] public static partial short GetKeyState(int vk);
    [LibraryImport("user32.dll")] public static partial short GetAsyncKeyState(int vk);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool OpenClipboard(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool EmptyClipboard();
    [LibraryImport("user32.dll")] public static partial nint SetClipboardData(uint fmt, nint mem);
    [LibraryImport("user32.dll")] public static partial nint GetClipboardData(uint fmt);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsClipboardFormatAvailable(uint fmt);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool CloseClipboard();
    [LibraryImport("kernel32.dll")] public static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll")] public static partial nint GlobalFree(nint mem);
    [LibraryImport("kernel32.dll")] public static partial nint GlobalLock(nint mem);
    [LibraryImport("kernel32.dll")] public static partial nuint GlobalSize(nint mem);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GlobalUnlock(nint mem);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint ShellExecuteW(nint h, string? op, string file, string? args, string? dir, int show);
    [LibraryImport("shell32.dll")] public static partial void DragAcceptFiles(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool accept);
    [LibraryImport("shell32.dll")] public static partial uint DragQueryFileW(nint hDrop, uint iFile, char* lpszFile, uint cch);
    [LibraryImport("shell32.dll")] public static partial void DragFinish(nint hDrop);
    [LibraryImport("user32.dll")] public static partial nuint SetTimer(nint h, nuint id, uint ms, nint proc);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool KillTimer(nint h, nuint id);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool TrackMouseEvent(TRACKMOUSEEVENT* e);
    [LibraryImport("user32.dll")] public static partial nint SetFocus(nint h);
    [LibraryImport("user32.dll")] public static partial nint GetFocus();
    [LibraryImport("user32.dll")] public static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FlashWindow(nint h, [MarshalAs(UnmanagedType.Bool)] bool invert);
    [LibraryImport("user32.dll")] public static partial nint SetWindowLongPtrW(nint h, int idx, nint v);
    [LibraryImport("user32.dll")] public static partial nint CallWindowProcW(nint prev, nint h, uint m, nuint w, nint l);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowTextW(nint h, string s);
    [LibraryImport("user32.dll")] public static partial int GetWindowTextW(nint h, char* buf, int max);
    [LibraryImport("user32.dll")] public static partial int GetWindowTextLengthW(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsZoomed(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsIconic(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindowVisible(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetForegroundWindow(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetCursorPos(POINT* lpPoint);
    [LibraryImport("user32.dll")] public static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyMenu(nint hMenu);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetMenuDefaultItem(nint hMenu, uint uItem, uint fByPos);
    [LibraryImport("user32.dll")] public static partial int TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial uint RegisterWindowMessageW(string lpString);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial nint FindWindowW(string? lpClassName, string? lpWindowName);
    [LibraryImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool Shell_NotifyIconW(uint dwMessage, NOTIFYICONDATAW* lpData);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyWindow(nint h);

    [LibraryImport("kernel32.dll")] public static partial nint GetModuleHandleW(nint name);
    [LibraryImport("kernel32.dll")] public static partial nint LocalFree(nint hMem);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outp, uint clip, uint quality, uint pitch, string face);
    [LibraryImport("gdi32.dll")] public static partial nint CreateSolidBrush(uint color);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] public static partial uint SetTextColor(nint dc, uint color);
    [LibraryImport("gdi32.dll")] public static partial uint SetBkColor(nint dc, uint color);
    [LibraryImport("gdi32.dll")] public static partial int SetBkMode(nint dc, int mode);
    [LibraryImport("gdi32.dll")] public static partial nint CreateRectRgn(int x1, int y1, int x2, int y2);
    [LibraryImport("gdi32.dll")] public static partial int CombineRgn(nint dst, nint src1, nint src2, int mode);
    [LibraryImport("user32.dll")] public static partial int SetWindowRgn(nint h, nint rgn, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int L, R, T, B; }

    [LibraryImport("dwmapi.dll")] public static partial int DwmSetWindowAttribute(nint h, uint attr, void* value, uint size);
    [LibraryImport("dwmapi.dll")] public static partial int DwmExtendFrameIntoClientArea(nint h, MARGINS* margins);
    [LibraryImport("uxtheme.dll", EntryPoint = "#135")] public static partial int SetPreferredAppMode(int mode);

    [LibraryImport("crypt32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptProtectData(DATA_BLOB* pDataIn, nint szDataDescr, nint pOptionalEntropy, nint pvReserved, nint pPromptStruct, uint dwFlags, DATA_BLOB* pDataOut);

    [LibraryImport("crypt32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptUnprotectData(DATA_BLOB* pDataIn, nint ppszDataDescr, nint pOptionalEntropy, nint pvReserved, nint pPromptStruct, uint dwFlags, DATA_BLOB* pDataOut);

    public static int LoWord(nint l) => (short)(l.ToInt64() & 0xFFFF);
    public static int HiWord(nint l) => (short)((l.ToInt64() >> 16) & 0xFFFF);
    public static bool KeyDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;
    public static bool AsyncKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    public static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    public static int ResolveKeyVk(string keyName)
    {
        string k = (keyName ?? "").Trim().ToUpperInvariant();
        if (k.Length == 4 && k.StartsWith("KEY", StringComparison.Ordinal) && k[3] is >= 'A' and <= 'Z')
            return k[3];
        if (k.Length == 1 && k[0] is >= 'A' and <= 'Z')
            return k[0];
        if (k.Length == 6 && k.StartsWith("DIGIT", StringComparison.Ordinal) && k[5] is >= '0' and <= '9')
            return k[5];
        if (k.Length == 1 && k[0] is >= '0' and <= '9')
            return k[0];
        return k switch
        {
            "LCONTROL" or "CONTROLLEFT" or "CTRL" or "CONTROL" => 0xA2,
            "RCONTROL" or "CONTROLRIGHT" or "RCTRL" => 0xA3,
            "LSHIFT" or "SHIFTLEFT" or "SHIFT" => 0xA0,
            "RSHIFT" or "SHIFTRIGHT" => 0xA1,
            "LMENU" or "ALTLEFT" or "LALT" or "ALT" => 0xA4,
            "RMENU" or "ALTRIGHT" or "RALT" => 0xA5,
            "CAPSLOCK" or "CAPS" => 0x14,
            "TAB" => 0x09,
            "BACKQUOTE" or "TILDE" or "`" => 0xC0,
            "MBUTTON" or "MOUSE3" or "MOUSEBUTTON2" => 0x04,
            "XBUTTON1" or "MOUSE4" or "MOUSEBUTTON3" => 0x05,
            "XBUTTON2" or "MOUSE5" or "MOUSEBUTTON4" => 0x06,
            "SPACE" => 0x20,
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
            "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
            "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
            _ => VK_RCONTROL
        };
    }

    public static void CopyText(nint hwnd, string text)
    {
        if (string.IsNullOrEmpty(text) || !OpenClipboard(hwnd)) return;
        try
        {
            EmptyClipboard();
            nuint bytes = (nuint)((text.Length + 1) * 2);
            nint mem = GlobalAlloc(0x0002, bytes); // GMEM_MOVEABLE
            if (mem == 0) return;
            nint ptr = GlobalLock(mem);
            if (ptr != 0)
            {
                fixed (char* src = text) Buffer.MemoryCopy(src, (void*)ptr, bytes, (nuint)(text.Length * 2));
                ((char*)ptr)[text.Length] = '\0';
                GlobalUnlock(mem);
            }
            if (SetClipboardData(13, mem) == 0) GlobalFree(mem); // prevent leak if SetClipboardData fails
        }
        finally { CloseClipboard(); }
    }

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnregisterHotKey(nint hWnd, int id);

    [StructLayout(LayoutKind.Sequential)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFO { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
    [LibraryImport("user32.dll")] public static partial nint GetDC(nint hWnd);
    [LibraryImport("user32.dll")] public static partial int ReleaseDC(nint hWnd, nint hDC);
    [LibraryImport("gdi32.dll")] public static partial nint CreateCompatibleDC(nint hdc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteDC(nint hdc);
    [LibraryImport("gdi32.dll")] public static partial nint SelectObject(nint hdc, nint h);
    [LibraryImport("gdi32.dll")] public static partial nint CreateDIBSection(nint hdc, BITMAPINFO* pbmi, uint usage, out byte* ppvBits, nint hSection, uint offset);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial int DrawTextW(nint hdc, string lpchText, int cchText, RECT* lprc, uint format);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UpdateLayeredWindow(nint hWnd, nint hdcDst, POINT* pptDst, POINT* psize, nint hdcSrc, POINT* pptSrc, uint crKey, BLENDFUNCTION* pblend, uint dwFlags);

    public static string FormatHotkey(uint mod, uint vk)
    {
        var parts = new List<string>();
        if ((mod & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mod & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mod & MOD_ALT) != 0) parts.Add("Alt");
        string kn = vk switch
        {
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),
            >= 0x70 and <= 0x7B => $"F{vk - 0x6F}",
            0x20 => "Space",
            0xC0 => "`",
            0x09 => "Tab",
            _ => $"0x{vk:X2}"
        };
        parts.Add(kn);
        return string.Join("+", parts);
    }
    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegCreateKeyExW(nint hKey, string lpSubKey, uint reserved, string? lpClass, uint dwOptions, uint samDesired, nint lpSecurityAttributes, out nint phkResult, out uint lpdwDisposition);
    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegSetValueExW(nint hKey, string lpValueName, uint reserved, uint dwType, char* lpData, uint cbData);
    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegDeleteValueW(nint hKey, string lpValueName);
    [LibraryImport("advapi32.dll")]
    private static partial int RegCloseKey(nint hKey);

    public static void SetWindowsAutostart(bool enable, bool startMinimized)
    {
        nint HKEY_CURRENT_USER = unchecked((nint)0x80000001);
        const uint KEY_WRITE = 0x20006;
        if (RegCreateKeyExW(HKEY_CURRENT_USER, @"Software\Microsoft\Windows\CurrentVersion\Run", 0, null, 0, KEY_WRITE, 0, out nint hKey, out _) != 0)
            return;
        try
        {
            if (enable)
            {
                string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FluxCordP2P.exe");
                string val = startMinimized ? $"\"{exe}\" --minimized" : $"\"{exe}\"";
                fixed (char* p = val)
                    RegSetValueExW(hKey, "FluxCordP2P", 0, 1 /*REG_SZ*/, p, (uint)((val.Length + 1) * 2));
            }
            else
            {
                RegDeleteValueW(hKey, "FluxCordP2P");
            }
        }
        finally { RegCloseKey(hKey); }
    }

    public static void OpenLocalFile(string path)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
            ShellExecuteW(0, "open", path, null, Path.GetDirectoryName(path), 5);
    }

    public static void OpenUrl(string url)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            ShellExecuteW(0, "open", url, null, null, 5);
    }

    /// <summary>
    /// Converts clipboard CF_DIB screenshot into a compact PNG file (10-30x smaller than raw BMP, never hits Discord 413!).
    /// </summary>
    public static string? SaveClipboardImageToTemp(nint hwnd)
    {
        const uint CF_DIB = 8;
        if (!IsClipboardFormatAvailable(CF_DIB) || !OpenClipboard(hwnd)) return null;
        byte[]? bmp = null;
        try
        {
            nint hMem = GetClipboardData(CF_DIB);
            if (hMem == 0) return null;
            nuint dibSize = GlobalSize(hMem);
            if (dibSize < 40 || dibSize > 64 * 1024 * 1024) return null;
            nint ptr = GlobalLock(hMem);
            if (ptr == 0) return null;
            try
            {
                uint biSize = *(uint*)ptr;
                if (biSize < 40 || biSize > (uint)dibSize - 4) return null;
                ushort bitCount = *(ushort*)((byte*)ptr + 14);
                uint compression = *(uint*)((byte*)ptr + 16);
                uint clrUsed = *(uint*)((byte*)ptr + 32);
                if (clrUsed > 65536) return null;
                uint paletteBytes = clrUsed > 0 ? clrUsed * 4 : (bitCount is > 0 and <= 8 ? (1u << bitCount) * 4 : (compression == 3 && biSize == 40 ? 12u : 0u));
                uint pixelOff = 14 + biSize + paletteBytes;
                uint fileSize = 14 + (uint)dibSize;
                if (pixelOff > fileSize) return null;

                bmp = new byte[fileSize];
                bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
                bmp[2] = (byte)(fileSize & 0xFF); bmp[3] = (byte)((fileSize >> 8) & 0xFF);
                bmp[4] = (byte)((fileSize >> 16) & 0xFF); bmp[5] = (byte)((fileSize >> 24) & 0xFF);
                bmp[10] = (byte)(pixelOff & 0xFF); bmp[11] = (byte)((pixelOff >> 8) & 0xFF);
                bmp[12] = (byte)((pixelOff >> 16) & 0xFF); bmp[13] = (byte)((pixelOff >> 24) & 0xFF);
                Marshal.Copy(ptr, bmp, 14, (int)dibSize);
            }
            finally { GlobalUnlock(hMem); }
        }
        catch { return null; }
        finally { CloseClipboard(); }

        if (bmp == null) return null;
        try
        {
            var png = ImageCache.ConvertImageToPng(bmp);
            if (png != null && png.Length > 0)
            {
                string pngPath = Path.Combine(Path.GetTempPath(), $"fluxcord_paste_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
                File.WriteAllBytes(pngPath, png);
                return pngPath;
            }
            string bmpPath = Path.Combine(Path.GetTempPath(), $"fluxcord_paste_{DateTime.Now:yyyyMMdd_HHmmss_fff}.bmp");
            File.WriteAllBytes(bmpPath, bmp);
            return bmpPath;
        }
        catch { return null; }
    }

    public static List<string> ReadDroppedFiles(nint hDrop)
    {
        var files = new List<string>();
        uint count = DragQueryFileW(hDrop, 0xFFFFFFFFu, null, 0);
        var buf = new char[1024];
        fixed (char* p = buf)
        {
            for (uint i = 0; i < Math.Min(count, 8u); i++)
            {
                uint len = DragQueryFileW(hDrop, i, p, (uint)buf.Length);
                if (len > 0) files.Add(new string(p, 0, (int)len));
            }
        }
        DragFinish(hDrop);
        return files;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OPENFILENAMEW
    {
        public uint lStructSize;
        public nint hwndOwner, hInstance;
        public char* lpstrFilter, lpstrCustomFilter;
        public uint nMaxCustFilter, nFilterIndex;
        public char* lpstrFile;
        public uint nMaxFile;
        public char* lpstrFileTitle;
        public uint nMaxFileTitle;
        public char* lpstrInitialDir, lpstrTitle;
        public uint Flags;
        public ushort nFileOffset, nFileExtension;
        public char* lpstrDefExt;
        public nint lCustData, lpfnHook;
        public char* lpTemplateName;
        public nint pvReserved;
        public uint dwReserved, FlagsEx;
    }

    [LibraryImport("comdlg32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetOpenFileNameW(OPENFILENAMEW* ofn);

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public int fIcon;
        public uint xHotspot, yHotspot;
        public nint hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPV5HEADER
    {
        public uint bV5Size;
        public int bV5Width, bV5Height;
        public ushort bV5Planes, bV5BitCount;
        public uint bV5Compression, bV5SizeImage;
        public int bV5XPelsPerMeter, bV5YPelsPerMeter;
        public uint bV5ClrUsed, bV5ClrImportant;
        public uint bV5RedMask, bV5GreenMask, bV5BlueMask, bV5AlphaMask;
        public uint bV5CSType;
        public fixed int bV5Endpoints[9];
        public uint bV5GammaRed, bV5GammaGreen, bV5GammaBlue;
        public uint bV5Intent, bV5ProfileData, bV5ProfileSize, bV5Reserved;
    }

    [LibraryImport("user32.dll")] public static partial nint CreateIconIndirect(ICONINFO* piconinfo);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyIcon(nint hIcon);
    [LibraryImport("gdi32.dll")] public static partial nint CreateDIBSection(nint hdc, BITMAPV5HEADER* pbmi, uint usage, void** ppvBits, nint hSection, uint offset);
    [LibraryImport("gdi32.dll")] public static partial nint CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, nint lpBits);

    public static nint CreateHIconFromBgra(byte[] bgra, int size)
    {
        if (bgra.Length < size * size * 4) return 0;
        var bi = new BITMAPV5HEADER
        {
            bV5Size = (uint)sizeof(BITMAPV5HEADER),
            bV5Width = size,
            bV5Height = -size, // top-down
            bV5Planes = 1,
            bV5BitCount = 32,
            bV5Compression = 3, // BI_BITFIELDS
            bV5RedMask = 0x00FF0000,
            bV5GreenMask = 0x0000FF00,
            bV5BlueMask = 0x000000FF,
            bV5AlphaMask = 0xFF000000
        };
        void* bits = null;
        nint hColor = CreateDIBSection(0, &bi, 0, &bits, 0, 0);
        if (hColor == 0 || bits == null) return 0;
        fixed (byte* src = bgra) Buffer.MemoryCopy(src, bits, size * size * 4, size * size * 4);
        nint hMask = CreateBitmap(size, size, 1, 1, 0);
        var ii = new ICONINFO { fIcon = 1, hbmMask = hMask, hbmColor = hColor };
        nint hIcon = CreateIconIndirect(&ii);
        DeleteObject(hColor);
        if (hMask != 0) DeleteObject(hMask);
        return hIcon;
    }

    public static string? PickFile(nint owner, bool imagesOnly = false)
    {
        var buf = new char[1024];
        string filter = imagesOnly
            ? "Изображения (*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp)\0*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp\0Все файлы (*.*)\0*.*\0\0"
            : "Все файлы (*.*)\0*.*\0Изображения (*.png;*.jpg;*.webp;*.gif)\0*.png;*.jpg;*.jpeg;*.webp;*.gif\0\0";
        fixed (char* pBuf = buf)
        fixed (char* pFilter = filter)
        {
            var ofn = new OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(OPENFILENAMEW),
                hwndOwner = owner,
                lpstrFilter = pFilter,
                lpstrFile = pBuf,
                nMaxFile = (uint)buf.Length,
                Flags = 0x00080000 | 0x00001000 | 0x00000008 // OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_NOCHANGEDIR
            };
            if (GetOpenFileNameW(&ofn)) return new string(pBuf).TrimEnd('\0');
        }
        return null;
    }

    public static byte[] ProtectDpapi(byte[] plain)
    {
        fixed (byte* p = plain)
        {
            DATA_BLOB din = new() { cbData = (uint)plain.Length, pbData = p };
            DATA_BLOB dout = default;
            if (!CryptProtectData(&din, 0, 0, 0, 0, 0, &dout)) return [];
            try
            {
                var res = new byte[dout.cbData];
                Marshal.Copy((nint)dout.pbData, res, 0, res.Length);
                return res;
            }
            finally { LocalFree((nint)dout.pbData); }
        }
    }

    public static byte[] UnprotectDpapi(byte[] enc)
    {
        fixed (byte* p = enc)
        {
            DATA_BLOB din = new() { cbData = (uint)enc.Length, pbData = p };
            DATA_BLOB dout = default;
            if (!CryptUnprotectData(&din, 0, 0, 0, 0, 0, &dout)) return [];
            try
            {
                var res = new byte[dout.cbData];
                Marshal.Copy((nint)dout.pbData, res, 0, res.Length);
                return res;
            }
            finally { LocalFree((nint)dout.pbData); }
        }
    }
}
