#include <algorithm>
#include <array>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <memory>
#include <string>
#include <string_view>
#include <thread>
#include <vector>
#if defined(__clang__)
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wundef"
#pragma clang diagnostic ignored "-Wold-style-cast"
#endif
#include <zlib.h>
#if defined(__clang__)
#pragma clang diagnostic pop
#endif

#if defined(_WIN32)
#include <fcntl.h>
#include <io.h>
#include <windows.h>
#else
#include <unistd.h>
#include <sys/wait.h>
#include <signal.h>
#if defined(__APPLE__)
#include <mach-o/dyld.h>
#endif
#endif

namespace {

[[nodiscard]] std::string environment_value(char const* name)
{
#if defined(_WIN32)
    std::wstring wide_name;
    if (name)
    {
        int needed = ::MultiByteToWideChar(CP_UTF8, 0, name, -1, nullptr, 0);
        if (needed > 0)
        {
            wide_name.resize(static_cast<std::size_t>(needed - 1));
            ::MultiByteToWideChar(CP_UTF8, 0, name, -1, wide_name.data(), needed);
        }
    }
    std::vector<wchar_t> buffer(32768);
    auto const length = ::GetEnvironmentVariableW(wide_name.c_str(), buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length > 0 && length < buffer.size())
    {
        int size_needed = ::WideCharToMultiByte(CP_UTF8, 0, buffer.data(), static_cast<int>(length), nullptr, 0, nullptr, nullptr);
        std::string result(size_needed, '\0');
        ::WideCharToMultiByte(CP_UTF8, 0, buffer.data(), static_cast<int>(length), result.data(), size_needed, nullptr, nullptr);
        return result;
    }
    char* raw = nullptr;
    std::size_t size = 0;
    if (_dupenv_s(&raw, &size, name) != 0 || raw == nullptr)
    {
        return {};
    }
    std::unique_ptr<char, decltype(&std::free)> value(raw, &std::free);
    return value.get();
#else
    char const* val = std::getenv(name);
    return val ? std::string(val) : std::string{};
#endif
}

[[nodiscard]] std::filesystem::path to_path(std::string const& utf8)
{
#if defined(_WIN32)
    if (utf8.empty()) return {};
    int needed = ::MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
    if (needed > 0)
    {
        std::wstring wide(static_cast<std::size_t>(needed), L'\0');
        ::MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), wide.data(), needed);
        return std::filesystem::path(wide);
    }
    return std::filesystem::path(utf8);
#else
    return std::filesystem::path(utf8);
#endif
}

void record_arguments(std::vector<std::string> const& args)
{
    auto const configured = environment_value("UMA_FAKE_ADB_COMMANDS_FILE");
    if (configured.empty()) return;
    std::ofstream output(configured, std::ios::app | std::ios::binary);
    for (std::size_t index = 0; index < args.size(); ++index)
    {
        if (index != 0) output << '\t';
        output << args[index];
    }
    output << '\n';
}

#if defined(_WIN32)
[[nodiscard]] std::string to_utf8(std::wstring_view value)
{
    if (value.empty()) return {};
    auto const required = ::WideCharToMultiByte(
        CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
        nullptr, 0, nullptr, nullptr);
    if (required <= 0) return {};
    std::string result(static_cast<std::size_t>(required), '\0');
    auto const written = ::WideCharToMultiByte(
        CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
        result.data(), required, nullptr, nullptr);
    if (written != required) return {};
    return result;
}
#endif

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

