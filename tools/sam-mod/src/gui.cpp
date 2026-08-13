// sam-mod-gui - a double-clickable front end for the installer.
//
// Plain Win32, no toolkit. The CLI already links only against Windows libraries, and a
// GUI that pulled in a framework would defeat the point of handing someone a single .exe.
//
// All the real work lives in the same headers the CLI uses; this file is only presentation
// and threading. Long operations run on a worker thread and report back through
// PostMessage, so the window never freezes and the worker never touches a control directly.

#ifndef UNICODE
#define UNICODE
#endif

#include <windows.h>
#include <commctrl.h>
#include <shlobj.h>

#include <atomic>
#include <filesystem>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "github.hpp"
#include "install.hpp"

#pragma comment(lib, "comctl32.lib")

namespace fs = std::filesystem;
using namespace sam;

namespace {

constexpr const wchar_t* kWindowClass = L"SamModGuiWindow";
constexpr const wchar_t* kTitle = L"Shift At Midnight - Mod Manager";
constexpr const char* kDefaultOwner = "M3RT1N99";
constexpr const char* kDefaultRepo = "shift-at-midnight-mods";

enum : int {
    IdList = 1001, IdUpdate, IdInstall, IdUninstall, IdVerify, IdBrowse, IdStatus, IdGameDir
};
enum : UINT {
    WmLog = WM_APP + 1,   // wParam unused, lParam = new std::wstring*
    WmDone = WM_APP + 2,  // worker finished; refresh and re-enable
};

std::wstring widen(const std::string& s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring out((size_t)n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), out.data(), n);
    return out;
}

struct App {
    HWND window = nullptr, list = nullptr, status = nullptr, gameLabel = nullptr;
    HWND update = nullptr, install = nullptr, uninstall = nullptr, verify = nullptr, browse = nullptr;
    HFONT font = nullptr;

    fs::path gameDir;
    std::unique_ptr<Installer> installer;
    std::vector<Receipt> mods;
    std::atomic<bool> busy{false};
};

App g;

// ---------------------------------------------------------------- reporting

void Log(const std::wstring& line) {
    PostMessageW(g.window, WmLog, 0, (LPARAM) new std::wstring(line));
}
void Log(const std::string& line) { Log(widen(line)); }

void SetButtonsEnabled(bool enabled) {
    const BOOL flag = enabled ? TRUE : FALSE;
    for (HWND h : {g.update, g.install, g.uninstall, g.verify, g.browse})
        if (h) EnableWindow(h, flag);
}

// ---------------------------------------------------------------- game directory

fs::path DetectGameDir() {
    for (const wchar_t* root : {L"C:", L"D:", L"E:", L"F:"}) {
        for (const wchar_t* tail : {
                 L"/Program Files (x86)/Steam/steamapps/common/Shift At Midnight",
                 L"/Steam/steamapps/common/Shift At Midnight",
                 L"/Games/Steam Games/steamapps/common/Shift At Midnight",
                 L"/SteamLibrary/steamapps/common/Shift At Midnight"}) {
            fs::path candidate = fs::path(root) / fs::path(tail).relative_path();
            if (fs::exists(candidate / "ShiftAtMidnight.exe")) return candidate;
        }
    }
    return {};
}

bool AdoptGameDir(const fs::path& dir) {
    try {
        g.installer = std::make_unique<Installer>(dir);
        g.gameDir = dir;
        SetWindowTextW(g.gameLabel, (L"Spiel:  " + dir.wstring()).c_str());
        return true;
    } catch (const std::exception& ex) {
        g.installer.reset();
        SetWindowTextW(g.gameLabel, L"Spiel:  nicht gefunden");
        Log(std::string("Spielordner nicht nutzbar: ") + ex.what());
        return false;
    }
}

fs::path PickFolder(HWND owner) {
    BROWSEINFOW info{};
    info.hwndOwner = owner;
    info.lpszTitle = L"Installationsordner von Shift At Midnight wählen";
    info.ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE;

    LPITEMIDLIST picked = SHBrowseForFolderW(&info);
    if (!picked) return {};

    wchar_t buffer[MAX_PATH] = {};
    const bool ok = SHGetPathFromIDListW(picked, buffer);
    CoTaskMemFree(picked);
    return ok ? fs::path(buffer) : fs::path{};
}

fs::path PickPackage(HWND owner) {
    wchar_t buffer[MAX_PATH] = {};
    OPENFILENAMEW dialog{};
    dialog.lStructSize = sizeof(dialog);
    dialog.hwndOwner = owner;
    dialog.lpstrFilter = L"Mod-Paket (*.modpkg)\0*.modpkg\0Alle Dateien\0*.*\0";
    dialog.lpstrFile = buffer;
    dialog.nMaxFile = MAX_PATH;
    dialog.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST;
    return GetOpenFileNameW(&dialog) ? fs::path(buffer) : fs::path{};
}

// ---------------------------------------------------------------- list

