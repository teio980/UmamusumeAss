#include "NumericImageProcessing.hpp"
#include <algorithm>
#include <stdexcept>

namespace UmaAssistant::Vision {
namespace {
struct Component { int left, top, right, bottom; std::vector<int> pixels; };
void make_rgba(GrayImage& image)
{
    image.rgba.resize(image.gray.size() * 4);
    for (std::size_t i = 0; i < image.gray.size(); ++i)
    {
        image.rgba[i * 4] = image.gray[i]; image.rgba[i * 4 + 1] = image.gray[i];
        image.rgba[i * 4 + 2] = image.gray[i]; image.rgba[i * 4 + 3] = 255;
    }
}
bool cross_strokes(Component const& c, int width)
{
    std::vector<int> rows(static_cast<std::size_t>(c.bottom - c.top));
    std::vector<int> columns(static_cast<std::size_t>(c.right - c.left));
    for (int p : c.pixels) { ++rows[p / width - c.top]; ++columns[p % width - c.left]; }
    return *std::max_element(rows.begin(), rows.end()) >= static_cast<double>(columns.size()) * .8
        && *std::max_element(columns.begin(), columns.end()) >= static_cast<double>(rows.size()) * .8;
}
void restore_interiors(GrayImage& result, GrayImage const& source, int min_x, int min_y, int padding)
{
    std::vector<std::uint8_t> visited(result.gray.size());
    for (int p = 0; p < static_cast<int>(result.gray.size()); ++p)
    {
        if (visited[p] || result.gray[p] != 255) continue;
        std::vector<int> hole{p}; bool exterior = false; visited[p] = 1;
        auto visit = [&](int x, int y) {
            if (x < 0 || y < 0 || x >= result.width || y >= result.height) return;
            int const i = y * result.width + x;
            if (visited[i] || result.gray[i] != 255) return;
            visited[i] = 1; hole.push_back(i);
        };
        for (std::size_t read = 0; read < hole.size(); ++read)
        {
            int const x = hole[read] % result.width, y = hole[read] / result.width;
            exterior |= x == 0 || y == 0 || x == result.width - 1 || y == result.height - 1;
            visit(x - 1, y); visit(x + 1, y); visit(x, y - 1); visit(x, y + 1);
        }
        if (exterior) continue;
        for (int i : hole)
        {
            int const x = i % result.width + min_x - padding, y = i / result.width + min_y - padding;
            if (x < 0 || y < 0 || x >= source.width || y >= source.height) continue;
            auto const at = (static_cast<std::size_t>(y) * source.width + x) * 4;
            if (source.rgba[at] >= 235 && source.rgba[at + 1] >= 225) result.gray[i] = 0;
        }
    }
}
}

std::optional<GrayImage> prepare_numeric_glyphs(GrayImage const& image, bool gain, UmaColorRule const& seed_rule)
{
    if (image.rgba.empty()) return image;
    auto const seed_mask = binarize_color(image, seed_rule);
    std::vector<UmaImageRect> blue_rows(static_cast<std::size_t>(image.height));
    int blue_count = 0;
    if (!gain)
        for (int y = 0; y < image.height; ++y)
        {
            int left = image.width, right = -1, count = 0;
            for (int x = 0; x < image.width; ++x)
            {
                auto const at = (static_cast<std::size_t>(y) * image.width + x) * 4;
                int const r = image.rgba[at], g = image.rgba[at + 1], b = image.rgba[at + 2];
                if (b <= r + 60 || b <= g + 15 || g < 60) continue;
                left = std::min(left, x); right = x; ++count;
            }
            if (count >= image.width * .35) { blue_rows[y] = {left, y, right - left + 1, 1}; ++blue_count; }
        }
    bool const has_blue_body = blue_count >= image.height * .25;
    std::vector<std::uint8_t> foreground(image.gray.size());
    for (std::size_t p = 0; p < foreground.size(); ++p)
    {
        int const r = image.rgba[p * 4], g = image.rgba[p * 4 + 1], b = image.rgba[p * 4 + 2];
        auto const body = blue_rows[p / image.width]; int const x = static_cast<int>(p % image.width);
        bool const inside = !has_blue_body || (body.width > 0 && x >= body.x && x < body.x + body.width);
        foreground[p] = static_cast<std::uint8_t>(gain
            ? r >= 150 && g >= 30 && g <= 245 && b <= 230 && r >= g + 15 && g >= b + 10
            : inside && ((r >= 220 && g >= 220 && b >= 220) || (r >= 220 && g >= 180 && b <= 100)));
    }
    std::vector<Component> components;
    for (int p = 0; p < static_cast<int>(foreground.size()); ++p)
    {
        if (!foreground[p]) continue;
        Component c{image.width, image.height, 0, 0, {p}}; foreground[p] = 0; int seeds = 0;
        auto visit = [&](int x, int y) {
            if (x < 0 || y < 0 || x >= image.width || y >= image.height) return;
            int const i = y * image.width + x;
            if (!foreground[i]) return;
            foreground[i] = 0; c.pixels.push_back(i);
        };
        for (std::size_t read = 0; read < c.pixels.size(); ++read)
        {
            int const i = c.pixels[read], x = i % image.width, y = i / image.width;
            c.left = std::min(c.left, x); c.top = std::min(c.top, y);
            c.right = std::max(c.right, x + 1); c.bottom = std::max(c.bottom, y + 1);
            if (seed_mask.gray[i] == 0) ++seeds;
            visit(x - 1, y); visit(x + 1, y); visit(x, y - 1); visit(x, y + 1);
        }
        if (!gain || (seeds >= std::max(3, static_cast<int>(c.pixels.size() / 50))
            && c.left > 0 && c.top > 0 && c.right < image.width && c.bottom < image.height
            && c.bottom - c.top >= image.height * .3)) components.push_back(std::move(c));
    }
    if (gain && !components.empty())
    {
        int plus_left = image.width;
        for (auto const& c : components)
        {
            double const ratio = (c.right - c.left) / static_cast<double>(c.bottom - c.top);
            if (ratio >= .9 && ratio <= 1.2 && c.bottom - c.top < image.height * .6
                && cross_strokes(c, image.width)) plus_left = std::min(plus_left, c.left);
        }
        if (plus_left == image.width) return std::nullopt;
        std::erase_if(components, [plus_left](auto const& c) { return c.left < plus_left; });
    }
    else if (!gain && !components.empty())
    {
        std::erase_if(components, [&](auto const& c) {
            double const center = (c.left + c.right) / 2.0;
            return c.left <= 0 || c.top <= 0 || c.right >= image.width || c.bottom >= image.height
                || (!has_blue_body && (center < image.width * .25 || center > image.width * .75));
        });
        int bottom = -1;
        for (auto const& c : components)
            if (c.bottom - c.top >= image.height * .15 && c.right - c.left >= image.width * .025)
                bottom = std::max(bottom, c.bottom);
        if (bottom < 0) return std::nullopt;
        std::erase_if(components, [&](auto const& c) { return c.bottom < bottom - image.height * .2 || c.top > bottom; });
    }
    int min_x = image.width, min_y = image.height, max_x = 0, max_y = 0;
    if (components.empty()) { min_x = min_y = 0; max_x = image.width; max_y = image.height; }
    for (auto const& c : components)
    {
        min_x = std::min(min_x, c.left); min_y = std::min(min_y, c.top);
        max_x = std::max(max_x, c.right); max_y = std::max(max_y, c.bottom);
    }
    int const padding = std::max(6, image.height / 6);
    int const width = max_x - min_x + padding * 2, height = max_y - min_y + padding * 2;
    if (width > 10000 || height > 10000) throw std::invalid_argument("Prepared image is too large");
    GrayImage result{width, height, std::vector<std::uint8_t>(static_cast<std::size_t>(width) * height, 255), {}};
    for (auto const& c : components)
        for (int p : c.pixels) result.gray[(p / image.width - min_y + padding) * width
            + p % image.width - min_x + padding] = 0;
    if (gain) restore_interiors(result, image, min_x, min_y, padding);
    make_rgba(result);
    return result;
}

GrayImage compose_gray(std::span<GrayImage const> images, std::span<UmaImageRect const> placements, int width, int height)
{
    GrayImage result{width, height, std::vector<std::uint8_t>(static_cast<std::size_t>(width) * height, 255), {}};
    for (std::size_t i = 0; i < images.size(); ++i)
    {
        auto const& image = images[i]; auto const p = placements[i];
        for (int y = 0; y < image.height; ++y)
            std::copy_n(image.gray.data() + static_cast<std::size_t>(y) * image.width, image.width,
                result.gray.data() + static_cast<std::size_t>(p.y + y) * width + p.x);
    }
    make_rgba(result);
    return result;
}
int count_ink_groups(GrayImage const& image, int threshold)
{
    int groups = 0; bool inside = false;
    for (int x = 0; x < image.width; ++x)
    {
        bool ink = false;
        for (int y = 0; y < image.height; ++y)
            if (image.gray[static_cast<std::size_t>(y) * image.width + x] < threshold) { ink = true; break; }
        if (ink && !inside) ++groups;
        inside = ink;
    }
    return groups;
}
std::uint64_t sample_difference(GrayImage const& before, GrayImage const& after, std::span<UmaImagePoint const> points)
{
    std::uint64_t sum = 0;
    for (auto const p : points)
    {
        auto const i = static_cast<std::size_t>(p.y) * before.width + p.x;
        sum += static_cast<std::uint64_t>(std::abs(static_cast<int>(before.gray[i]) - after.gray[i]));
    }
    return sum;
}
}
