#pragma once

#include <filesystem>
#include <string>
#include <variant>

namespace UmaAssistant {

// ---------------------------------------------------------------------------
// SmokCliArgs — result of a successful argument parse.
// ---------------------------------------------------------------------------
struct SmokCliArgs
{
    std::filesystem::path adb_path;
    std::string           serial;
};

// ---------------------------------------------------------------------------
// SmokCliError — description of why argument parsing failed.
// ---------------------------------------------------------------------------
struct SmokCliError
{
    std::string message;
};

// ---------------------------------------------------------------------------
// SmokCliParseResult — either valid parsed args or a descriptive error.
// ---------------------------------------------------------------------------
using SmokCliParseResult = std::variant<SmokCliArgs, SmokCliError>;

// ---------------------------------------------------------------------------
// parse_smoke_args — parse argc/argv into a SmokCliArgs.
//
// Expects exactly 2 positional arguments: <adb_path> <serial>.
// Returns SmokCliError with a human-readable message on failure.
// ---------------------------------------------------------------------------
[[nodiscard]] SmokCliParseResult parse_smoke_args(int argc, char* argv[]);

} // namespace UmaAssistant
