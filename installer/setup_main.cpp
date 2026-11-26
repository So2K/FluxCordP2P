// Sleek Native Dark-UI Windows Installer for FluxCord P2P • Jetson Orin Edition
// Self-extracting single-file Setup.exe: appends [zipPayload][uint64 zipSize][8-byte "FCP2PZIP"] trailer.
// Installs to %LOCALAPPDATA%\Programs\FluxCordP2P (no UAC!), creates Desktop + Start Menu shortcuts + Uninstall entry, and launches!
#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dwmapi.h>
#include <shellapi.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <tlhelp32.h>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <thread>
#include <vector>

#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "user32.lib")

namespace fs = std::filesystem;

static std::atomic<int> g_progress{ 5 };
static std::atomic<bool> g_done{ false };
static std::atomic<bool> g_ok{ false };
static CRITICAL_SECTION g_cs;
static std::wstring g_status = L"Подготовка к установке FluxCord P2P...";
static std::wstring g_sub = L"Jetson Orin Nano • Zero-Copy 4K60 & Direct UDP P2P";
static HWND g_hwnd = nullptr;
static HICON g_icon = nullptr;

static void setStatus(const std::wstring& s, int pct) {
    EnterCriticalSection(&g_cs);
    g_status = s;
    LeaveCriticalSection(&g_cs);
    g_progress.store(pct);
    if (g_hwnd) InvalidateRect(g_hwnd, nullptr, FALSE);
}

static void stopRunningFluxCordP2P() {
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return;
    PROCESSENTRY32W pe{ sizeof(pe) };
    if (Process32FirstW(snap, &pe)) {
        do {
            if (_wcsicmp(pe.szExeFile, L"FluxCordP2P.exe") == 0) {
                HANDLE h = OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE, FALSE, pe.th32ProcessID);
                if (h) {
                    TerminateProcess(h, 0);
                    WaitForSingleObject(h, 3000);
                    CloseHandle(h);
                }
            }
        } while (Process32NextW(snap, &pe));
    }
    CloseHandle(snap);
    Sleep(180);
}

static bool createShortcut(const std::wstring& lnkPath, const std::wstring& targetExe, const std::wstring& workDir, const std::wstring& desc) {
    IShellLinkW* psl = nullptr;
    if (FAILED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_IShellLinkW, (void**)&psl))) return false;
    psl->SetPath(targetExe.c_str());
    psl->SetWorkingDirectory(workDir.c_str());
    psl->SetDescription(desc.c_str());
    psl->SetIconLocation(targetExe.c_str(), 0);
    IPersistFile* ppf = nullptr;
    bool ok = false;
    if (SUCCEEDED(psl->QueryInterface(IID_IPersistFile, (void**)&ppf))) {
        ok = SUCCEEDED(ppf->Save(lnkPath.c_str(), TRUE));
        ppf->Release();
    }
    psl->Release();
    return ok;
}

static bool runHidden(const std::wstring& cmdLine) {
    STARTUPINFOW si{ sizeof(si) };
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;
    PROCESS_INFORMATION pi{};
    std::wstring mut = cmdLine;
    if (!CreateProcessW(nullptr, mut.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi))
        return false;
    WaitForSingleObject(pi.hProcess, 60000);
    DWORD code = 1;
    GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return code == 0;
}

static void registerUninstall(const std::wstring& installDir, const std::wstring& exePath) {
    HKEY hKey = nullptr;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\FluxCordP2P",
                        0, nullptr, 0, KEY_WRITE, nullptr, &hKey, nullptr) != ERROR_SUCCESS) return;
    auto setSz = [&](const wchar_t* name, const std::wstring& val) {
        RegSetValueExW(hKey, name, 0, REG_SZ, (const BYTE*)val.c_str(), DWORD((val.size() + 1) * sizeof(wchar_t)));
    };
    setSz(L"DisplayName", L"FluxCord P2P (Jetson Orin Edition)");
    setSz(L"DisplayVersion", L"2.2.0");
    setSz(L"Publisher", L"So2K / FluxCord");
    setSz(L"InstallLocation", installDir);
    setSz(L"DisplayIcon", exePath);
    setSz(L"UninstallString", L"powershell.exe -NoProfile -Command \"Stop-Process -Name FluxCordP2P -Force -EA SilentlyContinue; Start-Sleep -Milliseconds 300; Remove-Item '" + installDir + L"' -Recurse -Force -EA SilentlyContinue; Remove-ItemProperty 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\FluxCordP2P' -Name * -EA SilentlyContinue; Remove-Item 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\FluxCordP2P' -Force -EA SilentlyContinue\"");
    DWORD noMod = 1;
    RegSetValueExW(hKey, L"NoModify", 0, REG_DWORD, (const BYTE*)&noMod, sizeof(noMod));
    RegSetValueExW(hKey, L"NoRepair", 0, REG_DWORD, (const BYTE*)&noMod, sizeof(noMod));
    RegCloseKey(hKey);
}

