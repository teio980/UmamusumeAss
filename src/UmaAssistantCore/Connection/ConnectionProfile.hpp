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

// ---------------------------------------------------------------------------
// AdbInvocation — the result of expanding a profile command.
// Never contains a shell command line; only executable + separated arguments.
// ---------------------------------------------------------------------------
struct AdbInvocation
{
    std::filesystem::path   executable;
    std::vector<std::string> arguments;
};

// ---------------------------------------------------------------------------
// ProfileError — thrown when a connection profile JSON is invalid.
// ---------------------------------------------------------------------------
class ProfileError final : public std::runtime_error
{
public:
    using std::runtime_error::runtime_error;
};

// ---------------------------------------------------------------------------
// ConnectionProfile — immutable loaded profile set.
// ---------------------------------------------------------------------------
class ConnectionProfile
{
public:
    /// Loads and validates a connection profile JSON file.
    /// Throws ProfileError if the schema is invalid, names are duplicated,
    /// base profiles are unknown, inheritance cycles exist, commands are
    /// non-arrays, or placeholders are malformed.
    [[nodiscard]] static ConnectionProfile load(std::filesystem::path const& json_path);

    /// Expands a named command from a named profile by substituting
    /// the [AdbSerial] placeholder and setting the executable.
    /// Returns std::nullopt when the profile or command is not found,
    /// or when a placeholder would remain unresolved or is malformed.
    [[nodiscard]] std::optional<AdbInvocation> expand(
        std::string_view          profile_name,
        std::string_view          command,
        std::filesystem::path const& adb_path,
        std::string_view          serial) const;

private:
    using CommandMap = std::unordered_map<std::string, std::vector<std::string>>;
    using ProfileMap = std::unordered_map<std::string, CommandMap>;

    explicit ConnectionProfile(ProfileMap profiles);

    /// Parses a commands JSON object into a CommandMap.
    [[nodiscard]] static CommandMap parse_commands(
        nlohmann::json const& commands_obj);

    ProfileMap profiles_;
};

} // namespace UmaAssistant
