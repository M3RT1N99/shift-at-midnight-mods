// SHA-256 via the Windows CNG provider. Using the OS implementation keeps the tool
// dependency-free: no vendored crypto, nothing to keep patched.
#pragma once

#include <windows.h>
#include <bcrypt.h>

#include <array>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <span>
#include <stdexcept>
#include <string>
#include <vector>

namespace sam {

class Sha256 {
public:
    Sha256() {
        if (!BCRYPT_SUCCESS(BCryptOpenAlgorithmProvider(
                &alg_, BCRYPT_SHA256_ALGORITHM, nullptr, 0)))
            throw std::runtime_error("cannot open the SHA-256 provider");

        DWORD objectSize = 0, written = 0;
        if (!BCRYPT_SUCCESS(BCryptGetProperty(alg_, BCRYPT_OBJECT_LENGTH,
                reinterpret_cast<PUCHAR>(&objectSize), sizeof(objectSize), &written, 0)))
            throw std::runtime_error("cannot size the SHA-256 hash object");

        object_.resize(objectSize);
        if (!BCRYPT_SUCCESS(BCryptCreateHash(alg_, &hash_, object_.data(),
                objectSize, nullptr, 0, 0)))
            throw std::runtime_error("cannot create the SHA-256 hash");
    }

    ~Sha256() {
        if (hash_) BCryptDestroyHash(hash_);
        if (alg_) BCryptCloseAlgorithmProvider(alg_, 0);
    }

    Sha256(const Sha256&) = delete;
    Sha256& operator=(const Sha256&) = delete;

    void update(std::span<const std::uint8_t> data) {
        if (data.empty()) return;
        if (!BCRYPT_SUCCESS(BCryptHashData(hash_,
                const_cast<PUCHAR>(data.data()), static_cast<ULONG>(data.size()), 0)))
            throw std::runtime_error("SHA-256 update failed");
    }

    // Lowercase hex, matching the SHA256SUMS files the packer writes.
    std::string hex() {
        std::array<std::uint8_t, 32> digest{};
        if (!BCRYPT_SUCCESS(BCryptFinishHash(hash_, digest.data(),
                static_cast<ULONG>(digest.size()), 0)))
            throw std::runtime_error("SHA-256 finalisation failed");

        static constexpr char kHex[] = "0123456789abcdef";
        std::string out;
        out.reserve(64);
        for (std::uint8_t b : digest) {
            out.push_back(kHex[b >> 4]);
            out.push_back(kHex[b & 0x0F]);
        }
        return out;
    }

    static std::string ofBytes(std::span<const std::uint8_t> data) {
        Sha256 h;
        h.update(data);
        return h.hex();
    }

    static std::string ofFile(const std::filesystem::path& path) {
        std::ifstream in(path, std::ios::binary);
        if (!in) throw std::runtime_error("cannot read " + path.string());

        Sha256 h;
        std::vector<std::uint8_t> buffer(64 * 1024);
        while (in) {
            in.read(reinterpret_cast<char*>(buffer.data()),
                    static_cast<std::streamsize>(buffer.size()));
            const auto got = static_cast<std::size_t>(in.gcount());
            if (got == 0) break;
            h.update(std::span<const std::uint8_t>(buffer.data(), got));
        }
        return h.hex();
    }

private:
    BCRYPT_ALG_HANDLE alg_{};
    BCRYPT_HASH_HANDLE hash_{};
    std::vector<std::uint8_t> object_;
};

}  // namespace sam
