#pragma once

#include <string_view>

namespace UmaAssistant {

[[nodiscard]] inline bool valid_touch_remote_path(std::string_view path) noexcept
{
    constexpr std::string_view prefix = "/data/local/tmp/";
    if (!path.starts_with(prefix) || path.size() > 192 || path.back() == '/') return false;
    for (auto const value : path)
        if (!((value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z')
            || (value >= '0' && value <= '9') || value == '/' || value == '_' || value == '-'
            || value == '.'))
            return false;
    if (path.find("..") != std::string_view::npos) return false;

    std::size_t segment_start = prefix.size();
    while (segment_start < path.size())
    {
        auto const segment_end = path.find('/', segment_start);
        auto const end = segment_end == std::string_view::npos ? path.size() : segment_end;
        auto const segment = path.substr(segment_start, end - segment_start);
        if (segment.empty() || segment == "." || segment == "..") return false;
        segment_start = end + 1;
    }
    return true;
}

}
