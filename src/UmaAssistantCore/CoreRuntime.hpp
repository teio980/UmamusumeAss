#pragma once

// ---------------------------------------------------------------------------
// CoreRuntime.hpp — singleton runtime for Phase 3 C ABI
//
// Owns the loaded resource profile and provides thread-safe access to it.
// All UmaCaller.cpp exports delegate here for shared state.
// ---------------------------------------------------------------------------

#include "Connection/ConnectionProfile.hpp"
#include "UmaAssistant/Connection.hpp"

#include <atomic>
#include <memory>
#include <mutex>
#include <string>

namespace UmaAssistant {

class CoreRuntime
{
public:
    static CoreRuntime& instance();

    CoreRuntime(CoreRuntime const&) = delete;
    CoreRuntime& operator=(CoreRuntime const&) = delete;

    /// Stores a user directory path.  Returns true.
    bool set_user_dir(std::string const& path);

    /// Loads resource/connection.json from <base_path>/resource/.
    /// Returns true on success, false on failure.
    bool load_resource(std::string const& base_path);

    /// True after a successful load_resource call.
    bool is_resource_loaded() const noexcept;

    /// Returns a shared pointer to the loaded profile.
    /// Caller must check is_resource_loaded() first.
    std::shared_ptr<ConnectionProfile const> profile() const noexcept;

    std::uint64_t allocate_operation_id() noexcept;

private:
    CoreRuntime() = default;

    mutable std::mutex                         mutex_;
    std::string                                user_dir_;
    std::shared_ptr<ConnectionProfile const>   profile_;
    bool                                       resource_loaded_ = false;
    std::atomic<std::uint64_t>                 next_operation_id_{1};
};

} // namespace UmaAssistant
