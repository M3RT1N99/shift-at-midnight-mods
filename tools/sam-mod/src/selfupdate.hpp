// Keeping the manager itself current.
//
// The whole point of this tool is that someone can be handed one .exe. That falls apart if
// the copy they were handed months ago cannot install today's mods, so it updates itself
// from the same release the mods come from.
//
// It compares VERSIONS, not file contents. Comparing hashes seemed simpler, but a hash
// cannot tell newer from older: a locally built manager differs from the published one and
// was therefore "updated" straight back to the older release. Only a strictly newer release
// is installed now.
//
// Windows will not let a running .exe be overwritten, but it will let one be RENAMED. The
// running file is moved aside as a hidden file, the new build takes its place, and the
// leftover is deleted at the next start - so nothing the user can see is left behind.
#pragma once

#include <windows.h>

#include <filesystem>
#include <fstream>
#include <functional>
#include <string>

#include "github.hpp"
#include "version.hpp"

namespace sam {

namespace fs = std::filesystem;

class SelfUpdater {
public:
    /// Leading dot plus the hidden attribute: invisible in Explorer and in a plain listing.
    static constexpr const wchar_t* kDisplacedPrefix = L".";
    static constexpr const wchar_t* kDisplacedSuffix = L".previous";

    static fs::path ownPath() {
        wchar_t buffer[MAX_PATH]{};
        GetModuleFileNameW(nullptr, buffer, MAX_PATH);
        return fs::path(buffer);
    }

    static fs::path displacedPath() {
        const fs::path self = ownPath();
        return self.parent_path() /
               (kDisplacedPrefix + self.filename().wstring() + kDisplacedSuffix);
    }

    /// <summary>
    /// Deletes the build displaced by a previous update. Call once at start: it cannot be
    /// removed while it is the running image, only afterwards.
    /// </summary>
    static void cleanUpPrevious() {
        const fs::path stale = displacedPath();
        std::error_code ignored;
        if (!fs::exists(stale, ignored)) return;

        SetFileAttributesW(stale.c_str(), FILE_ATTRIBUTE_NORMAL);
        if (fs::remove(stale, ignored)) return;

        // Still locked somehow - have Windows remove it on the next boot rather than
        // leaving it lying around for good.
        MoveFileExW(stale.c_str(), nullptr, MOVEFILE_DELAY_UNTIL_REBOOT);
    }

    /// <summary>
    /// Installs the released build when its tag is strictly newer than this one. Returns
    /// true when an update was staged, meaning the caller should ask for a restart.
    /// </summary>
    static bool update(const std::string& owner, const std::string& repo,
                       const std::string& assetName,
                       const std::function<void(const std::string&)>& report) {
        try {
            const std::string api =
                "https://api.github.com/repos/" + owner + "/" + repo + "/releases/latest";

            auto listing = Http::get(std::wstring(api.begin(), api.end()), "");
            if (listing.status != 200) return false;

            const Json release = Json::parse(listing.text());
            const std::string tag = release["tag_name"].str();
            if (tag.empty()) return false;

            // The decisive check. Equal or older stays put, so a locally built manager is
            // never replaced by an older published one.
            if (compareVersions(tag, kManagerVersion) <= 0) return false;

            std::string downloadUrl;
            for (const Json& asset : release["assets"].array()) {
                if (asset["name"].str() != assetName) continue;
                downloadUrl = asset["url"].str();
                break;
            }
            if (downloadUrl.empty()) return false;

            if (report)
                report("Neuere Version " + tag + " gefunden (installiert: " +
                       kManagerVersion + ").");

            auto payload = Http::get(std::wstring(downloadUrl.begin(), downloadUrl.end()),
                                     "", "application/octet-stream");
            if (payload.status != 200 || payload.body.empty()) return false;

            const fs::path self = ownPath();
            const fs::path staged = self.wstring() + L".new";
            {
                std::ofstream out(staged, std::ios::binary | std::ios::trunc);
                out.write(reinterpret_cast<const char*>(payload.body.data()),
                          static_cast<std::streamsize>(payload.body.size()));
                if (!out) return false;
            }

            const fs::path displaced = displacedPath();
            std::error_code ignored;
            SetFileAttributesW(displaced.c_str(), FILE_ATTRIBUTE_NORMAL);
            fs::remove(displaced, ignored);

            // Renaming the running image is allowed; overwriting it is not.
            std::error_code renameError;
            fs::rename(self, displaced, renameError);
            if (renameError) {
                fs::remove(staged, ignored);
                return false;
            }

            fs::rename(staged, self, renameError);
            if (renameError) {
                fs::rename(displaced, self, ignored);   // put the working build back
                fs::remove(staged, ignored);
                return false;
            }

            // Hidden so the folder still contains just the one executable to the eye.
            SetFileAttributesW(displaced.c_str(), FILE_ATTRIBUTE_HIDDEN);

            if (report) report("Aktualisiert auf " + tag + ".");
            return true;
        } catch (...) {
            // Offline, rate limited or read-only: the existing build keeps working, and
            // that is not worth interrupting anyone over.
            return false;
        }
    }
};

}  // namespace sam