[[nodiscard]] std::size_t next_get_state_invocation()
{
    auto const configured = environment_value("UMA_FAKE_ADB_GET_STATE_FILE");
    if (configured.empty()) return 0;
    auto const path = std::filesystem::path(configured);
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

[[nodiscard]] bool consume_recovery_failure()
{
    auto const configured = environment_value("UMA_FAKE_ADB_RECOVERY_ONCE_FILE");
    if (configured.empty()) return false;
    auto const path = std::filesystem::path(configured);
    bool already_consumed = false;
    {
        std::ifstream input(path);
        int value = 0;
        if (input >> value) already_consumed = value != 0;
    }
    if (already_consumed) return false;
    {
        std::ofstream output(path, std::ios::trunc);
        output << '1';
    }
    return true;
}

void apply_delay()
{
    auto const val = environment_value("UMA_FAKE_ADB_DELAY_MS");
    if (val.empty()) return;
    try
    {
        auto const delay = std::stoi(val);
        if (delay > 0) std::this_thread::sleep_for(std::chrono::milliseconds(delay));
    }
    catch (...) {}
}

[[nodiscard]] bool contains(std::vector<std::string> const& args, std::string const& value)
{
    return std::find(args.begin(), args.end(), value) != args.end();
}

[[nodiscard]] bool contains_fragment(
    std::vector<std::string> const& args, std::string const& fragment)
{
    return std::any_of(args.begin(), args.end(), [&fragment](std::string const& argument) {
        return argument.find(fragment) != std::string::npos;
    });
}

[[nodiscard]] std::pair<std::uint32_t, std::uint32_t> fake_resolution(
    std::vector<std::string> const& args)
{
    auto const display_specific = contains(args, "-d")
        || contains_fragment(args, "screencap -d ");
    auto text = display_specific
        ? environment_value("UMA_FAKE_ADB_DISPLAY_RESOLUTION") : std::string{};
    if (text.empty()) text = environment_value("UMA_FAKE_ADB_RESOLUTION");
    if (text.empty()) return {1080, 1920};
    auto const separator = text.find('x');
    if (separator == std::string::npos) return {1080, 1920};
    try
    {
        auto const width = static_cast<std::uint32_t>(std::stoul(text.substr(0, separator)));
        auto const height = static_cast<std::uint32_t>(std::stoul(text.substr(separator + 1)));
        if (width == 0 || height == 0 || width > 16384 || height > 16384)
            return {1080, 1920};
        return {width, height};
    }
    catch (...)
    {
        return {1080, 1920};
    }
}

[[nodiscard]] std::string raw_screenshot(std::vector<std::string> const& args)
{
    auto const [width, height] = fake_resolution(args);
    auto const pixel_bytes = static_cast<std::size_t>(width) * height * 4;
    std::string bytes;
    bytes.reserve(12 + pixel_bytes);
    auto append_u32 = [&bytes](std::uint32_t value) {
        for (int shift = 0; shift < 32; shift += 8)
            bytes.push_back(static_cast<char>((value >> shift) & 0xff));
    };
    append_u32(width);
    append_u32(height);
    append_u32(1);
    for (std::size_t pixel = 0; pixel < pixel_bytes / 4; ++pixel)
    {
        auto const channel = static_cast<char>((pixel * 31) & 0xff);
        bytes.push_back(channel);
        bytes.push_back(static_cast<char>(channel ^ 0x5a));
        bytes.push_back(static_cast<char>(channel ^ 0xa5));
        bytes.push_back(static_cast<char>(0xff));
    }
    return bytes;
}

[[nodiscard]] std::string gzip_compress(std::string const& input)
{
    z_stream stream{};
    if (deflateInit2_(&stream, Z_BEST_SPEED, Z_DEFLATED,
                      MAX_WBITS + 16, 8, Z_DEFAULT_STRATEGY,
                      ZLIB_VERSION, static_cast<int>(sizeof(z_stream))) != Z_OK)
        return {};
    auto const bound = deflateBound(&stream, static_cast<uLong>(input.size()));
    std::string output(static_cast<std::size_t>(bound), '\0');
    stream.next_in = reinterpret_cast<Bytef*>(const_cast<char*>(input.data()));
    stream.avail_in = static_cast<uInt>(input.size());
    stream.next_out = reinterpret_cast<Bytef*>(output.data());
    stream.avail_out = static_cast<uInt>(output.size());
    auto const result = deflate(&stream, Z_FINISH);
    auto const output_size = stream.total_out;
    deflateEnd(&stream);
    if (result != Z_STREAM_END) return {};
    output.resize(static_cast<std::size_t>(output_size));
    return output;
}

inline void sleep_ms(int ms)
{
    std::this_thread::sleep_for(std::chrono::milliseconds(ms));
}

[[nodiscard]] bool spawn_pipe_child(
    bool wait_for_child,
    bool continuous_writer = false,
    std::string const& pid_path_override = {},
    bool inherit_handles = true)
{
#if defined(_WIN32)
    wchar_t executable[MAX_PATH]{};
    auto const length = ::GetModuleFileNameW(nullptr, executable, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) return false;

    std::wstring command_line = L"\"";
    command_line.append(executable, length);
    command_line += continuous_writer
        ? L"\" --test-pipe-child-continuous"
        : L"\" --test-pipe-child";
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!::CreateProcessW(
            executable,
            command_line.data(),
            nullptr,
            nullptr,
            inherit_handles ? TRUE : FALSE,
            CREATE_NO_WINDOW,
            nullptr,
            nullptr,
            &startup,
            &process))
        return false;

    auto const pid_path = pid_path_override.empty()
        ? environment_value("UMA_FAKE_ADB_CHILD_PID_FILE") : pid_path_override;
    if (!pid_path.empty())
    {
        std::ofstream pid_file(pid_path, std::ios::out | std::ios::trunc);
        pid_file << process.dwProcessId;
    }
    ::CloseHandle(process.hThread);
    if (wait_for_child)
    {
        ::WaitForSingleObject(process.hProcess, 30000);
        ::CloseHandle(process.hProcess);
    }
    else
    {
        ::CloseHandle(process.hProcess);
    }
    return true;
#else
    char executable[PATH_MAX]{};
#if defined(__APPLE__)
    uint32_t size = sizeof(executable);
    if (_NSGetExecutablePath(executable, &size) != 0) return false;
#else
    ssize_t len = readlink("/proc/self/exe", executable, sizeof(executable) - 1);
    if (len <= 0) return false;
    executable[len] = '\0';
#endif
    pid_t pid = fork();
    if (pid < 0) return false;
    if (pid == 0)
    {
        if (!inherit_handles)
        {
            close(STDIN_FILENO);
        }
        char const* flag = continuous_writer ? "--test-pipe-child-continuous" : "--test-pipe-child";
        char* const args[] = {executable, const_cast<char*>(flag), nullptr};
        execv(executable, args);
        _exit(1);
    }
    auto const pid_path = pid_path_override.empty()
        ? environment_value("UMA_FAKE_ADB_CHILD_PID_FILE") : pid_path_override;
    if (!pid_path.empty())
    {
        std::ofstream pid_file(pid_path, std::ios::out | std::ios::trunc);
        pid_file << pid;
    }
    if (wait_for_child)
    {
        int status = 0;
        waitpid(pid, &status, 0);
    }
    return true;
#endif
}

}

