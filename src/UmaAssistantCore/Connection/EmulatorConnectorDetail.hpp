#pragma once

#include "AdbCommandRunner.hpp"
#include "UmaAssistant/Connection.hpp"

#include <charconv>
#include <cctype>
#include <cstdint>
#include <optional>
#include <sstream>
#include <string>
#include <string_view>
#include <system_error>
#include <vector>

namespace UmaAssistant {
namespace detail {

struct DeviceEntry
{
    std::string serial;
    std::string state;
};

struct ParsedSize
{
    std::int32_t physical_width{};
    std::int32_t physical_height{};
    std::int32_t override_width{};
    std::int32_t override_height{};
    bool         has_override{};
};

[[nodiscard]] inline std::string_view trim(std::string_view s) noexcept
{
    while (!s.empty() && (s.front() == ' ' || s.front() == '\t'
                          || s.front() == '\r' || s.front() == '\n'))
    {
        s.remove_prefix(1);
    }
    while (!s.empty() && (s.back() == ' ' || s.back() == '\t'
                          || s.back() == '\r' || s.back() == '\n'))
    {
        s.remove_suffix(1);
    }
    return s;
}

[[nodiscard]] inline bool has_control_characters(std::string_view s) noexcept
{
    for (auto const ch : s)
    {
        auto const uc = static_cast<unsigned char>(ch);
        if (uc <= 0x1F || uc == 0x7F) return true;
    }
    return false;
}

[[nodiscard]] inline bool is_hex(std::string_view s) noexcept
{
    if (s.empty()) return false;
    for (auto const ch : s)
    {
        if (!std::isxdigit(static_cast<unsigned char>(ch))) return false;
    }
    return true;
}

[[nodiscard]] inline bool is_tcp_endpoint(std::string_view serial) noexcept
{
    if (serial.empty() || has_control_characters(serial)) return false;
    if (serial.front() == '[')
    {
        auto const close_bracket = serial.find(']');
        if (close_bracket == std::string_view::npos) return false;
        if (close_bracket == serial.size() - 1) return false;
        if (serial[close_bracket + 1] != ':') return false;
        if (close_bracket == 1 || serial.find(']', close_bracket + 1) != std::string_view::npos)
            return false;
        auto const port_str = serial.substr(close_bracket + 2);
        if (port_str.empty()) return false;
        int port = 0;
        auto const [ptr, ec] = std::from_chars(
            port_str.data(), port_str.data() + port_str.size(), port);
        return ec == std::errc{} && ptr == port_str.data() + port_str.size()
               && port >= 1 && port <= 65535;
    }
    auto const last_colon = serial.rfind(':');
    if (last_colon == std::string_view::npos) return false;
    // IPv6 endpoints must use the bracketed form.  Without this check an
    // arbitrary serial containing a colon could accidentally be passed to
    // `adb connect`.
    if (serial.find(':') != last_colon || last_colon == 0) return false;
    if (last_colon == serial.size() - 1) return false;
    auto const host = serial.substr(0, last_colon);
    if (host.empty() || host.find_first_of(" \t") != std::string_view::npos)
        return false;
    auto const port_str = serial.substr(last_colon + 1);
    if (port_str.empty()) return false;
    int port = 0;
    auto const [ptr, ec] = std::from_chars(
        port_str.data(), port_str.data() + port_str.size(), port);
    return ec == std::errc{} && ptr == port_str.data() + port_str.size()
           && port >= 1 && port <= 65535;
}

[[nodiscard]] inline std::optional<ConnectionFailure> check_runner_result(
    AdbCommandResult const& r,
    std::string_view        phase,
    std::stop_token const&  cancellation)
{
    if (cancellation.stop_requested())
    {
        return ConnectionFailure{
            ConnectionErrorCode::Canceled, std::string(phase), "canceled"
        };
    }
    if (!r.started)
    {
        return ConnectionFailure{
            ConnectionErrorCode::ProcessStartFailed, std::string(phase),
            "process failed to start"
        };
    }
    if (r.timed_out)
    {
        return ConnectionFailure{
            ConnectionErrorCode::CommandTimedOut, std::string(phase),
            "command timed out"
        };
    }
    if (r.canceled)
    {
        return ConnectionFailure{
            ConnectionErrorCode::Canceled, std::string(phase), "canceled"
        };
    }
    if (r.exit_code != 0)
    {
        auto msg = "exit code " + std::to_string(r.exit_code);
        if (!r.standard_error.empty())
        {
            msg += ": " + r.standard_error;
        }
        return ConnectionFailure{
            ConnectionErrorCode::CommandFailed, std::string(phase), std::move(msg)
        };
    }
    return std::nullopt;
}

[[nodiscard]] inline std::vector<DeviceEntry> parse_devices_output(
    std::string const& stdout_str)
{
    std::vector<DeviceEntry> entries;
    std::istringstream stream(stdout_str);
    std::string line;
    while (std::getline(stream, line))
    {
        auto const trimmed = trim(line);
        if (trimmed.empty()) continue;
        if (trimmed == "List of devices attached") continue;
        auto const tab_pos = trimmed.find('\t');
        if (tab_pos == std::string_view::npos) continue;
        auto const serial = std::string(trimmed.substr(0, tab_pos));
        auto const state  = std::string(trim(trimmed.substr(tab_pos + 1)));
        if (!serial.empty() && !state.empty())
        {
            entries.push_back({std::move(serial), std::move(state)});
        }
    }
    return entries;
}

[[nodiscard]] inline std::optional<ParsedSize> parse_wm_size_output(
    std::string const& stdout_str)
{
    ParsedSize result{};
    auto const trimmed = trim(stdout_str);
    auto const phys_prefix = std::string_view("Physical size: ");
    auto const phys_pos = trimmed.find(phys_prefix);
    if (phys_pos == std::string_view::npos) return std::nullopt;
    auto const after_phys = trimmed.substr(phys_pos + phys_prefix.size());
    auto const newline_pos = after_phys.find('\n');
    auto const phys_line = trim(newline_pos == std::string_view::npos
                                    ? after_phys
                                    : after_phys.substr(0, newline_pos));
    auto const x_pos = phys_line.find('x');
    if (x_pos == std::string_view::npos) return std::nullopt;
    int w = 0, h = 0;
    {
        auto const w_str = phys_line.substr(0, x_pos);
        auto const h_str = phys_line.substr(x_pos + 1);
        auto [w_ptr, w_ec] = std::from_chars(
            w_str.data(), w_str.data() + w_str.size(), w);
        auto [h_ptr, h_ec] = std::from_chars(
            h_str.data(), h_str.data() + h_str.size(), h);
        if (w_ec != std::errc{} || h_ec != std::errc{}) return std::nullopt;
        if (w_ptr != w_str.data() + w_str.size()) return std::nullopt;
        if (h_ptr != h_str.data() + h_str.size()) return std::nullopt;
    }
    if (w <= 0 || h <= 0) return std::nullopt;
    result.physical_width  = w;
    result.physical_height = h;
    auto const ovr_prefix = std::string_view("Override size: ");
    auto const ovr_pos = trimmed.find(ovr_prefix);
    if (ovr_pos != std::string_view::npos)
    {
        auto const after_ovr = trimmed.substr(ovr_pos + ovr_prefix.size());
        auto const ovr_newline = after_ovr.find('\n');
        auto const ovr_line = trim(ovr_newline == std::string_view::npos
                                       ? after_ovr
                                       : after_ovr.substr(0, ovr_newline));
        auto const ox_pos = ovr_line.find('x');
        if (ox_pos != std::string_view::npos)
        {
            int ow = 0, oh = 0;
            auto const ow_str = ovr_line.substr(0, ox_pos);
            auto const oh_str = ovr_line.substr(ox_pos + 1);
            auto [ow_ptr, ow_ec] = std::from_chars(
                ow_str.data(), ow_str.data() + ow_str.size(), ow);
            auto [oh_ptr, oh_ec] = std::from_chars(
                oh_str.data(), oh_str.data() + oh_str.size(), oh);
            if (ow_ec == std::errc{} && oh_ec == std::errc{}
                && ow_ptr == ow_str.data() + ow_str.size()
                && oh_ptr == oh_str.data() + oh_str.size()
                && ow > 0 && oh > 0)
            {
                result.override_width  = ow;
                result.override_height = oh;
                result.has_override    = true;
            }
        }
    }
    return result;
}

} // namespace detail
} // namespace UmaAssistant
