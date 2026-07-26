#include "ConnectionProfile.hpp"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <cstddef>
#include <fstream>
#include <iterator>
#include <string>
#include <unordered_set>
#include <utility>

namespace UmaAssistant {
namespace {

// ---------------------------------------------------------------------------
// Internal helpers
// ---------------------------------------------------------------------------

[[nodiscard]] bool has_balanced_brackets(std::string_view s) noexcept
{
    auto const open  = static_cast<std::size_t>(std::count(s.begin(), s.end(), '['));
    auto const close = static_cast<std::size_t>(std::count(s.begin(), s.end(), ']'));
    return open == close;
}

[[nodiscard]] bool contains_bracket(std::string_view s) noexcept
{
    return s.find('[') != std::string_view::npos
        || s.find(']') != std::string_view::npos;
}

// Throws ProfileError if any command argument has unbalanced brackets.
void validate_placeholders(nlohmann::json const& commands_obj)
{
    for (auto const& [cmd_name, args] : commands_obj.items())
    {
        if (!args.is_array())
        {
            throw ProfileError(
                "command '" + cmd_name + "' value is not an array");
        }
        for (auto const& arg_val : args)
        {
            if (!arg_val.is_string())
            {
                throw ProfileError(
                    "command '" + cmd_name + "' argument is not a string");
            }
            auto const arg_str = arg_val.get<std::string>();
            if (!has_balanced_brackets(arg_str))
            {
                throw ProfileError(
                    "unbalanced bracket in command '" + cmd_name
                    + "' argument: \"" + arg_str + '"');
            }
        }
    }
}

void detect_cycles(
    std::string const& name,
    nlohmann::json const& entries_array,
    std::unordered_map<std::string, std::size_t> const& name_index,
    std::unordered_set<std::string>& visiting,
    std::unordered_set<std::string>& visited)
{
    if (visiting.contains(name))
    {
        throw ProfileError(
            "inheritance cycle detected involving profile '" + name + "'");
    }
    if (visited.contains(name)) return;

    auto const& entry = entries_array[name_index.at(name)];
    visiting.insert(name);

    auto const base_it = entry.find("baseConfig");
    if (base_it != entry.end() && !base_it->is_null())
    {
        auto const base = base_it->get<std::string>();
        if (base == name)
        {
            throw ProfileError(
                "profile '" + name + "' inherits from itself");
        }
        if (!name_index.contains(base))
        {
            throw ProfileError(
                "profile '" + name + "' references unknown base '"
                + base + "'");
        }
        detect_cycles(base, entries_array, name_index, visiting, visited);
    }

    visiting.erase(name);
    visited.insert(name);
}

} // anonymous namespace

// ===========================================================================
// ConnectionProfile implementation
// ===========================================================================

ConnectionProfile ConnectionProfile::load(std::filesystem::path const& json_path)
{
    std::ifstream ifs(json_path);
    if (!ifs.is_open())
    {
        throw ProfileError(
            "cannot open connection profile: " + json_path.string());
    }

    nlohmann::json root;
    try { ifs >> root; }
    catch (nlohmann::json::parse_error const& e)
    {
        throw ProfileError(
            "JSON parse error in " + json_path.string() + ": " + e.what());
    }

    // ---- Top-level schema validation ----
    if (!root.is_object())
    {
        throw ProfileError("root value must be a JSON object");
    }
    auto const conn_it = root.find("connection");
    if (conn_it == root.end())
    {
        throw ProfileError("missing required 'connection' key");
    }
    if (!conn_it->is_array())
    {
        throw ProfileError("'connection' must be a JSON array");
    }
    if (conn_it->empty())
    {
        throw ProfileError("'connection' array must not be empty");
    }

    auto const& entries = *conn_it;

    // ---- Build a name -> entry index for cycle detection + duplicate check ----
    std::unordered_map<std::string, std::size_t> name_index;
    for (std::size_t i = 0; i < entries.size(); ++i)
    {
        auto const& entry = entries[i];
        if (!entry.is_object())
        {
            throw ProfileError("connection entry " + std::to_string(i)
                               + " is not an object");
        }

        auto const name_it = entry.find("configName");
        if (name_it == entry.end() || !name_it->is_string()
            || name_it->get<std::string>().empty())
        {
            throw ProfileError("connection entry " + std::to_string(i)
                               + " is missing or has an invalid 'configName'");
        }
        auto const name = name_it->get<std::string>();

        if (name_index.contains(name))
        {
            throw ProfileError(
                "duplicate profile name '" + name + "'");
        }
        name_index[name] = i;

        auto const cmd_it = entry.find("commands");
        if (cmd_it == entry.end() || !cmd_it->is_object())
        {
            throw ProfileError(
                "profile '" + name + "' is missing required 'commands' object");
        }

        // Validate placeholders and command array types
        validate_placeholders(*cmd_it);
    }

    // ---- Cycle detection ----
    {
        std::unordered_set<std::string> visiting;
        std::unordered_set<std::string> visited;
        for (auto const& [name, _] : name_index)
        {
            detect_cycles(name, entries, name_index, visiting, visited);
        }
    }

    // ---- Resolve inheritance ----
    // Resolve in topological order: process nodes that have all their
    // dependencies already processed. Since we verified no cycles, a
    // simple iterative approach works: resolve profiles with no base
    // first, then profiles whose base is already resolved.
    ProfileMap resolved;

    // First pass: resolve profiles without a base (or with null base)
    for (auto const& [name, idx] : name_index)
    {
        auto const& entry = entries[idx];
        auto const base_it = entry.find("baseConfig");
        if (base_it == entry.end() || base_it->is_null())
        {
            resolved[name] = parse_commands(entry["commands"]);
        }
    }

    // Subsequent passes: resolve remaining profiles whose base is resolved
    // Since we have at most a few profiles, a simple N-pass loop is fine.
    bool changed = true;
    while (changed && resolved.size() < name_index.size())
    {
        changed = false;
        for (auto const& [name, idx] : name_index)
        {
            if (resolved.contains(name)) continue;

            auto const& entry = entries[idx];
            auto const base = entry["baseConfig"].get<std::string>();

            if (resolved.contains(base))
            {
                // Start from base, overlay own commands
                auto merged = resolved[base];           // copy
                auto const own = parse_commands(entry["commands"]);
                for (auto& [cmd, args] : own)
                {
                    merged[cmd] = std::move(args);
                }
                resolved[name] = std::move(merged);
                changed = true;
            }
        }
    }

    // All profiles should be resolved now (cycles were checked above)
    return ConnectionProfile{std::move(resolved)};
}

// ===========================================================================

ConnectionProfile::ConnectionProfile(ProfileMap profiles)
    : profiles_(std::move(profiles))
{
}

// ===========================================================================

std::optional<AdbInvocation> ConnectionProfile::expand(
    std::string_view          profile_name,
    std::string_view          command,
    std::filesystem::path const& adb_path,
    std::string_view          serial) const
{
    auto const prof_it = profiles_.find(std::string(profile_name));
    if (prof_it == profiles_.end()) return std::nullopt;

    auto const cmd_it = prof_it->second.find(std::string(command));
    if (cmd_it == prof_it->second.end()) return std::nullopt;

    std::vector<std::string> expanded_args;
    expanded_args.reserve(cmd_it->second.size());

    std::string const placeholder  = "[AdbSerial]";
    std::string const serial_str(serial);

    for (auto const& arg : cmd_it->second)
    {
        std::string result = arg;

        // Replace every occurrence of [AdbSerial] with the actual serial
        std::string::size_type pos = 0;
        while ((pos = result.find(placeholder, pos)) != std::string::npos)
        {
            result.replace(pos, placeholder.size(), serial_str);
            pos += serial_str.size();
        }

        // If any brackets remain, there was an unrecognised placeholder
        if (contains_bracket(result))
        {
            return std::nullopt;
        }

        expanded_args.push_back(std::move(result));
    }

    return AdbInvocation{adb_path, std::move(expanded_args)};
}

// ===========================================================================
// Private helpers
// ===========================================================================

ConnectionProfile::CommandMap ConnectionProfile::parse_commands(
    nlohmann::json const& commands_obj)
{
    CommandMap result;
    for (auto const& [cmd_name, args_val] : commands_obj.items())
    {
        std::vector<std::string> args;
        args.reserve(args_val.size());
        for (auto const& arg_val : args_val)
        {
            args.push_back(arg_val.get<std::string>());
        }
        result[cmd_name] = std::move(args);
    }
    return result;
}

} // namespace UmaAssistant
