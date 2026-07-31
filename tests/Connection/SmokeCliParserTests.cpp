







#include <catch2/catch_test_macros.hpp>

#include <cstddef>
#include <optional>
#include <string>
#include <string_view>
#include <vector>


#include "Connection/SmokeCliParser.hpp"

using UmaAssistant::SmokCliArgs;
using UmaAssistant::SmokCliError;
using UmaAssistant::SmokCliParseResult;
using UmaAssistant::parse_smoke_args;





namespace {



struct ArgvFixture
{
    int                    argc{};
    std::vector<char*>     argv;
    std::vector<std::string> storage;

    explicit ArgvFixture(std::vector<std::string> args)
        : storage(std::move(args))
    {
        argc = static_cast<int>(storage.size());
        argv.reserve(storage.size());
        for (auto& s : storage) argv.push_back(s.data());
    }
};

}





TEST_CASE("parse_smoke_args returns adb_path and serial for 2 args",
          "[SmokeCliParser][success]")
{
    ArgvFixture fixture({"uma_connect_smoke.exe", R"(C:\adb\adb.exe)", "127.0.0.1:5555"});

    auto const result = parse_smoke_args(fixture.argc, fixture.argv.data());

    REQUIRE(std::holds_alternative<SmokCliArgs>(result));
    auto const& args = std::get<SmokCliArgs>(result);
    REQUIRE(args.adb_path == std::filesystem::path{R"(C:\adb\adb.exe)"});
    REQUIRE(args.serial   == "127.0.0.1:5555");
}

TEST_CASE("parse_smoke_args accepts forward-slash paths",
          "[SmokeCliParser][success]")
{
    ArgvFixture fixture({"smoke", R"(C:/adb/adb.exe)", "emulator-5554"});

    auto const result = parse_smoke_args(fixture.argc, fixture.argv.data());

    REQUIRE(std::holds_alternative<SmokCliArgs>(result));
    auto const& args = std::get<SmokCliArgs>(result);
    REQUIRE(args.adb_path == std::filesystem::path{R"(C:/adb/adb.exe)"});
    REQUIRE(args.serial   == "emulator-5554");
}

TEST_CASE("parse_smoke_args accepts serial with only digits",
          "[SmokeCliParser][success]")
{
    ArgvFixture fixture({"smoke", R"(C:\adb\adb.exe)", "0123456789ABCDEF"});

    auto const result = parse_smoke_args(fixture.argc, fixture.argv.data());

    REQUIRE(std::holds_alternative<SmokCliArgs>(result));
    auto const& args = std::get<SmokCliArgs>(result);
    REQUIRE(args.serial == "0123456789ABCDEF");
}





TEST_CASE("parse_smoke_args with only 1 arg returns error",
          "[SmokeCliParser][error][arg-count]")
{
    ArgvFixture fixture({"uma_connect_smoke.exe"});

    auto const result = parse_smoke_args(fixture.argc, fixture.argv.data());

    REQUIRE(std::holds_alternative<SmokCliError>(result));
    auto const& err = std::get<SmokCliError>(result);
    auto const has_usage = err.message.find("Usage") != std::string_view::npos;
    auto const has_count = err.message.find("2 arguments") != std::string_view::npos;
    REQUIRE((has_usage || has_count));
}

TEST_CASE("parse_smoke_args with 0 args returns error",
          "[SmokeCliParser][error][arg-count]")
{
    ArgvFixture fixture({});

    auto const result = parse_smoke_args(fixture.argc, fixture.argv.data());

    REQUIRE(std::holds_alternative<SmokCliError>(result));
}

TEST_CASE("parse_smoke_args with 3 args returns error",
          "[SmokeCliParser][error][arg-count]")
{
    ArgvFixture fixture({"smoke", "adb", "serial", "extra"});

    auto const result = parse_smoke_args(fixture.argc, fixture.argv.data());

    REQUIRE(std::holds_alternative<SmokCliError>(result));
    auto const& err = std::get<SmokCliError>(result);
    auto const has_usage = err.message.find("Usage") != std::string_view::npos;
    auto const has_count = err.message.find("2 arguments") != std::string_view::npos;
    REQUIRE((has_usage || has_count));
}





TEST_CASE("parse_smoke_args with empty adb_path returns error",
          "[SmokeCliParser][error][empty]")
{
    ArgvFixture fixture({"smoke", "", "127.0.0.1:5555"});

    auto const result = parse_smoke_args(fixture.argc, fixture.argv.data());

    REQUIRE(std::holds_alternative<SmokCliError>(result));
}

TEST_CASE("parse_smoke_args with empty serial returns error",
          "[SmokeCliParser][error][empty]")
{
    ArgvFixture fixture({"smoke", R"(C:\adb\adb.exe)", ""});

    auto const result = parse_smoke_args(fixture.argc, fixture.argv.data());

    REQUIRE(std::holds_alternative<SmokCliError>(result));
}

TEST_CASE("parse_smoke_args with both empty returns error",
          "[SmokeCliParser][error][empty]")
{
    ArgvFixture fixture({"smoke", "", ""});

    auto const result = parse_smoke_args(fixture.argc, fixture.argv.data());

    REQUIRE(std::holds_alternative<SmokCliError>(result));
}





TEST_CASE("SmokCliArgs fields hold assigned values",
          "[SmokeCliParser][fields]")
{
    SmokCliArgs args;
    args.adb_path = R"(C:\adb\adb.exe)";
    args.serial   = "127.0.0.1:5555";

    REQUIRE(args.adb_path == std::filesystem::path{R"(C:\adb\adb.exe)"});
    REQUIRE(args.serial   == "127.0.0.1:5555");
}

TEST_CASE("SmokCliError message holds assigned value",
          "[SmokeCliParser][fields]")
{
    SmokCliError err;
    err.message = "something went wrong";

    REQUIRE(err.message == "something went wrong");
}
