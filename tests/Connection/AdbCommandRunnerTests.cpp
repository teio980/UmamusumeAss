





#include <catch2/catch_test_macros.hpp>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <exception>
#include <filesystem>
#include <fstream>
#include <future>
#include <memory>
#include <optional>
#include <stop_token>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <winsock2.h>
#include <windows.h>
#include <tlhelp32.h>
#else
#include <unistd.h>
#include <signal.h>
#include <sys/wait.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <cerrno>

using DWORD = uint32_t;
using SOCKET = int;
constexpr int INVALID_SOCKET = -1;
constexpr int SOCKET_ERROR = -1;
inline int closesocket(int s) { return ::close(s); }
constexpr DWORD WAIT_OBJECT_0 = 0;
constexpr DWORD WAIT_TIMEOUT = 0x00000102L;
constexpr DWORD WAIT_FAILED = 0xFFFFFFFFL;

struct ScopedWinsock { int const result{0}; };
#endif

inline uint32_t current_process_id() noexcept
{
#if defined(_WIN32)
    return ::GetCurrentProcessId();
#else
    return static_cast<uint32_t>(getpid());
#endif
}

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
        std::chrono::milliseconds  ,
        std::size_t  ,
        bool leave_children_running_on_success) override
    {
        UmaAssistant::IWin32Process::Result r;
        r.exit_code       = result_.exit_code;
        r.standard_output = result_.standard_output;
        r.standard_error  = result_.standard_error;
        r.started         = result_.started;
        r.timed_out       = result_.timed_out;
        r.canceled        = result_.canceled;
        observed_leave_children_running_on_success_ = leave_children_running_on_success;
        return r;
    }

    [[nodiscard]] bool observed_leave_children_running_on_success() const noexcept
    {
        return observed_leave_children_running_on_success_;
    }

private:
    ScriptedResult result_;
    bool observed_leave_children_running_on_success_{};
};

class ScopedEnvironment final
{
public:
    ScopedEnvironment(char const* name, std::string value)
        : name_{name}
    {
#if defined(_WIN32)
        char* previous = nullptr;
        std::size_t size = 0;
        if (_dupenv_s(&previous, &size, name) == 0 && previous != nullptr)
        {
            previous_ = previous;
            std::free(previous);
            existed_ = true;
        }
        _putenv_s(name, value.c_str());
#else
        char const* val = std::getenv(name);
        if (val != nullptr)
        {
            previous_ = val;
            existed_ = true;
        }
        setenv(name, value.c_str(), 1);
#endif
    }

    ~ScopedEnvironment()
    {
#if defined(_WIN32)
        _putenv_s(name_.c_str(), existed_ ? previous_.c_str() : "");
#else
        if (existed_)
        {
            setenv(name_.c_str(), previous_.c_str(), 1);
        }
        else
        {
            unsetenv(name_.c_str());
        }
#endif
    }

    ScopedEnvironment(ScopedEnvironment const&) = delete;
    ScopedEnvironment& operator=(ScopedEnvironment const&) = delete;

private:
    std::string name_;
    std::string previous_;
    bool existed_{};
};

#if defined(_WIN32)
struct ScopedWinsock
{
    WSADATA data{};
    int const result{::WSAStartup(MAKEWORD(2, 2), &data)};
    ~ScopedWinsock() { if (result == 0) ::WSACleanup(); }
};
#endif

[[nodiscard]] std::optional<std::uint16_t> find_free_tcp_port()
{
    SOCKET const listener = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (listener == INVALID_SOCKET) return std::nullopt;
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = 0;
    if (::bind(listener, reinterpret_cast<sockaddr*>(&address), sizeof(address)) == SOCKET_ERROR)
    {
        ::closesocket(listener);
        return std::nullopt;
    }
#if defined(_WIN32)
    int address_size = sizeof(address);
#else
    socklen_t address_size = sizeof(address);
#endif
    auto const result = ::getsockname(listener,
        reinterpret_cast<sockaddr*>(&address), &address_size);
    ::closesocket(listener);
    return result == 0 ? std::optional{ntohs(address.sin_port)} : std::nullopt;
}

