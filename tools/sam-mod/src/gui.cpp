// sam-mod-gui - the double-clickable front end.
//
// Layout follows the same shape as the 7DTD Mod Updater in this author's other project:
// a game-folder box with a live validity indicator, an installed-vs-latest line, one
// prominent "update and play" button with two narrower alternatives, and a log.
//
// Plain Win32 rather than WinForms on purpose. The CLI already links only against Windows
// libraries, so this stays a ~500 KB self-contained binary: no .NET runtime to install and
// nothing to explain to whoever receives it.
//
// All the real work lives in the same headers the CLI uses; this file is presentation and
// threading only. Long operations run on a worker thread and report back through
// PostMessage, so the window never freezes and the worker never touches a control directly.

#ifndef UNICODE
#define UNICODE
#endif

#include <windows.h>
#include <commctrl.h>
#include <shlobj.h>
#include <tlhelp32.h>

#include <atomic>
#include <filesystem>
#include <fstream>
#include <functional>
#include <memory>
#include <string>
#include <vector>
#include <thread>

#include "github.hpp"
#include "install.hpp"

#pragma comment(lib, "comctl32.lib")

namespace fs = std::filesystem;
using namespace sam;

namespace {

constexpr const wchar_t* kWindowClass = L"SamModGuiWindow";
constexpr const wchar_t* kTitle = L"Shift At Midnight - Mod Updater";
constexpr const wchar_t* kGameExe = L"ShiftAtMidnight.exe";
constexpr const char* kOwner = "M3RT1N99";
constexpr const char* kRepo = "shift-at-midnight-mods";

enum : int {
    IdPath = 1001, IdBrowse, IdUpdatePlay, IdUpdateOnly, IdPlayOnly, IdLog, IdStatus, IdVersions,
    IdMods, IdToggle, IdUninstall
};
enum : UINT {
    WmLog = WM_APP + 1,      // lParam = new std::wstring*
    WmVersions = WM_APP + 2, // lParam = new std::wstring*
    WmDone = WM_APP + 3,
    WmPlay = WM_APP + 4,     // worker asks the UI thread to launch the game
};

std::wstring widen(const std::string& s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring out((size_t)n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), out.data(), n);
    return out;
}

struct App {
    HWND window{}, pathBox{}, browse{}, status{}, versions{}, log{};
    HWND updatePlay{}, updateOnly{}, playOnly{};
    HWND mods{}, toggle{}, uninstall{};
    HFONT font{}, bigFont{}, monoFont{};

    fs::path gameDir;
    std::vector<Receipt> installed;
    std::vector<bool> enabled;
    std::atomic<bool> busy{false};
    bool playAfterUpdate = false;
};

App g;

// ---------------------------------------------------------------- config

// Remembered next to the executable so a copied .exe carries its own settings.
fs::path ConfigPath() {
    wchar_t buffer[MAX_PATH]{};
    GetModuleFileNameW(nullptr, buffer, MAX_PATH);
    return fs::path(buffer).parent_path() / "sam-mod-gui.txt";
}

std::wstring LoadSavedPath() {
    std::wifstream in(ConfigPath());
    std::wstring line;
    if (in && std::getline(in, line)) return line;
    return {};
}

void SaveePath(const std::wstring& value) {
    std::wofstream out(ConfigPath(), std::ios::trunc);
    if (out) out << value << L"\n";
}

// ---------------------------------------------------------------- helpers

void Log(const std::wstring& line) {
    PostMessageW(g.window, WmLog, 0, (LPARAM) new std::wstring(line));
}
void Log(const std::string& line) { Log(widen(line)); }

std::wstring PathBoxText() {
    const int length = GetWindowTextLengthW(g.pathBox);
    std::wstring text((size_t)length, L'\0');
    GetWindowTextW(g.pathBox, text.data(), length + 1);
    return text;
}

