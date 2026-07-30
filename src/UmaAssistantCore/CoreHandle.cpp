#include "CoreHandle.hpp"

#include "CoreRuntime.hpp"
#include "Utf8Path.hpp"
#include "Connection/AdbCommandRunner.hpp"
#include "Connection/EmulatorConnector.hpp"

#include <nlohmann/json.hpp>

#include <filesystem>
#include <memory>
#include <mutex>
#include <stop_token>
#include <thread>
#include <unordered_set>
#include <utility>

struct UmaHandleImpl
{
    UmaApiCallback callback;
    void* custom_arg;
    std::shared_ptr<UmaAssistant::ConnectionProfile const> profile;
    std::mutex mutex;
    std::jthread worker;
    std::stop_source cancellation;
    std::uint64_t active_operation_id = 0;
    std::unordered_set<std::uint64_t> terminal_operation_ids;
};

namespace UmaAssistant::CoreApi {
namespace {

[[nodiscard]] std::string envelope(
    std::uint64_t operation_id,
    std::string const& type,
    nlohmann::json payload)
{
    return nlohmann::json{
        {"version", 1},
        {"operation_id", operation_id},
        {"type", type},
        {"payload", std::move(payload)},
    }.dump();
}

void emit(
    UmaHandleImpl& handle,
    std::int32_t message,
    std::uint64_t operation_id,
    std::string const& type,
    nlohmann::json payload) noexcept
{
    try
    {
        auto const details = envelope(operation_id, type, std::move(payload));
        handle.callback(message, details.c_str(), handle.custom_arg);
    }
    catch (...)
    {
        return;
    }
}

void mark_terminal(UmaHandleImpl& handle, std::uint64_t operation_id) noexcept
{
    std::lock_guard lock(handle.mutex);
    handle.active_operation_id = 0;
    try
    {
        handle.terminal_operation_ids.insert(operation_id);
    }
    catch (...)
    {
        // A finished operation must not block a subsequent connection if
        // diagnostic bookkeeping cannot allocate.
    }
}

void emit_failure(
    UmaHandleImpl& handle,
    std::uint64_t operation_id,
    ConnectionFailure const& failure) noexcept
{
    emit(
        handle,
        UMA_MSG_CONNECTION_FAILED,
        operation_id,
        "ConnectionFailed",
        nlohmann::json{
            {"error_code", static_cast<std::int32_t>(failure.error_code)},
            {"phase", failure.phase},
            {"message", failure.message},
            {"attempt", failure.attempt},
            {"max_attempts", failure.max_attempts},
        });
}

void run_connect(
    UmaHandleImpl& handle,
    std::uint64_t operation_id,
    ConnectionRequest request,
    std::stop_token cancellation)
{
    emit(handle, UMA_MSG_CONNECTION_STARTED, operation_id, "ConnectionStarted", nlohmann::json::object());

    ConnectionResult result = ConnectionFailure{
        ConnectionErrorCode::CommandFailed,
        "runtime",
        "unexpected native connection failure",
    };

    try
    {
        auto process = create_win32_process();
        AdbCommandRunnerWin32 runner{*process};
        EmulatorConnector connector{*handle.profile, runner};
        result = connector.connect(
            request,
            cancellation,
            [&](std::string_view phase) {
                emit(
                    handle,
                    UMA_MSG_CONNECTION_PROGRESS,
                    operation_id,
                    "ConnectionProgress",
                    nlohmann::json{{"phase", phase}});
            });
    }
    catch (...)
    {
        result = ConnectionFailure{
            ConnectionErrorCode::CommandFailed,
            "runtime",
            "unexpected native connection failure",
        };
    }

    if (auto const* device = std::get_if<ConnectedDevice>(&result))
    {
        auto const size_source =
            device->width == device->physical_width
                && device->height == device->physical_height
            ? "physical"
            : "override";
        emit(
            handle,
            UMA_MSG_CONNECTION_SUCCEEDED,
            operation_id,
            "ConnectionSucceeded",
            nlohmann::json{
                {"serial", device->serial},
                {"android_id", device->android_id},
                {"android_version", device->android_version},
                {"width", device->width},
                {"height", device->height},
                {"physical_width", device->physical_width},
                {"physical_height", device->physical_height},
                {"size_source", size_source},
            });
    }
    else
    {
        emit_failure(handle, operation_id, std::get<ConnectionFailure>(result));
    }

    mark_terminal(handle, operation_id);
}

}

UmaHandle create_handle(UmaApiCallback callback, void* custom_arg)
{
    auto profile = CoreRuntime::instance().profile();
    if (callback == nullptr || profile == nullptr) return nullptr;
    return new UmaHandleImpl{
        .callback = callback,
        .custom_arg = custom_arg,
        .profile = std::move(profile),
        .mutex = {},
        .worker = {},
        .cancellation = {},
        .active_operation_id = 0,
        .terminal_operation_ids = {},
    };
}

void destroy_handle(UmaHandle handle) noexcept
{
    if (handle == nullptr) return;

    std::jthread worker;
    {
        std::lock_guard lock(handle->mutex);
        handle->cancellation.request_stop();
        worker = std::move(handle->worker);
    }
    if (worker.joinable()) worker.join();
    delete handle;
}

UmaStartResult start_connect(
    UmaHandle handle, std::string adb_path, std::string serial, std::string profile)
{
    if (handle == nullptr) return {0, UMA_ERROR_INVALID_ARGUMENT};

    auto decoded_adb_path = path_from_utf8(adb_path);
    if (!decoded_adb_path) return {0, UMA_ERROR_INVALID_ARGUMENT};

    ConnectionRequest request{
        .adb_path = std::move(*decoded_adb_path),
        .serial = std::move(serial),
        .profile_name = std::move(profile),
    };
    if (auto const failure = EmulatorConnector::validate_request(request))
    {
        return {0, static_cast<std::int32_t>(failure->error_code)};
    }

    std::lock_guard lock(handle->mutex);
    if (handle->active_operation_id != 0) return {0, UMA_ERROR_BUSY};
    if (handle->worker.joinable()) handle->worker.join();

    auto const operation_id = CoreRuntime::instance().allocate_operation_id();
    handle->cancellation = std::stop_source{};
    auto const token = handle->cancellation.get_token();
    handle->active_operation_id = operation_id;
    handle->worker = std::jthread(
        [handle, operation_id, request = std::move(request), token](std::stop_token) mutable {
            try
            {
                run_connect(*handle, operation_id, std::move(request), token);
            }
            catch (...)
            {
                std::lock_guard worker_lock(handle->mutex);
                handle->active_operation_id = 0;
            }
        });
    return {operation_id, UMA_SUCCESS};
}

std::int32_t cancel_operation(
    UmaHandle handle, std::uint64_t operation_id) noexcept
{
    if (handle == nullptr || operation_id == 0) return UMA_ERROR_INVALID_ARGUMENT;
    std::lock_guard lock(handle->mutex);
    if (handle->active_operation_id == operation_id)
    {
        handle->cancellation.request_stop();
        return UMA_SUCCESS;
    }
    if (handle->terminal_operation_ids.contains(operation_id)) return UMA_SUCCESS;
    return UMA_ERROR_INVALID_ARGUMENT;
}

}
