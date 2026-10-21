#include "CareerObservationReader.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cctype>
#include <regex>
#include <string_view>
#include <unordered_map>

namespace UmaAssistant::Career
{
namespace
{
int ScaleCoordinate(std::int64_t coordinate, int reference, int actual)
{
    double const scaled = static_cast<double>(coordinate) * std::max(1, actual) / std::max(1, reference);
    // CareerEnergyBarReader explicitly uses AwayFromZero, unlike template matching.
    return static_cast<int>(std::round(std::clamp(scaled, 0.0, static_cast<double>(actual - 1))));
}

std::string Lower(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });
    return value;
}

double FiniteOr(double value, double fallback, double lower = 0.0, double upper = 1.0)
{
    return std::clamp(std::isfinite(value) ? value : fallback, lower, upper);
}
}

std::optional<EnergyBarMeasurement> CareerObservationReader::MeasureEnergyBar(
    const uint8_t* pixels, int imageWidth, int imageHeight,
    const std::vector<int>& roi, const std::vector<int>& innerInset,
    int referenceWidth, int referenceHeight,
    double fillSaturationMin, double fillValueMin, double columnMatchRatio,
    std::size_t pixelBytes)
{
    if (!pixels || roi.size() < 4 || imageWidth <= 0 || imageHeight <= 0
        || imageWidth > 10000 || imageHeight > 10000)
        return {};
    auto const requiredBytes = static_cast<std::size_t>(imageWidth) * static_cast<std::size_t>(imageHeight) * 4;
    if (pixelBytes != 0 && pixelBytes < requiredBytes) return {};
    auto const inset = innerInset.size() >= 4 ? innerInset : std::vector<int>{0, 0, roi[2], roi[3]};
    auto const x = static_cast<std::int64_t>(roi[0]) + inset[0];
    auto const y = static_cast<std::int64_t>(roi[1]) + inset[1];
    int const left = ScaleCoordinate(x, referenceWidth, imageWidth);
    int const top = ScaleCoordinate(y, referenceHeight, imageHeight);
    int const right = std::max(left, ScaleCoordinate(x + std::max(1, inset[2]) - 1, referenceWidth, imageWidth));
    int const bottom = std::max(top, ScaleCoordinate(y + std::max(1, inset[3]) - 1, referenceHeight, imageHeight));
    int const width = right - left + 1;
    int const height = bottom - top + 1;
    if (width < 8 || height < 3) return {};

    fillSaturationMin = FiniteOr(fillSaturationMin, 0.18);
    fillValueMin = FiniteOr(fillValueMin, 0.18);
    columnMatchRatio = FiniteOr(columnMatchRatio, 0.55, 0.1);
    std::vector<double> fillScores(static_cast<std::size_t>(width));
    std::vector<double> neutralScores(static_cast<std::size_t>(width));
    for (int column = 0; column < width; ++column)
    {
        int filled = 0;
        int neutral = 0;
        for (int row = top; row <= bottom; ++row)
        {
            auto const offset = (static_cast<std::size_t>(row) * static_cast<std::size_t>(imageWidth)
                + static_cast<std::size_t>(left + column)) * 4;
            double const r = pixels[offset] / 255.0;
            double const g = pixels[offset + 1] / 255.0;
            double const b = pixels[offset + 2] / 255.0;
            double const maximum = std::max({r, g, b});
            double const minimum = std::min({r, g, b});
            double const saturation = maximum <= 0 ? 0 : (maximum - minimum) / maximum;
            if (saturation >= fillSaturationMin && maximum >= fillValueMin) ++filled;
            if (saturation <= 0.12 && maximum >= 0.12 && maximum <= 0.92) ++neutral;
        }
        fillScores[static_cast<std::size_t>(column)] = static_cast<double>(filled) / height;
        neutralScores[static_cast<std::size_t>(column)] = static_cast<double>(neutral) / height;
    }

    int firstFilled = -1;
    int consecutive = 0;
    for (int column = 0; column < width; ++column)
    {
        consecutive = fillScores[static_cast<std::size_t>(column)] >= columnMatchRatio ? consecutive + 1 : 0;
        if (consecutive >= 3) { firstFilled = column - 2; break; }
    }
    if (firstFilled < 0)
    {
        auto const neutralColumns = std::count_if(neutralScores.begin(), neutralScores.end(), [](double score) { return score >= 0.55; });
        if (neutralColumns >= width * 0.80) return EnergyBarMeasurement{0, 0.75};
        return {};
    }
    int end = firstFilled;
    int misses = 0;
    double const relaxed = std::max(0.35, columnMatchRatio * 0.70);
    for (int column = firstFilled; column < width; ++column)
    {
        if (fillScores[static_cast<std::size_t>(column)] >= relaxed) { end = column + 1; misses = 0; }
        else if (++misses >= 3) break;
    }
    int const filledWidth = std::clamp(end - firstFilled, 0, width);
    int const percent = std::clamp(static_cast<int>(std::round(static_cast<double>(filledWidth) / width * 100.0)), 0, 100);
    double sum = 0;
    for (int column = firstFilled; column < end; ++column) sum += fillScores[static_cast<std::size_t>(column)];
    double const fillConfidence = filledWidth > 0 ? sum / filledWidth : 0;
    double const boundary = end >= width ? 1 : std::clamp(
        neutralScores[static_cast<std::size_t>(end)] - fillScores[static_cast<std::size_t>(end)], 0.0, 1.0);
    return EnergyBarMeasurement{percent, std::clamp(fillConfidence * 0.75 + boundary * 0.25, 0.0, 1.0)};
}