bool IsValidGameDir(const std::wstring& dir) {
    if (dir.empty()) return false;
    std::error_code ignored;
    return fs::exists(fs::path(dir) / kGameExe, ignored);
}

bool IsGameRunning() {
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return false;

    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    bool found = false;
    if (Process32FirstW(snapshot, &entry)) {
        do {
            if (_wcsicmp(entry.szExeFile, kGameExe) == 0) { found = true; break; }
        } while (Process32NextW(snapshot, &entry));
    }
    CloseHandle(snapshot);
    return found;
}

fs::path DetectGameDir() {
    for (const wchar_t* root : {L"C:", L"D:", L"E:", L"F:"}) {
        for (const wchar_t* tail : {
                 L"Program Files (x86)/Steam/steamapps/common/Shift At Midnight",
                 L"Steam/steamapps/common/Shift At Midnight",
                 L"Games/Steam Games/steamapps/common/Shift At Midnight",
                 L"SteamLibrary/steamapps/common/Shift At Midnight"}) {
            fs::path candidate = fs::path(std::wstring(root) + L"\\") / tail;
            std::error_code ignored;
            if (fs::exists(candidate / kGameExe, ignored)) return candidate;
        }
    }
    return {};
}

// ---------------------------------------------------------------- state display

void RefreshValidity() {
    const std::wstring dir = PathBoxText();
    const bool ok = IsValidGameDir(dir);

    SetWindowTextW(g.status, ok ? L"Spielordner in Ordnung  ✓"
                                : L"ShiftAtMidnight.exe nicht in diesem Ordner  ✗");
    InvalidateRect(g.status, nullptr, TRUE);

    g.gameDir = ok ? fs::path(dir) : fs::path{};
    EnableWindow(g.playOnly, ok && !g.busy);
}

/// Fills the mod list and updates the toggle label to match the selection.
void RefreshMods() {
    SendMessageW(g.mods, LB_RESETCONTENT, 0, 0);
    g.installed.clear();
    g.enabled.clear();

    if (g.gameDir.empty()) return;

    try {
        Installer installer(g.gameDir);
        g.installed = installer.installed();
        for (const auto& mod : g.installed) {
            const bool on = installer.isEnabled(mod.slug);
            g.enabled.push_back(on);

            const std::wstring line = (on ? L"[an]   " : L"[aus] ") +
                                      widen(mod.name) + L"   " + widen(mod.version);
            SendMessageW(g.mods, LB_ADDSTRING, 0, (LPARAM)line.c_str());
        }
    } catch (...) { /* an unreadable install simply shows an empty list */ }

    if (g.installed.empty())
        SendMessageW(g.mods, LB_ADDSTRING, 0, (LPARAM)L"(keine Mods installiert)");
    else
        SendMessageW(g.mods, LB_SETCURSEL, 0, 0);
}

/// Index into g.installed, or -1 when the placeholder row is selected.
int SelectedMod() {
    if (g.installed.empty()) return -1;
    const LRESULT index = SendMessageW(g.mods, LB_GETCURSEL, 0, 0);
    if (index == LB_ERR || index < 0 || (size_t)index >= g.installed.size()) return -1;
    return (int)index;
}

void RefreshToggleLabel() {
    const int index = SelectedMod();
    const bool on = index >= 0 && g.enabled[(size_t)index];
    SetWindowTextW(g.toggle, on ? L"Deaktivieren" : L"Aktivieren");
    EnableWindow(g.toggle, index >= 0 && !g.busy);
    EnableWindow(g.uninstall, index >= 0 && !g.busy);
}

void ShowVersions(const std::wstring& latest) {
    std::wstring installed = L"(keiner)";
    try {
        if (!g.gameDir.empty()) {
            Installer installer(g.gameDir);
            const auto mods = installer.installed();
            if (!mods.empty()) {
                installed.clear();
                for (size_t i = 0; i < mods.size(); ++i) {
                    if (i) installed += L", ";
                    installed += widen(mods[i].name) + L" " + widen(mods[i].version);
                }
            }
        }
    } catch (...) { /* an unreadable install simply shows "(keiner)" */ }

    const std::wstring line = L"Installiert: " + installed +
                              L"     |     Neuestes Release: " + latest;
    PostMessageW(g.window, WmVersions, 0, (LPARAM) new std::wstring(line));
}

