// sam-mod - installer and updater for Shift At Midnight mods.
//
// Dependency-free by design: SHA-256 comes from Windows CNG, HTTPS from WinHTTP, and the
// zip/deflate reader is in-tree. The result is one .exe that can be handed to someone who
// has nothing installed.

#include <windows.h>

#include <filesystem>
#include <iostream>
#include <string>
#include <vector>

#include "github.hpp"
#include "install.hpp"
#include "loader.hpp"
#include "json.hpp"
#include "zip.hpp"

namespace fs = std::filesystem;
using namespace sam;

namespace {

constexpr const char* kVersion = "1.0.0";
constexpr const char* kDefaultOwner = "M3RT1N99";
constexpr const char* kDefaultRepo = "shift-at-midnight-mods";

struct Options {
    fs::path gameDir;
    std::string owner = kDefaultOwner;
    std::string repo = kDefaultRepo;
    std::string token;
    bool force = false;
    bool yes = false;
};

void printUsage() {
    std::cout <<
        "sam-mod " << kVersion << " - mod installer for Shift At Midnight\n\n"
        "USAGE\n"
        "  sam-mod <command> [options]\n\n"
        "COMMANDS\n"
        "  list                    show installed mods\n"
        "  install <file.modpkg>   install from a local package\n"
        "  update                  fetch and install the newest release from GitHub\n"
        "  disable <slug>          keep a mod installed but stop the game loading it\n"
        "  enable <slug>           load it again\n"
        "  uninstall <slug>        remove a mod and restore the originals\n"
        "  verify [slug]           check installed files against their recorded hashes\n"
        "  install-loader          install MelonLoader, which every mod here needs\n"
        "  mods-off                turn every mod off at once (parks the loader)\n"
        "  mods-on                 turn them back on\n"
        "  where                   show the detected game directory\n\n"
        "OPTIONS\n"
        "  --game <dir>    game directory (default: auto-detected from Steam)\n"
        "  --repo <o/r>    GitHub repository (default: " << kDefaultOwner << "/" << kDefaultRepo << ")\n"
        "  --token <tok>   GitHub token; also read from SAM_MOD_TOKEN.\n"
        "                  Required for a private repository.\n"
        "  --force         reinstall even when the version already matches\n"
        "  --yes           do not ask for confirmation\n";
}

// Steam's default locations, then the drives most likely to hold a second library.
fs::path detectGameDir() {
    std::vector<fs::path> candidates;
    for (const char* root : {"C:", "D:", "E:", "F:"}) {
        candidates.emplace_back(std::string(root) +
                                "/Program Files (x86)/Steam/steamapps/common/Shift At Midnight");
        candidates.emplace_back(std::string(root) + "/Steam/steamapps/common/Shift At Midnight");
        candidates.emplace_back(std::string(root) +
                                "/Games/Steam Games/steamapps/common/Shift At Midnight");
        candidates.emplace_back(std::string(root) +
                                "/SteamLibrary/steamapps/common/Shift At Midnight");
    }
    for (const auto& c : candidates)
        if (fs::exists(c / "ShiftAtMidnight.exe")) return c;
    return {};
}

bool confirm(const std::string& question, bool assumeYes) {
    if (assumeYes) return true;
    std::cout << question << " [y/N] " << std::flush;
    std::string answer;
    std::getline(std::cin, answer);
    return answer == "y" || answer == "Y" || answer == "j" || answer == "J";
}

// Numeric-aware compare so 1.10.0 sorts above 1.9.0.
int compareVersions(const std::string& a, const std::string& b) {
    auto split = [](const std::string& v) {
        std::vector<int> parts;
        std::string current;
        for (char c : v + ".") {
            if (c == '.') { parts.push_back(current.empty() ? 0 : std::atoi(current.c_str()));
                            current.clear(); }
            else if (std::isdigit(static_cast<unsigned char>(c))) current.push_back(c);
        }
        return parts;
    };
    const auto left = split(a), right = split(b);
    for (std::size_t i = 0; i < (std::max)(left.size(), right.size()); ++i) {
        const int l = i < left.size() ? left[i] : 0;
        const int r = i < right.size() ? right[i] : 0;
        if (l != r) return l < r ? -1 : 1;
    }
    return 0;
}

int cmdList(Installer& installer) {
    const auto mods = installer.installed();
    if (mods.empty()) {
        std::cout << "No mods installed in " << installer.gameDir().string() << "\n";
        return 0;
    }
    std::cout << "Installed in " << installer.gameDir().string() << ":\n\n";
    for (const auto& mod : mods) {
        const bool enabled = installer.isEnabled(mod.slug);
        std::cout << "  " << (enabled ? "[on ] " : "[off] ")
                  << mod.name << "  " << mod.version
                  << "  (" << mod.slug << ", " << mod.files.size() << " files)\n";
    }
    return 0;
}

int cmdSetEnabled(Installer& installer, const std::string& slug, bool enable) {
    const int changed = installer.setEnabled(slug, enable);
    if (changed == 0) {
        std::cout << slug << " is already " << (enable ? "enabled" : "disabled") << "\n";
        return 0;
    }
    std::cout << (enable ? "Enabled " : "Disabled ") << slug
              << " (" << changed << " file" << (changed == 1 ? "" : "s") << ")\n";
    if (!enable)
        std::cout << "Its settings and music are untouched; enable it again at any time.\n";
    return 0;
}

int cmdInstall(Installer& installer, const fs::path& package, const Options& options) {
    if (!fs::exists(package)) {
        std::cerr << "error: no such file: " << package.string() << "\n";
        return 1;
    }
    const Receipt receipt = installer.install(package, options.force);
    std::cout << "Installed " << receipt.name << " " << receipt.version
              << " (" << receipt.files.size() << " files)\n";
    return 0;
}

void reportToStdout(const std::string& line) {
    std::cout << "  " << line << "\n";
}

int cmdInstallLoader(const fs::path& gameDir) {
    if (LoaderInstaller::isInstalled(gameDir)) {
        std::cout << "MelonLoader is already installed.\n";
        if (!LoaderInstaller::hasGeneratedAssemblies(gameDir))
            std::cout << "It has not run yet - start the game once so it sets itself up.\n";
        return 0;
    }

    LoaderInstaller::install(gameDir, reportToStdout);
    return 0;
}

/// Turns every mod off at once by parking the loader's proxy DLL.
int cmdSetLoaderEnabled(const fs::path& gameDir, bool enable) {
    if (!LoaderInstaller::isInstalled(gameDir) && enable == false) {
        std::cout << "MelonLoader is not installed, so nothing is loading anyway.\n";
        return 0;
    }

    if (!LoaderInstaller::setEnabled(gameDir, enable)) {
        std::cout << "MelonLoader is already " << (enable ? "enabled" : "disabled") << ".\n";
        return 0;
    }

    std::cout << (enable ? "MelonLoader enabled - mods load again.\n"
                         : "MelonLoader disabled - the game starts unmodded.\n"
                           "Nothing was removed; enable it again at any time.\n");
    return 0;
}

int cmdUpdate(Installer& installer, const Options& options) {
    // Mods are inert without the loader, so it is installed first rather than leaving the
    // player with files that quietly do nothing.
    if (!LoaderInstaller::isInstalled(installer.gameDir())) {
        std::cout << "MelonLoader is missing; installing it first.\n";
        LoaderInstaller::install(installer.gameDir(), reportToStdout);
    }

    GitHubSource source(options.owner, options.repo, options.token);
    std::cout << "Checking " << source.slug() << " ...\n";

    const auto assets = source.latest();
    if (assets.empty()) {
        std::cout << "The latest release has no .modpkg assets.\n";
        return 0;
    }

    int installedCount = 0;
    for (const auto& asset : assets) {
        const auto current = installer.receiptFor(asset.modSlug);
        if (current && compareVersions(current->version, asset.version) >= 0 && !options.force) {
            std::cout << "  " << asset.modSlug << " " << current->version << " is up to date\n";
            continue;
        }

        std::cout << "  " << asset.modSlug << ": "
                  << (current ? current->version + " -> " : std::string("installing "))
                  << asset.version << "\n";
        if (!confirm("    download and install?", options.yes)) continue;

        const auto bytes = source.download(asset);

        // Staged next to the game so the install reads from a real local file; the archive
        // is verified against its own SHA256SUMS during install regardless.
        const fs::path staged = fs::temp_directory_path() / asset.fileName;
        {
            std::ofstream out(staged, std::ios::binary | std::ios::trunc);
            out.write(reinterpret_cast<const char*>(bytes.data()),
                      static_cast<std::streamsize>(bytes.size()));
        }

        try {
            const Receipt receipt = installer.install(staged, true);
            std::cout << "    installed " << receipt.name << " " << receipt.version << "\n";
            ++installedCount;
        } catch (const std::exception& ex) {
            std::cerr << "    failed: " << ex.what() << "\n";
        }
        std::error_code ignored;
        fs::remove(staged, ignored);
    }

    if (installedCount == 0) std::cout << "Nothing to do.\n";
    return 0;
}

int cmdUninstall(Installer& installer, const std::string& slug, const Options& options) {
    if (!confirm("Remove " + slug + " and restore the original files?", options.yes)) return 0;
    installer.uninstall(slug);
    std::cout << "Removed " << slug << "\n";
    return 0;
}

int cmdVerify(Installer& installer, const std::string& slug) {
    std::vector<Receipt> mods;
    if (slug.empty()) mods = installer.installed();
    else if (auto one = installer.receiptFor(slug)) mods.push_back(*one);
    else { std::cerr << "error: " << slug << " is not installed\n"; return 1; }

    bool allClean = true;
    for (const auto& mod : mods) {
        const auto result = installer.verify(mod.slug);
        if (result.clean()) {
            std::cout << "  " << mod.slug << ": ok\n";
            continue;
        }
        allClean = false;
        std::cout << "  " << mod.slug << ":\n";
        for (const auto& f : result.missing)  std::cout << "      missing   " << f << "\n";
        for (const auto& f : result.modified) std::cout << "      changed   " << f << "\n";
    }
    if (!allClean)
        std::cout << "\nChanged or missing files usually mean the game was updated.\n"
                     "Re-run 'sam-mod update' or reinstall the affected mod.\n";
    return allClean ? 0 : 2;
}

}  // namespace

