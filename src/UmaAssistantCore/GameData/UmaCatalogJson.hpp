#pragma once

#include <filesystem>
#include <string_view>

#include <nlohmann/json.hpp>

namespace UmaAssistant::GameData {

/// Returns an existing resource DTO catalog without renaming or dropping its
/// packaged fields. `all` returns one object containing each supported catalog.
[[nodiscard]] nlohmann::json BuildCatalogJson(
    std::filesystem::path const& resource_root,
    std::string_view catalog_type,
    nlohmann::json strategies);

}