[[nodiscard]] bool tcp_port_is_listening(std::uint16_t port)
{
    SOCKET const probe = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (probe == INVALID_SOCKET) return false;
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = htons(port);
    bool const listening = ::connect(probe,
        reinterpret_cast<sockaddr*>(&address), sizeof(address)) == 0;
    ::closesocket(probe);
    return listening;
}

struct IsolatedAdbServerCleanup
{
    std::filesystem::path executable;
    std::string port;
    bool armed{};
    ~IsolatedAdbServerCleanup()
    {
        if (!armed) return;
        auto process = UmaAssistant::create_win32_process();
        UmaAssistant::AdbCommandRunnerWin32 runner{*process};
        (void)runner.run(
            AdbInvocation{executable, {"-P", port, "kill-server"}},
            10s, std::stop_token{});
    }
};

[[nodiscard]] std::filesystem::path child_pid_file()
{
    return std::filesystem::temp_directory_path()
        / ("uma-adb-runner-child-" + std::to_string(current_process_id()) + ".pid");
}

[[nodiscard]] std::filesystem::path interactive_child_pid_file()
{
    return std::filesystem::path{CMAKE_SOURCE_DIR} / "build"
        / ("uma-interactive-child-" + std::to_string(current_process_id()) + ".pid");
}

[[nodiscard]] std::optional<DWORD> read_child_pid(std::filesystem::path const& pid_path)
{
    std::ifstream input(pid_path);
    DWORD pid = 0;
    input >> pid;
    if (!input || pid == 0) return std::nullopt;
    return pid;
}

[[nodiscard]] std::optional<DWORD> wait_for_child_pid(
    std::filesystem::path const& pid_path,
    std::chrono::milliseconds timeout)
{
    auto const deadline = std::chrono::steady_clock::now() + timeout;
    do
    {
        if (auto const pid = read_child_pid(pid_path)) return pid;
        std::this_thread::sleep_for(10ms);
    } while (std::chrono::steady_clock::now() < deadline);
    return read_child_pid(pid_path);
}

void terminate_child_from_pid_file(std::filesystem::path const& pid_path) noexcept
{
    try
    {
        auto const pid = read_child_pid(pid_path);
        if (!pid) return;
#if defined(_WIN32)
        HANDLE process = ::OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE, FALSE, *pid);
        if (process == nullptr) return;
        if (::WaitForSingleObject(process, 0) == WAIT_TIMEOUT)
        {
            (void)::TerminateProcess(process, 1);
            (void)::WaitForSingleObject(process, 5000);
        }
        ::CloseHandle(process);
#else
        kill(static_cast<pid_t>(*pid), SIGKILL);
#endif
    }
    catch (...) {}
}

struct ScopedChildProcessCleanup
{
    std::filesystem::path pid_path;
    ~ScopedChildProcessCleanup()
    {
        terminate_child_from_pid_file(pid_path);
        try
        {
            std::error_code error;
            std::filesystem::remove(pid_path, error);
        }
        catch (...) {}
    }
};

[[nodiscard]] DWORD wait_for_process_exit(std::filesystem::path const& pid_path)
{
    auto const pid = read_child_pid(pid_path);
    if (!pid) return WAIT_FAILED;
#if defined(_WIN32)
    HANDLE process = ::OpenProcess(SYNCHRONIZE, FALSE, *pid);
    if (process == nullptr)
    {
        HANDLE snapshot = ::CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE) return WAIT_FAILED;
        PROCESSENTRY32W entry{};
        entry.dwSize = sizeof(entry);
        bool found = ::Process32FirstW(snapshot, &entry) != FALSE;
        while (found && entry.th32ProcessID != *pid)
            found = ::Process32NextW(snapshot, &entry) != FALSE;
        ::CloseHandle(snapshot);
        return found ? WAIT_TIMEOUT : WAIT_OBJECT_0;
    }
    auto const result = ::WaitForSingleObject(process, 5000);
    ::CloseHandle(process);
    return result;
