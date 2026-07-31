#pragma once

#include <filesystem>
#include <string>
#include <variant>

namespace UmaAssistant {




struct SmokCliArgs
{
    std::filesystem::path adb_path;
    std::string           serial;
};




struct SmokCliError
{
    std::string message;
};




using SmokCliParseResult = std::variant<SmokCliArgs, SmokCliError>;







[[nodiscard]] SmokCliParseResult parse_smoke_args(int argc, char* argv[]);

}
