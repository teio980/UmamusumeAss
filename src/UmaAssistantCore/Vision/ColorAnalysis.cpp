#include "ColorAnalysis.hpp"
#include <algorithm>
#include <cmath>
#include <limits>
#include <stdexcept>

namespace UmaAssistant::Vision {
namespace {
void validate(GrayImage const& image)
{
    if (image.width <= 0 || image.height <= 0 || image.width > 10000 || image.height > 10000
        || image.rgba.size() != static_cast<std::size_t>(image.width) * image.height * 4)
        throw std::invalid_argument("Color analysis requires straight RGBA pixels");
}
bool accepts(GrayImage const& image, std::size_t index, UmaColorRule const& rule)
{
    int const r = image.rgba[index * 4], g = image.rgba[index * 4 + 1], b = image.rgba[index * 4 + 2];
    bool const rgb = r >= rule.minimum_red && r <= rule.maximum_red
        && g >= rule.minimum_green && g <= rule.maximum_green
        && b >= rule.minimum_blue && b <= rule.maximum_blue
        && r - g >= rule.minimum_red_green && r - g <= rule.maximum_red_green
        && g - b >= rule.minimum_green_blue && g - b <= rule.maximum_green_blue;
    if (!rgb || rule.use_hsv == 0) return rgb;
    double const red = r / 255.0, green = g / 255.0, blue = b / 255.0;
    double const maximum = std::max(red, std::max(green, blue)), minimum = std::min(red, std::min(green, blue));
    double const delta = maximum - minimum;
    double const saturation = maximum <= 0 ? 0 : delta / maximum;
    double hue = 0;
    if (delta > std::numeric_limits<double>::denorm_min())
    {
        hue = maximum == red ? 60.0 * std::fmod((green - blue) / delta, 6.0)
            : maximum == green ? 60.0 * ((blue - red) / delta + 2.0)
            : 60.0 * ((red - green) / delta + 4.0);
        hue = std::fmod(hue, 360.0);
        if (hue < 0) hue += 360.0;
    }
    bool const hue_matches = rule.hue_min <= rule.hue_max
        ? hue >= rule.hue_min && hue <= rule.hue_max : hue >= rule.hue_min || hue <= rule.hue_max;
    return hue_matches && saturation >= rule.saturation_min && saturation <= rule.saturation_max
        && maximum >= rule.value_min && maximum <= rule.value_max;
}
UmaImageRect clipped(GrayImage const& image, UmaImageRect const& region)
{
    auto const x = std::clamp(region.x, 0, image.width), y = std::clamp(region.y, 0, image.height);
    auto const right = std::clamp(static_cast<std::int64_t>(region.x) + region.width,
        static_cast<std::int64_t>(x), static_cast<std::int64_t>(image.width));
    auto const bottom = std::clamp(static_cast<std::int64_t>(region.y) + region.height,
        static_cast<std::int64_t>(y), static_cast<std::int64_t>(image.height));
    return {x, y, static_cast<int>(right - x), static_cast<int>(bottom - y)};
}
bool contains(UmaImageRect const& r, int x, int y)
{
    return x >= r.x && y >= r.y && static_cast<std::int64_t>(x) < static_cast<std::int64_t>(r.x) + r.width
        && static_cast<std::int64_t>(y) < static_cast<std::int64_t>(r.y) + r.height;
}
}
GrayImage binarize_color(GrayImage const& image, UmaColorRule const& rule)
{
    validate(image);
    GrayImage result{image.width, image.height, {}, image.rgba};
    result.gray.resize(static_cast<std::size_t>(image.width) * image.height);
    for (std::size_t index = 0; index < result.gray.size(); ++index)
    {
        auto const value = static_cast<std::uint8_t>(accepts(image, index, rule) ? 0 : 255);
        result.gray[index] = value;
        result.rgba[index * 4] = value; result.rgba[index * 4 + 1] = value;
        result.rgba[index * 4 + 2] = value; result.rgba[index * 4 + 3] = 255;
    }
    return result;
}
std::vector<UmaColorBlob> find_color_blobs(GrayImage const& image, UmaColorRule const& rule, UmaImageRect const& region)
{
    validate(image);
    auto const bounds = clipped(image, region);
    std::vector<UmaColorBlob> result;
    if (bounds.width == 0 || bounds.height == 0) return result;
    std::vector<std::uint8_t> visited(static_cast<std::size_t>(bounds.width) * bounds.height);
    std::vector<std::pair<int, int>> pending;
    for (int y = bounds.y; y < bounds.y + bounds.height; ++y)
        for (int x = bounds.x; x < bounds.x + bounds.width; ++x)
        {
            auto add = [&](int px, int py)
            {
                if (!contains(bounds, px, py)) return;
                auto const at = static_cast<std::size_t>(py - bounds.y) * bounds.width + px - bounds.x;
                if (visited[at] != 0) return;
                visited[at] = 1;
                if (accepts(image, static_cast<std::size_t>(py) * image.width + px, rule)) pending.emplace_back(px, py);
            };
            pending.clear();
            add(x, y);
            if (pending.empty()) continue;
            int min_x = x, max_x = x, min_y = y, max_y = y;
            for (std::size_t read = 0; read < pending.size(); ++read)
            {
                auto const [px, py] = pending[read];
                min_x = std::min(min_x, px); max_x = std::max(max_x, px);
                min_y = std::min(min_y, py); max_y = std::max(max_y, py);
                add(px - 1, py); add(px + 1, py); add(px, py - 1); add(px, py + 1);
                add(px - 1, py - 1); add(px + 1, py - 1); add(px - 1, py + 1); add(px + 1, py + 1);
            }
            result.push_back({static_cast<int>(pending.size()), min_x, min_y, max_x - min_x + 1, max_y - min_y + 1});
        }
    return result;
}
std::pair<std::uint64_t, std::uint64_t> count_color(GrayImage const& image, UmaColorRule const& rule,
    UmaImageRect const& region, UmaImageRect const* exclude)
{
    validate(image);
    auto const bounds = clipped(image, region);
    std::uint64_t matching = 0, sampled = 0;
    int const step = std::clamp(rule.sample_step, 1, 10000);
    for (int y = bounds.y; y < bounds.y + bounds.height; y += step)
        for (int x = bounds.x; x < bounds.x + bounds.width; x += step)
        {
            if (exclude != nullptr && contains(*exclude, x, y)) continue;
            ++sampled;
            if (accepts(image, static_cast<std::size_t>(y) * image.width + x, rule)) ++matching;
        }
    return {matching, sampled};
}
}