int main(int argc, char** argv) {
    std::vector<std::string> args(argv + 1, argv + argc);
    if (args.empty() || args[0] == "-h" || args[0] == "--help") {
        printUsage();
        return args.empty() ? 1 : 0;
    }

    Options options;
    const std::string command = args[0];
    std::string positional;

    for (std::size_t i = 1; i < args.size(); ++i) {
        auto next = [&]() -> std::string {
            if (i + 1 >= args.size()) throw std::runtime_error("missing value for " + args[i]);
            return args[++i];
        };
        if (args[i] == "--game") options.gameDir = next();
        else if (args[i] == "--token") options.token = next();
        else if (args[i] == "--force") options.force = true;
        else if (args[i] == "--yes" || args[i] == "-y") options.yes = true;
        else if (args[i] == "--repo") {
            const std::string value = next();
            const std::size_t slash = value.find('/');
            if (slash == std::string::npos) {
                std::cerr << "error: --repo expects owner/repo\n";
                return 1;
            }
            options.owner = value.substr(0, slash);
            options.repo = value.substr(slash + 1);
        } else if (args[i].rfind("--", 0) == 0) {
            std::cerr << "error: unknown option " << args[i] << "\n";
            return 1;
        } else {
            positional = args[i];
        }
    }

    try {
        if (options.gameDir.empty()) options.gameDir = detectGameDir();
        if (options.gameDir.empty()) {
            std::cerr << "error: could not find the game. Pass --game <dir>.\n";
            return 1;
        }

        if (command == "where") {
            std::cout << options.gameDir.string() << "\n";
            return 0;
        }

        Installer installer(options.gameDir);

        if (command == "install-loader") return cmdInstallLoader(options.gameDir);
        if (command == "mods-off") return cmdSetLoaderEnabled(options.gameDir, false);
        if (command == "mods-on")  return cmdSetLoaderEnabled(options.gameDir, true);
        if (command == "list")    return cmdList(installer);
        if (command == "update")  return cmdUpdate(installer, options);
        if (command == "verify")  return cmdVerify(installer, positional);
        if (command == "install") {
            if (positional.empty()) { std::cerr << "error: install needs a .modpkg path\n"; return 1; }
            return cmdInstall(installer, positional, options);
        }
        if (command == "uninstall") {
            if (positional.empty()) { std::cerr << "error: uninstall needs a mod slug\n"; return 1; }
            return cmdUninstall(installer, positional, options);
        }
        if (command == "disable" || command == "enable") {
            if (positional.empty()) {
                std::cerr << "error: " << command << " needs a mod slug\n";
                return 1;
            }
            return cmdSetEnabled(installer, positional, command == "enable");
        }

        std::cerr << "error: unknown command '" << command << "'\n\n";
        printUsage();
        return 1;
    } catch (const std::exception& ex) {
        std::cerr << "error: " << ex.what() << "\n";
        return 1;
    }
}
