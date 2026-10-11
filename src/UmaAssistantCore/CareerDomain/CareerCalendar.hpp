#pragma once

#include <charconv>
#include <cctype>
#include <optional>
#include <string>
#include <string_view>

namespace UmaAssistant::CareerDomain
{

class CareerCalendar final
{
public:
    // Race catalog slots use the one-based index emitted by the managed
    // Career turn parser.
    [[nodiscard]] static std::optional<int> TryMapRaceSlot(
        std::string_view year, std::string_view turn)
    {
        const auto normalizedYear = Normalize(year);
        const int yearOffset = normalizedYear == "first year" || normalizedYear == "junior year"
                || normalizedYear == "junior" ? 0
            : normalizedYear == "second year" || normalizedYear == "classic year"
                || normalizedYear == "classic" ? 24
            : normalizedYear == "third year" || normalizedYear == "senior year"
                || normalizedYear == "senior" ? 48 : -1;
        if (yearOffset < 0)
            return std::nullopt;

        const auto separator = turn.find('_');
        if (separator == std::string_view::npos
            || turn.find('_', separator + 1) != std::string_view::npos)
            return std::nullopt;
        int month = 0;
        int half = 0;
        if (!ParseInteger(turn.substr(0, separator), month)
            || !ParseInteger(turn.substr(separator + 1), half)
            || month < 1 || month > 12 || half < 1 || half > 2)
            return std::nullopt;

        return yearOffset + ((month - 1) * 2) + half;
    }

private:
    [[nodiscard]] static std::string Normalize(std::string_view value)
    {
        std::string result;
        bool pendingSpace = false;
        for (const unsigned char character : value)
        {
            if (std::isspace(character) || character == '_')
            {
                pendingSpace = !result.empty();
                continue;
            }
            if (pendingSpace)
            {
                result.push_back(' ');
                pendingSpace = false;
            }
            result.push_back(static_cast<char>(std::tolower(character)));
        }
        return result;
    }

    [[nodiscard]] static bool ParseInteger(std::string_view value, int& result) noexcept
    {
        if (value.empty())
            return false;
        const auto parsed = std::from_chars(value.data(), value.data() + value.size(), result);
        return parsed.ec == std::errc{} && parsed.ptr == value.data() + value.size();
    }
};

} // namespace UmaAssistant::CareerDomain
