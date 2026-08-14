// The manager's own version, in one place, shared by both front ends.
#pragma once

#include <string>
#include <vector>

namespace sam {

constexpr const char* kManagerVersion = "1.2.3";

/// <summary>
/// Numeric-aware comparison, so 1.10.0 sorts above 1.9.0 where a string compare would not.
/// Returns -1, 0 or 1. Any leading "v" is ignored, since release tags carry one and the
/// embedded version does not.
/// </summary>
inline int compareVersions(const std::string& a, const std::string& b) {
    auto split = [](const std::string& value) {
        std::vector<int> parts;
        std::string current;
        for (char c : value + ".") {
            if (c == '.') {
                parts.push_back(current.empty() ? 0 : std::atoi(current.c_str()));
                current.clear();
            } else if (c >= '0' && c <= '9') {
                current.push_back(c);
            }
            // Anything else - a leading 'v', a pre-release suffix - is ignored.
        }
        return parts;
    };

    const auto left = split(a), right = split(b);
    const std::size_t count = left.size() > right.size() ? left.size() : right.size();

    for (std::size_t i = 0; i < count; ++i) {
        const int l = i < left.size() ? left[i] : 0;
        const int r = i < right.size() ? right[i] : 0;
        if (l != r) return l < r ? -1 : 1;
    }
    return 0;
}

}  // namespace sam
