#include "NativeGameTaskPackageOperations.hpp"

#include "UmaAssistant/Connection.hpp"

#include <chrono>
#include <condition_variable>
#include <mutex>
#include <stdexcept>

namespace UmaAssistant::GameTasks
{

namespace
{

GameTaskPackageResult Failure(int errorCode, std::string message, bool cancelled = false)
{
    return GameTaskPackageResult{
        false,
        false,
        cancelled,
        errorCode,
        std::move(message)};
}

} // namespace

NativeGameTaskPackageOperations::NativeGameTaskPackageOperations(
    std::shared_ptr<DeviceSession> session,
    std::stop_token cancellation)
    : m_session(std::move(session))
    , m_cancellation(cancellation)
{
    if (!m_session)
        throw std::invalid_argument("Native game task package operations require a device session.");
}

GameTaskPackageResult NativeGameTaskPackageOperations::Start(
    const std::string& packageId,
    const std::string& activityName)
{
    using namespace std::chrono_literals;
    constexpr int maxAttempts = 30;
    auto const operation = activityName.empty()
        ? PackageOperationKind::start_package
        : PackageOperationKind::start_activity;
    auto const terminal = [](const GameTaskPackageResult& result) {
        return result.cancelled
            || result.errorCode == static_cast<int>(ConnectionErrorCode::DeviceDisconnected)
            || result.errorCode == static_cast<int>(ConnectionErrorCode::InvalidArgument);
    };
    auto const cancelled = [] {
        return Failure(static_cast<int>(ConnectionErrorCode::Canceled),
            "Game launch was canceled.", true);
    };
    auto const wait = [&](std::chrono::milliseconds duration) {
        std::mutex mutex;
        std::condition_variable_any changed;
        std::unique_lock lock(mutex);
        changed.wait_for(lock, m_cancellation, duration, [] { return false; });
        return !m_cancellation.stop_requested();
    };

    GameTaskPackageResult last;
    for (int attempt = 0; attempt < maxAttempts; ++attempt)
    {
        if (m_cancellation.stop_requested()) return cancelled();
        last = Execute(operation, packageId, activityName);
        if (terminal(last)) return last;
        if (last.succeeded)
        {
            // am start only acknowledges the launch. process_running is populated
            // by query_running, so never interpret the launch's default false as
            // evidence that startup failed. Match the managed launch probe window.
            auto const deadline = std::chrono::steady_clock::now() + 250ms;
            while (true)
            {
                auto running = Query(packageId);
                if (terminal(running)) return running;
                if (running.succeeded && running.processDetected)
                {
                    running.message = "The game process is running.";
                    return running;
                }
                last = running.succeeded
                    ? Failure(static_cast<int>(ConnectionErrorCode::CommandFailed),
                        "The launch command completed, but the game process was not detected.")
                    : std::move(running);
                if (std::chrono::steady_clock::now() >= deadline) break;
                if (!wait(250ms)) return cancelled();
            }
        }
        if (attempt + 1 < maxAttempts && !wait(1500ms)) return cancelled();
    }
    if (last.message.empty()) last.message = "The game launch failed after the retry window.";
    return last;
}

GameTaskPackageResult NativeGameTaskPackageOperations::Stop(const std::string& packageId)
{
    return Execute(PackageOperationKind::stop_package, packageId);
}

GameTaskPackageResult NativeGameTaskPackageOperations::Query(const std::string& packageId)
{
    return Execute(PackageOperationKind::query_running, packageId);
}

GameTaskPackageResult NativeGameTaskPackageOperations::Execute(
    PackageOperationKind operation,
    const std::string& packageId,
    const std::string& activityName)
{
    auto const result = m_session->package_operation(
        operation,
        packageId,
        activityName,
        m_cancellation);

    if (result.failure)
    {
        auto const code = result.failure->error_code;
        return Failure(
            code,
            result.failure->message,
            code == static_cast<int>(ConnectionErrorCode::Canceled));
    }

    if (!result.package_command)
    {
        return Failure(
            static_cast<int>(ConnectionErrorCode::InvalidDeviceResponse),
            "Package operation returned no command result.");
    }

    auto const& command = *result.package_command;
    if (command.canceled)
    {
        return Failure(
            static_cast<int>(ConnectionErrorCode::Canceled),
            command.error_message.empty() ? "Package operation was canceled." : command.error_message,
            true);
    }
    if (command.error_code != static_cast<int>(ConnectionErrorCode::Success))
    {
        return Failure(command.error_code, command.error_message);
    }
    if (command.timed_out)
    {
        return Failure(
            static_cast<int>(ConnectionErrorCode::CommandTimedOut),
            command.error_message.empty() ? "Package operation timed out." : command.error_message);
    }
    if (command.exit_code != 0)
    {
        return Failure(
            static_cast<int>(ConnectionErrorCode::CommandFailed),
            command.error_message.empty()
                ? "Package operation exited with code " + std::to_string(command.exit_code) + "."
                : command.error_message);
    }

    return GameTaskPackageResult{
        true,
        command.process_running,
        false,
        static_cast<int>(ConnectionErrorCode::Success),
        {}};
}

} // namespace UmaAssistant::GameTasks