std::optional<std::string> CareerObservationReader::ParseMood(const std::string& text)
{
    std::string letters;
    for (unsigned char c : text)
        if (std::isalpha(c)) letters.push_back(static_cast<char>(std::tolower(c)));
    // Preserve the old parser's order when OCR accidentally joins two labels.
    for (auto const* mood : {"awful", "bad", "normal", "good", "great"})
        if (letters.find(mood) != std::string::npos) return mood;
    return {};
}

std::optional<ParsedTurnPosition> CareerObservationReader::ParseTurnPosition(const std::string& text)
{
    static std::regex const typos(R"(\b(?:Jui|Iul|Juiy)\b)", std::regex::icase);
    static std::regex const nonLetters("[^A-Za-z]+");
    static std::regex const spaces("\\s+");
    static std::regex const preDebut(R"(\bJunior\s*Year\s+Pre\s*Debut\b)", std::regex::icase);
    static std::regex const dated(R"(\b(Junior|Classic|Senior)\s*Year\s+(Early|Late)\s+([A-Za-z]+)\b)", std::regex::icase);
    auto normalized = std::regex_replace(text, typos, "Jul");
    normalized = std::regex_replace(normalized, nonLetters, " ");
    normalized = std::regex_replace(normalized, spaces, " ");
    if (std::regex_search(normalized, preDebut))
        return ParsedTurnPosition{"Junior Year Pre-Debut", 0, "junior", "pre-debut", {}};
    std::smatch match;
    if (!std::regex_search(normalized, match, dated)) return {};
    static std::unordered_map<std::string, int> const months = {
        {"jan",1},{"january",1},{"feb",2},{"february",2},{"mar",3},{"march",3},
        {"apr",4},{"april",4},{"may",5},{"jun",6},{"june",6},{"jul",7},{"july",7},
        {"aug",8},{"august",8},{"sep",9},{"sept",9},{"september",9},
        {"oct",10},{"october",10},{"nov",11},{"november",11},{"dec",12},{"december",12}};
    auto const year = Lower(match[1].str());
    auto const phase = Lower(match[2].str());
    auto const monthToken = match[3].str();
    auto const month = months.find(Lower(monthToken));
    if (month == months.end()) return {};
    int const offset = year == "senior" ? 48 : year == "classic" ? 24 : 0;
    auto yearLabel = year;
    auto phaseLabel = phase;
    yearLabel[0] = static_cast<char>(std::toupper(static_cast<unsigned char>(yearLabel[0])));
    phaseLabel[0] = static_cast<char>(std::toupper(static_cast<unsigned char>(phaseLabel[0])));
    return ParsedTurnPosition{yearLabel + " Year " + phaseLabel + " " + monthToken.substr(0, 3),
        1 + offset + (month->second - 1) * 2 + (phase == "late" ? 1 : 0), year, phase, month->second};
}

} // namespace UmaAssistant::Career
