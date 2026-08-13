// HTTPS access to the GitHub Releases API via WinHTTP.
//
// WinHTTP ships with Windows, so the updater needs no curl, no OpenSSL and no vendored
// TLS stack - it stays a single .exe the user can hand to a friend.
//
// Private repositories: release assets are not publicly downloadable. A token can be
// supplied via the SAM_MOD_TOKEN environment variable or the --token flag; without one,
// a private repo will answer 404 and the updater says so plainly rather than looking broken.
#pragma once

#include <windows.h>
#include <winhttp.h>

#include <cstdint>
#include <cstdlib>
#include <stdexcept>
#include <string>
#include <vector>

#include "json.hpp"

namespace sam {

class HttpError : public std::runtime_error {
public:
    HttpError(const std::string& what, int status = 0)
        : std::runtime_error(what), status(status) {}
    int status;
};

class Http {
public:
    struct Response {
        int status = 0;
        std::vector<std::uint8_t> body;
        std::string text() const { return std::string(body.begin(), body.end()); }
    };

    // GitHub redirects asset downloads to a storage host, so redirects are followed.
    static Response get(const std::wstring& url, const std::string& token = "",
                        const std::string& accept = "application/vnd.github+json") {
        URL_COMPONENTS parts{};
        parts.dwStructSize = sizeof(parts);
        wchar_t host[256] = {}, path[2048] = {};
        parts.lpszHostName = host;
        parts.dwHostNameLength = 255;
        parts.lpszUrlPath = path;
        parts.dwUrlPathLength = 2047;

        if (!WinHttpCrackUrl(url.c_str(), 0, 0, &parts))
            throw HttpError("malformed URL");

        Handle session(WinHttpOpen(L"sam-mod/1.0",
                                   WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
                                   WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0));
        if (!session) throw HttpError("cannot initialise WinHTTP");

        Handle connect(WinHttpConnect(session.get(), host, parts.nPort, 0));
        if (!connect) throw HttpError("cannot connect to the host");

        const DWORD flags = (parts.nScheme == INTERNET_SCHEME_HTTPS) ? WINHTTP_FLAG_SECURE : 0;
        Handle request(WinHttpOpenRequest(connect.get(), L"GET", path, nullptr,
                                          WINHTTP_NO_REFERER,
                                          WINHTTP_DEFAULT_ACCEPT_TYPES, flags));
        if (!request) throw HttpError("cannot create the request");

        std::wstring headers = L"Accept: " + widen(accept) + L"\r\n";
        headers += L"X-GitHub-Api-Version: 2022-11-28\r\n";
        if (!token.empty()) headers += L"Authorization: Bearer " + widen(token) + L"\r\n";

        if (!WinHttpSendRequest(request.get(), headers.c_str(),
                                static_cast<DWORD>(headers.size()),
                                WINHTTP_NO_REQUEST_DATA, 0, 0, 0))
            throw HttpError("the request could not be sent");

        if (!WinHttpReceiveResponse(request.get(), nullptr))
            throw HttpError("no response from the server");

        Response response;
        DWORD statusCode = 0, statusSize = sizeof(statusCode);
        WinHttpQueryHeaders(request.get(),
                            WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                            WINHTTP_HEADER_NAME_BY_INDEX, &statusCode, &statusSize,
                            WINHTTP_NO_HEADER_INDEX);
        response.status = static_cast<int>(statusCode);

        for (;;) {
            DWORD available = 0;
            if (!WinHttpQueryDataAvailable(request.get(), &available) || available == 0) break;

            const std::size_t offset = response.body.size();
            response.body.resize(offset + available);
            DWORD read = 0;
            if (!WinHttpReadData(request.get(), response.body.data() + offset, available, &read))
                throw HttpError("the response could not be read");
            response.body.resize(offset + read);
        }
        return response;
    }

private:
    class Handle {
    public:
        explicit Handle(HINTERNET h) : h_(h) {}
        ~Handle() { if (h_) WinHttpCloseHandle(h_); }
        Handle(const Handle&) = delete;
        Handle& operator=(const Handle&) = delete;
        HINTERNET get() const { return h_; }
        explicit operator bool() const { return h_ != nullptr; }
    private:
        HINTERNET h_;
    };

