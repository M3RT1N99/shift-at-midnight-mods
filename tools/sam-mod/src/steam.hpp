// Finding the game the way Steam knows where it is.
//
// Guessing a handful of likely paths only works until someone keeps their library somewhere
// unlikely - and then the answer was a settings file remembering what the user typed, which
// is a lot of ceremony for something the machine already knows. Steam records its own
// location in the registry and lists every library in libraryfolders.vdf, so both are read
// instead. The hardcoded guesses stay as a last resort.
#pragma once

#include <windows.h>

#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace sam {

namespace fs = std::filesystem;

class SteamLocator {
public:
    /// <summary>Every Steam library on this machine, most authoritative source first.</summary>
    static std::vector<fs::path> libraries() {
        std::vector<fs::path> found;

        const fs::path root = steamRoot();
        if (!root.empty()) {
            found.push_back(root);
            appendFromLibraryFolders(root, found);
        }

        // Last resort for an unusual install where the registry says nothing.
        for (const wchar_t* guess : {L"C:/Program Files (x86)/Steam",
                                     L"C:/Steam", L"D:/Steam", L"D:/SteamLibrary",
                                     L"D:/Games/Steam Games", L"E:/SteamLibrary",
                                     L"F:/SteamLibrary", L"G:/SteamLibrary"}) {
            add(found, fs::path(guess));
        }
        return found;
    }

    /// <summary>Locates an installed game by folder name, or an empty path.</summary>
    static fs::path findGame(const std::wstring& folderName, const std::wstring& executable) {
        std::error_code ignored;
        for (const fs::path& library : libraries()) {
            const fs::path candidate = library / "steamapps" / "common" / folderName;
            if (fs::exists(candidate / executable, ignored)) return candidate;
        }
        return {};
    }

private:
    /// Steam writes its install path on setup; HKCU first, then the 32-bit HKLM view.
    static fs::path steamRoot() {
        for (const auto& [hive, subKey, value] :
             {std::tuple{HKEY_CURRENT_USER, L"Software\\Valve\\Steam", L"SteamPath"},
              std::tuple{HKEY_LOCAL_MACHINE, L"SOFTWARE\\WOW6432Node\\Valve\\Steam", L"InstallPath"},
              std::tuple{HKEY_LOCAL_MACHINE, L"SOFTWARE\\Valve\\Steam", L"InstallPath"}}) {
            wchar_t buffer[MAX_PATH]{};
            DWORD size = sizeof(buffer);

            if (RegGetValueW(hive, subKey, value, RRF_RT_REG_SZ, nullptr, buffer, &size) != ERROR_SUCCESS)
                continue;

            std::error_code ignored;
            fs::path path(buffer);
            if (!path.empty() && fs::is_directory(path, ignored)) return path;
        }
        return {};
    }

    /// <summary>
    /// Reads the library list. The file is Valve's own key-value format; only the "path"
    /// entries matter here, so it is scanned rather than fully parsed - a malformed or
    /// future-format file then costs nothing but the fallbacks.
    /// </summary>
    static void appendFromLibraryFolders(const fs::path& steamRoot, std::vector<fs::path>& out) {
        const fs::path listing = steamRoot / "steamapps" / "libraryfolders.vdf";

        std::ifstream in(listing);
        if (!in) return;

        std::string line;
        while (std::getline(in, line)) {
            const std::size_t key = line.find("\"path\"");
            if (key == std::string::npos) continue;

            const std::size_t open = line.find('"', key + 6);
            if (open == std::string::npos) continue;
            const std::size_t close = line.find('"', open + 1);
            if (close == std::string::npos) continue;

            std::string value = line.substr(open + 1, close - open - 1);

            // Paths are stored with escaped separators: C:\\Games -> C:\Games
            std::string unescaped;
            unescaped.reserve(value.size());
            for (std::size_t i = 0; i < value.size(); ++i) {
                if (value[i] == '\\' && i + 1 < value.size() && value[i + 1] == '\\') ++i;
                unescaped.push_back(value[i]);
            }

            if (!unescaped.empty()) add(out, fs::path(unescaped));
        }
    }

    static void add(std::vector<fs::path>& out, const fs::path& candidate) {
        std::error_code ignored;
        if (!fs::is_directory(candidate, ignored)) return;

        for (const fs::path& existing : out)
            if (fs::equivalent(existing, candidate, ignored)) return;

        out.push_back(candidate);
    }
};

}  // namespace sam
