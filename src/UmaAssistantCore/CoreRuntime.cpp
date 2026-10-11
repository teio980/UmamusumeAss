#include "CoreRuntime.hpp"
#include "Utf8Path.hpp"

#include <filesystem>
#include <fstream>

namespace UmaAssistant {





CoreRuntime& CoreRuntime::instance()
{
    static CoreRuntime runtime;
    return runtime;
}





bool CoreRuntime::set_user_dir(std::string const& path)
{
    std::lock_guard lock(mutex_);
    user_dir_ = path;
    return true;
}

std::string CoreRuntime::user_dir() const
{
    std::lock_guard lock(mutex_);
    return user_dir_;
}

bool CoreRuntime::load_resource(std::string const& base_path)
{

    auto const decoded_base = path_from_utf8(base_path);
    if (!decoded_base) return false;

    std::error_code path_error;
    if (!std::filesystem::is_directory(*decoded_base, path_error))
    {
        return false;
    }

    auto const json_path = *decoded_base / "resource" / "connection.json";
    auto resources = std::make_shared<Resources::ResourceSnapshot const>(*decoded_base / "resource");



    if (!std::filesystem::exists(json_path))
    {
        auto profile = ConnectionProfile::default_profile();
        std::lock_guard lock(mutex_);
        profile_        = std::make_shared<ConnectionProfile const>(
            std::move(profile));
        resources_ = std::move(resources);
        resource_loaded_ = true;
        return true;
    }


    try
    {
        auto profile = ConnectionProfile::load(json_path);
        std::lock_guard lock(mutex_);
        profile_        = std::make_shared<ConnectionProfile const>(
            std::move(profile));
        resources_ = std::move(resources);
        resource_loaded_ = true;
        return true;
    }
    catch (ProfileError const&)
    {
        return false;
    }
}

bool CoreRuntime::is_resource_loaded() const noexcept
{
    std::lock_guard lock(mutex_);
    return resource_loaded_;
}

ResourceBundle CoreRuntime::resource_bundle() const noexcept
{
    std::lock_guard lock(mutex_);
    return {profile_, resources_};
}

std::uint64_t CoreRuntime::allocate_operation_id() noexcept
{
    return next_operation_id_.fetch_add(1, std::memory_order_relaxed);
}

}
