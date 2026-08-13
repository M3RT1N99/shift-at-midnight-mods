// A compact DEFLATE (RFC 1951) decompressor.
//
// Written out rather than vendored so the installer stays a single self-contained binary
// with no third-party build inputs. Only decompression is needed: the packer writes
// archives, this tool only reads them.
#pragma once

#include <cstdint>
#include <cstring>
#include <span>
#include <stdexcept>
#include <vector>

namespace sam {

class InflateError : public std::runtime_error {
public:
    explicit InflateError(const std::string& what) : std::runtime_error(what) {}
};

namespace detail {

// Canonical Huffman decoding table, built from a list of code lengths as RFC 1951 §3.2.2
// describes: codes are assigned in order of increasing length, then of increasing symbol.
class Huffman {
public:
    void build(const std::uint8_t* lengths, std::size_t count) {
        counts_.assign(16, 0);
        symbols_.assign(count, 0);

        for (std::size_t i = 0; i < count; ++i) {
            if (lengths[i] > 15) throw InflateError("code length out of range");
            ++counts_[lengths[i]];
        }
        // Length 0 means "symbol unused"; it must not take part in code assignment.
        counts_[0] = 0;

        std::vector<int> offsets(16, 0);
        for (int len = 1; len < 16; ++len)
            offsets[len] = offsets[len - 1] + counts_[len - 1];

        for (std::size_t i = 0; i < count; ++i)
            if (lengths[i] != 0) symbols_[offsets[lengths[i]]++] = static_cast<int>(i);
    }

    const std::vector<int>& counts() const { return counts_; }
    const std::vector<int>& symbols() const { return symbols_; }

private:
    std::vector<int> counts_;
    std::vector<int> symbols_;
};

}  // namespace detail

class Inflater {
public:
    // Raw DEFLATE stream in, decompressed bytes out. expectedSize is a hard cap so a
    // corrupt or hostile archive cannot make us allocate without bound.
    static std::vector<std::uint8_t> run(std::span<const std::uint8_t> input,
                                         std::size_t expectedSize) {
        Inflater self(input, expectedSize);
        self.decode();
        return std::move(self.out_);
    }

private:
    Inflater(std::span<const std::uint8_t> input, std::size_t cap)
        : in_(input), cap_(cap) {
        out_.reserve(cap);
    }

    std::span<const std::uint8_t> in_;
    std::vector<std::uint8_t> out_;
    std::size_t cap_;
    std::size_t pos_ = 0;
    std::uint32_t bitBuffer_ = 0;
    int bitCount_ = 0;

    int bits(int need) {
        while (bitCount_ < need) {
            if (pos_ >= in_.size()) throw InflateError("stream ended mid-symbol");
            bitBuffer_ |= static_cast<std::uint32_t>(in_[pos_++]) << bitCount_;
            bitCount_ += 8;
        }
        const int value = static_cast<int>(bitBuffer_ & ((1u << need) - 1));
        bitBuffer_ >>= need;
        bitCount_ -= need;
        return value;
    }

    // Huffman codes are packed most-significant-bit first, so the code is rebuilt one bit
    // at a time and compared against the running first-code of each length.
    int decodeSymbol(const detail::Huffman& table) {
        int code = 0, first = 0, index = 0;
        for (int len = 1; len < 16; ++len) {
            code |= bits(1);
            const int count = table.counts()[len];
            if (code - first < count) return table.symbols()[index + (code - first)];
            index += count;
            first = (first + count) << 1;
            code <<= 1;
        }
        throw InflateError("invalid Huffman code");
    }

    void emit(std::uint8_t byte) {
        if (out_.size() >= cap_) throw InflateError("output exceeds the declared size");
        out_.push_back(byte);
    }

    void decode() {
        bool finalBlock = false;
        while (!finalBlock) {
            finalBlock = bits(1) != 0;
            switch (bits(2)) {
                case 0: storedBlock(); break;
                case 1: compressedBlock(fixedLiterals(), fixedDistances()); break;
                case 2: dynamicBlock(); break;
                default: throw InflateError("reserved block type");
            }
        }
    }

    void storedBlock() {
        bitBuffer_ = 0;
        bitCount_ = 0;
        if (pos_ + 4 > in_.size()) throw InflateError("truncated stored block header");

        const std::uint16_t len = static_cast<std::uint16_t>(in_[pos_] | (in_[pos_ + 1] << 8));
        const std::uint16_t nlen = static_cast<std::uint16_t>(in_[pos_ + 2] | (in_[pos_ + 3] << 8));
        pos_ += 4;

        if (static_cast<std::uint16_t>(~len & 0xFFFF) != nlen)
            throw InflateError("stored block length check failed");
        if (pos_ + len > in_.size()) throw InflateError("truncated stored block");
        if (out_.size() + len > cap_) throw InflateError("output exceeds the declared size");

        out_.insert(out_.end(), in_.begin() + static_cast<std::ptrdiff_t>(pos_),
                    in_.begin() + static_cast<std::ptrdiff_t>(pos_ + len));
        pos_ += len;
    }

