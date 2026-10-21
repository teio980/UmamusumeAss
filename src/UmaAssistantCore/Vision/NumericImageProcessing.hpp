#pragma once
#include "ColorAnalysis.hpp"
#include <optional>
#include <span>

namespace UmaAssistant::Vision {
// Image-only recipes. Callers retain numeric parsing and game decisions.
[[nodiscard]] std::optional<GrayImage> prepare_numeric_glyphs(GrayImage const& source,
    bool warm_gain, UmaColorRule const& seed_rule);
[[nodiscard]] GrayImage compose_gray(std::span<GrayImage const> images,
    std::span<UmaImageRect const> placements, int width, int height);
[[nodiscard]] int count_ink_groups(GrayImage const& image, int threshold);
[[nodiscard]] std::uint64_t sample_difference(GrayImage const& before, GrayImage const& after,
    std::span<UmaImagePoint const> points);
}
