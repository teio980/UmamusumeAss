#include "NativeGameTaskPackageOperations.hpp"

#include "UmaAssistant/Connection.hpp"

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
    return Execute(
        activityName.empty()
            ? PackageOperationKind::start_package
            : PackageOperationKind::start_activity,
        packageId,
        activityName);
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
