#pragma once

#include <filesystem>
#include <optional>
#include <stdexcept>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

#include <nlohmann/json.hpp>

namespace UmaAssistant {





struct AdbInvocation
{
    std::filesystem::path   executable;
    std::vector<std::string> arguments;
};




class ProfileError final : public std::runtime_error
{
public:
    using std::runtime_error::runtime_error;
};




class ConnectionProfile
{
public:




    [[nodiscard]] static ConnectionProfile load(std::filesystem::path const& json_path);





    [[nodiscard]] static ConnectionProfile default_profile();





    [[nodiscard]] std::optional<AdbInvocation> expand(
        std::string_view          profile_name,
        std::string_view          command,
        std::filesystem::path const& adb_path,
        std::string_view          serial) const;

private:
    using CommandMap = std::unordered_map<std::string, std::vector<std::string>>;
    using ProfileMap = std::unordered_map<std::string, CommandMap>;

    explicit ConnectionProfile(ProfileMap profiles);


    [[nodiscard]] static CommandMap parse_commands(
        nlohmann::json const& commands_obj);

    ProfileMap profiles_;
};

}
