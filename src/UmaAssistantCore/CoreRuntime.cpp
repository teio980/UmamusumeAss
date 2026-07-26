#include "CoreRuntime.hpp"
#include "Utf8Path.hpp"

#include <fstream>

namespace UmaAssistant {

// ===========================================================================
// Singleton
// ===========================================================================

CoreRuntime& CoreRuntime::instance()
{
    static CoreRuntime runtime;
    return runtime;
}

// ===========================================================================
// Resource management
// ===========================================================================

bool CoreRuntime::set_user_dir(std::string const& path)
{
    std::lock_guard lock(mutex_);
    user_dir_ = path;
    return true;
}

bool CoreRuntime::load_resource(std::string const& base_path)
{
    // Build path: <base_path>/resource/connection.json
    auto const decoded_base = path_from_utf8(base_path);
    if (!decoded_base) return false;
    auto const json_path = *decoded_base / "resource" / "connection.json";

    // Attempt to load and validate
    try
    {
        auto profile = ConnectionProfile::load(json_path);
        std::lock_guard lock(mutex_);
        profile_        = std::make_shared<ConnectionProfile const>(
            std::move(profile));
        resource_loaded_ = true;
        return true;
    }
    catch (ProfileError const&)
    {
        std::lock_guard lock(mutex_);
        profile_.reset();
        resource_loaded_ = false;
        return false;
    }
}

bool CoreRuntime::is_resource_loaded() const noexcept
{
    std::lock_guard lock(mutex_);
    return resource_loaded_;
}

std::shared_ptr<ConnectionProfile const> CoreRuntime::profile() const noexcept
{
    std::lock_guard lock(mutex_);
    return profile_;
}

std::uint64_t CoreRuntime::allocate_operation_id() noexcept
{
    return next_operation_id_.fetch_add(1, std::memory_order_relaxed);
}

} // namespace UmaAssistant
