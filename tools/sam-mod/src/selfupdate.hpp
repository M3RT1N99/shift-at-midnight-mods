// Keeping the manager itself current.
//
// The whole point of this tool is that someone can be handed one .exe. That falls apart if
// the copy they were handed months ago cannot install today's mods, so it updates itself
// from the same release the mods come from.
//
// Windows will not let a running .exe be overwritten, but it will let one be RENAMED. So
// the running file is moved aside, the new build is written in its place, and the leftover
// is deleted on the next start. No helper process, no scheduled task.
#pragma once

#include <windows.h>

#include <filesystem>
#include <fstream>
#include <functional>
#include <string>

#include "github.hpp"
#include "sha256.hpp"

namespace sam {

namespace fs = std::filesystem;

class SelfUpdater {
public:
    /// Suffix for the displaced previous build.
    static constexpr const char* kOldSuffix = ".old";

    static fs::path ownPath() {
        wchar_t buffer[MAX_PATH]{};
        GetModuleFileNameW(nullptr, buffer, MAX_PATH);
        return fs::path(buffer);
    }

    /// <summary>
    /// Removes the previous build left behind by an update. Call once at start: the old
    /// file cannot be deleted while it is the running process, only afterwards.
    /// </summary>
    static void cleanUpPrevious() {
        std::error_code ignored;
        fs::remove(fs::path(ownPath().string() + kOldSuffix), ignored);
    }

    /// <summary>
    /// Replaces this executable if the release carries a different build.
    ///
    /// Compared by SHA-256 rather than a version string: the binary carries no version
    /// resource, and the hash answers the only question that matters - is the published
    /// build the one running. Returns true when an update was staged, meaning the caller
    /// should restart.
    /// </summary>
    static bool update(const std::string& owner, const std::string& repo,
                       const std::string& assetName,
                       const std::function<void(const std::string&)>& report) {
        try {
            const fs::path self = ownPath();

            const std::string url =
                "https://api.github.com/repos/" + owner + "/" + repo + "/releases/latest";
            auto listing = Http::get(std::wstring(url.begin(), url.end()), "");
            if (listing.status != 200) return false;

            const Json release = Json::parse(listing.text());

            std::string downloadUrl;
            for (const Json& asset : release["assets"].array()) {
                if (asset["name"].str() != assetName) continue;
                downloadUrl = asset["url"].str();
                break;
            }
            if (downloadUrl.empty()) return false;

            auto payload = Http::get(std::wstring(downloadUrl.begin(), downloadUrl.end()),
                                     "", "application/octet-stream");
            if (payload.status != 200 || payload.body.empty()) return false;

            // Identical build: nothing to do, and no reason to disturb a working install.
            if (Sha256::ofBytes(payload.body) == Sha256::ofFile(self)) return false;

            if (report) report("Neue Version des Mod-Managers gefunden.");

            const fs::path staged = self.string() + ".new";
            {
                std::ofstream out(staged, std::ios::binary | std::ios::trunc);
                out.write(reinterpret_cast<const char*>(payload.body.data()),
                          static_cast<std::streamsize>(payload.body.size()));
                if (!out) return false;
            }

            const fs::path displaced = self.string() + kOldSuffix;
            std::error_code ignored;
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

            if (report) report("Aktualisiert. Beim nächsten Start ist die neue Version aktiv.");
            return true;
        } catch (...) {
            // Being offline, rate limited or without write access is not an error worth
            // interrupting anyone over - the existing build keeps working.
            return false;
        }
    }
};

}  // namespace sam
