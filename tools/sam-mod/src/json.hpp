// A small read-only JSON parser, enough for mod.json and the GitHub releases API.
//
// Deliberately strict: unknown input is rejected rather than guessed at, because this
// parser decides which files get written into a game installation.
#pragma once

#include <cctype>
#include <cstdint>
#include <map>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

namespace sam {

class JsonError : public std::runtime_error {
public:
    explicit JsonError(const std::string& what) : std::runtime_error(what) {}
};

class Json {
public:
    enum class Kind { Null, Bool, Number, String, Array, Object };

    Kind kind = Kind::Null;
    bool boolean = false;
    double number = 0;
    std::string text;
    std::vector<Json> items;
    std::map<std::string, Json> fields;

    static Json parse(const std::string& input) {
        std::size_t at = 0;
        Json value = parseValue(input, at);
        skipSpace(input, at);
        if (at != input.size()) throw JsonError("trailing content after the JSON value");
        return value;
    }

    bool isNull() const { return kind == Kind::Null; }
    bool has(const std::string& key) const {
        return kind == Kind::Object && fields.count(key) != 0;
    }

    const Json& operator[](const std::string& key) const {
        static const Json kNull;
        auto it = fields.find(key);
        return it == fields.end() ? kNull : it->second;
    }

    std::string str(const std::string& fallback = "") const {
        return kind == Kind::String ? text : fallback;
    }

    double num(double fallback = 0) const { return kind == Kind::Number ? number : fallback; }

    bool flag(bool fallback = false) const { return kind == Kind::Bool ? boolean : fallback; }

    const std::vector<Json>& array() const {
        static const std::vector<Json> kEmpty;
        return kind == Kind::Array ? items : kEmpty;
    }

private:
    static void skipSpace(const std::string& s, std::size_t& at) {
        while (at < s.size() && (s[at] == ' ' || s[at] == '\t' || s[at] == '\n' || s[at] == '\r'))
            ++at;
    }

    static void expect(const std::string& s, std::size_t& at, char c) {
        skipSpace(s, at);
        if (at >= s.size() || s[at] != c)
            throw JsonError(std::string("expected '") + c + "' at offset " + std::to_string(at));
        ++at;
    }

    static Json parseValue(const std::string& s, std::size_t& at) {
        skipSpace(s, at);
        if (at >= s.size()) throw JsonError("unexpected end of input");

        switch (s[at]) {
            case '{': return parseObject(s, at);
            case '[': return parseArray(s, at);
            case '"': {
                Json v;
                v.kind = Kind::String;
                v.text = parseString(s, at);
                return v;
            }
            case 't':
            case 'f': {
                const bool isTrue = s[at] == 't';
                const std::string word = isTrue ? "true" : "false";
                if (s.compare(at, word.size(), word) != 0) throw JsonError("bad literal");
                at += word.size();
                Json v;
                v.kind = Kind::Bool;
                v.boolean = isTrue;
                return v;
            }
            case 'n': {
                if (s.compare(at, 4, "null") != 0) throw JsonError("bad literal");
                at += 4;
                return Json{};
            }
            default: return parseNumber(s, at);
        }
    }

    static Json parseObject(const std::string& s, std::size_t& at) {
        Json v;
        v.kind = Kind::Object;
        expect(s, at, '{');
        skipSpace(s, at);
        if (at < s.size() && s[at] == '}') { ++at; return v; }

        for (;;) {
            skipSpace(s, at);
            std::string key = parseString(s, at);
            expect(s, at, ':');
            v.fields[std::move(key)] = parseValue(s, at);
            skipSpace(s, at);
            if (at < s.size() && s[at] == ',') { ++at; continue; }
            expect(s, at, '}');
            return v;
        }
    }

    static Json parseArray(const std::string& s, std::size_t& at) {
        Json v;
        v.kind = Kind::Array;
        expect(s, at, '[');
        skipSpace(s, at);
        if (at < s.size() && s[at] == ']') { ++at; return v; }

        for (;;) {
            v.items.push_back(parseValue(s, at));
            skipSpace(s, at);
            if (at < s.size() && s[at] == ',') { ++at; continue; }
            expect(s, at, ']');
            return v;
        }
    }

    static std::string parseString(const std::string& s, std::size_t& at) {
        expect(s, at, '"');
        std::string out;
        while (at < s.size()) {
            const char c = s[at++];
            if (c == '"') return out;
            if (c != '\\') { out.push_back(c); continue; }
            if (at >= s.size()) break;

            switch (s[at++]) {
                case '"':  out.push_back('"');  break;
                case '\\': out.push_back('\\'); break;
                case '/':  out.push_back('/');  break;
                case 'b':  out.push_back('\b'); break;
                case 'f':  out.push_back('\f'); break;
                case 'n':  out.push_back('\n'); break;
                case 'r':  out.push_back('\r'); break;
                case 't':  out.push_back('\t'); break;
                case 'u': {
                    if (at + 4 > s.size()) throw JsonError("truncated \\u escape");
                    const int code = std::stoi(s.substr(at, 4), nullptr, 16);
                    at += 4;
                    appendUtf8(out, code);
                    break;
                }
                default: throw JsonError("unknown escape sequence");
            }
        }
        throw JsonError("unterminated string");
    }

    // Surrogate pairs are not recombined: the values this parser reads (names, tags, URLs)
    // are ASCII in practice, and emitting each half as UTF-8 keeps them round-trippable.
    static void appendUtf8(std::string& out, int code) {
        if (code < 0x80) {
            out.push_back(static_cast<char>(code));
        } else if (code < 0x800) {
            out.push_back(static_cast<char>(0xC0 | (code >> 6)));
            out.push_back(static_cast<char>(0x80 | (code & 0x3F)));
        } else {
            out.push_back(static_cast<char>(0xE0 | (code >> 12)));
            out.push_back(static_cast<char>(0x80 | ((code >> 6) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | (code & 0x3F)));
        }
    }

    static Json parseNumber(const std::string& s, std::size_t& at) {
        const std::size_t start = at;
        if (at < s.size() && (s[at] == '-' || s[at] == '+')) ++at;
        while (at < s.size() &&
               (std::isdigit(static_cast<unsigned char>(s[at])) || s[at] == '.' ||
                s[at] == 'e' || s[at] == 'E' || s[at] == '-' || s[at] == '+'))
            ++at;
        if (at == start) throw JsonError("expected a value");

        Json v;
        v.kind = Kind::Number;
        v.number = std::stod(s.substr(start, at - start));
        return v;
    }
};

}  // namespace sam
