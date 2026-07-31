





#include <catch2/catch_test_macros.hpp>

#include <chrono>
#include <cstdint>
#include <filesystem>
#include <stop_token>
#include <string>
#include <string_view>
#include <vector>

#include "Connection/AdbCommandRunner.hpp"

using namespace std::chrono_literals;
using UmaAssistant::AdbCommandResult;
using UmaAssistant::IAdbCommandRunner;
using UmaAssistant::AdbInvocation;





TEST_CASE("quote_windows_argument wraps simple path in double quotes",
    "[AdbCommandRunner][quote]")
{
    auto const quoted = UmaAssistant::quote_windows_argument(LR"(C:\Program Files\adb\adb.exe)");
    REQUIRE(quoted == LR"("C:\Program Files\adb\adb.exe")");
}

TEST_CASE("quote_windows_argument escapes trailing backslash before closing quote",
    "[AdbCommandRunner][quote]")
{
    auto const quoted = UmaAssistant::quote_windows_argument(LR"(C:\Program Files\adb\)");
    REQUIRE(quoted == LR"("C:\Program Files\adb\\")");
}

TEST_CASE("quote_windows_argument escapes embedded quote characters",
    "[AdbCommandRunner][quote]")
{
    auto const quoted = UmaAssistant::quote_windows_argument(LR"(a\"b)");
    REQUIRE(quoted == LR"("a\\\"b")");
}

TEST_CASE("quote_windows_argument leaves simple alphanumeric unchanged",
    "[AdbCommandRunner][quote]")
{
    auto const quoted = UmaAssistant::quote_windows_argument(L"simple");
    REQUIRE(quoted == L"simple");
}

TEST_CASE("quote_windows_argument doubles backslashes preceding an embedded quote",
    "[AdbCommandRunner][quote]")
{
    auto const quoted = UmaAssistant::quote_windows_argument(LR"(foo\\bar\"baz)");
    REQUIRE(quoted == LR"("foo\\bar\\\"baz")");
}

TEST_CASE("quote_windows_argument returns empty quoted string for empty input",
    "[AdbCommandRunner][quote]")
{
    auto const quoted = UmaAssistant::quote_windows_argument(L"");
    REQUIRE(quoted == LR"("")");
}

TEST_CASE("build_windows_command_line produces expected command line for simple args",
    "[AdbCommandRunner][command_line]")
{
    auto const cmdline = UmaAssistant::build_windows_command_line(
        LR"(C:\adb\adb.exe)",
        {"devices"});
    REQUIRE(cmdline == LR"(C:\adb\adb.exe devices)");
}

TEST_CASE("build_windows_command_line quotes arguments with spaces",
    "[AdbCommandRunner][command_line]")
{
    auto const cmdline = UmaAssistant::build_windows_command_line(
        LR"(C:\Program Files\adb\adb.exe)",
        {"-s", "127.0.0.1:5555", "shell", "wm", "size"});
    REQUIRE(cmdline == LR"("C:\Program Files\adb\adb.exe" -s 127.0.0.1:5555 shell wm size)");
}

TEST_CASE("build_windows_command_line preserves UTF-8 multi-byte arguments",
    "[AdbCommandRunner][command_line][utf8]")
{






    std::string const jp_connect =
        "\xE6\x8E\xA5"
        "\xE7\xB6\x9A";

    auto const cmdline = UmaAssistant::build_windows_command_line(
        LR"(C:\adb\adb.exe)",
        {"shell", jp_connect});

    REQUIRE(cmdline.find(L"\u63A5\u7D9A") != std::wstring::npos);
}





namespace {





class FakeWin32Process final : public UmaAssistant::IWin32Process
{
public:
    struct ScriptedResult
    {
        int         exit_code{};
        std::string standard_output;
        std::string standard_error;
        bool        started   = true;
        bool        timed_out = false;
        bool        canceled  = false;
    };

    explicit FakeWin32Process(ScriptedResult result)
        : result_(std::move(result))
    {
    }