#if defined(_WIN32)
int wmain(int argc, wchar_t** argv)
{
    _setmode(_fileno(stdout), _O_BINARY);
    _setmode(_fileno(stderr), _O_BINARY);
    _setmode(_fileno(stdin), _O_BINARY);
    apply_delay();
    std::vector<std::string> args;
    args.reserve(static_cast<std::size_t>(argc > 0 ? argc - 1 : 0));
    for (int index = 1; index < argc; ++index)
        args.push_back(to_utf8(argv[index]));
    record_arguments(args);
#else
int main(int argc, char** argv)
{
    apply_delay();
    std::vector<std::string> args;
    args.reserve(static_cast<std::size_t>(argc > 0 ? argc - 1 : 0));
    for (int index = 1; index < argc; ++index)
        args.push_back(argv[index]);
    record_arguments(args);
#endif

    auto const touch_mode = environment_value("UMA_FAKE_ADB_INTERACTIVE_TOUCH_MODE");
    if (!touch_mode.empty() && contains(args, "shell") && contains(args, "sh")
        && contains(args, "-c") && contains_fragment(args, "__UMA_TOUCH_PID="))
    {
        auto const input_path = environment_value("UMA_FAKE_ADB_INTERACTIVE_TOUCH_INPUT_FILE");
        std::ofstream input_log(input_path, std::ios::out | std::ios::trunc | std::ios::binary);
        if (touch_mode == "delayed-owner")
        {
            auto const started = environment_value("UMA_FAKE_ADB_TOUCH_PROCESS_STARTED_FILE");
            if (!started.empty())
            {
                std::ofstream marker(started, std::ios::out | std::ios::trunc);
                marker << '1';
            }
            sleep_ms(150);
            std::cerr << "__UMA_TOUCH_PID=42424:123456\n";
            std::cerr.flush();
        }
        else if (touch_mode == "maatouch-split")
        {
            std::cout << "^ 10 4000";
            std::cout.flush();
            sleep_ms(20);
            std::cerr << "__UMA_TOUCH_PID=42424:123456\nowner diagnostic chunk\n";
            std::cerr.flush();
            sleep_ms(20);
            std::cout << " 3000 0\n$ 255\n";
        }
        else if (touch_mode == "stdout-handshake")
        {
            std::cerr << "__UMA_TOUCH_PID=42424:123456\n";
            std::cout << "^ 10 4000 3000 0\n$ 0\n";
        }
        else if (touch_mode == "missing-handshake")
        {
            std::cerr << "__UMA_TOUCH_PID=42424:123456\n";
            std::cerr << "$ 255\n";
        }
        else
        {
            std::cerr << "__UMA_TOUCH_PID=42424:123456\n";
            std::cerr << "^ 10 4000";
            std::cerr.flush();
            sleep_ms(20);
            std::cerr << " 3000 0\n$ 0\n";
        }
        std::cerr.flush();
        std::string line;
        while (std::getline(std::cin, line))
        {
            if (input_log) input_log << line << '\n';
            input_log.flush();
            if (line.starts_with("d "))
            {
                auto const marker = environment_value("UMA_FAKE_ADB_TOUCH_PROTOCOL_STARTED_FILE");
                if (!marker.empty())
                {
                    std::ofstream started(marker, std::ios::out | std::ios::trunc);
                    started << '1';
                }
            }
        }
        return 0;
    }

    if (contains(args, "--test-interactive-touch"))
    {
        std::cerr << "^ 10 4095";
        std::cerr.flush();
        sleep_ms(25);
        std::cerr << " 4095 255\n$ 0\n";
        std::cerr.flush();
        auto const configured = args.size() > 1
            ? args[1] : environment_value("UMA_FAKE_ADB_INTERACTIVE_INPUT_FILE");
        std::ofstream input_log(
            configured, std::ios::out | std::ios::trunc | std::ios::binary);
        std::string line;
        while (std::getline(std::cin, line))
        {
            if (input_log) input_log << line << '\n';
            input_log.flush();
        }
        return 0;
    }
    if (contains(args, "--test-interactive-blocked-stdin"))
    {
        auto const pid_path = args.size() > 1 ? args[1] : std::string{};
        if (!spawn_pipe_child(false, false, pid_path)) return 4;
        std::cerr << "^ 10 4095 4095 255\n$ 0\n";
        std::cerr.flush();
        std::this_thread::sleep_for(std::chrono::seconds(30));
        return 0;
    }
    if (contains(args, "--test-interactive-broken-stdin"))
    {
        auto const pid_path = args.size() > 1 ? args[1] : std::string{};
        if (!spawn_pipe_child(false, false, pid_path, false)) return 4;
        std::cerr << "^ 10 4095 4095 255\n$ 0\n";
        std::cerr.flush();
#if defined(_WIN32)
        auto const input = ::GetStdHandle(STD_INPUT_HANDLE);
        if (input && input != INVALID_HANDLE_VALUE) ::CloseHandle(input);
        ::SetStdHandle(STD_INPUT_HANDLE, nullptr);
#else
        close(STDIN_FILENO);
#endif
        std::this_thread::sleep_for(std::chrono::seconds(30));
        return 0;
    }

    if (contains(args, "--test-pipe-child"))
    {
        std::this_thread::sleep_for(std::chrono::seconds(30));
        std::cout << "late child output";
        return 0;
    }
    if (contains(args, "--test-pipe-child-continuous"))
    {
        std::string const bytes(4096, 'x');
        for (;;)
        {
            std::cout.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
            std::cout.flush();
            sleep_ms(1);
        }
    }
    if (contains(args, "--test-binary-output"))
    {
        std::vector<char> bytes(128 * 1024);
        for (std::size_t index = 0; index < bytes.size(); ++index)
            bytes[index] = static_cast<char>((index * 31) & 0xff);
        std::cout.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
        return 0;
    }
    if (contains(args, "--test-separate-streams"))
    {
        constexpr char stdout_bytes[]{'o', 'u', 't', '\0', 's', 't', 'd', 'o', 'u', 't'};
        std::cout.write(stdout_bytes, sizeof(stdout_bytes));
        std::cerr << "stderr-only";
        return 0;
    }
    if (contains(args, "--test-parent-exits-with-child"))
        return spawn_pipe_child(false) ? 0 : 4;
    if (contains(args, "--test-parent-exits-with-writer"))
        return spawn_pipe_child(false, true) ? 0 : 4;
    if (contains(args, "--test-parent-hangs-with-child"))
    {
        if (!spawn_pipe_child(false)) return 4;
        std::this_thread::sleep_for(std::chrono::seconds(30));
        return 0;
    }

    if (contains_fragment(args, "screencap"))
    {
        auto const mode = environment_value("UMA_FAKE_ADB_CAPTURE_MODE");
        auto const exec_out = contains(args, "exec-out");
        auto const png = contains(args, "-p");
        auto const gzip = contains_fragment(args, "gzip");
        if ((mode == "raw" || mode == "raw-crlf" || mode == "raw-truncated")
            && exec_out && !png && !gzip)
        {
            auto bytes = raw_screenshot(args);
            if (mode == "raw-crlf")
            {
                std::string text_mode;
                text_mode.reserve(bytes.size() + bytes.size() / 128);
                for (char value : bytes)
                {
                    if (value == '\n') text_mode.push_back('\r');
                    text_mode.push_back(value);
                }
                bytes = std::move(text_mode);
            }
            else if (mode == "raw-truncated")
            {
                bytes.resize((std::min)(bytes.size(), std::size_t{16}));
            }
            std::cout.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
            return 0;
        }
        if ((mode == "gzip" || mode == "gzip-truncated" || mode == "gzip-corrupt") && gzip)
        {
            auto bytes = gzip_compress(raw_screenshot(args));
            if (bytes.empty()) return 3;
            if (mode == "gzip-truncated") bytes.resize(bytes.size() / 2);
            if (mode == "gzip-corrupt") bytes[bytes.size() / 2] ^= static_cast<char>(0x80);
            std::cout.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
            return 0;
        }
        auto const shell_png = contains(args, "shell") && png;
        if ((mode.empty() || mode == "png") && exec_out && png)
        {
            auto const configured = environment_value("UMA_FAKE_ADB_SCREENSHOT_PATH");
            auto const image_path = to_path(configured.empty() ? UMA_FAKE_PNG_PATH : configured);
            std::ifstream image(image_path, std::ios::binary);
            if (!image) return 3;
            std::cout << image.rdbuf();
            return 0;
        }
        if (mode == "shell-png" && shell_png)
        {
            auto const configured = environment_value("UMA_FAKE_ADB_SCREENSHOT_PATH");
            auto const image_path = to_path(configured.empty() ? UMA_FAKE_PNG_PATH : configured);
            std::ifstream image(image_path, std::ios::binary);
            if (!image) return 3;
            std::cout << image.rdbuf();
            return 0;
        }
        if (!mode.empty() && mode != "raw" && mode != "raw-crlf"
            && mode != "raw-truncated" && mode != "gzip"
            && mode != "gzip-truncated" && mode != "gzip-corrupt"
            && mode != "png" && mode != "shell-png")
            return 3;
        if (mode == "raw" || mode == "raw-crlf" || mode == "raw-truncated"
            || mode == "gzip" || mode == "gzip-truncated" || mode == "gzip-corrupt"
            || mode == "shell-png") return 3;
        auto const configured = environment_value("UMA_FAKE_ADB_SCREENSHOT_PATH");
        auto const image_path = to_path(configured.empty() ? UMA_FAKE_PNG_PATH : configured);
        std::ifstream image(image_path, std::ios::binary);
        if (!image) return 3;
        std::cout << image.rdbuf();
        return 0;
    }

    if (contains(args, "pm") && contains(args, "path"))
    {
        auto const stderr_mode = environment_value("UMA_FAKE_ADB_STDERR_MODE");
        if (stderr_mode == "invalid-utf8")
        {
            constexpr char invalid_utf8[]{static_cast<char>(0xff), static_cast<char>(0xfe), 'x'};
            std::cerr.write(invalid_utf8, sizeof(invalid_utf8));
            return 9;
        }
        if (stderr_mode == "large")
        {
            std::string const diagnostic(128 * 1024, 'e');
            std::cerr.write(diagnostic.data(), static_cast<std::streamsize>(diagnostic.size()));
            return 9;
        }
        auto const package = args.back();
        if (package == "com.example.game" || package == "com.cygames.umamusume")
            std::cout << "package:/data/app/com.example.game/base.apk\n";
        else
            std::cout << "Failure [package not found]\n";
        return 0;
    }

    if (contains(args, "resolve-activity"))
    {
        auto const mode = environment_value("UMA_FAKE_ADB_RESOLVE_ACTIVITY_MODE");
        if (mode == "fail")
        {
            std::cerr << "resolver unavailable\n";
            return 7;
        }
        if (mode == "uppercase-no-activity")
        {
            std::cout << "NO ACTIVITY FOUND\n";
            return 0;
        }
        if (mode == "mixed-case-no-activity")
        {
            std::cout << "nO AcTiViTy found\n";
            return 0;
        }
        std::cout << "priority=0 preferredOrder=0 match=0x108000 isDefault=true\n"
                     "com.example.game/.MainActivity\n";
        return 0;
    }
    if (contains(args, "am") && contains(args, "start"))
    {
        if (environment_value("UMA_FAKE_ADB_AM_START_FAIL") == "1")
        {
            std::cerr << "unable to start activity\n";
            return 7;
        }
        std::cout << "Starting: Intent { act=android.intent.action.MAIN }\n";
        return 0;
    }
    if (contains(args, "am") && contains(args, "force-stop"))
    {
        if (environment_value("UMA_FAKE_ADB_AM_STOP_FAIL") == "1")
        {
            std::cerr << "force-stop failed\n";
            return 7;
        }
        return 0;
    }
    if (contains(args, "pidof"))
    {
        auto const mode = environment_value("UMA_FAKE_ADB_PACKAGE_PROCESS_MODE");
        auto const probe_file = environment_value("UMA_FAKE_ADB_PROCESS_PROBE_FILE");
        if (!probe_file.empty())
        {
            int probes = 0;
            { std::ifstream input(probe_file); input >> probes; }
            { std::ofstream output(probe_file); output << probes + 1; }
            if (mode == "delayed-running" && probes < 2) return 0;
        }
        if (mode == "pidof-fail-ps-running" || mode == "pidof-fail-ps-lookalike"
            || mode == "pidof-fail-ps-empty")
        {
            std::cerr << "pidof failed\n";
            return 1;
        }
        if (mode == "pidof-empty") return 0;
        std::cout << "1234\n";
        return 0;
    }
    if (contains(args, "ps") && contains(args, "-A"))
    {
        auto const mode = environment_value("UMA_FAKE_ADB_PACKAGE_PROCESS_MODE");
        if (mode == "pidof-fail-ps-running")
            std::cout << "USER PID PPID NAME\nu0_a1 1234 1 com.example.game\n";
        else if (mode == "pidof-fail-ps-lookalike")
            std::cout << "USER PID PPID NAME\nu0_a1 1234 1 com.example.game.extra\n";
        else
            std::cout << "USER PID PPID NAME\n";
        return 0;
    }

    if (contains(args, "getevent") && contains(args, "-lp"))
    {
        auto const started_marker = environment_value("UMA_FAKE_ADB_GETEVENT_STARTED_FILE");
        if (!started_marker.empty())
        {
            std::ofstream started(started_marker, std::ios::trunc);
            started << '1';
        }
        auto const delay = environment_value("UMA_FAKE_ADB_GETEVENT_DELAY_MS");
        if (!delay.empty())
        {
            auto const milliseconds = std::stoi(delay);
            if (milliseconds > 0)
                std::this_thread::sleep_for(std::chrono::milliseconds(milliseconds));
        }
        if (environment_value("UMA_FAKE_ADB_GETEVENT_FAIL") == "1")
            return 7;
        std::cout << "add device 1: /dev/input/event5\n"
                     "  name: \"Fake multi-touch screen\"\n"
                     "  events:\n"
                     "    ABS (0003): ABS_MT_POSITION_X : value 0, min 0, max 4095, fuzz 0, flat 0, resolution 0\n"
                     "               ABS_MT_POSITION_Y : value 0, min 0, max 4095, fuzz 0, flat 0, resolution 0\n"
                     "    KEY (0001): BTN_TOOL_FINGER BTN_TOUCH\n";
        return 0;
    }

    if (contains(args, "sendevent") || contains_fragment(args, "sendevent"))
    {
        if (contains_fragment(args, "3 57 123"))
        {
            auto const marker = environment_value("UMA_FAKE_ADB_TOUCH_DOWN_STARTED_FILE");
            if (!marker.empty())
            {
                std::ofstream started(marker, std::ios::trunc);
                started << '1';
            }
            auto const delay = environment_value("UMA_FAKE_ADB_TOUCH_DOWN_DELAY_MS");
            if (!delay.empty())
            {
                auto const milliseconds = std::stoi(delay);
                if (milliseconds > 0)
                    std::this_thread::sleep_for(std::chrono::milliseconds(milliseconds));
            }
        }
        return 0;
    }

    if (contains_fragment(args, "kill -TERM "))
    {
        if (environment_value("UMA_FAKE_ADB_TOUCH_STOP_FAIL") == "1")
            return 7;
        std::cout << "stopped\n";
        return 0;
    }
    if (contains(args, "push") || contains(args, "chmod"))
        return 0;

    if (contains(args, "input")
        && (contains(args, "tap") || contains(args, "swipe")
            || contains(args, "keyevent") || contains(args, "text")))
    {
        if (environment_value("UMA_FAKE_ADB_INPUT_FAIL") == "1") return 7;
        return 0;
    }

    if (args == std::vector<std::string>{"devices"})
    {
        auto const device_mode = environment_value("UMA_FAKE_ADB_DEVICES_MODE");
        auto serial = environment_value("UMA_FAKE_ADB_DEVICES_SERIAL");
        if (serial.empty()) serial = "test-serial";
        if (device_mode == "missing")
        {
            std::cout << "List of devices attached\n";
            return 0;
        }
        if (device_mode == "offline")
        {
            std::cout << "List of devices attached\n" << serial << "\toffline\n";
            return 0;
        }
        if (offline_then_ready_enabled())
        {
            auto const state = next_devices_invocation() == 0 ? "offline" : "device";
            std::cout << "List of devices attached\n127.0.0.1:16384\t"
                      << state << "\n";
        }
        else
        {
            std::cout << "List of devices attached\n" << serial << "\tdevice\n";
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
        auto const mode = environment_value("UMA_FAKE_ADB_MONITOR_MODE");
        auto const invocation = next_get_state_invocation();
        if (mode == "recover_block" && invocation == 1)
        {
            std::cout << "offline\n";
            return 0;
        }
        if (mode == "recover_block" && invocation >= 2)
        {
            auto const marker = environment_value("UMA_FAKE_ADB_RECOVERY_MARKER");
            if (!marker.empty())
            {
                std::ofstream output(marker, std::ios::trunc);
                output << "recovery get-state started\n";
            }
            std::this_thread::sleep_for(std::chrono::seconds(30));
            std::cout << "device\n";
            return 0;
        }
        auto const disconnected = environment_value("UMA_FAKE_ADB_DISCONNECTED") == "1"
            || mode == "offline"
            || (mode == "recover_once" && consume_recovery_failure());
        auto const state_log = environment_value("UMA_FAKE_ADB_STATE_LOG_FILE");
        if (!state_log.empty())
        {
            std::ofstream output(state_log, std::ios::app);
            output << invocation << '\t' << mode << '\t'
                   << (disconnected ? "offline" : "device") << '\n';
        }
        std::cout << (disconnected ? "offline\n" : "device\n");
        return 0;
    }
    if (contains(args, "shell") && contains(args, "sh") && contains(args, "-c")
        && contains_fragment(args, "__UMA_GEOMETRY_ORIENTATION__"))
    {
        auto const mode = environment_value("UMA_FAKE_ADB_GEOMETRY_BATCH_MODE");
        if (mode == "unsupported") return 2;
        if (mode == "malformed") { std::cout << "unexpected geometry output\n"; return 0; }
        auto const resolution = environment_value("UMA_FAKE_ADB_RESOLUTION");
        auto const orientation = environment_value("UMA_FAKE_ADB_ORIENTATION");
        std::cout << "Physical size: " << (resolution.empty() ? "1080x1920" : resolution)
                  << "\n__UMA_GEOMETRY_ORIENTATION__\nSurfaceOrientation: "
                  << (orientation.empty() ? "0" : orientation) << "\n";
        return 0;
    }
    if (contains(args, "dumpsys") && contains(args, "input"))
    {
        auto const orientation = environment_value("UMA_FAKE_ADB_ORIENTATION");
        std::cout << "SurfaceOrientation: "
                  << (orientation.empty() ? "0" : orientation) << "\n";
        return 0;
    }
    if (contains(args, "dumpsys") && contains(args, "display"))
    {
        auto const display_id = environment_value("UMA_FAKE_ADB_DISPLAY_ID");
        auto const orientation = environment_value("UMA_FAKE_ADB_DISPLAY_ORIENTATION");
        std::cout << "DisplayInfo{\"Other display\", displayId 70, rotation 1}\n"
                  << "DisplayInfo{\"Fake display\", displayId "
                  << (display_id.empty() ? "7" : display_id)
                  << ", rotation " << (orientation.empty() ? "0" : orientation)
                  << "}\n";
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
        auto override_size = contains(args, "-d")
            ? environment_value("UMA_FAKE_ADB_DISPLAY_RESOLUTION") : std::string{};
        if (override_size.empty()) override_size = environment_value("UMA_FAKE_ADB_RESOLUTION");
        std::cout << "Physical size: "
                  << (override_size.empty() ? "1080x1920" : override_size)
                  << "\n";
        return 0;
    }

    std::cerr << "unexpected fake adb arguments\n";
    return 2;
}
