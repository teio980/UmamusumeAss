#pragma once

#include "TemplateMatcher.hpp"
#include "UmaAssistant/UmaCaller.h"
#include <utility>

namespace UmaAssistant::Vision {
[[nodiscard]] GrayImage binarize_color(GrayImage const& image, UmaColorRule const& rule);
[[nodiscard]] std::vector<UmaColorBlob> find_color_blobs(GrayImage const& image,
    UmaColorRule const& rule, UmaImageRect const& region);
[[nodiscard]] std::pair<std::uint64_t, std::uint64_t> count_color(GrayImage const& image,
    UmaColorRule const& rule, UmaImageRect const& region, UmaImageRect const* exclude);
}