// ---------------------------------------------------------------- work

void RunAsync(std::function<void()> job) {
    if (g.busy.exchange(true)) return;
    for (HWND h : {g.updatePlay, g.updateOnly, g.playOnly, g.browse}) EnableWindow(h, FALSE);

    std::thread([job = std::move(job)] {
        try { job(); }
        catch (const std::exception& ex) { Log(std::string("Fehler: ") + ex.what()); }
        catch (...) { Log(std::wstring(L"Unbekannter Fehler.")); }
        PostMessageW(g.window, WmDone, 0, 0);
    }).detach();
}

void LaunchGame() {
    if (g.gameDir.empty()) return;
    const fs::path exe = g.gameDir / kGameExe;

    SHELLEXECUTEINFOW info{};
    info.cbSize = sizeof(info);
    info.lpVerb = L"open";
    info.lpFile = exe.c_str();
    info.lpDirectory = g.gameDir.c_str();
    info.nShow = SW_SHOWNORMAL;
    info.fMask = SEE_MASK_NOASYNC;

    if (ShellExecuteExW(&info)) Log(L"Spiel gestartet.");
    else Log(L"Spiel konnte nicht gestartet werden.");
}

void DoUpdate(bool thenPlay) {
    const fs::path dir = g.gameDir;
    g.playAfterUpdate = thenPlay;

    RunAsync([dir, thenPlay] {
        Installer installer(dir);

        Log(L"Suche nach Aktualisierungen …");
        GitHubSource source(kOwner, kRepo, "");
        const auto assets = source.latest();

        if (assets.empty()) {
            Log(L"Das neueste Release enthält keine Mod-Pakete.");
        } else {
            int changed = 0;
            for (const auto& asset : assets) {
                const auto current = installer.receiptFor(asset.modSlug);
                if (current && current->version == asset.version) {
                    Log(asset.modSlug + " " + current->version + " ist aktuell.");
                    continue;
                }

                Log(asset.modSlug + ": " +
                    (current ? current->version + " -> " : std::string("neu ")) +
                    asset.version + " wird geladen …");

                const auto bytes = source.download(asset);
                const fs::path staged = fs::temp_directory_path() / asset.fileName;
                {
                    std::ofstream out(staged, std::ios::binary | std::ios::trunc);
                    out.write(reinterpret_cast<const char*>(bytes.data()),
                              (std::streamsize)bytes.size());
                }

                try {
                    const Receipt receipt = installer.install(staged, true);
                    Log(receipt.name + " " + receipt.version + " installiert.");
                    ++changed;
                } catch (const std::exception& ex) {
                    Log(asset.modSlug + " fehlgeschlagen: " + ex.what());
                }
                std::error_code ignored;
                fs::remove(staged, ignored);
            }
            if (changed == 0) Log(L"Alles auf dem neuesten Stand.");
        }

        ShowVersions(assets.empty() ? L"—" : widen(assets.front().version));

        // Launching must happen on the UI thread, not from the worker.
        if (thenPlay) PostMessageW(g.window, WmPlay, 0, 0);
    });
}

// ---------------------------------------------------------------- window

HWND Make(HWND parent, const wchar_t* cls, const wchar_t* text, DWORD style,
          int x, int y, int w, int h, int id, HFONT font) {
    HWND control = CreateWindowExW(0, cls, text, WS_CHILD | WS_VISIBLE | style,
                                   x, y, w, h, parent, (HMENU)(INT_PTR)id,
                                   GetModuleHandleW(nullptr), nullptr);
    SendMessageW(control, WM_SETFONT, (WPARAM)font, TRUE);
    return control;
}

