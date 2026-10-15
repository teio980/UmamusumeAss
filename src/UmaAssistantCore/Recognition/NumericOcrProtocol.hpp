#pragma once

#include <cstdint>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace UmaAssistant::Recognition::NumericOcrProtocol {

inline constexpr std::size_t max_request_bytes = 2u * 1024u * 1024u;
inline constexpr std::size_t max_response_bytes = 60u * 1024u;

[[nodiscard]] inline std::string encode_pixels(std::span<std::uint8_t const> pixels)
{
    constexpr char digits[] = "0123456789abcdef";
    std::string text;
    text.reserve(pixels.size() * 2u);
    for (auto const pixel : pixels)
    {
        text.push_back(digits[pixel >> 4]);
        text.push_back(digits[pixel & 15]);
    }
    return text;
}

[[nodiscard]] inline std::vector<std::uint8_t> decode_pixels(std::string_view text, std::size_t expected)
{
    if (expected > max_request_bytes / 2u || text.size() != expected * 2u)
        throw std::runtime_error("Invalid numeric OCR pixel count.");
    auto nibble = [](char value) -> unsigned {
        if (value >= '0' && value <= '9') return static_cast<unsigned>(value - '0');
        if (value >= 'a' && value <= 'f') return static_cast<unsigned>(value - 'a' + 10);
        throw std::runtime_error("Invalid numeric OCR pixel encoding.");
    };
    std::vector<std::uint8_t> pixels(expected);
    for (std::size_t index = 0; index < expected; ++index)
        pixels[index] = static_cast<std::uint8_t>((nibble(text[index * 2u]) << 4) | nibble(text[index * 2u + 1u]));
    return pixels;
}

} // namespace UmaAssistant::Recognition::NumericOcrProtocol
