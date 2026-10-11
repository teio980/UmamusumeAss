#pragma once

#include <algorithm>
#include <charconv>
#include <cctype>
#include <optional>
#include <regex>
#include <string>
#include <string_view>

namespace UmaAssistant::CareerDomain
{

enum class CareerGoalKind
{
    Unknown,
    Fans,
    Race,
    GradeRaceCount,
    Completed
};

// Pure parser for the visible Career goal banner. Ambiguous or incomplete OCR
// remains unknown; callers carry the OCR confidence separately.
class CareerGoalTextParser final
{
public:
    [[nodiscard]] static std::optional<int> ParseTurnsLeft(std::string_view text)
    {
        const auto normalized = Trim(text);
        if (normalized.empty())
            return std::nullopt;

        static const std::regex turnsPattern(
            R"((^|[^0-9])([0-9]+)[[:space:]]*turns?(?:[[:space:]]*\(s\))?\b)",
            std::regex::icase);
        std::smatch match;
        if (std::regex_search(normalized, match, turnsPattern))
        {
            int turns = 0;
            if (ParseInteger(match[2].str(), turns))
                return turns;
        }

        static const std::regex standalonePattern(R"(^[[:space:]]*([0-9]+)[[:space:]]*$)");
        if (std::regex_match(normalized, match, standalonePattern))
        {
            int turns = 0;
            if (ParseInteger(match[1].str(), turns))
                return turns;
        }
        return std::nullopt;
    }

    [[nodiscard]] static std::optional<int> ParseFansToGo(std::string_view text)
    {
        const auto normalized = Trim(text);
        if (normalized.empty())
            return std::nullopt;

        static const std::regex fansPattern(
            R"((^|[^0-9])([0-9,]{1,7})[[:space:]]*fans?(?:[[:space:]]*\(s\))?[[:space:]]*to[[:space:]]*go\b)",
            std::regex::icase);
        std::smatch match;
        if (!std::regex_search(normalized, match, fansPattern))
            return std::nullopt;

        std::string digits = match[2].str();
        digits.erase(std::remove(digits.begin(), digits.end(), ','), digits.end());
        int fans = 0;
        if (!ParseInteger(digits, fans))
            return std::nullopt;
        return std::clamp(fans, 0, 9'999'999);
    }

    [[nodiscard]] static std::optional<int> ParseRaceCountLeft(std::string_view text)
    {
        const auto normalized = Trim(text);
        if (normalized.empty())
            return std::nullopt;

        static const std::regex countPattern(
            R"((^|[^A-Za-z0-9])([0-9]+|[Il|])[[:space:]]*time(?:\(s\)|s)?[[:space:]]*left\b)",
            std::regex::icase);
        std::smatch match;
        if (!std::regex_search(normalized, match, countPattern))
            return std::nullopt;

        const std::string countText = match[2].str();
        if (countText == "I" || countText == "i" || countText == "l"
            || countText == "L" || countText == "|")
            return 1;
        int count = 0;
        return ParseInteger(countText, count) ? std::optional<int>(count) : std::nullopt;
    }

    [[nodiscard]] static std::optional<std::string> ParseRaceGrade(std::string_view text)
    {
        const auto normalized = Trim(text);
        if (normalized.empty())
            return std::nullopt;

        static const std::regex gradePattern(
            R"((^|[^A-Za-z0-9])G[[:space:]]*(III|II|I|[123]|lll|ll|l)(?![A-Za-z0-9]))",
            std::regex::icase);
        std::smatch match;
        if (!std::regex_search(normalized, match, gradePattern))
            return std::nullopt;

        auto level = Lower(match[2].str());
        std::replace(level.begin(), level.end(), 'l', 'i');
        if (level == "1" || level == "i") return std::string("G1");
        if (level == "2" || level == "ii") return std::string("G2");
        if (level == "3" || level == "iii") return std::string("G3");
        return std::nullopt;
    }

    [[nodiscard]] static CareerGoalKind Classify(std::string_view text)
    {
        const auto normalized = Trim(text);
        if (normalized.empty())
            return CareerGoalKind::Unknown;

        static const std::regex achievedPattern(
            R"(\bgoal[[:space:]]+achieved\b)", std::regex::icase);
        if (std::regex_search(normalized, achievedPattern))
            return CareerGoalKind::Completed;

        const auto folded = Lower(normalized);
        if (folded.find("fan") != std::string::npos)
            return CareerGoalKind::Fans;

        if (ParseRaceCountLeft(normalized).has_value()
            && ParseRaceGrade(normalized).has_value())
            return CareerGoalKind::GradeRaceCount;

        static const std::regex runInPattern(
            R"(\brun[[:space:]]+in\b)", std::regex::icase);
        if (folded.find("race") != std::string::npos
            || folded.find("place") != std::string::npos
            || folded.find("win") != std::string::npos
            || folded.find("participat") != std::string::npos
            || std::regex_search(normalized, runInPattern)
            || folded.find("g1") != std::string::npos
            || folded.find("g2") != std::string::npos
            || folded.find("g3") != std::string::npos
            || folded.find("pre-op") != std::string::npos)
            return CareerGoalKind::Race;

        return CareerGoalKind::Unknown;
    }

private:
    [[nodiscard]] static std::string Trim(std::string_view value)
    {
        auto first = value.begin();
        auto last = value.end();
        while (first != last && std::isspace(static_cast<unsigned char>(*first))) ++first;
        while (first != last && std::isspace(static_cast<unsigned char>(*(last - 1)))) --last;
        return std::string(first, last);
    }

    [[nodiscard]] static std::string Lower(std::string value)
    {
        std::transform(value.begin(), value.end(), value.begin(), [](unsigned char character) {
            return static_cast<char>(std::tolower(character));
        });
        return value;
    }

    [[nodiscard]] static bool ParseInteger(const std::string& value, int& result) noexcept
    {
        if (value.empty())
            return false;
        const auto parsed = std::from_chars(value.data(), value.data() + value.size(), result);
        return parsed.ec == std::errc{} && parsed.ptr == value.data() + value.size();
    }
};

} // namespace UmaAssistant::CareerDomain