void BuildUi(HWND parent) {
    g.font = CreateFontW(-12, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET,
                         OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                         DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI");
    g.bigFont = CreateFontW(-16, 0, 0, 0, FW_BOLD, 0, 0, 0, DEFAULT_CHARSET,
                            OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                            DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI");
    g.monoFont = CreateFontW(-12, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET,
                             OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                             FIXED_PITCH | FF_MODERN, L"Consolas");

    Make(parent, L"STATIC", L"Spielordner:", 0, 12, 16, 84, 20, 0, g.font);

    g.pathBox = Make(parent, L"EDIT", L"", WS_BORDER | ES_AUTOHSCROLL,
                     98, 13, 468, 24, IdPath, g.font);
    g.browse = Make(parent, L"BUTTON", L"Durchsuchen …", BS_PUSHBUTTON,
                    572, 12, 116, 26, IdBrowse, g.font);

    g.status = Make(parent, L"STATIC", L"", 0, 98, 42, 590, 20, IdStatus, g.font);
    g.versions = Make(parent, L"STATIC", L"Installiert: —     |     Neuestes Release: —",
                      0, 12, 66, 676, 20, IdVersions, g.font);

    g.updatePlay = Make(parent, L"BUTTON", L"Mods aktualisieren && Spiel starten",
                        BS_PUSHBUTTON, 12, 92, 340, 48, IdUpdatePlay, g.bigFont);
    g.updateOnly = Make(parent, L"BUTTON", L"Nur aktualisieren", BS_PUSHBUTTON,
                        360, 92, 160, 48, IdUpdateOnly, g.font);
    g.playOnly = Make(parent, L"BUTTON", L"Spiel starten", BS_PUSHBUTTON,
                      528, 92, 160, 48, IdPlayOnly, g.font);

    Make(parent, L"STATIC", L"Installierte Mods", 0, 12, 152, 200, 18, 0, g.font);
    g.mods = Make(parent, L"LISTBOX", nullptr, WS_BORDER | WS_VSCROLL | LBS_NOTIFY,
                  12, 172, 500, 96, IdMods, g.font);

    g.toggle = Make(parent, L"BUTTON", L"Deaktivieren", BS_PUSHBUTTON,
                    522, 172, 166, 30, IdToggle, g.font);
    g.uninstall = Make(parent, L"BUTTON", L"Deinstallieren", BS_PUSHBUTTON,
                       522, 206, 166, 30, IdUninstall, g.font);

    g.log = Make(parent, L"EDIT", L"",
                 WS_BORDER | WS_VSCROLL | ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL,
                 12, 280, 676, 200, IdLog, g.monoFont);
}

void AppendLog(const std::wstring& line) {
    const int length = GetWindowTextLengthW(g.log);
    SendMessageW(g.log, EM_SETSEL, length, length);
    SendMessageW(g.log, EM_REPLACESEL, FALSE, (LPARAM)(line + L"\r\n").c_str());
}

LRESULT CALLBACK WndProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    switch (message) {
        case WM_CREATE:
            g.window = window;
            BuildUi(window);
            return 0;

        // Green when the folder is valid, red when it is not - the same at-a-glance cue
        // the 7DTD updater uses.
        case WM_CTLCOLORSTATIC:
            if ((HWND)lParam == g.status) {
                const bool ok = IsValidGameDir(PathBoxText());
                SetTextColor((HDC)wParam, ok ? RGB(0x2E, 0x7D, 0x32) : RGB(0xC6, 0x28, 0x28));
                SetBkMode((HDC)wParam, TRANSPARENT);
                return (LRESULT)GetSysColorBrush(COLOR_BTNFACE);
            }
            if ((HWND)lParam == g.versions) {
                SetTextColor((HDC)wParam, RGB(0x60, 0x60, 0x60));
                SetBkMode((HDC)wParam, TRANSPARENT);
                return (LRESULT)GetSysColorBrush(COLOR_BTNFACE);
            }
            return DefWindowProcW(window, message, wParam, lParam);

        case WmLog: {
            std::unique_ptr<std::wstring> line((std::wstring*)lParam);
            AppendLog(*line);
            return 0;
        }

        case WmVersions: {
            std::unique_ptr<std::wstring> line((std::wstring*)lParam);
            SetWindowTextW(g.versions, line->c_str());
            InvalidateRect(g.versions, nullptr, TRUE);
            return 0;
        }

        case WmPlay:
            LaunchGame();
            return 0;

        case WmDone:
            g.busy = false;
            for (HWND h : {g.updatePlay, g.updateOnly, g.browse}) EnableWindow(h, TRUE);
            RefreshValidity();
            RefreshMods();
            RefreshToggleLabel();
            return 0;

        case WM_COMMAND: {
            const int id = LOWORD(wParam);

            if (id == IdPath && HIWORD(wParam) == EN_CHANGE) { RefreshValidity(); return 0; }
            if (g.busy) return 0;

            switch (id) {
                case IdBrowse: {
                    BROWSEINFOW info{};
                    info.hwndOwner = window;
                    info.lpszTitle = L"Installationsordner von Shift At Midnight wählen";
                    info.ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE;

                    LPITEMIDLIST picked = SHBrowseForFolderW(&info);
                    if (!picked) return 0;

                    wchar_t buffer[MAX_PATH]{};
                    const bool ok = SHGetPathFromIDListW(picked, buffer);
                    CoTaskMemFree(picked);
                    if (ok) { SetWindowTextW(g.pathBox, buffer); SaveePath(buffer); }
                    return 0;
                }

                case IdUpdatePlay:
                case IdUpdateOnly: {
                    if (g.gameDir.empty()) {
                        MessageBoxW(window,
                                    L"Der Spielordner stimmt nicht - ShiftAtMidnight.exe wurde "
                                    L"dort nicht gefunden.\n\nWähle den Ordner über "
                                    L"„Durchsuchen“.",
                                    kTitle, MB_OK | MB_ICONWARNING);
                        return 0;
                    }

                    // Mod DLLs are locked while the game runs, so replacing them would fail
                    // halfway. Better to say so than to roll back a doomed install.
                    if (IsGameRunning()) {
                        const int answer = MessageBoxW(
                            window,
                            L"Shift At Midnight läuft gerade. Die Mod-Dateien sind dann "
                            L"gesperrt und können nicht ersetzt werden.\n\n"
                            L"Schließe das Spiel und klicke dann OK.",
                            kTitle, MB_OKCANCEL | MB_ICONWARNING);
                        if (answer != IDOK) { AppendLog(L"Abgebrochen (Spiel läuft)."); return 0; }
                        if (IsGameRunning()) {
                            AppendLog(L"Spiel läuft weiterhin - abgebrochen.");
                            return 0;
                        }
                    }

                    SaveePath(PathBoxText());
                    DoUpdate(id == IdUpdatePlay);
                    return 0;
                }

                case IdPlayOnly:
                    LaunchGame();
                    return 0;

                case IdMods:
                    if (HIWORD(wParam) == LBN_SELCHANGE) RefreshToggleLabel();
                    return 0;

                case IdToggle: {
                    const int index = SelectedMod();
                    if (index < 0) return 0;

                    const Receipt mod = g.installed[(size_t)index];
                    const bool turnOn = !g.enabled[(size_t)index];
                    const fs::path dir = g.gameDir;

                    RunAsync([dir, mod, turnOn] {
                        Installer installer(dir);
                        int changed = installer.setEnabled(mod.slug, turnOn);
                        Log(mod.name + (turnOn ? " aktiviert" : " deaktiviert") +
                            " (" + std::to_string(changed) + " Datei(en)).");
                        if (!turnOn)
                            Log(std::wstring(L"Einstellungen und Musik bleiben erhalten."));
                    });
                    return 0;
                }

                case IdUninstall: {
                    const int index = SelectedMod();
                    if (index < 0) return 0;

                    const Receipt mod = g.installed[(size_t)index];
                    const std::wstring question =
                        L"„" + widen(mod.name) + L"“ vollständig entfernen?\r\n\r\n"
                        L"Die Originaldateien des Spiels werden wiederhergestellt. "
                        L"Deine Musik und Einstellungen bleiben erhalten.\r\n\r\n"
                        L"Zum vorübergehenden Abschalten reicht „Deaktivieren“.";
                    if (MessageBoxW(window, question.c_str(), kTitle,
                                    MB_YESNO | MB_ICONQUESTION) != IDYES)
                        return 0;

                    const fs::path dir = g.gameDir;
                    RunAsync([dir, mod] {
                        Installer installer(dir);
                        installer.uninstall(mod.slug);
                        Log(mod.name + " entfernt.");
                    });
                    return 0;
                }
            }
            return 0;
        }

        case WM_CLOSE:
            if (g.busy) {
                if (MessageBoxW(window,
                                L"Es läuft noch eine Installation. Trotzdem beenden?",
                                kTitle, MB_YESNO | MB_ICONWARNING) != IDYES)
                    return 0;
            }
            DestroyWindow(window);
            return 0;

        case WM_DESTROY:
            for (HFONT f : {g.font, g.bigFont, g.monoFont}) if (f) DeleteObject(f);
            PostQuitMessage(0);
            return 0;
    }
    return DefWindowProcW(window, message, wParam, lParam);
}

}  // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int show) {
    INITCOMMONCONTROLSEX controls{sizeof(controls), ICC_STANDARD_CLASSES};
    InitCommonControlsEx(&controls);
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

    WNDCLASSEXW cls{};
    cls.cbSize = sizeof(cls);
    cls.lpfnWndProc = WndProc;
    cls.hInstance = instance;
    cls.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    cls.hbrBackground = (HBRUSH)(COLOR_BTNFACE + 1);
    cls.lpszClassName = kWindowClass;
    RegisterClassExW(&cls);

    // Centred on the monitor that currently holds the cursor, so it opens where the user
    // is looking rather than wherever Windows would have stacked it.
    constexpr int kWidth = 716, kHeight = 560;
    int left = CW_USEDEFAULT, top = CW_USEDEFAULT;
    {
        POINT cursor{};
        GetCursorPos(&cursor);
        HMONITOR monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTOPRIMARY);

        MONITORINFO info{};
        info.cbSize = sizeof(info);
        if (GetMonitorInfoW(monitor, &info)) {
            const RECT& area = info.rcWork;
            left = area.left + ((area.right - area.left) - kWidth) / 2;
            top = area.top + ((area.bottom - area.top) - kHeight) / 2;
        }
    }

    HWND window = CreateWindowExW(
        0, kWindowClass, kTitle,
        WS_OVERLAPPEDWINDOW & ~WS_THICKFRAME & ~WS_MAXIMIZEBOX,
        left, top, kWidth, kHeight,
        nullptr, nullptr, instance, nullptr);
    if (!window) return 1;

    ShowWindow(window, show);
    UpdateWindow(window);

    // A remembered folder wins over detection: the user chose it deliberately.
    std::wstring startPath = LoadSavedPath();
    if (!IsValidGameDir(startPath)) startPath = DetectGameDir().wstring();
    SetWindowTextW(g.pathBox, startPath.c_str());
    RefreshValidity();

    RefreshMods();
    RefreshToggleLabel();

    AppendLog(std::wstring(L"Repository: ") + widen(std::string(kOwner) + "/" + kRepo));
    if (g.gameDir.empty())
        AppendLog(L"Spiel nicht gefunden - bitte den Ordner wählen.");
    else
        AppendLog(L"Bereit.");
    ShowVersions(L"—");

    MSG message;
    while (GetMessageW(&message, nullptr, 0, 0) > 0) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }

    CoUninitialize();
    return 0;
}
