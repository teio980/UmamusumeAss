#include "TemplateMatcher.hpp"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstddef>
#include <limits>
#include <thread>
#include <utility>
#include <vector>

namespace UmaAssistant::Vision {
namespace {

struct Bounds { int x, y, width, height; };

[[nodiscard]] int round_even(double value) noexcept
{
    if (std::isnan(value)) return 0;
    value = std::clamp(value,
        static_cast<double>((std::numeric_limits<int>::min)()),
        static_cast<double>((std::numeric_limits<int>::max)()));
    auto const base = std::floor(value);
    auto const fraction = value - base;
    if (fraction > 0.5 || (fraction == 0.5 && std::fmod(base, 2.0) != 0.0))
        return static_cast<int>(base + 1.0);
    return static_cast<int>(base);
}

[[nodiscard]] int ceil_to_int(double value) noexcept
{
    if (std::isnan(value)) return 0;
    value = std::ceil(value);
    value = std::clamp(value,
        static_cast<double>((std::numeric_limits<int>::min)()),
        static_cast<double>((std::numeric_limits<int>::max)()));
    return static_cast<int>(value);
}

[[nodiscard]] int scale_coordinate(int value, int actual, int reference) noexcept
{
    return round_even(value * static_cast<double>((std::max)(1, actual))
        / (std::max)(1, reference));
}

[[nodiscard]] Bounds scale_roi(Roi const& roi, int width, int height, int ref_width, int ref_height)
{
    if (!roi) return {0, 0, width, height};
    auto x = scale_coordinate((*roi)[0], width, ref_width);
    auto y = scale_coordinate((*roi)[1], height, ref_height);
    auto roi_width = (std::max)(1, scale_coordinate((*roi)[2], width, ref_width));
    auto roi_height = (std::max)(1, scale_coordinate((*roi)[3], height, ref_height));
    x = std::clamp(x, 0, width - 1);
    y = std::clamp(y, 0, height - 1);
    roi_width = (std::min)(roi_width, width - x);
    roi_height = (std::min)(roi_height, height - y);
    return {x, y, roi_width, roi_height};
}

[[nodiscard]] double clamp_score(double value) noexcept
{
    return std::clamp(value, -1.0, 1.0);
}

[[nodiscard]] double pearson(std::vector<double> const& first, std::vector<double> const& second)
{
    if (first.empty() || first.size() != second.size()) return 0;
    double first_total = 0, second_total = 0;
    for (std::size_t i = 0; i < first.size(); ++i)
    {
        first_total += first[i];
        second_total += second[i];
    }
    auto const first_mean = first_total / first.size();
    auto const second_mean = second_total / second.size();
    double numerator = 0, first_variance = 0, second_variance = 0;
    for (std::size_t i = 0; i < first.size(); ++i)
    {
        auto const a = first[i] - first_mean;
        auto const b = second[i] - second_mean;
        numerator += a * b;
        first_variance += a * a;
        second_variance += b * b;
    }
    if (first_variance < 1 || second_variance < 1) return 0;
    return clamp_score(numerator / std::sqrt(first_variance * second_variance));
}

[[nodiscard]] bool has_gray(GrayImage const& image)
{
    return image.width > 0 && image.height > 0
        && image.gray.size() == static_cast<std::size_t>(image.width) * image.height;
}

[[nodiscard]] bool has_rgba(GrayImage const& image)
{
    return image.width > 0 && image.height > 0
        && image.rgba.size() == static_cast<std::size_t>(image.width) * image.height * 4;
}

struct GrayComparison
{
    std::vector<std::uint8_t> pixels;
    std::vector<std::size_t> offsets;
    std::int64_t total{};
    double variance{};
};

[[nodiscard]] GrayComparison prepare_gray(
    GrayImage const& templ, int sample_width, int sample_height, int screen_width)
{
    GrayComparison out;
    out.pixels.reserve(static_cast<std::size_t>(sample_width) * sample_height);
    out.offsets.reserve(static_cast<std::size_t>(sample_width) * sample_height);
    std::int64_t square_total = 0;
    for (int sy = 0; sy < sample_height; ++sy)
    {
        auto const ty = sy * templ.height / sample_height;
        auto const screen_row = static_cast<std::size_t>(ty) * static_cast<std::size_t>(screen_width);
        for (int sx = 0; sx < sample_width; ++sx)
        {
            auto const tx = sx * templ.width / sample_width;
            out.offsets.push_back(screen_row + static_cast<std::size_t>(tx));
            auto const value = templ.gray[static_cast<std::size_t>(ty) * templ.width + tx];
            out.pixels.push_back(value);
            out.total += value;
            square_total += static_cast<std::int64_t>(value) * value;
        }
    }
    auto const count = static_cast<double>(out.pixels.size());
    out.variance = count == 0 ? 0 : square_total - out.total * (out.total / count);
    return out;
}

[[nodiscard]] double compare_gray(
    GrayImage const& screen, int x, int y, GrayComparison const& comparison)
{
    auto const count = comparison.pixels.size();
    if (count == 0) return 0;
    std::int64_t absolute_error = 0;
    auto const screen_base = static_cast<std::size_t>(y) * static_cast<std::size_t>(screen.width)
        + static_cast<std::size_t>(x);
    if (comparison.variance < 1)
    {
        for (std::size_t index = 0; index < count; ++index)
            absolute_error += std::abs(static_cast<int>(screen.gray[screen_base + comparison.offsets[index]])
                - static_cast<int>(comparison.pixels[index]));
        return 1.0 - absolute_error / (count * 255.0);
    }

    std::int64_t screen_total = 0, square_total = 0, cross_total = 0;
    for (std::size_t index = 0; index < count; ++index)
    {
        auto const templ_value = comparison.pixels[index];
        auto const screen_value = screen.gray[screen_base + comparison.offsets[index]];
        screen_total += screen_value;
        square_total += static_cast<std::int64_t>(screen_value) * screen_value;
        cross_total += static_cast<std::int64_t>(templ_value) * screen_value;
    }
    auto const screen_mean = screen_total / static_cast<double>(count);
    auto const screen_variance = square_total - screen_total * screen_mean;
    if (screen_variance < 1)
    {
        for (std::size_t index = 0; index < count; ++index)
            absolute_error += std::abs(static_cast<int>(screen.gray[screen_base + comparison.offsets[index]])
                - static_cast<int>(comparison.pixels[index]));
        return 1.0 - absolute_error / (count * 255.0);
    }
    auto const numerator = cross_total - comparison.total * screen_mean;
    return clamp_score(numerator / std::sqrt(comparison.variance * screen_variance));
}

[[nodiscard]] MatchResult find_at_size(
    GrayImage const& screen, GrayImage const& templ, Bounds bounds,
    int target_width, int target_height, int candidate_step, int sample_width, int sample_height)
{
    auto const max_x = (std::min)(screen.width - target_width, bounds.x + bounds.width - target_width);
    auto const max_y = (std::min)(screen.height - target_height, bounds.y + bounds.height - target_height);
    if (bounds.x > max_x || bounds.y > max_y)
        return {false, std::numeric_limits<double>::lowest(), 0, 0, target_width, target_height};
    sample_width = std::clamp(sample_width, 1, (std::min)({32, templ.width, target_width}));
    sample_height = std::clamp(sample_height, 1, (std::min)({32, templ.height, target_height}));
    candidate_step = std::clamp(candidate_step, 1, 32);
    candidate_step = (std::min)(candidate_step, (std::max)(1, (std::min)(target_width, target_height) / 2));

    auto const sample_count = static_cast<std::size_t>(sample_width) * sample_height;
    auto const gradient_count = static_cast<std::size_t>((std::max)(0, sample_width - 2))
        * (std::max)(0, sample_height - 2);
    std::vector<double> template_values(sample_count), screen_values(sample_count);
    std::vector<double> template_gx(gradient_count), template_gy(gradient_count);
    std::vector<double> screen_gx(gradient_count), screen_gy(gradient_count);
    auto compare = [&](int x, int y) {
        auto compare_region = [&](double left, double right, double top, double bottom) {
            std::size_t sample_index = 0;
            for (int sy = 0; sy < sample_height; ++sy)
            {
                auto const relative_y = top + sy * (bottom - top) / sample_height;
                auto const ty = (std::min)(templ.height - 1, (std::max)(0, round_even(relative_y * templ.height)));
                auto const iy = y + (std::min)(target_height - 1, (std::max)(0, round_even(relative_y * target_height)));
                for (int sx = 0; sx < sample_width; ++sx)
                {
                    auto const relative_x = left + sx * (right - left) / sample_width;
                    auto const tx = (std::min)(templ.width - 1, (std::max)(0, round_even(relative_x * templ.width)));
                    auto const ix = x + (std::min)(target_width - 1, (std::max)(0, round_even(relative_x * target_width)));
                    template_values[sample_index] = templ.gray[static_cast<std::size_t>(ty) * templ.width + tx];
                    screen_values[sample_index] = screen.gray[static_cast<std::size_t>(iy) * screen.width + ix];
                    ++sample_index;
                }
            }
            auto const intensity = pearson(template_values, screen_values);
            if (sample_width < 3 || sample_height < 3) return intensity;
            std::size_t gradient_index = 0;
            for (int py = 1; py < sample_height - 1; ++py)
                for (int px = 1; px < sample_width - 1; ++px)
                {
                    auto const center = static_cast<std::size_t>(py) * sample_width + px;
                    template_gx[gradient_index] = template_values[center + 1] - template_values[center - 1];
                    template_gy[gradient_index] = template_values[center + sample_width] - template_values[center - sample_width];
                    screen_gx[gradient_index] = screen_values[center + 1] - screen_values[center - 1];
                    screen_gy[gradient_index] = screen_values[center + sample_width] - screen_values[center - sample_width];
                    ++gradient_index;
                }
            auto const edge = (pearson(template_gx, screen_gx)
                + pearson(template_gy, screen_gy)) / 2.0;
            return clamp_score(intensity * 0.75 + edge * 0.25);
        };
        auto const full = compare_region(0, 1, 0, 1);
        auto const face = compare_region(0.10, 0.90, 0.08, 0.92);
        return clamp_score(full * 0.85 + face * 0.15);
    };

    double best = std::numeric_limits<double>::lowest();
    int best_x = bounds.x, best_y = bounds.y;
    for (int y = bounds.y; y <= max_y; y += candidate_step)
        for (int x = bounds.x; x <= max_x; x += candidate_step)
        {
            auto const score = compare(x, y);
            if (score > best) { best = score; best_x = x; best_y = y; }
        }
    auto const min_x = (std::max)(bounds.x, best_x - candidate_step + 1);
    auto const max_refine_x = (std::min)(max_x, best_x + candidate_step - 1);
    auto const min_y = (std::max)(bounds.y, best_y - candidate_step + 1);
    auto const max_refine_y = (std::min)(max_y, best_y + candidate_step - 1);
    for (int y = min_y; y <= max_refine_y; ++y)
        for (int x = min_x; x <= max_refine_x; ++x)
        {
            auto const score = compare(x, y);
            if (score > best) { best = score; best_x = x; best_y = y; }
        }
    return {false, best, best_x, best_y, target_width, target_height};
}

struct ButtonFeature { int x, y; std::uint8_t center; int dx, dy; bool dark; };
struct ButtonBackground { int x, y; std::uint8_t center; };
struct ButtonInk { int x, y; bool dark; };

[[nodiscard]] std::vector<ButtonFeature> button_features(GrayImage const& t, int sw, int sh)
{
    std::vector<ButtonFeature> out;
    for (int sy = 1; sy < sh - 1; ++sy)
    {
        auto const y = std::clamp(sy * t.height / sh, 1, t.height - 2);
        for (int sx = 1; sx < sw - 1; ++sx)
        {
            auto const x = std::clamp(sx * t.width / sw, 1, t.width - 2);
            auto const center = t.gray[static_cast<std::size_t>(y) * t.width + x];
            auto const dx = static_cast<int>(t.gray[static_cast<std::size_t>(y) * t.width + x + 1])
                - static_cast<int>(t.gray[static_cast<std::size_t>(y) * t.width + x - 1]);
            auto const dy = static_cast<int>(t.gray[static_cast<std::size_t>(y + 1) * t.width + x])
                - static_cast<int>(t.gray[static_cast<std::size_t>(y - 1) * t.width + x]);
            if (std::abs(dx) + std::abs(dy) >= 16)
                out.push_back({x, y, center, dx, dy, center <= 130});
        }
    }
    return out;
}

[[nodiscard]] std::vector<ButtonBackground> button_background(GrayImage const& t)
{
    std::vector<ButtonBackground> out;
    auto const top_end = (std::min)(6, t.height);
    auto const bottom_start = (std::max)(0, t.height - 6);
    for (int y = 1; y < top_end; ++y)
        for (int x = 3; x < t.width - 3; x += 3)
        {
            auto const value = t.gray[static_cast<std::size_t>(y) * t.width + x];
            if (value >= 200) out.push_back({x, y, value});
        }
    for (int y = bottom_start; y < t.height - 1; ++y)
        for (int x = 3; x < t.width - 3; x += 3)
        {
            auto const value = t.gray[static_cast<std::size_t>(y) * t.width + x];
            if (value >= 200) out.push_back({x, y, value});
        }
    return out;
}

[[nodiscard]] std::vector<ButtonInk> button_ink(GrayImage const& t)
{
    std::vector<ButtonInk> out;
    out.reserve(static_cast<std::size_t>(t.width) * t.height);
    for (int y = 0; y < t.height; ++y)
        for (int x = 0; x < t.width; ++x)
        {
            auto const dark = t.gray[static_cast<std::size_t>(y) * t.width + x] <= 130;
            if (dark || ((x & 1) == 0 && (y & 1) == 0)) out.push_back({x, y, dark});
        }
    return out;
}

[[nodiscard]] double compare_button(
    GrayImage const& screen, int x, int y,
    std::vector<ButtonFeature> const& features, int min_dark,
    std::vector<ButtonBackground> const& background, int min_bright,
    std::vector<ButtonInk> const& ink)
{
    double edge_error = 0, intensity_error = 0;
    int dark_count = 0, dark_total = 0;
    for (auto const& f : features)
    {
        auto const center = screen.gray[static_cast<std::size_t>(y + f.y) * screen.width + x + f.x];
        auto const dx = static_cast<int>(screen.gray[static_cast<std::size_t>(y + f.y) * screen.width + x + f.x + 1])
            - static_cast<int>(screen.gray[static_cast<std::size_t>(y + f.y) * screen.width + x + f.x - 1]);
        auto const dy = static_cast<int>(screen.gray[static_cast<std::size_t>(y + f.y + 1) * screen.width + x + f.x])
            - static_cast<int>(screen.gray[static_cast<std::size_t>(y + f.y - 1) * screen.width + x + f.x]);
        edge_error += (std::min)(510, std::abs(dx - f.dx) + std::abs(dy - f.dy));
        intensity_error += std::abs(static_cast<int>(center) - static_cast<int>(f.center));
        if (f.dark) { ++dark_total; if (center <= 130) ++dark_count; }
    }
    if (dark_count < min_dark) return std::numeric_limits<double>::lowest();
    int bright_count = 0;
    double background_error = 0;
    for (auto const& s : background)
    {
        auto const value = screen.gray[static_cast<std::size_t>(y + s.y) * screen.width + x + s.x];
        if (value >= 200) ++bright_count;
        background_error += std::abs(static_cast<int>(value) - static_cast<int>(s.center));
    }
    if (bright_count < min_bright) return std::numeric_limits<double>::lowest();
    int matched = 0, unexpected = 0, template_ink = 0;
    for (auto const& s : ink)
    {
        auto const dark = screen.gray[static_cast<std::size_t>(y + s.y) * screen.width + x + s.x] <= 130;
        if (s.dark) { ++template_ink; if (dark) ++matched; }
        else if (dark) ++unexpected;
    }
    auto const f1 = 2.0 * matched / (std::max)(1, 2 * matched + unexpected + template_ink - matched);
    if (f1 < 0.72) return std::numeric_limits<double>::lowest();
    auto const edge_score = 1.0 - edge_error / (features.size() * 510.0);
    auto const intensity_score = 1.0 - intensity_error / (features.size() * 255.0);
    auto const dark_coverage = std::clamp(dark_count / static_cast<double>((std::max)(1, dark_total)), 0.0, 1.0);
    auto const background_score = 1.0 - background_error / ((std::max)(1u, static_cast<unsigned>(background.size())) * 255.0);
    return clamp_score(edge_score * .50 + intensity_score * .15 + dark_coverage * .20
        + background_score * .10 + f1 * .05);
}

[[nodiscard]] MatchResult find_masked_size(
    GrayImage const& screen, GrayImage const& templ, Bounds bounds, int width, int height)
{
    auto const max_x = (std::min)(screen.width - width, bounds.x + bounds.width - width);
    auto const max_y = (std::min)(screen.height - height, bounds.y + bounds.height - height);
    if (bounds.x > max_x || bounds.y > max_y) return {false, std::numeric_limits<double>::lowest(), 0, 0, width, height};
    std::vector<double> template_samples;
    std::vector<double> screen_samples;
    template_samples.reserve(24u * 18u);
    screen_samples.reserve(24u * 18u);
    auto compare = [&](int x, int y) {
        template_samples.clear();
        screen_samples.clear();
        for (int sy = 0; sy < 24; ++sy)
        {
            auto const ty = (std::min)(templ.height - 1, sy * templ.height / 24);
            auto const py = y + (std::min)(height - 1, sy * height / 24);
            for (int sx = 0; sx < 24; ++sx)
            {
                if (sx >= 18) continue;
                auto const tx = (std::min)(templ.width - 1, sx * templ.width / 24);
                auto const ti = static_cast<std::size_t>(ty) * templ.width + tx;
                if (templ.rgba.size() < (ti + 1) * 4 || templ.rgba[ti * 4 + 3] < 48) continue;
                auto const px = x + (std::min)(width - 1, sx * width / 24);
                template_samples.push_back(templ.gray[ti]);
                screen_samples.push_back(screen.gray[static_cast<std::size_t>(py) * screen.width + px]);
            }
        }
        if (template_samples.size() < 32) return 0.0;
        return pearson(template_samples, screen_samples);
    };
    double best = std::numeric_limits<double>::lowest();
    int best_x = bounds.x, best_y = bounds.y;
    for (int y = bounds.y; y <= max_y; y += 4)
        for (int x = bounds.x; x <= max_x; x += 4)
        {
            auto const score = compare(x, y);
            if (score > best) { best = score; best_x = x; best_y = y; }
        }
    auto const refine_min_x = (std::max)(bounds.x, best_x - 3);
    auto const refine_max_x = (std::min)(max_x, best_x + 3);
    auto const refine_min_y = (std::max)(bounds.y, best_y - 3);
    auto const refine_max_y = (std::min)(max_y, best_y + 3);
    for (int y = refine_min_y; y <= refine_max_y; ++y)
        for (int x = refine_min_x; x <= refine_max_x; ++x)
        {
            auto const score = compare(x, y);
            if (score > best) { best = score; best_x = x; best_y = y; }
        }
    return {false, best, best_x, best_y, width, height};
}

}

MatchResult find(
    GrayImage const& screen, GrayImage const& templ, Roi roi, double threshold,
    std::int32_t reference_width, std::int32_t reference_height,
    std::optional<std::int32_t> candidate_step_override)
{
    if (!has_gray(screen) || !has_gray(templ) || templ.width > screen.width || templ.height > screen.height)
        return {false, 0, 0, 0, templ.width, templ.height};
    auto const bounds = scale_roi(roi, screen.width, screen.height, reference_width, reference_height);
    auto const max_x = (std::min)(screen.width - templ.width, bounds.x + bounds.width - templ.width);
    auto const max_y = (std::min)(screen.height - templ.height, bounds.y + bounds.height - templ.height);
    if (bounds.x > max_x || bounds.y > max_y) return {false, 0, 0, 0, templ.width, templ.height};
    auto const sample_width = templ.width <= 160 ? templ.width : 32;
    auto const sample_height = templ.height <= 80 ? templ.height : 32;
    auto step = candidate_step_override && *candidate_step_override > 0
        ? *candidate_step_override : (templ.width <= 160 ? 1 : 2);
    step = std::clamp(step, 1, 32);
    auto const comparison = prepare_gray(templ, sample_width, sample_height, screen.width);
    double best = std::numeric_limits<double>::lowest();
    int best_x = bounds.x, best_y = bounds.y;
    auto const candidates_per_row = static_cast<std::size_t>((max_x - bounds.x) / step + 1);
    auto const row_count = static_cast<std::size_t>((max_y - bounds.y) / step + 1);
    auto const estimated_comparisons = static_cast<long double>(candidates_per_row)
        * static_cast<long double>(row_count) * static_cast<long double>(comparison.pixels.size());
    auto const hardware_threads = std::thread::hardware_concurrency();
    auto const available_threads = hardware_threads == 0 ? 1u : hardware_threads;
    auto const worker_count = (std::min)({4u, available_threads, static_cast<unsigned>(
        (std::min)(row_count, static_cast<std::size_t>((std::numeric_limits<unsigned>::max)())))});

    if (estimated_comparisons >= 4'000'000.0L && worker_count > 1)
    {
        struct RowMatch { double score; int x; };
        std::vector<RowMatch> rows(row_count, {std::numeric_limits<double>::lowest(), bounds.x});
        std::atomic<std::size_t> next_row{};
        auto scan_rows = [&]() {
            for (;;)
            {
                auto const row_index = next_row.fetch_add(1, std::memory_order_relaxed);
                if (row_index >= row_count) break;
                auto const y = bounds.y + static_cast<int>(row_index) * step;
                auto row_best = std::numeric_limits<double>::lowest();
                auto row_best_x = bounds.x;
                for (int x = bounds.x; x <= max_x; x += step)
                {
                    auto const score = compare_gray(screen, x, y, comparison);
                    if (score > row_best) { row_best = score; row_best_x = x; }
                }
                rows[row_index] = {row_best, row_best_x};
            }
        };

        // Apple libc++ on the macOS CI runner does not provide std::jthread.
        std::vector<std::thread> workers;
        workers.reserve(worker_count - 1);
        struct JoinWorkers
        {
            std::vector<std::thread>& threads;
            ~JoinWorkers()
            {
                for (auto& thread : threads)
                    if (thread.joinable()) thread.join();
            }
        } join_workers{workers};
        try
        {
            for (unsigned index = 1; index < worker_count; ++index)
                workers.emplace_back(scan_rows);
        }
        catch (...)
        {
            // Resource pressure should reduce parallelism, not disable an otherwise
            // valid recognition request. The caller below drains all unclaimed rows;
            // join_workers also joins any workers if this path itself unwinds.
        }
        scan_rows();
        for (auto& worker : workers) worker.join();

        for (std::size_t row_index = 0; row_index < rows.size(); ++row_index)
        {
            auto const& candidate = rows[row_index];
            if (candidate.score > best)
            {
                best = candidate.score;
                best_x = candidate.x;
                best_y = bounds.y + static_cast<int>(row_index) * step;
            }
        }
    }
    else
        for (int y = bounds.y; y <= max_y; y += step)
            for (int x = bounds.x; x <= max_x; x += step)
            {
                auto const score = compare_gray(screen, x, y, comparison);
                if (score > best) { best = score; best_x = x; best_y = y; }
            }
    return {best >= std::clamp(threshold, 0.0, 1.0), (std::max)(0.0, best), best_x, best_y, templ.width, templ.height};
}

MatchResult find_color(
    GrayImage const& screen, GrayImage const& templ, Roi roi, double threshold,
    std::int32_t reference_width, std::int32_t reference_height, bool require_text_contrast)
{
    if (!has_rgba(screen) || !has_rgba(templ))
        return find(screen, templ, std::move(roi), threshold, reference_width, reference_height);
    if (templ.width > screen.width || templ.height > screen.height)
        return {false, 0, 0, 0, templ.width, templ.height};
    auto const bounds = scale_roi(roi, screen.width, screen.height, reference_width, reference_height);
    auto const max_x = (std::min)(screen.width - templ.width, bounds.x + bounds.width - templ.width);
    auto const max_y = (std::min)(screen.height - templ.height, bounds.y + bounds.height - templ.height);
    if (bounds.x > max_x || bounds.y > max_y) return {false, 0, 0, 0, templ.width, templ.height};
    auto const step = require_text_contrast || templ.width <= 240 ? 1 : 2;
    double best = std::numeric_limits<double>::lowest();
    int best_x = bounds.x, best_y = bounds.y;
    for (int y = bounds.y; y <= max_y; y += step)
        for (int x = bounds.x; x <= max_x; x += step)
        {
            double weighted_error = 0, total_weight = 0, foreground = 0, background = 0;
            int visible = 0, background_count = 0;
            auto const sw = (std::min)(32, templ.width), sh = (std::min)(24, templ.height);
            for (int sy = 0; sy < sh; ++sy)
            {
                auto const ty = sy * templ.height / sh;
                for (int sx = 0; sx < sw; ++sx)
                {
                    auto const tx = sx * templ.width / sw;
                    auto const to = (static_cast<std::size_t>(ty) * templ.width + tx) * 4;
                    auto const so = (static_cast<std::size_t>(y + ty) * screen.width + x + tx) * 4;
                    auto const alpha = templ.rgba[to + 3];
                    if (alpha < 24)
                    {
                        if (require_text_contrast)
                        {
                            background += (screen.rgba[so] * 299.0 + screen.rgba[so + 1] * 587.0
                                + screen.rgba[so + 2] * 114.0) / 1000.0;
                            ++background_count;
                        }
                        continue;
                    }
                    auto const error = std::abs(static_cast<int>(screen.rgba[so]) - templ.rgba[to])
                        + std::abs(static_cast<int>(screen.rgba[so + 1]) - templ.rgba[to + 1])
                        + std::abs(static_cast<int>(screen.rgba[so + 2]) - templ.rgba[to + 2]);
                    auto const weight = alpha / 255.0;
                    weighted_error += error * weight;
                    total_weight += 765.0 * weight;
                    foreground += (templ.rgba[to] * 299.0 + templ.rgba[to + 1] * 587.0
                        + templ.rgba[to + 2] * 114.0) / 1000.0;
                    ++visible;
                }
            }
            double score = 0;
            if (visible > 0 && total_weight > 0)
            {
                bool contrast_ok = true;
                if (require_text_contrast && background_count > 0)
                {
                    auto const bright_text = foreground / visible >= 180.0;
                    auto const average_background = background / background_count;
                    contrast_ok = bright_text ? average_background <= 215.0 : average_background >= 220.0;
                }
                if (contrast_ok) score = std::clamp(1.0 - weighted_error / total_weight, -1.0, 1.0);
            }
            if (score > best) { best = score; best_x = x; best_y = y; }
        }
    return {best >= std::clamp(threshold, 0.0, 1.0), (std::max)(0.0, best), best_x, best_y, templ.width, templ.height};
}

MatchResult score_colored_region(GrayImage const& screen, GrayImage const& templ,
    std::array<std::int32_t, 4> const& region, double threshold)
{
    auto const [x0, y0, width, height] = region;
    if (!has_rgba(screen) || !has_rgba(templ) || x0 < 0 || y0 < 0 || width <= 0 || height <= 0
        || static_cast<std::int64_t>(x0) + width > screen.width
        || static_cast<std::int64_t>(y0) + height > screen.height) return {false, 0, x0, y0, width, height};
    double error = 0; int count = 0;
    for (int y = 0; y < templ.height; ++y)
        for (int x = 0; x < templ.width; ++x)
        {
            auto const at = (static_cast<std::size_t>(y) * templ.width + x) * 4;
            int const r = templ.rgba[at], g = templ.rgba[at + 1], b = templ.rgba[at + 2];
            if (templ.rgba[at + 3] < 128 || std::max(r, std::max(g, b)) - std::min(r, std::min(g, b)) < 40) continue;
            int const sx = x0 + std::min(width - 1, static_cast<int>((x + .5) * width / templ.width));
            int const sy = y0 + std::min(height - 1, static_cast<int>((y + .5) * height / templ.height));
            auto const sp = (static_cast<std::size_t>(sy) * screen.width + sx) * 4;
            error += std::abs(static_cast<int>(screen.rgba[sp]) - r)
                + std::abs(static_cast<int>(screen.rgba[sp + 1]) - g) + std::abs(static_cast<int>(screen.rgba[sp + 2]) - b);
            ++count;
        }
    double const score = count == 0 ? 0 : 1.0 - error / (count * 765.0);
    return {count > 0 && score >= std::clamp(threshold, 0.0, 1.0), score, x0, y0, width, height};
}

MatchResult find_exhaustive_color(GrayImage const& screen, GrayImage const& templ,
    Roi roi, double threshold, std::int32_t reference_width, std::int32_t reference_height)
{
    if (!has_rgba(screen) || !has_rgba(templ))
        return find_color(screen, templ, std::move(roi), threshold, reference_width, reference_height);
    auto const bounds = scale_roi(roi, screen.width, screen.height, reference_width, reference_height);
    int const max_x = bounds.x + bounds.width - templ.width, max_y = bounds.y + bounds.height - templ.height;
    if (max_x < bounds.x || max_y < bounds.y) return {false, 0, 0, 0, templ.width, templ.height};
    struct Sample { std::size_t offset; int r, g, b; double weight; };
    std::vector<Sample> samples; double total = 0, mean_r = 0, mean_g = 0, mean_b = 0;
    int const sw = std::min(32, templ.width), sh = std::min(24, templ.height);
    for (int y = 0; y < sh; ++y)
        for (int x = 0; x < sw; ++x)
        {
            int const tx = x * templ.width / sw, ty = y * templ.height / sh;
            auto const at = (static_cast<std::size_t>(ty) * templ.width + tx) * 4;
            if (templ.rgba[at + 3] < 24) continue;
            double const weight = templ.rgba[at + 3] / 255.0;
            samples.push_back({(static_cast<std::size_t>(ty) * screen.width + tx) * 4,
                templ.rgba[at], templ.rgba[at + 1], templ.rgba[at + 2], weight});
            total += 765.0 * weight; mean_r += templ.rgba[at]; mean_g += templ.rgba[at + 1]; mean_b += templ.rgba[at + 2];
        }
    if (total <= 0) return {0 >= std::clamp(threshold, 0.0, 1.0), 0, bounds.x, bounds.y, templ.width, templ.height};
    mean_r /= samples.size(); mean_g /= samples.size(); mean_b /= samples.size();
    auto ordered = samples;
    std::stable_sort(ordered.begin(), ordered.end(), [&](auto const& a, auto const& b) {
        auto priority = [&](auto const& s) { return s.weight * (std::abs(s.r - mean_r) + std::abs(s.g - mean_g) + std::abs(s.b - mean_b)); };
        return priority(a) > priority(b);
    });
    double const tolerance = total * 1e-12;
    auto error = [&](int x, int y, double limit) {
        auto const origin = (static_cast<std::size_t>(y) * screen.width + x) * 4;
        auto contribution = [&](auto const& s) {
            auto const at = origin + s.offset;
            return (std::abs(static_cast<int>(screen.rgba[at]) - s.r)
                + std::abs(static_cast<int>(screen.rgba[at + 1]) - s.g)
                + std::abs(static_cast<int>(screen.rgba[at + 2]) - s.b)) * s.weight;
        };
        double sum = 0;
        for (auto const& s : ordered)
        {
            sum += contribution(s);
            if (sum > limit + tolerance) return std::numeric_limits<double>::infinity();
        }
        sum = 0;
        for (auto const& s : samples) sum += contribution(s);
        return sum;
    };
    double bound = std::numeric_limits<double>::infinity();
    for (int y = bounds.y; y <= max_y; y += 8)
        for (int x = bounds.x; x <= max_x; x += 8) bound = std::min(bound, error(x, y, bound));
    double best = std::numeric_limits<double>::lowest(); int best_x = bounds.x, best_y = bounds.y;
    int const step = templ.width <= 240 ? 1 : 2;
    for (int y = bounds.y; y <= max_y; y += step)
        for (int x = bounds.x; x <= max_x; x += step)
        {
            double const difference = error(x, y, bound);
            if (!std::isfinite(difference)) continue;
            bound = std::min(bound, difference);
            double const score = std::clamp(1.0 - difference / total, -1.0, 1.0);
            if (score > best) { best = score; best_x = x; best_y = y; }
        }
    return {best >= std::clamp(threshold, 0.0, 1.0), std::max(0.0, best), best_x, best_y, templ.width, templ.height};
}

MatchResult find_button(
    GrayImage const& screen, GrayImage const& templ, Roi roi, double threshold,
    std::int32_t reference_width, std::int32_t reference_height)
{
    if (!has_gray(screen) || !has_gray(templ) || templ.width > screen.width || templ.height > screen.height)
        return {false, 0, 0, 0, templ.width, templ.height};
    auto const bounds = scale_roi(roi, screen.width, screen.height, reference_width, reference_height);
    auto const max_x = (std::min)(screen.width - templ.width, bounds.x + bounds.width - templ.width);
    auto const max_y = (std::min)(screen.height - templ.height, bounds.y + bounds.height - templ.height);
    if (bounds.x > max_x || bounds.y > max_y) return {false, 0, 0, 0, templ.width, templ.height};
    auto const features = button_features(templ, (std::min)(32, templ.width), (std::min)(16, templ.height));
    if (features.size() < 8) return find(screen, templ, std::move(roi), threshold, reference_width, reference_height);
    auto const dark_features = std::count_if(features.begin(), features.end(), [](auto const& f) { return f.dark; });
    auto const min_dark = (std::max)(8, static_cast<int>(std::ceil(dark_features * .20)));
    auto const background = button_background(templ);
    auto const min_bright = (std::max)(8, static_cast<int>(std::ceil(background.size() * .70)));
    auto const ink = button_ink(templ);
    double best = std::numeric_limits<double>::lowest();
    int best_x = bounds.x, best_y = bounds.y;
    for (int y = bounds.y; y <= max_y; y += 2)
        for (int x = bounds.x; x <= max_x; x += 2)
        {
            auto const score = compare_button(screen, x, y, features, min_dark, background, min_bright, ink);
            if (score > best) { best = score; best_x = x; best_y = y; }
        }
    return {best >= std::clamp(threshold, 0.0, 1.0), (std::max)(0.0, best), best_x, best_y, templ.width, templ.height};
}

MatchResult find_scaled(
    GrayImage const& screen, GrayImage const& templ, Roi roi, double threshold,
    std::int32_t reference_width, std::int32_t reference_height, ScaledOptions const& options)
{
    if (options.scales.empty()) return find(screen, templ, std::move(roi), threshold, reference_width, reference_height);
    if (!has_gray(screen) || !has_gray(templ)) return {false, 0, 0, 0, templ.width, templ.height};
    auto const bounds = scale_roi(roi, screen.width, screen.height, reference_width, reference_height);
    auto const reference_scale = (std::min)(screen.width / static_cast<double>((std::max)(1, reference_width)),
        screen.height / static_cast<double>((std::max)(1, reference_height)));
    double best = std::numeric_limits<double>::lowest(), best_scale = std::numeric_limits<double>::quiet_NaN();
    int best_x = bounds.x, best_y = bounds.y, best_w = templ.width, best_h = templ.height;
    auto evaluate = [&](double scale, std::optional<Bounds> local = std::nullopt, std::optional<int> step = std::nullopt) {
        if (!std::isfinite(scale) || scale <= 0) return;
        auto const target_w = (std::max)(1, round_even(templ.width * reference_scale * scale));
        auto const target_h = (std::max)(1, round_even(templ.height * reference_scale * scale));
        if (target_w > screen.width || target_h > screen.height) return;
        auto const result = find_at_size(screen, templ, local.value_or(bounds), target_w, target_h,
            step.value_or(options.candidate_step), options.sample_width, options.sample_height);
        if (result.score > best)
        {
            best = result.score; best_x = result.x; best_y = result.y;
            best_w = target_w; best_h = target_h; best_scale = scale;
        }
    };
    for (auto scale : options.scales) evaluate(scale);
    if (std::isfinite(best_scale) && options.refine_scale)
    {
        auto const coarse = best_scale;
        auto const padding = std::clamp(options.candidate_step * 2, 8, 32);
        auto const local_x = (std::max)(bounds.x, best_x - padding);
        auto const local_y = (std::max)(bounds.y, best_y - padding);
        auto const largest_w = ceil_to_int(templ.width * reference_scale * (coarse + .04));
        auto const largest_h = ceil_to_int(templ.height * reference_scale * (coarse + .04));
        Bounds local{local_x, local_y,
            (std::min)(bounds.x + bounds.width - local_x, largest_w + padding * 2),
            (std::min)(bounds.y + bounds.height - local_y, largest_h + padding * 2)};
        for (int offset = -4; offset <= 4; ++offset) evaluate(coarse + offset * .01, local, 1);
        if (options.full_search_on_miss && best < std::clamp(threshold, 0.0, 1.0))
            for (int offset = -4; offset <= 4; ++offset) evaluate(coarse + offset * .01, bounds, 1);
    }
    if (best == std::numeric_limits<double>::lowest()) return {false, 0, 0, 0, templ.width, templ.height};
    return {best >= std::clamp(threshold, 0.0, 1.0), (std::max)(0.0, best), best_x, best_y, best_w, best_h};
}

MatchResult find_scaled_masked(
    GrayImage const& screen, GrayImage const& normalized_mask_template, Roi roi, double threshold,
    std::int32_t reference_width, std::int32_t reference_height, std::vector<double> const& scales)
{
    auto const& templ = normalized_mask_template;
    if (!has_gray(screen) || !has_gray(templ) || !has_rgba(templ)) return {false, 0, 0, 0, 0, 0};
    auto const reference_scale = (std::min)(screen.width / static_cast<double>((std::max)(1, reference_width)),
        screen.height / static_cast<double>((std::max)(1, reference_height)));
    auto const bounds = scale_roi(roi, screen.width, screen.height, reference_width, reference_height);
    double best = std::numeric_limits<double>::lowest();
    int best_x = 0, best_y = 0, best_w = templ.width, best_h = templ.height;
    for (auto scale : scales)
    {
        if (!std::isfinite(scale) || scale <= 0) continue;
        auto const w = (std::max)(1, round_even(templ.width * reference_scale * scale));
        auto const h = (std::max)(1, round_even(templ.height * reference_scale * scale));
        if (w > screen.width || h > screen.height) continue;
        auto const candidate = find_masked_size(screen, templ, bounds, w, h);
        if (candidate.score > best)
        {
            best = candidate.score; best_x = candidate.x; best_y = candidate.y; best_w = w; best_h = h;
        }
    }
    if (best == std::numeric_limits<double>::lowest()) return {false, 0, 0, 0, templ.width, templ.height};
    return {best >= std::clamp(threshold, 0.0, 1.0), (std::max)(0.0, best), best_x, best_y, best_w, best_h};
}

MatchResult score_masked_region(GrayImage const& screen, GrayImage const& templ,
    std::array<std::int32_t, 4> const& region, double threshold)
{
    auto const [x, y, width, height] = region;
    MatchResult result{false, 0, x, y, width, height};
    if (!has_gray(screen) || !has_gray(templ) || !has_rgba(templ)
        || x < 0 || y < 0 || width <= 0 || height <= 0
        || static_cast<std::int64_t>(x) + width > screen.width
        || static_cast<std::int64_t>(y) + height > screen.height) return result;
    std::vector<double> a, b;
    for (int ty = 0; ty < templ.height; ty += 2)
        for (int tx = 0; tx < templ.width; tx += 2)
        {
            auto const i = static_cast<std::size_t>(ty) * templ.width + tx;
            if (templ.rgba[i * 4 + 3] < 48) continue;
            a.push_back(templ.gray[i]);
            b.push_back(screen.gray[static_cast<std::size_t>(y + ty * height / templ.height)
                * screen.width + x + tx * width / templ.width]);
        }
    if (a.size() >= 32) result.score = pearson(a, b);
    result.found = result.score >= threshold;
    return result;
}

MatchResult find_screenshot_crop(GrayImage const& screen, GrayImage const& templ,
    std::array<std::int32_t, 4> const& region, int width, int height, double threshold)
{
    auto const [left, top, region_width, region_height] = region;
    MatchResult result{false, std::numeric_limits<double>::lowest(), left, top, width, height};
    if (!has_gray(screen) || !has_gray(templ) || left < 0 || top < 0
        || width <= 0 || height <= 0 || region_width < width || region_height < height
        || static_cast<std::int64_t>(left) + region_width > screen.width
        || static_cast<std::int64_t>(top) + region_height > screen.height) return result;
    int const sw = std::min(16, templ.width), sh = std::min(16, templ.height);
    auto const count = static_cast<std::size_t>(sw) * sh;
    std::vector<double> a(count), b(count), ax, ay, bx, by;
    if (sw >= 3 && sh >= 3)
    {
        auto const gradients = static_cast<std::size_t>(sw - 2) * (sh - 2);
        ax.resize(gradients); ay.resize(gradients); bx.resize(gradients); by.resize(gradients);
    }
    auto compare = [&](int x, int y)
    {
        for (int sy = 0; sy < sh; ++sy)
            for (int sx = 0; sx < sw; ++sx)
            {
                auto const i = static_cast<std::size_t>(sy) * sw + sx;
                a[i] = templ.gray[static_cast<std::size_t>(sy * templ.height / sh) * templ.width
                    + sx * templ.width / sw];
                b[i] = screen.gray[static_cast<std::size_t>(y + sy * height / sh) * screen.width
                    + x + sx * width / sw];
            }
        double const intensity = pearson(a, b);
        if (ax.empty()) return intensity;
        std::size_t index = 0;
        for (int sy = 1; sy < sh - 1; ++sy)
            for (int sx = 1; sx < sw - 1; ++sx, ++index)
            {
                auto const i = static_cast<std::size_t>(sy) * sw + sx;
                ax[index] = a[i + 1] - a[i - 1]; ay[index] = a[i + sw] - a[i - sw];
                bx[index] = b[i + 1] - b[i - 1]; by[index] = b[i + sw] - b[i - sw];
            }
        return clamp_score(intensity * 0.75 + (pearson(ax, bx) + pearson(ay, by)) * 0.125);
    };
    auto consider = [&](int x, int y)
    {
        double const score = compare(x, y);
        if (score > result.score) { result.score = score; result.x = x; result.y = y; }
    };
    int const right = left + region_width - width, bottom = top + region_height - height;
    for (int y = top; y <= bottom; y += 4)
        for (int x = left; x <= right; x += 4) consider(x, y);
    int const min_x = std::max(left, result.x - 3), max_x = std::min(right, result.x + 3);
    int const min_y = std::max(top, result.y - 3), max_y = std::min(bottom, result.y + 3);
    for (int y = min_y; y <= max_y; ++y)
        for (int x = min_x; x <= max_x; ++x) consider(x, y);
    result.found = result.score >= threshold;
    return result;
}

bool has_meaningful_change(GrayImage const& before, GrayImage const& after, MatchResult const& match)
{
    if (before.width != after.width || before.height != after.height) return true;
    if (!has_gray(before) || !has_gray(after)) return false;
    auto const padding_x = (std::max)(1, match.width * 2);
    auto const padding_top = (std::max)(1, match.height * 3);
    auto const padding_bottom = (std::max)(1, match.height * 3);
    auto const left = std::clamp(match.x - padding_x, 0, before.width - 1);
    auto const top = std::clamp(match.y - padding_top, 0, before.height - 1);
    auto const right = std::clamp(match.x + match.width + padding_x, left + 1, before.width);
    auto const bottom = std::clamp(match.y + match.height + padding_bottom, top + 1, before.height);
    std::int64_t diff = 0;
    int changed = 0, sampled = 0;
    for (int y = top; y < bottom; y += 2)
        for (int x = left; x < right; x += 2)
        {
            auto const delta = std::abs(static_cast<int>(before.gray[static_cast<std::size_t>(y) * before.width + x])
                - static_cast<int>(after.gray[static_cast<std::size_t>(y) * after.width + x]));
            diff += delta; if (delta >= 8) ++changed; ++sampled;
        }
    if (!sampled) return false;
    return diff / static_cast<double>(sampled) >= 3.0 || changed / static_cast<double>(sampled) >= .015;
}

bool has_persistent_change(
    GrayImage const& before, GrayImage const& first_after,
    GrayImage const& settled_after, MatchResult const& match)
{
    return has_meaningful_change(before, first_after, match)
        && has_meaningful_change(before, settled_after, match)
        && !has_meaningful_change(first_after, settled_after, match);
}

}