static void installWorker() {
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    try {
        wchar_t selfPath[MAX_PATH]{};
        GetModuleFileNameW(nullptr, selfPath, MAX_PATH);

        setStatus(L"Проверка пакета установки...", 12);
        std::ifstream f(selfPath, std::ios::binary | std::ios::ate);
        if (!f) { setStatus(L"Ошибка чтения установщика!", 100); g_done = true; CoUninitialize(); return; }
        int64_t totalSize = f.tellg();
        if (totalSize < 32) { setStatus(L"Повреждённый пакет установки!", 100); g_done = true; CoUninitialize(); return; }

        char magic[8]{};
        uint64_t zipSize = 0;
        f.seekg(totalSize - 16);
        f.read(reinterpret_cast<char*>(&zipSize), 8);
        f.read(magic, 8);
        if (std::memcmp(magic, "FCP2PZIP", 8) != 0 || zipSize == 0 || int64_t(zipSize) > totalSize - 16) {
            setStatus(L"Не найден встроенный архив FluxCord P2P!", 100);
            g_done = true;
            CoUninitialize();
            return;
        }

        wchar_t localApp[MAX_PATH]{};
        SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, 0, localApp);
        fs::path installDir = fs::path(localApp) / L"Programs" / L"FluxCordP2P";
        fs::path tmpZip = fs::temp_directory_path() / L"FluxCordP2P_Setup_Payload.zip";

        setStatus(L"Извлечение архива...", 28);
        {
            f.seekg(totalSize - 16 - int64_t(zipSize));
            std::ofstream out(tmpZip, std::ios::binary | std::ios::trunc);
            std::vector<char> buf(256 * 1024);
            uint64_t rem = zipSize;
            while (rem > 0) {
                size_t chunk = (size_t)std::min<uint64_t>(rem, buf.size());
                f.read(buf.data(), chunk);
                out.write(buf.data(), chunk);
                rem -= chunk;
            }
        }
        f.close();

        setStatus(L"Остановка запущенного FluxCord P2P...", 45);
        stopRunningFluxCordP2P();

        fs::create_directories(installDir);
        setStatus(L"Установка файлов в %LOCALAPPDATA%\\Programs\\FluxCordP2P...", 62);

        // Fast native tar.exe (built into Windows 10/11) with PowerShell Expand-Archive fallback
        std::wstring tarCmd = L"tar.exe -xf \"" + tmpZip.wstring() + L"\" -C \"" + installDir.wstring() + L"\"";
        if (!runHidden(tarCmd)) {
            std::wstring psCmd = L"powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"Expand-Archive -LiteralPath '" +
                                 tmpZip.wstring() + L"' -DestinationPath '" + installDir.wstring() + L"' -Force\"";
            if (!runHidden(psCmd)) {
                setStatus(L"Ошибка распаковки файлов!", 100);
                try { fs::remove(tmpZip); } catch (...) {}
                g_done = true;
                CoUninitialize();
                return;
            }
        }
        try { fs::remove(tmpZip); } catch (...) {}

        setStatus(L"Создание ярлыков на Рабочем столе и в меню Пуск...", 85);
        std::wstring exePath = (installDir / L"FluxCordP2P.exe").wstring();

        wchar_t desktopDir[MAX_PATH]{};
        if (SUCCEEDED(SHGetFolderPathW(nullptr, CSIDL_DESKTOPDIRECTORY, nullptr, 0, desktopDir))) {
            createShortcut((fs::path(desktopDir) / L"FluxCord P2P.lnk").wstring(), exePath, installDir.wstring(), L"FluxCord P2P • Jetson Orin");
        }
        wchar_t programsDir[MAX_PATH]{};
        if (SUCCEEDED(SHGetFolderPathW(nullptr, CSIDL_PROGRAMS, nullptr, 0, programsDir))) {
            fs::path smDir = fs::path(programsDir) / L"FluxCord P2P";
            fs::create_directories(smDir);
            createShortcut((smDir / L"FluxCord P2P.lnk").wstring(), exePath, installDir.wstring(), L"FluxCord P2P • Jetson Orin");
        }
        registerUninstall(installDir.wstring(), exePath);

        setStatus(L"Готово! Запуск FluxCord P2P...", 100);
        g_ok = true;
        g_done = true;
        Sleep(550);
        ShellExecuteW(nullptr, L"open", exePath.c_str(), nullptr, installDir.wstring().c_str(), SW_SHOWNORMAL);
        PostMessageW(g_hwnd, WM_CLOSE, 0, 0);
    } catch (...) {
        setStatus(L"Неожиданная ошибка установки!", 100);
        g_done = true;
    }
    CoUninitialize();
}

