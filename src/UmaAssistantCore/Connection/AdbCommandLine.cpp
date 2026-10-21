#include "AdbCommandRunner.hpp"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#if defined(_WIN32)
#include <windows.h>
#endif

#include <cstdlib>
#include <filesystem>
#include <string>
#include <string_view>
#include <vector>

namespace UmaAssistant {





std::wstring quote_windows_argument(std::wstring_view arg)
{
    bool needs_quoting = arg.empty();
    if (!needs_quoting) {
        for (auto const ch : arg) {
            if (ch == L' ' || ch == L'\t' || ch == L'"') {
                needs_quoting = true;
                break;
            }
        }
    }

    if (!needs_quoting) {
        return std::wstring{arg};
    }

    std::wstring result;
    result.reserve(arg.size() + 4);
    result += L'"';

    size_t backslash_count = 0;

    for (auto const ch : arg) {
        if (ch == L'\\') {
            ++backslash_count;
        } else if (ch == L'"') {
            result.append(backslash_count * 2, L'\\');
            result += L'\\';
            result += L'"';
            backslash_count = 0;
        } else {
            result.append(backslash_count, L'\\');
            result += ch;
            backslash_count = 0;
        }
    }

    result.append(backslash_count * 2, L'\\');
    result += L'"';

    return result;
}





std::wstring build_windows_command_line(
    std::filesystem::path const& executable,
    std::vector<std::string> const& arguments)
{
    std::wstring cmdline = quote_windows_argument(executable.wstring());

    for (auto const& arg : arguments) {
        cmdline += L' ';

        std::wstring wide_arg;
#if defined(_WIN32)
        if (!arg.empty()) {
            int const needed = ::MultiByteToWideChar(
                CP_UTF8, 0, arg.data(), static_cast<int>(arg.size()),
                nullptr, 0);
            if (needed > 0) {
                wide_arg.resize(static_cast<std::size_t>(needed));
                ::MultiByteToWideChar(
                    CP_UTF8, 0, arg.data(), static_cast<int>(arg.size()),
                    wide_arg.data(), needed);
            }
        }
#else
        for (std::size_t i = 0; i < arg.size(); ) {
            unsigned char c = static_cast<unsigned char>(arg[i]);
            if (c < 0x80) { wide_arg.push_back(static_cast<wchar_t>(c)); ++i; }
            else if ((c & 0xE0) == 0xC0 && i + 1 < arg.size()) {
                wide_arg.push_back(static_cast<wchar_t>(((c & 0x1F) << 6) | (static_cast<unsigned char>(arg[i+1]) & 0x3F)));
                i += 2;
            } else if ((c & 0xF0) == 0xE0 && i + 2 < arg.size()) {
                wide_arg.push_back(static_cast<wchar_t>(((c & 0x0F) << 12) | ((static_cast<unsigned char>(arg[i+1]) & 0x3F) << 6) | (static_cast<unsigned char>(arg[i+2]) & 0x3F)));
                i += 3;
            } else { wide_arg.push_back(static_cast<wchar_t>(c)); ++i; }
        }
#endif
        cmdline += quote_windows_argument(wide_arg);
    }

    return cmdline;
}

IWin32Process::Result IWin32Process::execute_args(
    std::filesystem::path const& executable,
    std::vector<std::string> const& arguments,
    std::stop_token              cancellation,
    std::chrono::milliseconds    timeout,
    std::size_t                  max_standard_output_bytes,
    bool                         leave_children_running_on_success)
{
    auto const cmdline = build_windows_command_line(executable, arguments);
    return execute(executable, cmdline, cancellation, timeout,
        max_standard_output_bytes, leave_children_running_on_success);
}

}
