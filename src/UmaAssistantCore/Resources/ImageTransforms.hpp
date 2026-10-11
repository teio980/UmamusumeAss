#pragma once

#include "Vision/TemplateMatcher.hpp"
#include <cstdint>
#include <optional>

namespace UmaAssistant::Resources {

enum class TemplatePreparation : std::int32_t { original = 0, head = 1, portrait = 2 };
struct PreparedTemplate
{
    Vision::GrayImage image;
    TemplatePreparation preparation{TemplatePreparation::original};
};

[[nodiscard]] Vision::GrayImage crop_image(Vision::GrayImage const& image,
    std::int32_t x, std::int32_t y, std::int32_t width, std::int32_t height);
// Straight RGBA8, alpha-premultiplied separable bicubic filtering. The float
// kernel and reduction order preserve the existing recognition pixel contract.
[[nodiscard]] Vision::GrayImage resize_image(Vision::GrayImage const& image,
    std::int32_t width, std::int32_t height, bool mean_gray = false);
[[nodiscard]] Vision::GrayImage resize_nearest(Vision::GrayImage const& image,
    std::int32_t width, std::int32_t height);
[[nodiscard]] std::optional<PreparedTemplate> prepare_template(
    Vision::GrayImage const& image, TemplatePreparation preparation);

}