    void compressedBlock(const detail::Huffman& literals, const detail::Huffman& distances) {
        static constexpr int kLengthBase[29] = {
            3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31,
            35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258};
        static constexpr int kLengthExtra[29] = {
            0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
            3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0};
        static constexpr int kDistBase[30] = {
            1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193,
            257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577};
        static constexpr int kDistExtra[30] = {
            0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
            7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13};

        for (;;) {
            const int symbol = decodeSymbol(literals);
            if (symbol < 256) {
                emit(static_cast<std::uint8_t>(symbol));
                continue;
            }
            if (symbol == 256) return;  // end of block

            const int lengthIndex = symbol - 257;
            if (lengthIndex >= 29) throw InflateError("invalid length symbol");
            const int length = kLengthBase[lengthIndex] + bits(kLengthExtra[lengthIndex]);

            const int distIndex = decodeSymbol(distances);
            if (distIndex >= 30) throw InflateError("invalid distance symbol");
            const int distance = kDistBase[distIndex] + bits(kDistExtra[distIndex]);

            if (static_cast<std::size_t>(distance) > out_.size())
                throw InflateError("back-reference points before the stream");

            // Copies may overlap - that is how DEFLATE encodes runs - so this must copy
            // byte by byte rather than with a bulk move.
            std::size_t from = out_.size() - static_cast<std::size_t>(distance);
            for (int i = 0; i < length; ++i) emit(out_[from + static_cast<std::size_t>(i)]);
        }
    }

    void dynamicBlock() {
        static constexpr int kOrder[19] = {
            16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15};

        const int litCount = bits(5) + 257;
        const int distCount = bits(5) + 1;
        const int codeCount = bits(4) + 4;
        if (litCount > 286 || distCount > 30) throw InflateError("too many codes declared");

        std::uint8_t codeLengths[19] = {};
        for (int i = 0; i < codeCount; ++i)
            codeLengths[kOrder[i]] = static_cast<std::uint8_t>(bits(3));

        detail::Huffman codeTable;
        codeTable.build(codeLengths, 19);

        std::vector<std::uint8_t> lengths(static_cast<std::size_t>(litCount + distCount), 0);
        std::size_t filled = 0;
        while (filled < lengths.size()) {
            const int symbol = decodeSymbol(codeTable);
            if (symbol < 16) {
                lengths[filled++] = static_cast<std::uint8_t>(symbol);
            } else if (symbol == 16) {
                if (filled == 0) throw InflateError("repeat with no previous length");
                const std::uint8_t previous = lengths[filled - 1];
                for (int n = bits(2) + 3; n > 0 && filled < lengths.size(); --n)
                    lengths[filled++] = previous;
            } else if (symbol == 17) {
                for (int n = bits(3) + 3; n > 0 && filled < lengths.size(); --n)
                    lengths[filled++] = 0;
            } else {
                for (int n = bits(7) + 11; n > 0 && filled < lengths.size(); --n)
                    lengths[filled++] = 0;
            }
        }

        detail::Huffman literals, distances;
        literals.build(lengths.data(), static_cast<std::size_t>(litCount));
        distances.build(lengths.data() + litCount, static_cast<std::size_t>(distCount));
        compressedBlock(literals, distances);
    }

    // The fixed tables of RFC 1951 §3.2.6, built once on first use.
    static const detail::Huffman& fixedLiterals() {
        static const detail::Huffman table = [] {
            std::uint8_t lengths[288];
            for (int i = 0; i < 144; ++i) lengths[i] = 8;
            for (int i = 144; i < 256; ++i) lengths[i] = 9;
            for (int i = 256; i < 280; ++i) lengths[i] = 7;
            for (int i = 280; i < 288; ++i) lengths[i] = 8;
            detail::Huffman h;
            h.build(lengths, 288);
            return h;
        }();
        return table;
    }

    static const detail::Huffman& fixedDistances() {
        static const detail::Huffman table = [] {
            std::uint8_t lengths[30];
            for (int i = 0; i < 30; ++i) lengths[i] = 5;
            detail::Huffman h;
            h.build(lengths, 30);
            return h;
        }();
        return table;
    }
};

}  // namespace sam
