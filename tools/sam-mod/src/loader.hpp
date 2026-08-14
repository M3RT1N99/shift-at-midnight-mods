// Installing MelonLoader itself.
//
// Every mod here needs it, and telling someone to go fetch a loader before their mods work
// is the same failed hand-off as telling them to install yt-dlp. The manager fetches it
// from LavaGang's own release page and unpacks it into the game directory - the same set of
// files their installer writes.
#pragma once

#include <filesystem>
#include <fstream>
#include <functional>
#include <string>
#include <vector>

#include "github.hpp"
#include "install.hpp"
#include "zip.hpp"

namespace sam {

namespace fs = std::filesystem;

class LoaderInstaller {
public:
    // The x64 archive; this game is x64 and the manager refuses anything else anyway.
    static constexpr const char* kDownloadUrl =
        "https://github.com/LavaGang/MelonLoader/releases/latest/download/MelonLoader.x64.zip";

    /// <summary>
    /// Installed means present, whether or not it is currently allowed to load. The proxy
    /// DLL is parked under a suffix when disabled, and treating that as "not installed"
    /// would grey out the very button that turns it back on. The MelonLoader directory has
    /// to be there too - a stray proxy DLL alone is not an install.
    /// </summary>
    static bool isInstalled(const fs::path& gameDir) {
        std::error_code ignored;
        if (!fs::is_directory(gameDir / "MelonLoader", ignored)) return false;
        return fs::exists(gameDir / "version.dll", ignored) ||
               fs::exists(gameDir / "version.dll.disabled", ignored);
    }

    /// <summary>
    /// The master switch. Parking the proxy DLL stops the game loading MelonLoader at all,
    /// so every mod goes quiet at once without touching a single mod file - useful for
    /// checking whether a problem is the game's or ours, and for playing vanilla for an
    /// evening. Nothing is deleted; the rename is its own undo.
    /// </summary>
    static bool setEnabled(const fs::path& gameDir, bool enable) {
        const fs::path active = gameDir / "version.dll";
        const fs::path parked = gameDir / "version.dll.disabled";

        std::error_code ignored;
        if (findReparsePoint(gameDir, active))
            throw InstallError("refusing to touch version.dll: it is a link into a real install");

        if (enable) {
            if (fs::exists(active, ignored)) return false;     // already on
            if (!fs::exists(parked, ignored))
                throw InstallError("MelonLoader is not installed - nothing to enable");
            fs::rename(parked, active);
            return true;
        }

        if (!fs::exists(active, ignored)) return false;        // already off
        fs::remove(parked, ignored);                           // stale leftover
        fs::rename(active, parked);
        return true;
    }

    /// True when the loader is installed and actually allowed to load.
    static bool isEnabled(const fs::path& gameDir) {
        std::error_code ignored;
        return fs::exists(gameDir / "version.dll", ignored);
    }

    /// True once the loader has run and generated the interop assemblies mods build against.
    static bool hasGeneratedAssemblies(const fs::path& gameDir) {
        std::error_code ignored;
        const fs::path generated = gameDir / "MelonLoader" / "Il2CppAssemblies";
        if (!fs::is_directory(generated, ignored)) return false;

        for (const auto& entry : fs::directory_iterator(generated, ignored))
            if (entry.path().extension() == ".dll") return true;
        return false;
    }

    /// <summary>
    /// Downloads and unpacks MelonLoader. Returns the number of files written.
    ///
    /// The archive is laid out relative to the game directory already, so entries are
    /// written as they come. Every destination is still checked: an archive must not be
    /// able to write outside the game folder or through a symlink into a real install.
    /// </summary>
    static int install(const fs::path& gameDir,
                       const std::function<void(const std::string&)>& report) {
        if (report) report("Lade MelonLoader herunter (etwa 10 MB) ...");

        auto response = Http::get(toWide(kDownloadUrl), "", "application/octet-stream");
        if (response.status != 200)
            throw InstallError("MelonLoader download failed with HTTP " +
                               std::to_string(response.status));
        if (response.body.empty())
            throw InstallError("the MelonLoader download was empty");

        // Staged to disk because the zip reader works on a file, and so a failed download
        // cannot be mistaken for a valid archive.
        const fs::path staged =
            fs::temp_directory_path() / "sam-mod-melonloader.zip";
        {
            std::ofstream out(staged, std::ios::binary | std::ios::trunc);
            out.write(reinterpret_cast<const char*>(response.body.data()),
                      static_cast<std::streamsize>(response.body.size()));
            if (!out) throw InstallError("could not stage the MelonLoader download");
        }

        int written = 0;
        try {
            if (report) report("Entpacke MelonLoader ...");
            ZipArchive zip(staged);

            for (const auto& entry : zip.entries()) {
                assertSafeRelative(entry.name);

                const fs::path target = gameDir / fs::path(entry.name);

                if (isForbiddenTarget(target.filename().string()))
                    throw InstallError("the archive tried to write a game file: " + entry.name);

                if (auto link = findReparsePoint(gameDir, target))
                    throw InstallError("refusing to write through the link '" +
                                       link->string() + "'");

                fs::create_directories(target.parent_path());

                const auto bytes = zip.read(entry);
                std::ofstream out(target, std::ios::binary | std::ios::trunc);
                if (!out) throw InstallError("cannot write " + target.string());
                if (!bytes.empty())
                    out.write(reinterpret_cast<const char*>(bytes.data()),
                              static_cast<std::streamsize>(bytes.size()));
                if (!out) throw InstallError("write failed for " + target.string());

                ++written;
            }
        } catch (...) {
            std::error_code ignored;
            fs::remove(staged, ignored);
            throw;
        }

        std::error_code ignored;
        fs::remove(staged, ignored);

        if (!isInstalled(gameDir))
            throw InstallError("MelonLoader was unpacked but version.dll or MelonLoader/ is missing");

        if (report)
            report("MelonLoader installiert (" + std::to_string(written) + " Dateien). "
                   "Starte das Spiel einmal, damit es sich einrichtet.");
        return written;
    }

private:
    static std::wstring toWide(const std::string& s) {
        return std::wstring(s.begin(), s.end());   // the URL is ASCII
    }
};

}  // namespace sam
