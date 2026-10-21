#pragma once

#include <filesystem>
#include <string>
#include <string_view>

namespace UmaAssistant::Paths
{

[[nodiscard]] inline std::filesystem::path FromUtf8(std::string_view value)
{
    std::u8string bytes;
    bytes.reserve(value.size());
    for (auto const byte : value)
        bytes.push_back(static_cast<char8_t>(static_cast<unsigned char>(byte)));
    return std::filesystem::path(bytes);
}

[[nodiscard]] inline std::string ToUtf8(std::filesystem::path const& value)
{
    auto const bytes = value.generic_u8string();
    return {reinterpret_cast<char const*>(bytes.data()), bytes.size()};
}

} // namespace UmaAssistant::Paths
