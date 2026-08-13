// Minimal ZIP reader for .modpkg archives.
//
// Reads the central directory rather than scanning local headers, so entry names and sizes
// come from the authoritative index. Only the two methods the packer emits are supported:
// stored (0) and deflate (8).
#pragma once

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <span>
#include <stdexcept>
#include <string>
#include <vector>

#include "inflate.hpp"

namespace sam {

class ZipError : public std::runtime_error {
public:
    explicit ZipError(const std::string& what) : std::runtime_error(what) {}
};

class ZipArchive {
public:
    struct Entry {
        std::string name;
        std::uint16_t method = 0;
        std::uint32_t compressedSize = 0;
        std::uint32_t uncompressedSize = 0;
        std::uint32_t localHeaderOffset = 0;
    };

    explicit ZipArchive(const std::filesystem::path& path) {
        std::ifstream in(path, std::ios::binary);
        if (!in) throw ZipError("cannot open " + path.string());
        data_.assign(std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>());
        if (data_.size() < 22) throw ZipError("file is too small to be a zip archive");
        readCentralDirectory();
    }

    const std::vector<Entry>& entries() const { return entries_; }

    bool has(const std::string& name) const { return index_.count(name) != 0; }

    // Extracts one entry into memory. Small enough for mod payloads; the largest thing a
    // package carries is a plugin assembly.
    std::vector<std::uint8_t> read(const std::string& name) const {
        auto it = index_.find(name);
        if (it == index_.end()) throw ZipError("no such entry: " + name);
        return read(entries_[it->second]);
    }

    std::vector<std::uint8_t> read(const Entry& entry) const {
        // The local header repeats the name and extra-field lengths, and only it tells us
        // where the payload actually starts.
        const std::size_t header = entry.localHeaderOffset;
        if (header + 30 > data_.size()) throw ZipError("local header past end of file");
        if (u32(header) != 0x04034B50u) throw ZipError("bad local header signature");

        const std::size_t nameLen = u16(header + 26);
        const std::size_t extraLen = u16(header + 28);
        const std::size_t start = header + 30 + nameLen + extraLen;
        if (start + entry.compressedSize > data_.size())
            throw ZipError("entry data past end of file: " + entry.name);

        std::span<const std::uint8_t> raw(data_.data() + start, entry.compressedSize);

        if (entry.method == 0) {
            if (entry.compressedSize != entry.uncompressedSize)
                throw ZipError("stored entry has mismatched sizes: " + entry.name);
            return std::vector<std::uint8_t>(raw.begin(), raw.end());
        }
        if (entry.method == 8) return Inflater::run(raw, entry.uncompressedSize);

        throw ZipError("unsupported compression method in " + entry.name);
    }

    std::string readText(const std::string& name) const {
        const auto bytes = read(name);
        return std::string(bytes.begin(), bytes.end());
    }

private:
    std::vector<std::uint8_t> data_;
    std::vector<Entry> entries_;
    std::map<std::string, std::size_t> index_;

    std::uint16_t u16(std::size_t at) const {
        if (at + 2 > data_.size()) throw ZipError("read past end of file");
        return static_cast<std::uint16_t>(data_[at] | (data_[at + 1] << 8));
    }

    std::uint32_t u32(std::size_t at) const {
        if (at + 4 > data_.size()) throw ZipError("read past end of file");
        return static_cast<std::uint32_t>(data_[at]) |
               (static_cast<std::uint32_t>(data_[at + 1]) << 8) |
               (static_cast<std::uint32_t>(data_[at + 2]) << 16) |
               (static_cast<std::uint32_t>(data_[at + 3]) << 24);
    }

    void readCentralDirectory() {
        // The end-of-central-directory record sits at the tail, after a comment of up to
        // 64 KiB, so it has to be searched for backwards.
        const std::size_t maxScan = std::min<std::size_t>(data_.size(), 0xFFFF + 22);
        std::size_t eocd = 0;
        bool found = false;
        for (std::size_t back = 22; back <= maxScan; ++back) {
            const std::size_t at = data_.size() - back;
            if (u32(at) == 0x06054B50u) {
                eocd = at;
                found = true;
                break;
            }
        }
        if (!found) throw ZipError("not a zip archive (no end-of-central-directory record)");

        const std::uint16_t count = u16(eocd + 10);
        std::size_t offset = u32(eocd + 16);

        entries_.reserve(count);
        for (std::uint16_t i = 0; i < count; ++i) {
            if (u32(offset) != 0x02014B50u) throw ZipError("bad central directory signature");

            Entry entry;
            entry.method = u16(offset + 10);
            entry.compressedSize = u32(offset + 20);
            entry.uncompressedSize = u32(offset + 24);
            entry.localHeaderOffset = u32(offset + 42);

            const std::size_t nameLen = u16(offset + 28);
            const std::size_t extraLen = u16(offset + 30);
            const std::size_t commentLen = u16(offset + 32);
            if (offset + 46 + nameLen > data_.size()) throw ZipError("entry name past end of file");

            entry.name.assign(reinterpret_cast<const char*>(data_.data() + offset + 46), nameLen);
            std::replace(entry.name.begin(), entry.name.end(), '\\', '/');

            // Directory markers carry no content and are recreated on extraction anyway.
            if (!entry.name.empty() && entry.name.back() != '/') {
                index_[entry.name] = entries_.size();
                entries_.push_back(std::move(entry));
            }
            offset += 46 + nameLen + extraLen + commentLen;
        }
    }
};

}  // namespace sam