void RefreshList() {
    SendMessageW(g.list, LB_RESETCONTENT, 0, 0);
    g.mods.clear();
    if (!g.installer) return;

    g.mods = g.installer->installed();
    if (g.mods.empty()) {
        SendMessageW(g.list, LB_ADDSTRING, 0, (LPARAM)L"(keine Mods installiert)");
        return;
    }
    for (const auto& mod : g.mods) {
        const std::wstring line = widen(mod.name) + L"   " + widen(mod.version);
        SendMessageW(g.list, LB_ADDSTRING, 0, (LPARAM)line.c_str());
    }
}

// The placeholder row is not a mod, so selection only counts when mods exist.
const Receipt* SelectedMod() {
    if (g.mods.empty()) return nullptr;
    const LRESULT index = SendMessageW(g.list, LB_GETCURSEL, 0, 0);
    if (index == LB_ERR || index < 0 || (size_t)index >= g.mods.size()) return nullptr;
    return &g.mods[(size_t)index];
}

// ---------------------------------------------------------------- work

// Runs `job` off the UI thread. Buttons stay disabled until it reports back.
void RunAsync(std::function<void()> job) {
    if (g.busy.exchange(true)) return;
    SetButtonsEnabled(false);

    std::thread([job = std::move(job)] {
        try {
            job();
        } catch (const std::exception& ex) {
            Log(std::string("Fehler: ") + ex.what());
        } catch (...) {
            Log(std::wstring(L"Unbekannter Fehler."));
        }
        PostMessageW(g.window, WmDone, 0, 0);
    }).detach();
}

void DoUpdate() {
    RunAsync([] {
        Log(L"Suche nach Aktualisierungen …");
        GitHubSource source(kDefaultOwner, kDefaultRepo, "");

        const auto assets = source.latest();
        if (assets.empty()) {
            Log(L"Das neueste Release enthält keine Mod-Pakete.");
            return;
        }

        int changed = 0;
        for (const auto& asset : assets) {
            const auto current = g.installer->receiptFor(asset.modSlug);
            if (current && current->version == asset.version) {
                Log(asset.modSlug + " " + current->version + " ist aktuell.");
                continue;
            }

            Log(asset.modSlug + ": " + (current ? current->version + " -> " : std::string("neu "))
                + asset.version + " wird geladen …");

            const auto bytes = source.download(asset);
            const fs::path staged = fs::temp_directory_path() / asset.fileName;
            {
                std::ofstream out(staged, std::ios::binary | std::ios::trunc);
                out.write(reinterpret_cast<const char*>(bytes.data()),
                          (std::streamsize)bytes.size());
            }

            try {
                const Receipt receipt = g.installer->install(staged, true);
                Log(receipt.name + " " + receipt.version + " installiert.");
                ++changed;
            } catch (const std::exception& ex) {
                Log(asset.modSlug + " fehlgeschlagen: " + ex.what());
            }
            std::error_code ignored;
            fs::remove(staged, ignored);
        }
        if (changed == 0) Log(L"Alles auf dem neuesten Stand.");
    });
}

void DoInstallFile(const fs::path& package) {
    RunAsync([package] {
        Log(L"Installiere " + package.filename().wstring() + L" …");
        const Receipt receipt = g.installer->install(package, true);
        Log(receipt.name + " " + receipt.version + " installiert ("
            + std::to_string(receipt.files.size()) + " Dateien).");
    });
}

void DoUninstall(const std::string& slug, const std::string& name) {
    RunAsync([slug, name] {
        g.installer->uninstall(slug);
        Log(name + " entfernt. Eigene Musik und Einstellungen bleiben erhalten.");
    });
}

void DoVerify() {
    RunAsync([] {
        const auto mods = g.installer->installed();
        if (mods.empty()) { Log(L"Nichts zu prüfen."); return; }

        bool clean = true;
        for (const auto& mod : mods) {
            const auto result = g.installer->verify(mod.slug);
            if (result.clean()) { Log(mod.name + ": in Ordnung."); continue; }
            clean = false;
            for (const auto& f : result.missing)  Log("  fehlt:     " + f);
            for (const auto& f : result.modified) Log("  verändert: " + f);
        }
        if (!clean)
            Log(L"Veränderte Dateien deuten meist auf ein Spiel-Update hin - "
                L"einfach neu installieren.");
    });
}

// ---------------------------------------------------------------- window

