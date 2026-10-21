#include "ResourceSnapshot.hpp"
#include "WicImageDecoder.hpp"

#include <algorithm>
#include <cstdint>
#include <stdexcept>
#include <utility>

namespace UmaAssistant::Resources {

ResourceSnapshot::ResourceSnapshot(std::filesystem::path root)
    : root_(std::move(root).lexically_normal())
{
}

std::filesystem::path ResourceSnapshot::resolve(
    std::string_view base_directory, std::string_view path) const
{
    auto to_path = [](std::string_view value)
    {
        std::u8string utf8;
        utf8.reserve(value.size());
        for (auto const byte : value)
            utf8.push_back(static_cast<char8_t>(static_cast<unsigned char>(byte)));
        return std::filesystem::path(utf8);
    };
    auto const requested = to_path(path);
    if (requested.empty()) throw std::invalid_argument("Resource image path cannot be empty");
    if (requested.is_absolute() || requested.has_root_name()) return requested.lexically_normal();

    auto base = base_directory.empty() ? root_ : to_path(base_directory);
    if (!base.is_absolute()) base = root_ / base;
    return (base / requested).lexically_normal();
}

std::shared_ptr<Vision::GrayImage const> ResourceSnapshot::load_image(std::string_view relative_path) const
{
    return load_image_from({}, relative_path);
}

std::shared_ptr<Vision::GrayImage const> ResourceSnapshot::load_image_from(
    std::string_view base_directory, std::string_view path) const
{
    auto const full_path = resolve(base_directory, path);
    auto const key_bytes = full_path.generic_u8string();
    auto const key = std::string(reinterpret_cast<char const*>(key_bytes.data()), key_bytes.size());
    auto prepared = load_template(key, TemplatePreparation::original);
    return prepared ? std::shared_ptr<Vision::GrayImage const>(prepared, &prepared->image) : nullptr;
}

std::shared_ptr<Vision::GrayImage const> ResourceSnapshot::decode(std::filesystem::path const& path) const
{
    return std::make_shared<Vision::GrayImage>(WicImageDecoder::decode_file(path));
}

std::shared_ptr<PreparedTemplate const> ResourceSnapshot::load_template(
    std::string_view path, TemplatePreparation preparation) const
{
    if (preparation < TemplatePreparation::original || preparation > TemplatePreparation::portrait)
        throw std::invalid_argument("Unknown template preparation");
    auto const resolved = resolve({}, path);
    auto const key_bytes = resolved.generic_u8string();
    std::string key(reinterpret_cast<char const*>(key_bytes.data()), key_bytes.size());
    std::lock_guard lock(cache_mutex_);
    std::error_code error;
    if (!std::filesystem::is_regular_file(resolved, error))
    {
        image_cache_.erase(key);
        return nullptr;
    }
    auto const modified = std::filesystem::last_write_time(resolved, error);
    if (error) return nullptr;
    auto const size = std::filesystem::file_size(resolved, error);
    if (error) return nullptr;
    auto position = image_cache_.find(key);
    if (position == image_cache_.end() || position->second.modified != modified || position->second.size != size)
    {
        CachedImage entry{modified, size, {}};
        entry.preparations[0] = std::make_shared<PreparedTemplate const>(PreparedTemplate{*decode(resolved)});
        position = image_cache_.insert_or_assign(key, std::move(entry)).first;
    }
    auto& cached = position->second.preparations[static_cast<std::size_t>(preparation)];
    if (cached) return cached;
    auto value = prepare_template(position->second.preparations[0]->image, preparation);
    if (!value) return nullptr;
    cached = std::make_shared<PreparedTemplate const>(std::move(*value));
    return cached;
}

}
