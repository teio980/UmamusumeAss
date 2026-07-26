#pragma once

#include <filesystem>
#include <optional>
#include <string_view>

namespace UmaAssistant {

[[nodiscard]] std::optional<std::filesystem::path> path_from_utf8(
    std::string_view value) noexcept;

}
