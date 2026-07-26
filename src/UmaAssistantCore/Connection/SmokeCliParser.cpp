#include "SmokeCliParser.hpp"

#include <string>
#include <string_view>

namespace UmaAssistant {

SmokCliParseResult parse_smoke_args(int argc, char* argv[])
{
    // We expect exactly 3 entries: <program> <adb_path> <serial>
    // (argc includes the program name)

    if (argc < 1)
    {
        return SmokCliError{
            "Usage: uma_connect_smoke <adb_path> <serial> — too few arguments"
        };
    }

    // Determine the program name for error messages.
    std::string_view const prog = (argv[0] != nullptr && argv[0][0] != '\0')
        ? std::string_view{argv[0]}
        : "uma_connect_smoke";

    if (argc != 3)
    {
        std::string msg = "Usage: ";
        msg += prog;
        msg += " <adb_path> <serial>\n"
               "Expected exactly 2 arguments (adb_path, serial), got ";
        msg += std::to_string(argc - 1);
        return SmokCliError{std::move(msg)};
    }

    std::string_view const adb_path_arg{argv[1]};
    std::string_view const serial_arg{argv[2]};

    if (adb_path_arg.empty())
    {
        return SmokCliError{"ADB path must not be empty"};
    }

    if (serial_arg.empty())
    {
        return SmokCliError{"Serial must not be empty"};
    }

    SmokCliArgs result;
    result.adb_path = std::filesystem::path{adb_path_arg};
    result.serial   = std::string{serial_arg};

    return result;
}

} // namespace UmaAssistant
