#include <algorithm>
#include <chrono>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <memory>
#include <string>
#include <thread>
#include <vector>

namespace {

[[nodiscard]] std::string environment_value(char const* name)
{
    char* raw = nullptr;
    std::size_t size = 0;
    if (_dupenv_s(&raw, &size, name) != 0 || raw == nullptr)
    {
        return {};
    }
    std::unique_ptr<char, decltype(&std::free)> value(raw, &std::free);
    return value.get();
}

[[nodiscard]] bool offline_then_ready_enabled()
{
    return environment_value("UMA_FAKE_ADB_OFFLINE_THEN_READY") == "1";
}

[[nodiscard]] std::filesystem::path state_file_path()
{
    auto const configured = environment_value("UMA_FAKE_ADB_STATE_FILE");
    return configured.empty()
        ? std::filesystem::temp_directory_path() / "uma-fake-adb-offline.state"
        : std::filesystem::path(configured);
}

[[nodiscard]] std::size_t next_devices_invocation()
{
    auto const path = state_file_path();
    std::size_t invocation = 0;
    {
        std::ifstream input(path);
        input >> invocation;
    }
    {
        std::ofstream output(path, std::ios::trunc);
        output << invocation + 1;
    }
    return invocation;
}

void apply_delay()
{
    char* raw = nullptr;
    std::size_t size = 0;
    if (_dupenv_s(&raw, &size, "UMA_FAKE_ADB_DELAY_MS") != 0 || raw == nullptr)
    {
        return;
    }
    std::unique_ptr<char, decltype(&std::free)> value(raw, &std::free);
    auto const delay = std::stoi(value.get());
    if (delay > 0) std::this_thread::sleep_for(std::chrono::milliseconds(delay));
}

[[nodiscard]] bool contains(std::vector<std::string> const& args, std::string const& value)
{
    return std::find(args.begin(), args.end(), value) != args.end();
}

}

int main(int argc, char** argv)
{
    apply_delay();
    std::vector<std::string> args(argv + 1, argv + argc);

    if (args == std::vector<std::string>{"devices"})
    {
        if (offline_then_ready_enabled())
        {
            auto const state = next_devices_invocation() == 0 ? "offline" : "device";
            std::cout << "List of devices attached\n127.0.0.1:16384\t"
                      << state << "\n";
        }
        else
        {
            std::cout << "List of devices attached\ntest-serial\tdevice\n";
        }
        return 0;
    }
    if (offline_then_ready_enabled() && contains(args, "connect"))
    {
        auto const connect_position = std::find(args.begin(), args.end(), "connect");
        if (connect_position + 1 != args.end())
        {
            std::cout << "connected to " << *(connect_position + 1) << "\n";
            return 0;
        }
    }
    if (contains(args, "get-state"))
    {
        std::cout << "device\n";
        return 0;
    }
    if (contains(args, "sys.boot_completed"))
    {
        std::cout << "1\n";
        return 0;
    }
    if (contains(args, "android_id"))
    {
        std::cout << "0123456789abcdef\n";
        return 0;
    }
    if (contains(args, "ro.build.version.release"))
    {
        std::cout << "14\n";
        return 0;
    }
    if (contains(args, "size"))
    {
        std::cout << "Physical size: 1080x1920\n";
        return 0;
    }

    std::cerr << "unexpected fake adb arguments\n";
    return 2;
}