void CreateControls(HWND parent) {
    auto make = [&](const wchar_t* cls, const wchar_t* text, DWORD style,
                    int x, int y, int w, int h, int id) {
        HWND control = CreateWindowExW(0, cls, text, WS_CHILD | WS_VISIBLE | style,
                                       x, y, w, h, parent, (HMENU)(INT_PTR)id,
                                       GetModuleHandleW(nullptr), nullptr);
        SendMessageW(control, WM_SETFONT, (WPARAM)g.font, TRUE);
        return control;
    };

    g.gameLabel = make(L"STATIC", L"Spiel:  wird gesucht …", 0, 12, 10, 560, 20, IdGameDir);
    g.browse    = make(L"BUTTON", L"Ordner wählen …", BS_PUSHBUTTON, 452, 34, 120, 26, IdBrowse);

    make(L"STATIC", L"Installierte Mods", 0, 12, 40, 200, 18, 0);
    g.list = make(L"LISTBOX", nullptr, WS_BORDER | WS_VSCROLL | LBS_NOTIFY,
                  12, 62, 430, 120, IdList);

    g.update    = make(L"BUTTON", L"Aktualisieren",  BS_PUSHBUTTON, 452, 62,  120, 30, IdUpdate);
    g.install   = make(L"BUTTON", L"Aus Datei …",    BS_PUSHBUTTON, 452, 96,  120, 30, IdInstall);
    g.uninstall = make(L"BUTTON", L"Entfernen",      BS_PUSHBUTTON, 452, 130, 120, 26, IdUninstall);
    g.verify    = make(L"BUTTON", L"Prüfen",         BS_PUSHBUTTON, 452, 160, 120, 26, IdVerify);

    g.status = make(L"EDIT", nullptr,
                    WS_BORDER | WS_VSCROLL | ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL,
                    12, 194, 560, 150, IdStatus);
}

void AppendStatus(const std::wstring& line) {
    const int length = GetWindowTextLengthW(g.status);
    SendMessageW(g.status, EM_SETSEL, length, length);
    const std::wstring text = line + L"\r\n";
    SendMessageW(g.status, EM_REPLACESEL, FALSE, (LPARAM)text.c_str());
}

LRESULT CALLBACK WndProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    switch (message) {
        case WM_CREATE:
            g.window = window;
            g.font = CreateFontW(-12, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET,
                                 OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                 DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI");
            CreateControls(window);
            return 0;

        case WmLog: {
            std::unique_ptr<std::wstring> line((std::wstring*)lParam);
            AppendStatus(*line);
            return 0;
        }

        case WmDone:
            g.busy = false;
            SetButtonsEnabled(true);
            RefreshList();
            return 0;

        case WM_COMMAND: {
            if (g.busy && LOWORD(wParam) != IdList) return 0;

            switch (LOWORD(wParam)) {
                case IdBrowse: {
                    const fs::path picked = PickFolder(window);
                    if (!picked.empty() && AdoptGameDir(picked)) RefreshList();
                    return 0;
                }
                case IdUpdate:
                    if (g.installer) DoUpdate();
                    else AppendStatus(L"Erst den Spielordner wählen.");
                    return 0;
                case IdInstall: {
                    if (!g.installer) { AppendStatus(L"Erst den Spielordner wählen."); return 0; }
                    const fs::path package = PickPackage(window);
                    if (!package.empty()) DoInstallFile(package);
                    return 0;
                }
                case IdUninstall: {
                    const Receipt* selected = SelectedMod();
                    if (!selected) { AppendStatus(L"Bitte zuerst einen Mod auswählen."); return 0; }

                    const std::wstring question =
                        L"„" + widen(selected->name) + L"“ entfernen?\r\n\r\n"
                        L"Die Originaldateien des Spiels werden wiederhergestellt. "
                        L"Deine Musik und Einstellungen bleiben erhalten.";
                    if (MessageBoxW(window, question.c_str(), kTitle,
                                    MB_YESNO | MB_ICONQUESTION) == IDYES)
                        DoUninstall(selected->slug, selected->name);
                    return 0;
                }
                case IdVerify:
                    if (g.installer) DoVerify();
                    return 0;
            }
            return 0;
        }

        case WM_CLOSE:
            // A half-finished install must not be abandoned mid-transaction.
            if (g.busy) {
                if (MessageBoxW(window, L"Es läuft noch eine Installation. Trotzdem beenden?",
                                kTitle, MB_YESNO | MB_ICONWARNING) != IDYES)
                    return 0;
            }
            DestroyWindow(window);
            return 0;

        case WM_DESTROY:
            if (g.font) DeleteObject(g.font);
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

    HWND window = CreateWindowExW(
        0, kWindowClass, kTitle,
        (WS_OVERLAPPEDWINDOW & ~WS_THICKFRAME & ~WS_MAXIMIZEBOX),
        CW_USEDEFAULT, CW_USEDEFAULT, 600, 400,
        nullptr, nullptr, instance, nullptr);
    if (!window) return 1;

    ShowWindow(window, show);
    UpdateWindow(window);

    const fs::path detected = DetectGameDir();
    if (detected.empty()) {
        AppendStatus(L"Spiel nicht gefunden. Bitte den Ordner von Hand wählen.");
    } else if (AdoptGameDir(detected)) {
        RefreshList();
        AppendStatus(L"Bereit.");
    }

    MSG message;
    while (GetMessageW(&message, nullptr, 0, 0) > 0) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }

    CoUninitialize();
    return 0;
}