static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    if (msg == WM_PAINT) {
        PAINTSTRUCT ps{};
        HDC hdc = BeginPaint(hwnd, &ps);
        RECT rc{};
        GetClientRect(hwnd, &rc);

        HDC mem = CreateCompatibleDC(hdc);
        HBITMAP bmp = CreateCompatibleBitmap(hdc, rc.right, rc.bottom);
        HGDIOBJ oldBmp = SelectObject(mem, bmp);

        HBRUSH bg = CreateSolidBrush(RGB(16, 18, 24));
        FillRect(mem, &rc, bg);
        DeleteObject(bg);

        HPEN pen = CreatePen(PS_SOLID, 1, RGB(45, 50, 68));
        HGDIOBJ oldPen = SelectObject(mem, pen);
        HGDIOBJ oldBr = SelectObject(mem, GetStockObject(NULL_BRUSH));
        Rectangle(mem, 0, 0, rc.right, rc.bottom);
        SelectObject(mem, oldBr);
        SelectObject(mem, oldPen);
        DeleteObject(pen);

        if (g_icon) DrawIconEx(mem, 28, 26, g_icon, 48, 48, 0, nullptr, DI_NORMAL);

        SetBkMode(mem, TRANSPARENT);
        HFONT fTitle = CreateFontW(-22, 0, 0, 0, FW_BOLD, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Segoe UI");
        HFONT fSub = CreateFontW(-13, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Segoe UI");
        HFONT fStat = CreateFontW(-14, 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Segoe UI");

        SelectObject(mem, fTitle);
        SetTextColor(mem, RGB(243, 244, 246));
        RECT rT{ 92, 26, rc.right - 24, 56 };
        DrawTextW(mem, L"FluxCord P2P • Установка", -1, &rT, DT_LEFT | DT_SINGLELINE);

        SelectObject(mem, fSub);
        SetTextColor(mem, RGB(56, 189, 248));
        RECT rS{ 92, 54, rc.right - 24, 74 };
        DrawTextW(mem, g_sub.c_str(), -1, &rS, DT_LEFT | DT_SINGLELINE);

        std::wstring stCopy;
        EnterCriticalSection(&g_cs);
        stCopy = g_status;
        LeaveCriticalSection(&g_cs);

        SelectObject(mem, fStat);
        SetTextColor(mem, g_ok.load() ? RGB(52, 211, 153) : RGB(209, 213, 219));
        RECT rSt{ 28, 102, rc.right - 28, 126 };
        DrawTextW(mem, stCopy.c_str(), -1, &rSt, DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS);

        // Progress bar track + fill
        RECT track{ 28, 134, rc.right - 28, 148 };
        HBRUSH trBr = CreateSolidBrush(RGB(32, 36, 48));
        FillRect(mem, &track, trBr);
        DeleteObject(trBr);

        int pct = g_progress.load();
        int fillW = (track.right - track.left) * pct / 100;
        RECT fill{ track.left, track.top, track.left + fillW, track.bottom };
        HBRUSH flBr = CreateSolidBrush(g_ok.load() ? RGB(35, 245, 110) : RGB(88, 101, 242));
        FillRect(mem, &fill, flBr);
        DeleteObject(flBr);

        DeleteObject(fTitle);
        DeleteObject(fSub);
        DeleteObject(fStat);

        BitBlt(hdc, 0, 0, rc.right, rc.bottom, mem, 0, 0, SRCCOPY);
        SelectObject(mem, oldBmp);
        DeleteObject(bmp);
        DeleteDC(mem);
        EndPaint(hwnd, &ps);
        return 0;
    }
    if (msg == WM_DESTROY) {
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

int WINAPI wWinMain(HINSTANCE hInst, HINSTANCE, PWSTR, int) {
    InitializeCriticalSection(&g_cs);
    g_icon = (HICON)LoadImageW(hInst, MAKEINTRESOURCEW(1), IMAGE_ICON, 48, 48, 0);

    WNDCLASSEXW wc{ sizeof(wc) };
    wc.lpfnWndProc = WndProc;
    wc.hInstance = hInst;
    wc.hIcon = g_icon;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.lpszClassName = L"FluxCordP2PSetupWnd";
    RegisterClassExW(&wc);

    int w = 480, h = 186;
    int sx = (GetSystemMetrics(SM_CXSCREEN) - w) / 2;
    int sy = (GetSystemMetrics(SM_CYSCREEN) - h) / 2;
    g_hwnd = CreateWindowExW(WS_EX_APPWINDOW, wc.lpszClassName, L"Установка FluxCord P2P",
                             WS_POPUP | WS_VISIBLE, sx, sy, w, h, nullptr, nullptr, hInst, nullptr);
    int corner = 2; // DWMWCP_ROUND
    DwmSetWindowAttribute(g_hwnd, 33, &corner, sizeof(corner));

    std::thread worker(installWorker);
    MSG msg{};
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    if (worker.joinable()) worker.join();
    DeleteCriticalSection(&g_cs);
    return g_ok.load() ? 0 : 1;
}
