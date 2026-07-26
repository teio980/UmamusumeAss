#include <algorithm>
#include <chrono>
#include <cstdlib>
#include <iostream>
#include <memory>
#include <string>
#include <thread>
#include <vector>

namespace {

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
        std::cout << "List of devices attached\ntest-serial\tdevice\n";
        return 0;
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