#else
    auto const deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(5000);
    while (std::chrono::steady_clock::now() < deadline)
    {
        if (kill(static_cast<pid_t>(*pid), 0) == -1 && errno == ESRCH)
        {
            return WAIT_OBJECT_0;
        }
        std::this_thread::sleep_for(10ms);
    }
    return (kill(static_cast<pid_t>(*pid), 0) == -1 && errno == ESRCH) ? WAIT_OBJECT_0 : WAIT_TIMEOUT;
#endif
}

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

TEST_CASE("AdbCommandRunnerWin32 forwards the explicit persistent-server lifetime policy",
    "[AdbCommandRunner][policy]")
{
    FakeWin32Process process{{.exit_code = 0, .standard_output = "", .standard_error = ""}};
    UmaAssistant::AdbCommandRunnerWin32 runner{process};

    (void)runner.run(
        AdbInvocation{R"(C:\adb.exe)", {"devices"}, true}, 15s, std::stop_token{});

    REQUIRE(process.observed_leave_children_running_on_success());
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

TEST_CASE("Real Win32 ADB process preserves binary stdout beyond 64 KiB and reports truncation",
    "[AdbCommandRunner][win32][binary]")
{
    auto process = UmaAssistant::create_win32_process();
    UmaAssistant::AdbCommandRunnerWin32 runner{*process};
    auto const invocation = AdbInvocation{UMA_FAKE_ADB_PATH, {"--test-binary-output"}};

    auto const complete = runner.run(invocation, 10s, std::stop_token{}, 256 * 1024);
    REQUIRE(complete.started);
    REQUIRE(complete.exit_code == 0);
    REQUIRE_FALSE(complete.standard_output_truncated);
    REQUIRE(complete.standard_output.size() == 128 * 1024);
    REQUIRE(complete.standard_output.find('\0') != std::string::npos);
    for (std::size_t index = 0; index < complete.standard_output.size(); ++index)
        REQUIRE(static_cast<unsigned char>(complete.standard_output[index])
                == static_cast<unsigned char>((index * 31) & 0xff));

    auto const bounded = runner.run(invocation, 10s, std::stop_token{}, 64 * 1024);
    REQUIRE(bounded.started);
    REQUIRE(bounded.standard_output.size() == 64 * 1024);
    REQUIRE(bounded.standard_output_truncated);
}

TEST_CASE("Real Win32 ADB process keeps stdout and stderr independent",
    "[AdbCommandRunner][win32][streams]")
{
    auto process = UmaAssistant::create_win32_process();
    UmaAssistant::AdbCommandRunnerWin32 runner{*process};
    auto const result = runner.run(
        AdbInvocation{UMA_FAKE_ADB_PATH, {"--test-separate-streams"}},
        10s,
        std::stop_token{});

    REQUIRE(result.started);
    REQUIRE(result.exit_code == 0);
    REQUIRE(result.standard_output == std::string("out\0stdout", 10));
    REQUIRE(result.standard_error == "stderr-only");
}

TEST_CASE("Win32 interactive process flushes each command and stays open between writes",
    "[AdbCommandRunner][win32][interactive]")
{
    auto const input_path = std::filesystem::path{CMAKE_SOURCE_DIR} / "build"
        / ("uma-interactive-input-" + std::to_string(current_process_id()) + ".log");
    std::error_code error;
    std::filesystem::remove(input_path, error);

    std::string start_error;
    auto session = UmaAssistant::start_win32_interactive_session(
        UMA_FAKE_ADB_PATH,
        {"--test-interactive-touch", input_path.string()},
        start_error);
    REQUIRE(session);
    REQUIRE(start_error.empty());
    auto handshake = session->read(2s, std::stop_token{});
    REQUIRE(handshake.standard_output.empty());
    REQUIRE_FALSE(handshake.standard_error_truncated);
    auto const handshake_deadline = std::chrono::steady_clock::now() + 2s;
    while (handshake.standard_error.find("$ 0") == std::string::npos
        && std::chrono::steady_clock::now() < handshake_deadline)
    {
        auto const next_chunk = session->read(100ms, std::stop_token{});
        handshake.standard_output += next_chunk.standard_output;
        handshake.standard_error += next_chunk.standard_error;
    }
    REQUIRE(handshake.standard_output.empty());
    REQUIRE(handshake.standard_error.find("^ 10 4095 4095 255") != std::string::npos);
    REQUIRE(handshake.standard_error.find("$ 0") != std::string::npos);

    REQUIRE(session->write("d 0 100 200 255\nc\n", std::stop_token{}));
    std::this_thread::sleep_for(100ms);
    REQUIRE(session->write("u 0\nc\n", std::stop_token{}));
    session->close();
    REQUIRE(session->has_exited());

    std::ifstream input(input_path, std::ios::binary);
    std::string recorded{std::istreambuf_iterator<char>{input}, {}};
    REQUIRE(recorded.find("d 0 100 200 255") != std::string::npos);
    REQUIRE(recorded.find("u 0") != std::string::npos);
    std::filesystem::remove(input_path, error);
}

TEST_CASE("Win32 interactive blocked stdin cancellation closes its child process tree",
    "[AdbCommandRunner][win32][interactive][cancel]")
{
    auto const pid_path = interactive_child_pid_file();
    std::error_code error;
    std::filesystem::remove(pid_path, error);
    std::string start_error;
    auto session = UmaAssistant::start_win32_interactive_session(
        UMA_FAKE_ADB_PATH,
        {"--test-interactive-blocked-stdin", pid_path.string()},
        start_error);
    REQUIRE(session);
    auto const handshake = session->read(2s, std::stop_token{});
    REQUIRE(handshake.standard_error.find("^ 10 4095 4095 255") != std::string::npos);
    REQUIRE(handshake.standard_error.find("$ 0") != std::string::npos);

    std::string const blocked(2 * 1024 * 1024, 'x');
    auto const started = std::chrono::steady_clock::now();
    std::stop_source cancellation;
    std::jthread cancel_after([&cancellation] {
        std::this_thread::sleep_for(150ms);
        cancellation.request_stop();
    });
    REQUIRE_FALSE(session->write(blocked, cancellation.get_token()));
    REQUIRE(std::chrono::steady_clock::now() - started < 5s);
    REQUIRE(session->has_exited());
    REQUIRE(wait_for_process_exit(pid_path) == WAIT_OBJECT_0);
    std::filesystem::remove(pid_path, error);
}

TEST_CASE("Win32 interactive blocked stdin deadline closes its child process tree",
    "[AdbCommandRunner][win32][interactive][timeout]")
{
    auto const pid_path = interactive_child_pid_file();
    std::error_code error;
    std::filesystem::remove(pid_path, error);
    std::string start_error;
    auto session = UmaAssistant::start_win32_interactive_session(
        UMA_FAKE_ADB_PATH,
        {"--test-interactive-blocked-stdin", pid_path.string()},
        start_error);
    REQUIRE(session);
    auto const handshake = session->read(2s, std::stop_token{});
    REQUIRE(handshake.standard_error.find("^ 10 4095 4095 255") != std::string::npos);

    std::string const blocked(2 * 1024 * 1024, 'x');
    auto const started = std::chrono::steady_clock::now();
    REQUIRE_FALSE(session->write(blocked, std::stop_token{}));
    REQUIRE(std::chrono::steady_clock::now() - started < 5s);
    REQUIRE(session->has_exited());
    REQUIRE(wait_for_process_exit(pid_path) == WAIT_OBJECT_0);
    std::filesystem::remove(pid_path, error);
}

TEST_CASE("Win32 interactive broken stdin cleans up its still-running child tree",
    "[AdbCommandRunner][win32][interactive][broken-pipe]")
{
    auto const pid_path = interactive_child_pid_file();
    std::error_code error;
    std::filesystem::remove(pid_path, error);
    std::string start_error;
    auto session = UmaAssistant::start_win32_interactive_session(
        UMA_FAKE_ADB_PATH,
        {"--test-interactive-broken-stdin", pid_path.string()},
        start_error);
    REQUIRE(session);
    auto const handshake = session->read(2s, std::stop_token{});
    REQUIRE(handshake.standard_error.find("^ 10 4095 4095 255") != std::string::npos);

    REQUIRE_FALSE(session->write("d 0 100 200 255\nc\n", std::stop_token{}));
    REQUIRE(session->has_exited());
    REQUIRE(wait_for_process_exit(pid_path) == WAIT_OBJECT_0);
    std::filesystem::remove(pid_path, error);
}

TEST_CASE("Real Win32 ADB runner returns when an exited parent leaves inherited pipes in a child",
    "[AdbCommandRunner][win32][child]")
{
    auto const pid_path = child_pid_file();
    std::error_code error;
    std::filesystem::remove(pid_path, error);
    ScopedEnvironment child_pid{"UMA_FAKE_ADB_CHILD_PID_FILE", pid_path.string()};
    auto process = UmaAssistant::create_win32_process();
    UmaAssistant::AdbCommandRunnerWin32 runner{*process};
    auto const started = std::chrono::steady_clock::now();
    auto const result = runner.run(
        AdbInvocation{UMA_FAKE_ADB_PATH, {"--test-parent-exits-with-child"}},
        10s,
        std::stop_token{});
    auto const elapsed = std::chrono::steady_clock::now() - started;

    REQUIRE(result.started);
    REQUIRE(result.exit_code == 0);
    REQUIRE(elapsed < 5s);
    REQUIRE(wait_for_process_exit(pid_path) == WAIT_OBJECT_0);
    std::filesystem::remove(pid_path, error);
}

TEST_CASE("Real Win32 ADB runner stops a descendant that keeps writing after its parent exits",
    "[AdbCommandRunner][win32][child][writer]")
{
    auto const pid_path = child_pid_file();
    std::error_code error;
    std::filesystem::remove(pid_path, error);
    ScopedEnvironment child_pid{"UMA_FAKE_ADB_CHILD_PID_FILE", pid_path.string()};
    auto process = UmaAssistant::create_win32_process();
    UmaAssistant::AdbCommandRunnerWin32 runner{*process};
    auto const started = std::chrono::steady_clock::now();
    auto const result = runner.run(
        AdbInvocation{UMA_FAKE_ADB_PATH, {"--test-parent-exits-with-writer"}},
        10s,
        std::stop_token{});
    auto const elapsed = std::chrono::steady_clock::now() - started;

    REQUIRE(result.started);
    REQUIRE(result.exit_code == 0);
    REQUIRE(elapsed < 5s);
    REQUIRE(wait_for_process_exit(pid_path) == WAIT_OBJECT_0);
    std::filesystem::remove(pid_path, error);
}

TEST_CASE("Real Win32 ADB timeout terminates the parent and its child process tree",
    "[AdbCommandRunner][win32][timeout]")
{
    auto const pid_path = child_pid_file();
    std::error_code error;
    std::filesystem::remove(pid_path, error);
    ScopedEnvironment child_pid{"UMA_FAKE_ADB_CHILD_PID_FILE", pid_path.string()};
    auto process = UmaAssistant::create_win32_process();
    UmaAssistant::AdbCommandRunnerWin32 runner{*process};
    auto const result = runner.run(
        AdbInvocation{UMA_FAKE_ADB_PATH, {"--test-parent-hangs-with-child"}},
        500ms,
        std::stop_token{});

    REQUIRE(result.started);
    REQUIRE(result.timed_out);
    REQUIRE(wait_for_process_exit(pid_path) == WAIT_OBJECT_0);
    std::filesystem::remove(pid_path, error);
}

TEST_CASE("Real Win32 ADB cancellation terminates the parent and its child process tree",
    "[AdbCommandRunner][win32][cancel]")
{
    auto const pid_path = child_pid_file();
    ScopedChildProcessCleanup child_cleanup{pid_path};
    std::error_code error;
    std::filesystem::remove(pid_path, error);
    ScopedEnvironment child_pid{"UMA_FAKE_ADB_CHILD_PID_FILE", pid_path.string()};
    auto process = UmaAssistant::create_win32_process();
    UmaAssistant::AdbCommandRunnerWin32 runner{*process};
    std::stop_source cancellation;
    std::promise<bool> child_started;
    auto child_started_result = child_started.get_future();
    std::jthread cancel_after([&cancellation, &child_started, &pid_path] {
        try
        {
            auto const child_pid = wait_for_child_pid(pid_path, 4s);
            child_started.set_value(child_pid.has_value());
        }
        catch (...)
        {
            try { child_started.set_exception(std::current_exception()); }
            catch (...) {}
        }
        cancellation.request_stop();
    });
    auto const result = runner.run(
        AdbInvocation{UMA_FAKE_ADB_PATH, {"--test-parent-hangs-with-child"}},
        10s,
        cancellation.get_token());
    cancel_after.join();

    REQUIRE(child_started_result.get());
    REQUIRE(result.started);
    REQUIRE(result.canceled);
    REQUIRE(wait_for_process_exit(pid_path) == WAIT_OBJECT_0);
    std::filesystem::remove(pid_path, error);
}

TEST_CASE("cold ADB server launched on an isolated port survives client cleanup",
    "[.AdbCommandRunner][win32][adb-server]")
{
    std::string adb_value;
#if defined(_WIN32)
    char* configured_adb = nullptr;
    std::size_t configured_adb_size = 0;
    auto const environment_result = _dupenv_s(
        &configured_adb, &configured_adb_size, "UMA_REAL_ADB_PATH");
    if (environment_result != 0 || configured_adb == nullptr)
        SKIP("Set UMA_REAL_ADB_PATH to an adb.exe to run the isolated cold-server check");
    adb_value = configured_adb;
    std::free(configured_adb);
#else
    char const* configured_adb = std::getenv("UMA_REAL_ADB_PATH");
    if (configured_adb == nullptr)
        SKIP("Set UMA_REAL_ADB_PATH to an adb.exe to run the isolated cold-server check");
    adb_value = configured_adb;
#endif
    std::filesystem::path const adb_path{adb_value};
    REQUIRE(std::filesystem::is_regular_file(adb_path));

    // Leave host selection at adb's local default: some bundled adb builds
    // treat even `-H 127.0.0.1` as a remote host and refuse to start a server.
    // Clearing overrides keeps this random port local without touching 5037.
    ScopedEnvironment adb_socket{"ADB_SERVER_SOCKET", ""};
    ScopedEnvironment adb_server_address{"ANDROID_ADB_SERVER_ADDRESS", ""};
    ScopedEnvironment adb_server_port{"ANDROID_ADB_SERVER_PORT", ""};

    ScopedWinsock winsock;
    REQUIRE(winsock.result == 0);
    auto const available_port = find_free_tcp_port();
    REQUIRE(available_port.has_value());
    std::string const port = std::to_string(*available_port);
    REQUIRE_FALSE(tcp_port_is_listening(*available_port));
    IsolatedAdbServerCleanup cleanup{adb_path, port, false};

    auto process = UmaAssistant::create_win32_process();
    UmaAssistant::AdbCommandRunnerWin32 runner{*process};
    auto const cold_start = runner.run(
        AdbInvocation{adb_path, {"-P", port, "devices"}, true},
        30s, std::stop_token{});
    cleanup.armed = cold_start.started;
    INFO("adb cold-start stderr: " << cold_start.standard_error);
    REQUIRE(cold_start.started);
    REQUIRE(cold_start.exit_code == 0);

    auto const startup_deadline = std::chrono::steady_clock::now() + 5s;
    while (!tcp_port_is_listening(*available_port)
        && std::chrono::steady_clock::now() < startup_deadline)
        std::this_thread::sleep_for(25ms);
    REQUIRE(tcp_port_is_listening(*available_port));

    auto const warm_query = runner.run(
        AdbInvocation{adb_path, {"-P", port, "devices"}},
        15s, std::stop_token{});
    REQUIRE(warm_query.started);
    REQUIRE(warm_query.exit_code == 0);
    REQUIRE(tcp_port_is_listening(*available_port));

    auto const stop_server = runner.run(
        AdbInvocation{adb_path, {"-P", port, "kill-server"}},
        10s, std::stop_token{});
    REQUIRE(stop_server.started);
    REQUIRE(stop_server.exit_code == 0);
    cleanup.armed = false;
    auto const shutdown_deadline = std::chrono::steady_clock::now() + 5s;
    while (tcp_port_is_listening(*available_port)
        && std::chrono::steady_clock::now() < shutdown_deadline)
        std::this_thread::sleep_for(25ms);
    REQUIRE_FALSE(tcp_port_is_listening(*available_port));
}