    UmaAssistant::IWin32Process::Result execute(
        std::filesystem::path const&  ,
        std::wstring const&  ,
        std::stop_token  ,
        std::chrono::milliseconds  ) override
    {
        UmaAssistant::IWin32Process::Result r;
        r.exit_code       = result_.exit_code;
        r.standard_output = result_.standard_output;
        r.standard_error  = result_.standard_error;
        r.started         = result_.started;
        r.timed_out       = result_.timed_out;
        r.canceled        = result_.canceled;
        return r;
    }

private:
    ScriptedResult result_;
};

}





TEST_CASE("AdbCommandRunnerWin32 preserves separate stdout and stderr streams",
    "[AdbCommandRunner][result]")
{
    FakeWin32Process process{{.exit_code = 0, .standard_output = "ok", .standard_error = "warning"}};
    UmaAssistant::AdbCommandRunnerWin32 runner{process};

    auto const result = runner.run(
        AdbInvocation{R"(C:\adb.exe)", {"devices"}}, 15s, std::stop_token{});

    REQUIRE(result.started);
    REQUIRE(result.standard_output == "ok");
    REQUIRE(result.standard_error == "warning");
    REQUIRE(result.exit_code == 0);
    REQUIRE_FALSE(result.timed_out);
    REQUIRE_FALSE(result.canceled);
}

TEST_CASE("AdbCommandRunnerWin32 preserves exit code and empty streams",
    "[AdbCommandRunner][result]")
{
    FakeWin32Process process{{.exit_code = 1, .standard_output = "", .standard_error = ""}};
    UmaAssistant::AdbCommandRunnerWin32 runner{process};

    auto const result = runner.run(
        AdbInvocation{R"(C:\adb.exe)", {"bad-command"}}, 5s, std::stop_token{});

    REQUIRE(result.started);
    REQUIRE(result.exit_code == 1);
    REQUIRE(result.standard_output.empty());
    REQUIRE(result.standard_error.empty());
}





TEST_CASE("AdbCommandRunnerWin32 maps process-started=false to result",
    "[AdbCommandRunner][state][started]")
{
    FakeWin32Process process{{.standard_output = "", .standard_error = "", .started = false}};
    UmaAssistant::AdbCommandRunnerWin32 runner{process};

    auto const result = runner.run(
        AdbInvocation{R"(C:\nonexistent.exe)", {}}, 10s, std::stop_token{});

    REQUIRE_FALSE(result.started);
    REQUIRE_FALSE(result.timed_out);
    REQUIRE_FALSE(result.canceled);
}





TEST_CASE("AdbCommandRunnerWin32 maps process-timed-out=true to result",
    "[AdbCommandRunner][state][timed_out]")
{
    FakeWin32Process process{{.exit_code = 0, .standard_output = "", .standard_error = "", .started = true, .timed_out = true}};
    UmaAssistant::AdbCommandRunnerWin32 runner{process};

    auto const result = runner.run(
        AdbInvocation{R"(C:\adb.exe)", {"wait"}}, 0ms, std::stop_token{});

    REQUIRE(result.started);
    REQUIRE(result.timed_out);
    REQUIRE_FALSE(result.canceled);
}





TEST_CASE("AdbCommandRunnerWin32 maps process-canceled=true to result",
    "[AdbCommandRunner][state][canceled]")
{
    FakeWin32Process process{{.exit_code = 0, .standard_output = "", .standard_error = "", .started = true, .canceled = true}};
    UmaAssistant::AdbCommandRunnerWin32 runner{process};

    auto const result = runner.run(
        AdbInvocation{R"(C:\adb.exe)", {"block"}}, 30s, std::stop_token{});

    REQUIRE(result.started);
    REQUIRE(result.canceled);
    REQUIRE_FALSE(result.timed_out);
}





TEST_CASE("AdbCommandRunnerWin32 maps started=false timed_out=false canceled=true",
    "[AdbCommandRunner][state][combined]")
{



    FakeWin32Process process{{.exit_code = 0, .standard_output = "", .standard_error = "", .started = false, .canceled = true}};
    UmaAssistant::AdbCommandRunnerWin32 runner{process};

    auto const result = runner.run(
        AdbInvocation{R"(C:\adb.exe)", {}}, 5s, std::stop_token{});

    REQUIRE_FALSE(result.started);
    REQUIRE(result.canceled);
    REQUIRE_FALSE(result.timed_out);
}
