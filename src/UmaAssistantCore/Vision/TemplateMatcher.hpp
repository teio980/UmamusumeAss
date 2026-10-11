#pragma once

#include <array>
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace UmaAssistant::Vision {

struct GrayImage
{
    std::int32_t width{};
    std::int32_t height{};
    std::vector<std::uint8_t> gray;
    std::vector<std::uint8_t> rgba;
};

struct MatchResult
{
    bool found{};
    double score{};
    std::int32_t x{};
    std::int32_t y{};
    std::int32_t width{};
    std::int32_t height{};
};

using Roi = std::optional<std::array<std::int32_t, 4>>;

struct ScaledOptions
{
    std::vector<double> scales;
    std::int32_t candidate_step{8};
    std::int32_t sample_width{16};
    std::int32_t sample_height{16};
    bool full_search_on_miss{true};
};

[[nodiscard]] MatchResult find(
    GrayImage const& screen,
    GrayImage const& templ,
    Roi roi,
    double threshold,
    std::int32_t reference_width,
    std::int32_t reference_height,
    std::optional<std::int32_t> candidate_step_override = std::nullopt);

[[nodiscard]] MatchResult find_color(
    GrayImage const& screen,
    GrayImage const& templ,
    Roi roi,
    double threshold,
    std::int32_t reference_width,
    std::int32_t reference_height,
    bool require_text_contrast = false);
[[nodiscard]] MatchResult score_colored_region(GrayImage const& screen, GrayImage const& templ,
    std::array<std::int32_t, 4> const& region, double threshold);
[[nodiscard]] MatchResult find_exhaustive_color(GrayImage const& screen, GrayImage const& templ,
    Roi roi, double threshold, std::int32_t reference_width, std::int32_t reference_height);

[[nodiscard]] MatchResult find_button(
    GrayImage const& screen,
    GrayImage const& templ,
    Roi roi,
    double threshold,
    std::int32_t reference_width,
    std::int32_t reference_height);

[[nodiscard]] MatchResult find_scaled(
    GrayImage const& screen,
    GrayImage const& templ,
    Roi roi,
    double threshold,
    std::int32_t reference_width,
    std::int32_t reference_height,
    ScaledOptions const& options);

[[nodiscard]] MatchResult find_scaled_masked(
    GrayImage const& screen,
    GrayImage const& normalized_mask_template,
    Roi roi,
    double threshold,
    std::int32_t reference_width,
    std::int32_t reference_height,
    std::vector<double> const& scales);

[[nodiscard]] bool has_meaningful_change(
    GrayImage const& before,
    GrayImage const& after,
    MatchResult const& match);

[[nodiscard]] MatchResult score_masked_region(GrayImage const& screen, GrayImage const& templ,
    std::array<std::int32_t, 4> const& region, double threshold);
[[nodiscard]] MatchResult find_screenshot_crop(GrayImage const& screen, GrayImage const& templ,
    std::array<std::int32_t, 4> const& region, std::int32_t width, std::int32_t height, double threshold);

[[nodiscard]] bool has_persistent_change(
    GrayImage const& before,
    GrayImage const& first_after,
    GrayImage const& settled_after,
    MatchResult const& match);

}
