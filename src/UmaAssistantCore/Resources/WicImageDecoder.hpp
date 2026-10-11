#pragma once

#include "Vision/TemplateMatcher.hpp"

#include <cstdint>
#include <filesystem>
#include <span>
#include <string_view>

namespace UmaAssistant::Resources::WicImageDecoder {

// Decodes the first frame as straight RGBA8 and derives the legacy grayscale
// plane with the same integer arithmetic used by GrayImageCodec.
[[nodiscard]] Vision::GrayImage decode_file(std::filesystem::path const& path);
[[nodiscard]] Vision::GrayImage decode_file_utf8(std::string_view path);
[[nodiscard]] Vision::GrayImage decode_bytes(std::span<std::uint8_t const> encoded);
[[nodiscard]] Vision::GrayImage from_rgba(
    std::int32_t width,
    std::int32_t height,
    std::span<std::uint8_t const> rgba);

}
