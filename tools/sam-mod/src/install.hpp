// Transactional install/uninstall of .modpkg archives into a game directory.
//
// The safety model, in order of importance:
//
// 1. NEVER WRITE THROUGH A REPARSE POINT. The user's working copy of this project mirrors
//    the Steam installation with symlinks, so a path that looks local can be the real game
//    file. Every destination is checked before it is touched.
// 2. Back up before overwriting. Anything displaced goes to a vault keyed by mod, so
//    uninstall restores the original bytes rather than merely deleting.
// 3. Journal every step. A crash mid-install leaves a replayable record, so the next run
//    rolls back instead of leaving a half-installed mod.
// 4. Record hashes. A file that no longer matches its receipt was changed by something
//    else - usually a game update - and is reported instead of silently clobbered.
#pragma once

#include <windows.h>

#include <algorithm>
#include <filesystem>
#include <fstream>
#include <map>
#include <optional>
#include <set>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

#include "json.hpp"
#include "sha256.hpp"
#include "zip.hpp"

namespace sam {

namespace fs = std::filesystem;

class InstallError : public std::runtime_error {
public:
    explicit InstallError(const std::string& what) : std::runtime_error(what) {}
};

// Files the packer must never ship and the installer must never write, even if a crafted
// package declares them. This is the last line of defence behind pack.ps1's content guard.
inline bool isForbiddenTarget(const std::string& fileName) {
    static const std::set<std::string> kNever = {
        "gameassembly.dll", "unityplayer.dll", "baselib.dll", "global-metadata.dat",
        "shiftatmidnight.exe", "unitycrashhandler64.exe", "steam_api64.dll"};
    std::string lower = fileName;
    std::transform(lower.begin(), lower.end(), lower.begin(), ::tolower);
    return kNever.count(lower) != 0;
}

inline bool isReparsePoint(const fs::path& p) {
    const DWORD attributes = GetFileAttributesW(p.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES &&
           (attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
}

// True if p, or any existing directory above it inside the game dir, is a link.
inline std::optional<fs::path> findReparsePoint(const fs::path& gameDir, const fs::path& target) {
    fs::path walk = target;
    while (walk != walk.root_path()) {
        if (fs::exists(walk) && isReparsePoint(walk)) return walk;
        if (walk == gameDir) break;
        const fs::path parent = walk.parent_path();
        if (parent == walk) break;
        walk = parent;
    }
    return std::nullopt;
}

// Rejects absolute paths, drive letters and traversal before any path is joined.
inline void assertSafeRelative(const std::string& rel) {
    if (rel.empty()) throw InstallError("empty path in package");
    if (rel.find(':') != std::string::npos || rel.front() == '/' || rel.front() == '\\')
        throw InstallError("absolute path in package: " + rel);

    std::stringstream parts(rel);
    std::string segment;
    while (std::getline(parts, segment, '/')) {
        if (segment == ".." || segment == ".")
            throw InstallError("path traversal in package: " + rel);
        if (!segment.empty() && (segment.back() == ' ' || segment.back() == '.'))
            throw InstallError("unsafe path segment in package: " + rel);
    }
}

struct InstalledFile {
    std::string relativePath;   // relative to the game directory
    std::string sha256;
    bool replacedExisting = false;
    // Seed files under UserData/ belong to the player once written - their music folder,
    // their edited config. Uninstalling the mod must not throw those away.
    bool keepOnUninstall = false;
};

struct Receipt {
    std::string slug, id, name, version;
    std::vector<InstalledFile> files;

    std::string toJson() const {
        std::ostringstream out;
        out << "{\n  \"slug\": \"" << slug << "\",\n"
            << "  \"id\": \"" << id << "\",\n"
            << "  \"name\": \"" << name << "\",\n"
            << "  \"version\": \"" << version << "\",\n"
            << "  \"files\": [\n";
        for (std::size_t i = 0; i < files.size(); ++i) {
            out << "    {\"path\": \"" << files[i].relativePath
                << "\", \"sha256\": \"" << files[i].sha256
                << "\", \"replaced\": " << (files[i].replacedExisting ? "true" : "false")
                << ", \"keep\": " << (files[i].keepOnUninstall ? "true" : "false")
                << "}" << (i + 1 < files.size() ? "," : "") << "\n";
        }
        out << "  ]\n}\n";
        return out.str();
    }

    static Receipt fromJson(const std::string& text) {
        const Json j = Json::parse(text);
        Receipt r;
        r.slug = j["slug"].str();
        r.id = j["id"].str();
        r.name = j["name"].str();
        r.version = j["version"].str();
        for (const Json& f : j["files"].array())
            r.files.push_back({f["path"].str(), f["sha256"].str(),
                               f["replaced"].flag(), f["keep"].flag()});
        return r;
    }
};

class Installer {
public:
    explicit Installer(fs::path gameDir) : gameDir_(std::move(gameDir)) {
        if (!fs::exists(gameDir_ / "ShiftAtMidnight.exe"))
            throw InstallError("no ShiftAtMidnight.exe in " + gameDir_.string());
        stateDir_ = gameDir_ / "Mods" / ".sam-mod";
    }

    const fs::path& gameDir() const { return gameDir_; }

    std::vector<Receipt> installed() const {
        std::vector<Receipt> out;
        const fs::path dir = stateDir_ / "installed";
        if (!fs::exists(dir)) return out;

        for (const auto& entry : fs::directory_iterator(dir)) {
            if (entry.path().extension() != ".json") continue;
            std::ifstream in(entry.path());
            std::stringstream buffer;
            buffer << in.rdbuf();
            try { out.push_back(Receipt::fromJson(buffer.str())); }
            catch (const std::exception&) { /* a damaged receipt must not hide the rest */ }
        }
        return out;
    }

    std::optional<Receipt> receiptFor(const std::string& slug) const {
        for (auto& r : installed())
            if (r.slug == slug) return r;
        return std::nullopt;
    }

    // Verifies the package end to end, then writes it. Any failure rolls everything back.
    Receipt install(const fs::path& packagePath, bool force) {
        ZipArchive zip(packagePath);
        verifyPackage(zip);

        const Json manifest = Json::parse(zip.readText("mod.json"));
        Receipt receipt;
        receipt.slug = manifest["slug"].str();
        receipt.id = manifest["id"].str();
        receipt.name = manifest["name"].str();
        receipt.version = manifest["version"].str();
        if (receipt.slug.empty()) throw InstallError("mod.json has no slug");

        if (auto previous = receiptFor(receipt.slug)) {
            if (previous->version == receipt.version && !force)
                throw InstallError(receipt.name + " " + receipt.version +
                                   " is already installed (use --force to reinstall)");
            uninstall(receipt.slug);
        }

        const auto plan = buildPlan(zip, manifest);

        const fs::path vault = stateDir_ / "vault" / receipt.slug;
        const fs::path journal = stateDir_ / "journal.tmp";
        fs::create_directories(vault);
        fs::create_directories(stateDir_ / "installed");

        std::vector<fs::path> written;
        std::vector<std::pair<fs::path, fs::path>> displaced;   // original -> vault copy
        std::ofstream log(journal, std::ios::trunc);

        try {
            for (const auto& [entryName, relativeTarget] : plan) {
                const fs::path target = gameDir_ / fs::path(relativeTarget);

                if (isForbiddenTarget(target.filename().string()))
                    throw InstallError("package tried to write a game file: " + relativeTarget);

                if (auto link = findReparsePoint(gameDir_, target))
                    throw InstallError(
                        "refusing to write through the link '" + link->string() +
                        "'. That path is a symlink into the real game installation - "
                        "point --game at the actual install instead.");

                fs::create_directories(target.parent_path());

                bool replaced = false;
                if (fs::exists(target)) {
                    const fs::path saved = vault / fs::path(relativeTarget);
                    fs::create_directories(saved.parent_path());
                    fs::copy_file(target, saved, fs::copy_options::overwrite_existing);
                    displaced.emplace_back(target, saved);
                    replaced = true;
                    log << "backup\t" << relativeTarget << "\n";
                }

                const auto bytes = zip.read(entryName);
                writeFile(target, bytes);
                written.push_back(target);
                log << "write\t" << relativeTarget << "\n";
                log.flush();

                receipt.files.push_back(
                    {relativeTarget, Sha256::ofBytes(bytes), replaced,
                     relativeTarget.rfind("UserData/", 0) == 0});
            }

            std::ofstream out(stateDir_ / "installed" / (receipt.slug + ".json"), std::ios::trunc);
            out << receipt.toJson();
            out.close();

            log.close();
            fs::remove(journal);
            return receipt;
        } catch (...) {
            log.close();
            rollback(written, displaced);
            fs::remove(journal);
            throw;
        }
    }

    void uninstall(const std::string& slug) {
        auto receipt = receiptFor(slug);
        if (!receipt) throw InstallError(slug + " is not installed");

        const fs::path vault = stateDir_ / "vault" / slug;

        // Reverse order so directories empty out from the leaves.
        for (auto it = receipt->files.rbegin(); it != receipt->files.rend(); ++it) {
            if (it->keepOnUninstall) continue;                  // the player's own data
            const fs::path target = gameDir_ / fs::path(it->relativePath);
            if (findReparsePoint(gameDir_, target)) continue;   // never follow a link out

            if (it->replacedExisting) {
                const fs::path saved = vault / fs::path(it->relativePath);
                if (fs::exists(saved)) {
                    fs::create_directories(target.parent_path());
                    fs::copy_file(saved, target, fs::copy_options::overwrite_existing);
                    continue;
                }
            }
            std::error_code ignored;
            fs::remove(target, ignored);

            // A disabled mod has its assemblies renamed, so the recorded name is gone and
            // removing only that would leave the parked file behind.
            fs::remove(fs::path(target.string() + kDisabledSuffix), ignored);

            pruneEmptyParents(target.parent_path());
        }

        std::error_code ignored;
        fs::remove_all(vault, ignored);
        fs::remove(stateDir_ / "installed" / (slug + ".json"), ignored);
    }

    /// Suffix appended to a plugin assembly so the loader stops seeing it.
    static constexpr const char* kDisabledSuffix = ".disabled";

    /// <summary>
    /// A mod is disabled by renaming its assemblies rather than deleting anything.
    /// MelonLoader only loads *.dll, so the suffix is enough to keep the mod installed,
    /// configured and indexed while it stays out of the game. Re-enabling is the same
    /// rename in reverse - no reinstall, no lost settings.
    /// </summary>
    bool isEnabled(const std::string& slug) const {
        const fs::path modDir = gameDir_ / "Mods" / slug;
        if (!fs::exists(modDir)) return true;   // nothing of ours to disable

        bool sawDisabled = false;
        for (const auto& entry : fs::recursive_directory_iterator(modDir)) {
            if (!entry.is_regular_file()) continue;
            if (entry.path().extension() == ".dll") return true;
            if (entry.path().string().size() > 9 &&
                entry.path().extension() == kDisabledSuffix)
                sawDisabled = true;
        }
        return !sawDisabled;
    }

    /// <summary>Enables or disables a mod. Returns how many files were renamed.</summary>
    int setEnabled(const std::string& slug, bool enabled) {
        if (!receiptFor(slug)) throw InstallError(slug + " is not installed");

        const fs::path modDir = gameDir_ / "Mods" / slug;
        if (!fs::exists(modDir)) throw InstallError("no files found for " + slug);

        if (findReparsePoint(gameDir_, modDir))
            throw InstallError("refusing to touch '" + modDir.string() +
                               "': that path is a link into the real game installation");

        std::vector<std::pair<fs::path, fs::path>> renames;
        for (const auto& entry : fs::recursive_directory_iterator(modDir)) {
            if (!entry.is_regular_file()) continue;
            const fs::path& from = entry.path();

            if (enabled) {
                if (from.extension() != kDisabledSuffix) continue;
                renames.emplace_back(from, fs::path(from).replace_extension());
            } else {
                if (from.extension() != ".dll") continue;
                renames.emplace_back(from, from.string() + kDisabledSuffix);
            }
        }

        // Applied as a unit: a half-renamed mod would load some assemblies and not
        // others, which is worse than either state.
        std::vector<std::pair<fs::path, fs::path>> done;
        try {
            for (const auto& [from, to] : renames) {
                fs::rename(from, to);
                done.emplace_back(from, to);
            }
        } catch (const std::exception& ex) {
            std::error_code ignored;
            for (auto it = done.rbegin(); it != done.rend(); ++it)
                fs::rename(it->second, it->first, ignored);
            throw InstallError(std::string("could not change state: ") + ex.what());
        }

        return static_cast<int>(renames.size());
    }

    struct VerifyResult {
        std::vector<std::string> missing, modified;
        bool clean() const { return missing.empty() && modified.empty(); }
    };

    VerifyResult verify(const std::string& slug) const {
        auto receipt = receiptFor(slug);
        if (!receipt) throw InstallError(slug + " is not installed");

        VerifyResult result;
        for (const auto& file : receipt->files) {
            fs::path target = gameDir_ / fs::path(file.relativePath);

            // A disabled mod has its assemblies renamed, so the recorded path is gone by
            // design. Check the renamed file instead of reporting it missing.
            if (!fs::exists(target)) {
                const fs::path parked = target.string() + kDisabledSuffix;
                if (fs::exists(parked)) target = parked;
                else { result.missing.push_back(file.relativePath); continue; }
            }

            if (Sha256::ofFile(target) != file.sha256)
                result.modified.push_back(file.relativePath);
        }
        return result;
    }

private:
    fs::path gameDir_, stateDir_;

    // Confirms the archive matches its own SHA256SUMS before anything is unpacked.
    void verifyPackage(const ZipArchive& zip) const {
        if (!zip.has("mod.json")) throw InstallError("package has no mod.json");
        if (!zip.has("SHA256SUMS")) throw InstallError("package has no SHA256SUMS");

        std::map<std::string, std::string> expected;
        std::istringstream sums(zip.readText("SHA256SUMS"));
        std::string line;
        while (std::getline(sums, line)) {
            if (!line.empty() && line.back() == '\r') line.pop_back();
            const std::size_t gap = line.find("  ");
            if (gap == std::string::npos) continue;
            expected[line.substr(gap + 2)] = line.substr(0, gap);
        }
        if (expected.empty()) throw InstallError("SHA256SUMS is empty");

        for (const auto& entry : zip.entries()) {
            if (entry.name == "SHA256SUMS") continue;
            auto it = expected.find(entry.name);
            if (it == expected.end())
                throw InstallError("package entry is not covered by SHA256SUMS: " + entry.name);
            if (Sha256::ofBytes(zip.read(entry)) != it->second)
                throw InstallError("checksum mismatch for " + entry.name);
        }
    }

    // Maps "payload/<root>/<rest>" onto "<root>/<rest>" in the game directory. The two
    // roots the manifest may target are Mods/ and UserData/, both created by the loader.
    std::vector<std::pair<std::string, std::string>> buildPlan(
        const ZipArchive& zip, const Json& manifest) const {
        std::vector<std::pair<std::string, std::string>> plan;
        const std::string slug = manifest["slug"].str();

        for (const auto& entry : zip.entries()) {
            if (entry.name.rfind("payload/", 0) != 0) continue;

            const std::string rest = entry.name.substr(8);
            assertSafeRelative(rest);

            const std::size_t slash = rest.find('/');
            if (slash == std::string::npos) continue;
            const std::string root = rest.substr(0, slash);
            if (root != "Mods" && root != "UserData")
                throw InstallError("package writes outside Mods/ and UserData/: " + rest);

            // Seed files must not overwrite a config the player has already edited.
            const fs::path target = gameDir_ / fs::path(rest);
            if (root == "UserData" && fs::exists(target)) continue;

            plan.emplace_back(entry.name, rest);
        }
        if (plan.empty()) throw InstallError("package contains no payload files");
        return plan;
    }

    static void writeFile(const fs::path& target, const std::vector<std::uint8_t>& bytes) {
        std::ofstream out(target, std::ios::binary | std::ios::trunc);
        if (!out) throw InstallError("cannot write " + target.string());
        if (!bytes.empty())
            out.write(reinterpret_cast<const char*>(bytes.data()),
                      static_cast<std::streamsize>(bytes.size()));
        if (!out) throw InstallError("write failed for " + target.string());
    }

    void rollback(const std::vector<fs::path>& written,
                  const std::vector<std::pair<fs::path, fs::path>>& displaced) const {
        std::error_code ignored;
        for (auto it = written.rbegin(); it != written.rend(); ++it)
            fs::remove(*it, ignored);
        for (const auto& [original, saved] : displaced)
            if (fs::exists(saved))
                fs::copy_file(saved, original, fs::copy_options::overwrite_existing, ignored);
    }

    void pruneEmptyParents(fs::path dir) const {
        std::error_code ignored;
        while (dir != gameDir_ && fs::exists(dir) && fs::is_empty(dir, ignored)) {
            if (!fs::remove(dir, ignored)) break;
            dir = dir.parent_path();
        }
    }
};

}  // namespace sam
