#pragma once








#include "Connection/ConnectionProfile.hpp"
#include "Resources/ResourceSnapshot.hpp"
#include "UmaAssistant/Connection.hpp"

#include <atomic>
#include <memory>
#include <mutex>
#include <string>

namespace UmaAssistant {

struct ResourceBundle
{
    std::shared_ptr<ConnectionProfile const> profile;
    std::shared_ptr<Resources::ResourceSnapshot const> resources;
};

class CoreRuntime
{
public:
    static CoreRuntime& instance();

    CoreRuntime(CoreRuntime const&) = delete;
    CoreRuntime& operator=(CoreRuntime const&) = delete;


    bool set_user_dir(std::string const& path);


    std::string user_dir() const;




    bool load_resource(std::string const& base_path);


    bool is_resource_loaded() const noexcept;



    [[nodiscard]] ResourceBundle resource_bundle() const noexcept;

    std::uint64_t allocate_operation_id() noexcept;

private:
    CoreRuntime() = default;

    mutable std::mutex                         mutex_;
    std::string                                user_dir_;
    std::shared_ptr<ConnectionProfile const>   profile_;
    std::shared_ptr<Resources::ResourceSnapshot const> resources_;
    bool                                       resource_loaded_ = false;
    std::atomic<std::uint64_t>                 next_operation_id_{1};
};

}