    static std::wstring widen(const std::string& s) {
        if (s.empty()) return {};
        const int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(),
                                          static_cast<int>(s.size()), nullptr, 0);
        std::wstring out(static_cast<std::size_t>(n), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()),
                            out.data(), n);
        return out;
    }
};

// A published mod package on a GitHub release.
struct RemoteAsset {
    std::string modSlug;      // derived from the file name: <slug>-<version>.modpkg
    std::string version;
    std::string fileName;
    std::string downloadUrl;
    std::uint64_t size = 0;
};

class GitHubSource {
public:
    GitHubSource(std::string owner, std::string repo, std::string token)
        : owner_(std::move(owner)), repo_(std::move(repo)), token_(std::move(token)) {
        if (token_.empty()) {
            char buffer[512] = {};
            const DWORD n = GetEnvironmentVariableA("SAM_MOD_TOKEN", buffer, sizeof(buffer));
            if (n > 0 && n < sizeof(buffer)) token_.assign(buffer, n);
        }
    }

    std::string slug() const { return owner_ + "/" + repo_; }

    // Every .modpkg attached to the newest release.
    std::vector<RemoteAsset> latest() const {
        const std::string url =
            "https://api.github.com/repos/" + owner_ + "/" + repo_ + "/releases/latest";

        auto response = Http::get(toWide(url), token_);
        if (response.status == 404) {
            throw HttpError(
                "no release found at " + slug() +
                (token_.empty()
                     ? " - if the repository is private, set SAM_MOD_TOKEN or pass --token"
                     : " - check that a release exists and the token can read this repository"),
                404);
        }
        if (response.status == 401 || response.status == 403) {
            throw HttpError("access to " + slug() + " was refused (HTTP " +
                            std::to_string(response.status) + ")", response.status);
        }
        if (response.status != 200)
            throw HttpError("GitHub answered HTTP " + std::to_string(response.status),
                            response.status);

        const Json release = Json::parse(response.text());
        const std::string tag = release["tag_name"].str();

        std::vector<RemoteAsset> found;
        for (const Json& asset : release["assets"].array()) {
            const std::string name = asset["name"].str();
            if (name.size() < 8 || name.compare(name.size() - 7, 7, ".modpkg") != 0) continue;

            RemoteAsset item;
            item.fileName = name;
            item.downloadUrl = asset["url"].str();   // API URL: works for private repos too
            item.size = static_cast<std::uint64_t>(asset["size"].num());

            // "<slug>-<version>.modpkg" - split on the last hyphen so slugs may contain one.
            const std::string stem = name.substr(0, name.size() - 7);
            const std::size_t dash = stem.rfind('-');
            if (dash == std::string::npos) continue;
            item.modSlug = stem.substr(0, dash);
            item.version = stem.substr(dash + 1);
            if (item.version.empty()) item.version = tag;

            found.push_back(std::move(item));
        }
        return found;
    }

    std::vector<std::uint8_t> download(const RemoteAsset& asset) const {
        // The octet-stream Accept header is what makes the API return the file itself
        // rather than its JSON description.
        auto response = Http::get(toWide(asset.downloadUrl), token_, "application/octet-stream");
        if (response.status != 200)
            throw HttpError("downloading " + asset.fileName + " failed with HTTP " +
                            std::to_string(response.status), response.status);
        return std::move(response.body);
    }

private:
    std::string owner_, repo_, token_;

    static std::wstring toWide(const std::string& s) {
        return std::wstring(s.begin(), s.end());   // URLs are ASCII
    }
};

}  // namespace sam
