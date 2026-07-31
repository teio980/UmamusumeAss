#include "AdbCommandRunner.hpp"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

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
    std::wstring cmdline = quote_windows_argument(executable.native());

    for (auto const& arg : arguments) {
        cmdline += L' ';

        std::wstring wide_arg;
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
        cmdline += quote_windows_argument(wide_arg);
    }

    return cmdline;
}

}
