#pragma once

#include <array>
#include <map>
#include <optional>
#include <string>
#include <string_view>

namespace UmaAssistant::Career
{

using SupportDeckTypes = std::map<std::string, int, std::less<>>;

// New compositions use types-; unprefixed strings retain legacy preset behavior.
inline std::optional<SupportDeckTypes> ParseSupportDeckTypes(std::string_view preset)
{
    if (preset.starts_with("types-")) preset.remove_prefix(6);
    SupportDeckTypes result;
    constexpr std::array<std::string_view, 6> types{"speed", "stamina", "power", "guts", "wit", "friend"};
    while (!preset.empty())
    {
        auto const separator = preset.find('-');
        auto const part = preset.substr(0, separator);
        bool matched = false;
        for (auto const type : types)
        {
            if (part.size() != type.size() + 1 || !part.starts_with(type)
                || part.back() < '0' || part.back() > '6') continue;
            if (!result.emplace(std::string(type), part.back() - '0').second) return {};
            matched = true;
            break;
        }
        if (!matched) return {};
        if (separator == std::string_view::npos) break;
        preset.remove_prefix(separator + 1);
        if (preset.empty()) return {};
    }
    if (result.empty()) return {};
    return result;
}

inline bool IsSixCardSupportDeck(SupportDeckTypes const& types)
{
    int total = 0;
    for (auto const& [type, count] : types) total += count;
    return total == 6;
}

} // namespace UmaAssistant::Career
