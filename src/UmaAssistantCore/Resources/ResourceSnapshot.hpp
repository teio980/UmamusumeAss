#pragma once

#include "Vision/TemplateMatcher.hpp"
#include "ImageTransforms.hpp"

#include <filesystem>
#include <array>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>

namespace UmaAssistant::Resources {

class ResourceSnapshot
{
public:
    // The caller passes a materialized resource view. ResourceStore creates a
    // new immutable views/<version-hash-guid> directory for every commit and
    // retains older views while native handles may still refer to them. This
    // snapshot pins that view path for lazy reads and does not follow the
    // mutable active pointer.
    explicit ResourceSnapshot(std::filesystem::path root);

    [[nodiscard]] std::filesystem::path const& root() const noexcept { return root_; }
    [[nodiscard]] std::shared_ptr<Vision::GrayImage const> load_image(std::string_view relative_path) const;
    [[nodiscard]] std::shared_ptr<Vision::GrayImage const> load_image_from(
        std::string_view base_directory, std::string_view path) const;
    [[nodiscard]] std::shared_ptr<PreparedTemplate const> load_template(
        std::string_view path, TemplatePreparation preparation) const;

private:
    [[nodiscard]] std::filesystem::path resolve(std::string_view base_directory, std::string_view path) const;
    [[nodiscard]] std::shared_ptr<Vision::GrayImage const> decode(std::filesystem::path const& path) const;

    std::filesystem::path root_;
    mutable std::mutex cache_mutex_;
    struct CachedImage
    {
        std::filesystem::file_time_type modified;
        std::uintmax_t size{};
        std::array<std::shared_ptr<PreparedTemplate const>, 3> preparations;
    };
    mutable std::unordered_map<std::string, CachedImage> image_cache_;
};

}
