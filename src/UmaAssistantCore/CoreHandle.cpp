#include "CoreHandle.hpp"

#include "Api/CallbackSender.hpp"
#include "CoreRuntime.hpp"
#include "Utf8Path.hpp"
#include "Connection/AdbCommandRunner.hpp"
#include "Connection/EmulatorConnector.hpp"
#include "Session/DeviceSession.hpp"
#include "Session/TouchOptions.hpp"
#include "Recognition/WindowsOcrRecognizer.hpp"
#include "Vision/ColorAnalysis.hpp"
#include "Vision/NumericImageProcessing.hpp"
#include "TaskEngine/HachimiPipelineLoader.hpp"
#include "TaskEngine/TaskGraphRunner.hpp"
#include "TaskEngine/NativeTaskExecutionContext.hpp"
#include "Common/PathEncoding.hpp"
#include "GameTasks/GameTaskActionExtensions.hpp"
#include "GameTasks/NativeGameTaskPackageOperations.hpp"
#include "GameTasks/NativeGameTaskRunner.hpp"
#include "GameData/CareerSelectionCatalog.hpp"
#include "GameData/UmaCatalogJson.hpp"
#include "GameData/UmaGameDatabase.hpp"
#include "Runtime/TaskQueueRuntime.hpp"
#include "CareerDomain/Scenarios/Ura/UraStrategyRegistry.hpp"

#include <nlohmann/json.hpp>

#include <chrono>
#include <array>
#include <charconv>
#include <cctype>
#include <cmath>
#include <condition_variable>
#include <cstring>
#include <filesystem>
#include <functional>
#include <limits>
#include <memory>
#include <mutex>
#include <stop_token>
#include <stdexcept>
#include <string_view>
#include <thread>
#include <unordered_set>
#include <utility>
#include <vector>

struct UmaHandleState
{
    UmaHandleState(UmaApiCallback callback, void* custom_arg)
        : sender{callback, custom_arg}
    {
    }

    UmaAssistant::Api::CallbackSender sender;
    std::shared_ptr<UmaAssistant::ConnectionProfile const> profile;
    std::shared_ptr<UmaAssistant::Resources::ResourceSnapshot const> resources;
    UmaAssistant::Recognition::WindowsOcrRecognizer ocr;
    UmaAssistant::DeviceSessionOptions device_options;
    std::mutex mutex;
    std::stop_source cancellation;
    std::shared_ptr<UmaAssistant::DeviceSession> session;
    std::uint64_t active_operation_id = 0;
    std::uint32_t active_recognition_count = 0;
    std::condition_variable recognition_finished;
    bool recovering_device = false;
    bool closing = false;
    std::unordered_set<std::uint64_t> terminal_operation_ids;
    UmaAssistant::Runtime::TaskQueueRuntime queue;
};
struct UmaHandleImpl
{
    std::shared_ptr<UmaHandleState> state;
    std::jthread worker;
    std::jthread monitor;
};

namespace UmaAssistant::CoreApi {
namespace {

void emit(
    UmaHandleState& handle,
    std::int32_t message,
    std::uint64_t operation_id,
    std::string const& type,
    nlohmann::json payload) noexcept
{
    handle.sender.send_connection(message, operation_id, type, std::move(payload));
}

void emit_runtime(
    UmaHandleState& handle,
    std::int32_t message,
    std::uint64_t operation_id,
    std::string const& type,
    nlohmann::json payload) noexcept
{
    handle.sender.send_runtime(message, operation_id, type, std::move(payload));
}

void mark_terminal(UmaHandleState& handle, std::uint64_t operation_id) noexcept
{
    std::lock_guard lock(handle.mutex);
    if (handle.active_operation_id == operation_id)
        handle.active_operation_id = 0;
    try
    {
        handle.terminal_operation_ids.insert(operation_id);
    }
    catch (...)
    {
    }
}

bool mark_terminal_once(UmaHandleState& handle, std::uint64_t operation_id) noexcept
{
    std::lock_guard lock(handle.mutex);
    if (handle.terminal_operation_ids.contains(operation_id)) return false;
    if (handle.active_operation_id != operation_id) return false;
    handle.active_operation_id = 0;
    try
    {
        handle.terminal_operation_ids.insert(operation_id);
    }
    catch (...)
    {
    }
    return true;
}

void join_prior(std::jthread& thread) noexcept
{
    if (!thread.joinable()) return;
    if (thread.get_id() == std::this_thread::get_id())
        thread.detach();
    else
        thread.join();
}

bool is_career_training_task(std::string_view task_id) noexcept
{
    constexpr std::string_view expected = "career-training";
    if (task_id.size() != expected.size()) return false;
    for (std::size_t index = 0; index < expected.size(); ++index)
    {
        auto const ch = static_cast<unsigned char>(task_id[index]);
        if (static_cast<char>(std::tolower(ch)) != expected[index]) return false;
    }
    return true;
}

void pin_career_checkpoint_directory(
    std::string_view task_id,
    nlohmann::json& settings,
    std::string_view user_directory)
{
    if (!is_career_training_task(task_id) || user_directory.empty()) return;

    auto const checkpoint = settings.find("checkpointDirectory");
    if (checkpoint != settings.end())
    {
        if (!checkpoint->is_string()) return; // Let task validation report the bad type.
        if (!checkpoint->get_ref<std::string const&>().empty()) return;
    }

    auto const path = path_from_utf8(user_directory);
    if (!path) throw std::invalid_argument("The configured user directory is not valid UTF-8.");
    settings["checkpointDirectory"] = Paths::ToUtf8(*path / "career-checkpoints");
}

void emit_failure(
    UmaHandleState& handle,
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

void start_device_monitor(
    UmaHandleImpl& facade,
    std::shared_ptr<UmaHandleState> state,
    std::shared_ptr<DeviceSession> session,
    ConnectionRequest request,
    std::shared_ptr<ConnectionProfile const> profile) noexcept
{
    try
    {
        std::lock_guard lock(state->mutex);
        if (state->closing || state->session != session || facade.monitor.joinable()) return;
        facade.monitor = std::jthread([
            state = std::move(state), session = std::move(session),
            request = std::move(request), profile = std::move(profile)](std::stop_token stop) mutable {
            while (!stop.stop_requested())
            {
                for (int tick = 0; tick < 50 && !stop.stop_requested(); ++tick)
                    std::this_thread::sleep_for(std::chrono::milliseconds(100));
                if (stop.stop_requested()) return;
                // Preserve the former health monitor's ordering: a target absent
                // from `adb devices` is disconnected; recovery is only attempted
                // when the target is still listed as ready but get-state failed.
                if (!session->listed_as_ready(stop))
                {
                    if (stop.stop_requested()) return;
                    {
                        std::lock_guard state_lock(state->mutex);
                        if (state->session != session || state->closing) return;
                        state->session.reset();
                        if (state->active_operation_id != 0)
                            state->cancellation.request_stop();
                    }
                    emit_runtime(
                        *state,
                        UMA_MSG_DEVICE_DISCONNECTED,
                        0,
                        "DeviceDisconnected",
                        nlohmann::json{
                            {"error_code", UMA_ERROR_DEVICE_DISCONNECTED},
                            {"session_generation", session->session_generation()},
                        });
                    return;
                }
                if (session->connected(stop)) continue;
                if (stop.stop_requested()) return;

                auto const previous_generation = session->session_generation();
                bool should_recover = false;
                {
                    std::lock_guard state_lock(state->mutex);
                    if (state->session != session || state->closing) return;
                    if (state->active_operation_id != 0)
                    {
                        state->session.reset();
                        state->cancellation.request_stop();
                    }
                    else
                    {
                        state->session.reset();
                        state->recovering_device = true;
                        should_recover = true;
                    }
                }

                if (!should_recover)
                {
                    emit_runtime(
                        *state,
                        UMA_MSG_DEVICE_DISCONNECTED,
                        0,
                        "DeviceDisconnected",
                        nlohmann::json{
                            {"error_code", UMA_ERROR_DEVICE_DISCONNECTED},
                            {"session_generation", previous_generation},
                        });
                    return;
                }

                // The former WPF health monitor attempted one recovery after the native
                // session's idle get-state probe failed. Keep that policy here so no second
                // UI-owned ADB poller can race the device session.
                try
                {
                    auto process = create_win32_process();
                    AdbCommandRunnerWin32 runner{*process};
                    EmulatorConnector connector{*profile, runner};
                    auto result = connector.connect(request, stop);
                    if (stop.stop_requested()) return;
                    if (auto const* device = std::get_if<ConnectedDevice>(&result))
                    {
                        auto const next_generation = CoreRuntime::instance().allocate_operation_id();
                        DeviceSessionOptions options;
                        {
                            std::lock_guard state_lock(state->mutex);
                            options = state->device_options;
                        }
                        auto replacement = std::make_shared<DeviceSession>(
                            profile, request.adb_path, request.profile_name, *device,
                            next_generation, std::move(options));
                        bool committed = false;
                        {
                            std::lock_guard state_lock(state->mutex);
                            if (!state->closing && !stop.stop_requested()
                                && state->recovering_device && state->active_operation_id == 0
                                && !state->session)
                            {
                                state->session = replacement;
                                state->recovering_device = false;
                                committed = true;
                            }
                        }
                        if (!committed) return;

                        auto const size_source = device->width == device->physical_width
                                && device->height == device->physical_height
                            ? "physical" : "override";
                        emit_runtime(
                            *state,
                            UMA_MSG_DEVICE_RECONNECTED,
                            0,
                            "DeviceReconnected",
                            nlohmann::json{
                                {"previous_session_generation", previous_generation},
                                {"session_generation", next_generation},
                                {"serial", device->serial},
                                {"width", device->width},
                                {"height", device->height},
                                {"physical_width", device->physical_width},
                                {"physical_height", device->physical_height},
                                {"size_source", size_source},
                            });
                        session = std::move(replacement);
                        continue;
                    }
                }
                catch (...)
                {
                    // Treat recovery exceptions the same as a failed recovery attempt.
                }

                bool report_disconnect = false;
                {
                    std::lock_guard state_lock(state->mutex);
                    if (!state->closing && state->recovering_device
                        && state->active_operation_id == 0 && !state->session)
                    {
                        state->recovering_device = false;
                        report_disconnect = true;
                    }
                }
                if (report_disconnect)
                {
                    emit_runtime(
                        *state,
                        UMA_MSG_DEVICE_DISCONNECTED,
                        0,
                        "DeviceDisconnected",
                        nlohmann::json{
                            {"error_code", UMA_ERROR_DEVICE_DISCONNECTED},
                            {"session_generation", previous_generation},
                        });
                }
                return;
            }
        });
    }
    catch (...)
    {
    }
}

void run_connect(
    UmaHandleImpl* facade,
    std::shared_ptr<UmaHandleState> state,
    std::uint64_t operation_id,
    ConnectionRequest request,
    DeviceSessionOptions device_options,
    std::stop_token cancellation)
{
    emit(*state, UMA_MSG_CONNECTION_STARTED, operation_id, "ConnectionStarted", nlohmann::json::object());

    std::shared_ptr<ConnectionProfile const> profile;
    {
        std::lock_guard lock(state->mutex);
        profile = state->profile;
    }

    ConnectionResult result = ConnectionFailure{
        ConnectionErrorCode::CommandFailed,
        "runtime",
        "unexpected native connection failure",
    };

    try
    {
        auto process = create_win32_process();
        AdbCommandRunnerWin32 runner{*process};
        EmulatorConnector connector{*profile, runner};
        result = connector.connect(
            request,
            cancellation,
            [&](std::string_view phase) {
                emit(
                    *state,
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

    std::shared_ptr<DeviceSession> connected_session;
    if (auto const* device = std::get_if<ConnectedDevice>(&result))
    {
        try
        {
            connected_session = std::make_shared<DeviceSession>(
                profile, request.adb_path, request.profile_name, *device,
                operation_id, std::move(device_options));
            std::lock_guard lock(state->mutex);
            if (!state->closing) state->session = connected_session;
            else connected_session.reset();
        }
        catch (...)
        {
            connected_session.reset();
            result = ConnectionFailure{
                ConnectionErrorCode::CommandFailed,
                "runtime",
                "device session could not be created",
            };
        }
    }

    if (auto const* device = std::get_if<ConnectedDevice>(&result))
    {
        auto const size_source =
            device->width == device->physical_width
                && device->height == device->physical_height
            ? "physical"
            : "override";
        mark_terminal(*state, operation_id);
        if (connected_session)
            start_device_monitor(*facade, state, connected_session, request, profile);
        emit(
            *state,
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
        mark_terminal(*state, operation_id);
        emit_failure(*state, operation_id, std::get<ConnectionFailure>(result));
    }
}

using DeviceAction = std::function<DeviceOperationResult(
    DeviceSession&, std::stop_token)>;

[[nodiscard]] nlohmann::json frame_details(DeviceFrameMetadata const& frame)
{
    auto constexpr canonical_width = 1080;
    auto constexpr canonical_height = 1920;
    auto const oriented_width = frame.orientation % 2 == 0
        ? canonical_width : canonical_height;
    auto const oriented_height = frame.orientation % 2 == 0
        ? canonical_height : canonical_width;
    return {
        {"frame_id", frame.id},
        {"session_generation", frame.session_generation},
        {"geometry_generation", frame.geometry_generation},
        {"width", frame.width},
        {"height", frame.height},
        {"orientation", frame.orientation},
        {"byte_count", frame.byte_count},
        {"transform", {
            {"canonical_width", canonical_width},
            {"canonical_height", canonical_height},
            {"rotation_degrees", frame.orientation * 90},
            {"device_width", frame.width},
            {"device_height", frame.height},
            {"scale", frame.display_scale},
            {"offset_x", frame.display_offset_x},
            {"offset_y", frame.display_offset_y},
            {"content_width", oriented_width * frame.display_scale},
            {"content_height", oriented_height * frame.display_scale},
        }},
    };
}

[[nodiscard]] UmaStartResult start_device_operation(
    UmaHandle handle,
    DeviceAction action,
    std::function<std::pair<std::int32_t, std::string>(DeviceOperationResult const&)> event)
{
    if (handle == nullptr) return {0, UMA_ERROR_INVALID_ARGUMENT};
    auto state = handle->state;
    if (!state) return {0, UMA_ERROR_INVALID_ARGUMENT};
    std::jthread previous_worker;
    std::shared_ptr<DeviceSession> session;
    std::stop_token token;
    auto const operation_id = CoreRuntime::instance().allocate_operation_id();
    {
        std::lock_guard lock(state->mutex);
        if (state->closing) return {0, UMA_ERROR_INVALID_ARGUMENT};
        if (state->active_operation_id != 0 || state->active_recognition_count != 0)
            return {0, UMA_ERROR_BUSY};
        if (state->recovering_device) return {0, UMA_ERROR_BUSY};
        if (!state->session) return {0, UMA_ERROR_DEVICE_NOT_READY};
        session = state->session;
        previous_worker = std::move(handle->worker);
        state->active_operation_id = operation_id;
        state->cancellation = std::stop_source{};
        token = state->cancellation.get_token();
    }
    join_prior(previous_worker);

    try
    {
        std::lock_guard lock(state->mutex);
        if (state->closing || state->active_operation_id != operation_id)
        {
            state->active_operation_id = 0;
            return {0, UMA_ERROR_INVALID_ARGUMENT};
        }
        handle->worker = std::jthread(
            [state, session = std::move(session), action = std::move(action),
             event = std::move(event), operation_id, token](std::stop_token) mutable {
                try
                {
                    DeviceOperationResult result;
                    try
                    {
                        result = action(*session, token);
                    }
                    catch (...)
                    {
                        result.failure = SessionFailure{
                            UMA_ERROR_COMMAND_FAILED,
                            "runtime",
                            "Unexpected device operation failure.",
                        };
                    }
                    auto const [message, type] = event(result);
                    nlohmann::json payload = nlohmann::json::object();
                    if (result.failure)
                    {
                        payload = {
                            {"error_code", result.failure->error_code},
                            {"phase", result.failure->phase},
                            {"message", result.failure->message},
                        };
                    }
                    else if (result.frame)
                    {
                        auto const& frame = *result.frame;
                        payload = frame_details(frame);
                    }
                    else if (!result.package_id.empty())
                    {
                        payload = {{"package_id", result.package_id}};
                    }
                    if (result.package_command)
                    {
                        auto const& command = *result.package_command;
                        payload = {
                            {"operation", command.operation},
                            {"command", {
                                {"started", command.started},
                                {"exit_code", command.exit_code},
                                {"timed_out", command.timed_out},
                                {"canceled", command.canceled},
                                {"error_code", command.error_code},
                                {"error_message", command.error_message},
                                {"stdout", command.standard_output},
                                {"stderr", command.standard_error},
                            }},
                            {"process_running", command.process_running},
                        };
                    }
                    auto append_point = [&payload](
                        char const* name, DeviceOperationResult::Point const& point) {
                        payload[name] = {
                            {"canonical_x", point.canonical_x},
                            {"canonical_y", point.canonical_y},
                            {"device_x", point.device_x},
                            {"device_y", point.device_y},
                        };
                    };
                    if (result.first_point) append_point("first_point", *result.first_point);
                    if (result.second_point) append_point("second_point", *result.second_point);
                    if (result.source_frame)
                        payload["source_frame"] = frame_details(*result.source_frame);
                    auto append_requested = [&payload](
                        char const* name, std::optional<DeviceOperationResult::Point> const& point) {
                        if (!point) return;
                        payload["requested"][name] = {
                            {"canonical_x", point->canonical_x},
                            {"canonical_y", point->canonical_y},
                        };
                    };
                    append_requested("first_point", result.requested_first_point);
                    append_requested("second_point", result.requested_second_point);
                    if (result.first_point || result.second_point)
                    {
                        payload["actual"] = nlohmann::json::object();
                        if (result.first_point)
                            payload["actual"]["first_point"] = {
                                {"device_x", result.first_point->device_x},
                                {"device_y", result.first_point->device_y},
                            };
                        if (result.second_point)
                            payload["actual"]["second_point"] = {
                                {"device_x", result.second_point->device_x},
                                {"device_y", result.second_point->device_y},
                            };
                    }
                    mark_terminal(*state, operation_id);
                    emit_runtime(*state, message, operation_id, type, std::move(payload));
                }
                catch (...)
                {
                    mark_terminal(*state, operation_id);
                    try
                    {
                        emit_runtime(*state, UMA_MSG_OPERATION_FAILED, operation_id,
                                     "OperationFailed", nlohmann::json{
                                         {"error_code", UMA_ERROR_COMMAND_FAILED},
                                         {"phase", "runtime"},
                                         {"message", "Unexpected native operation failure."},
                                     });
                    }
                    catch (...)
                    {
                    }
                }
            });
    }
    catch (...)
    {
        std::lock_guard lock(state->mutex);
        if (state->active_operation_id == operation_id)
            state->active_operation_id = 0;
        return {0, UMA_ERROR_PROCESS_START_FAILED};
    }
    return {operation_id, UMA_SUCCESS};
}

}

UmaHandle create_handle(UmaApiCallback callback, void* custom_arg)
{
    auto& runtime = CoreRuntime::instance();
    if (callback == nullptr || runtime.user_dir().empty())
    {
        return nullptr;
    }

    auto bundle = runtime.resource_bundle();
    if (bundle.profile == nullptr || bundle.resources == nullptr) return nullptr;
    auto state = std::make_shared<UmaHandleState>(callback, custom_arg);
    state->profile = std::move(bundle.profile);
    state->resources = std::move(bundle.resources);
    return new UmaHandleImpl{std::move(state), {}, {}};
}

void destroy_handle(UmaHandle handle) noexcept
{
    if (handle == nullptr) return;
    auto state = handle->state;
    if (!state)
    {
        delete handle;
        return;
    }
    std::jthread worker;
    std::jthread monitor;
    {
        std::lock_guard lock(state->mutex);
        state->closing = true;
        state->cancellation.request_stop();
        handle->monitor.request_stop();
        worker = std::move(handle->worker);
        monitor = std::move(handle->monitor);
        state->session.reset();
        state->recovering_device = false;
    }
    state->sender.close();
    join_prior(worker);
    join_prior(monitor);
    {
        std::unique_lock lock(state->mutex);
        state->recognition_finished.wait(lock, [&] {
            return state->active_recognition_count == 0;
        });
    }
    delete handle;
}

UmaStartResult start_connect(
    UmaHandle handle, std::string adb_path, std::string serial, std::string profile)
{
    if (handle == nullptr) return {0, UMA_ERROR_INVALID_ARGUMENT};
    auto state = handle->state;
    if (!state) return {0, UMA_ERROR_INVALID_ARGUMENT};
    auto decoded_adb_path = path_from_utf8(adb_path);
    if (!decoded_adb_path) return {0, UMA_ERROR_INVALID_ARGUMENT};
    ConnectionRequest request{
        .adb_path = std::move(*decoded_adb_path),
        .serial = std::move(serial),
        .profile_name = std::move(profile),
    };
    if (auto const failure = EmulatorConnector::validate_request(request))
        return {0, static_cast<std::int32_t>(failure->error_code)};

    auto const bundle = CoreRuntime::instance().resource_bundle();
    if (!bundle.profile || !bundle.resources) return {0, UMA_ERROR_DEVICE_NOT_READY};

    std::jthread previous_worker;
    std::jthread previous_monitor;
    auto const operation_id = CoreRuntime::instance().allocate_operation_id();
    std::stop_token token;
    DeviceSessionOptions device_options;
    {
        std::lock_guard lock(state->mutex);
        if (state->closing) return {0, UMA_ERROR_INVALID_ARGUMENT};
        if (state->active_operation_id != 0 || state->active_recognition_count != 0)
            return {0, UMA_ERROR_BUSY};
        state->profile = bundle.profile;
        state->resources = bundle.resources;
        device_options = state->device_options;
        state->recovering_device = false;
        previous_worker = std::move(handle->worker);
        handle->monitor.request_stop();
        previous_monitor = std::move(handle->monitor);
        state->session.reset();
        state->active_operation_id = operation_id;
        state->cancellation = std::stop_source{};
        token = state->cancellation.get_token();
    }
    join_prior(previous_monitor);
    join_prior(previous_worker);
    try
    {
        std::lock_guard lock(state->mutex);
        if (state->closing || state->active_operation_id != operation_id)
        {
            state->active_operation_id = 0;
            return {0, UMA_ERROR_INVALID_ARGUMENT};
        }
        handle->worker = std::jthread(
            [handle, state, operation_id, request = std::move(request),
             device_options = std::move(device_options), token](std::stop_token) mutable {
                try
                {
                    run_connect(handle, state, operation_id, std::move(request),
                                std::move(device_options), token);
                }
                catch (...)
                {
                    mark_terminal(*state, operation_id);
                    emit_failure(*state, operation_id, ConnectionFailure{
                        ConnectionErrorCode::CommandFailed, "runtime",
                        "Unexpected native connection failure."});
                }
            });
    }
    catch (...)
    {
        std::lock_guard lock(state->mutex);
        if (state->active_operation_id == operation_id)
            state->active_operation_id = 0;
        return {0, UMA_ERROR_PROCESS_START_FAILED};
    }
    return {operation_id, UMA_SUCCESS};
}

UmaStartResult start_verify_game(UmaHandle handle, std::string package_id)
{
    return start_device_operation(
        handle,
        [package_id = std::move(package_id)](DeviceSession& session, std::stop_token token) {
            return session.verify_game(package_id, token);
        },
        [](DeviceOperationResult const& result) {
            return result.failure
                ? std::pair{UMA_MSG_GAME_VERIFICATION_FAILED, std::string{"GameVerificationFailed"}}
                : std::pair{UMA_MSG_GAME_VERIFIED, std::string{"GameVerified"}};
        });
}

UmaStartResult start_capture(UmaHandle handle)
{
    return start_device_operation(
        handle,
        [](DeviceSession& session, std::stop_token token) { return session.capture(token); },
        [](DeviceOperationResult const& result) {
            return result.failure
                ? std::pair{UMA_MSG_OPERATION_FAILED, std::string{"OperationFailed"}}
                : std::pair{UMA_MSG_FRAME_CAPTURED, std::string{"FrameCaptured"}};
        });
}

std::int32_t get_frame_png_size(
    UmaHandle handle, std::uint64_t frame_id, std::uint64_t* size) noexcept
{
    if (handle == nullptr || frame_id == 0 || size == nullptr) return UMA_ERROR_INVALID_ARGUMENT;
    auto state = handle->state;
    if (!state) return UMA_ERROR_INVALID_ARGUMENT;
    std::shared_ptr<DeviceSession> session;
    {
        std::lock_guard lock(state->mutex);
        session = state->session;
    }
    if (!session) return UMA_ERROR_DEVICE_NOT_READY;
    auto const frame = session->frame_metadata(frame_id);
    if (!frame) return UMA_ERROR_INVALID_ARGUMENT;
    auto const png_size = session->frame_png_size(frame_id);
    if (!png_size) return UMA_ERROR_INVALID_DEVICE_RESPONSE;
    *size = *png_size;
    return UMA_SUCCESS;
}

std::int32_t copy_frame_png(
    UmaHandle handle, std::uint64_t frame_id,
    std::uint8_t* destination, std::uint64_t capacity) noexcept
{
    if (handle == nullptr || frame_id == 0 || destination == nullptr)
        return UMA_ERROR_INVALID_ARGUMENT;
    auto state = handle->state;
    if (!state) return UMA_ERROR_INVALID_ARGUMENT;
    std::shared_ptr<DeviceSession> session;
    {
        std::lock_guard lock(state->mutex);
        session = state->session;
    }
    if (!session) return UMA_ERROR_DEVICE_NOT_READY;
    return session->copy_frame_png(frame_id, destination, capacity)
        ? UMA_SUCCESS : UMA_ERROR_INVALID_ARGUMENT;
}

std::int32_t release_frame(UmaHandle handle, std::uint64_t frame_id) noexcept
{
    if (handle == nullptr || frame_id == 0) return UMA_ERROR_INVALID_ARGUMENT;
    auto state = handle->state;
    if (!state) return UMA_ERROR_INVALID_ARGUMENT;
    std::shared_ptr<DeviceSession> session;
    {
        std::lock_guard lock(state->mutex);
        session = state->session;
    }
    if (!session) return UMA_ERROR_DEVICE_NOT_READY;
    return session->release_frame(frame_id) ? UMA_SUCCESS : UMA_ERROR_INVALID_ARGUMENT;
}

UmaStartResult start_tap(
    UmaHandle handle, std::uint64_t frame_id,
    std::int32_t x, std::int32_t y)
{
    return start_device_operation(
        handle,
        [frame_id, x, y](DeviceSession& session, std::stop_token token) {
            return session.tap(frame_id, x, y, token);
        },
        [](DeviceOperationResult const& result) {
            return result.failure
                ? std::pair{UMA_MSG_INPUT_FAILED, std::string{"InputFailed"}}
                : std::pair{UMA_MSG_INPUT_SUCCEEDED, std::string{"InputSucceeded"}};
        });
}

UmaStartResult start_swipe(
    UmaHandle handle, std::uint64_t frame_id,
    std::int32_t x1, std::int32_t y1, std::int32_t x2, std::int32_t y2,
    std::int32_t duration_ms, bool extra_swipe)
{
    return start_device_operation(
        handle,
        [frame_id, x1, y1, x2, y2, duration_ms, extra_swipe](DeviceSession& session, std::stop_token token) {
            return session.swipe(frame_id, x1, y1, x2, y2, duration_ms, extra_swipe, token);
        },
        [](DeviceOperationResult const& result) {
            return result.failure
                ? std::pair{UMA_MSG_INPUT_FAILED, std::string{"InputFailed"}}
                : std::pair{UMA_MSG_INPUT_SUCCEEDED, std::string{"InputSucceeded"}};
        });
}

UmaStartResult start_touch(
    UmaHandle handle, std::uint64_t frame_id,
    std::int32_t x, std::int32_t y, std::int32_t phase)
{
    if (phase < UMA_TOUCH_DOWN || phase > UMA_TOUCH_CANCEL
        || frame_id == 0)
        return {0, UMA_ERROR_INVALID_ARGUMENT};
    auto const action = static_cast<HeldTouchPhase>(phase);
    return start_device_operation(
        handle,
        [frame_id, x, y, action](DeviceSession& session, std::stop_token token) {
            return session.touch(frame_id, x, y, action, token);
        },
        [](DeviceOperationResult const& result) {
            return result.failure
                ? std::pair{UMA_MSG_INPUT_FAILED, std::string{"InputFailed"}}
                : std::pair{UMA_MSG_INPUT_SUCCEEDED, std::string{"InputSucceeded"}};
        });
}

UmaStartResult start_prepare_touch(UmaHandle handle, std::uint64_t frame_id)
{
    if (frame_id == 0)
        return {0, UMA_ERROR_INVALID_ARGUMENT};
    return start_device_operation(
        handle,
        [frame_id](DeviceSession& session, std::stop_token token) {
            return session.prepare_touch(frame_id, token);
        },
        [](DeviceOperationResult const& result) {
            return result.failure
                ? std::pair{UMA_MSG_INPUT_FAILED, std::string{"InputFailed"}}
                : std::pair{UMA_MSG_INPUT_SUCCEEDED, std::string{"InputSucceeded"}};
        });
}

UmaStartResult start_key_event(UmaHandle handle, std::uint64_t frame_id, std::string key_code)
{
    if (frame_id == 0 || key_code.empty())
        return {0, UMA_ERROR_INVALID_ARGUMENT};
    return start_device_operation(
        handle,
        [frame_id, key_code = std::move(key_code)](DeviceSession& session, std::stop_token token) {
            return session.key_event(frame_id, key_code, token);
        },
        [](DeviceOperationResult const& result) {
            return result.failure
                ? std::pair{UMA_MSG_INPUT_FAILED, std::string{"InputFailed"}}
                : std::pair{UMA_MSG_INPUT_SUCCEEDED, std::string{"InputSucceeded"}};
        });
}

UmaStartResult start_input_text(UmaHandle handle, std::uint64_t frame_id, std::string text)
{
    if (frame_id == 0 || text.empty())
        return {0, UMA_ERROR_INVALID_ARGUMENT};
    return start_device_operation(
        handle,
        [frame_id, text = std::move(text)](DeviceSession& session, std::stop_token token) {
            return session.input_text(frame_id, text, token);
        },
        [](DeviceOperationResult const& result) {
            return result.failure
                ? std::pair{UMA_MSG_INPUT_FAILED, std::string{"InputFailed"}}
                : std::pair{UMA_MSG_INPUT_SUCCEEDED, std::string{"InputSucceeded"}};
        });
}

UmaStartResult start_package_operation(
    UmaHandle handle,
    std::string package_id,
    std::string activity,
    std::int32_t operation)
{
    if (operation < UMA_PACKAGE_START || operation > UMA_PACKAGE_QUERY_RUNNING)
        return {0, UMA_ERROR_INVALID_ARGUMENT};
    auto const kind = static_cast<PackageOperationKind>(operation);
    return start_device_operation(
        handle,
        [kind, package_id = std::move(package_id), activity = std::move(activity)](
            DeviceSession& session, std::stop_token token) {
            return session.package_operation(kind, package_id, activity, token);
        },
        [](DeviceOperationResult const& result) {
            if (result.failure)
                return std::pair{UMA_MSG_OPERATION_FAILED, std::string{"OperationFailed"}};
            return std::pair{UMA_MSG_PACKAGE_OPERATION_COMPLETED,
                             std::string{"PackageOperationCompleted"}};
        });
}

std::int32_t cancel_operation(
    UmaHandle handle, std::uint64_t operation_id) noexcept
{
    if (handle == nullptr || operation_id == 0) return UMA_ERROR_INVALID_ARGUMENT;
    auto state = handle->state;
    if (!state) return UMA_ERROR_INVALID_ARGUMENT;
    std::lock_guard lock(state->mutex);
    if (state->active_operation_id == operation_id)
    {
        state->cancellation.request_stop();
        return UMA_SUCCESS;
    }
    if (state->terminal_operation_ids.contains(operation_id)) return UMA_SUCCESS;
    return UMA_ERROR_INVALID_ARGUMENT;
}

std::int32_t set_instance_options(
    UmaHandle handle, std::string const& utf8_json) noexcept
{
    if (handle == nullptr || utf8_json.empty()) return UMA_ERROR_INVALID_ARGUMENT;
    try
    {
        auto const json = nlohmann::json::parse(utf8_json);
        auto const version = json.is_object() ? json.find("version") : json.end();
        if (!json.is_object() || version == json.end() || !version->is_number_integer()
            || version->get<std::int64_t>() != 1)
            return UMA_ERROR_INVALID_ARGUMENT;
        for (auto const& [key, _] : json.items())
            if (key != "version" && key != "display_id"
                && key != "surface_display_id" && key != "screenshot_method"
                && key != "swipe_duration_multiplier" && key != "extra_swipe_distance"
                && key != "extra_swipe_duration_ms" && key != "touch_backend"
                && key != "touch_binary_path" && key != "touch_remote_path"
                && key != "touch_event_id")
                return UMA_ERROR_INVALID_ARGUMENT;

        DeviceSessionOptions options;
        if (auto const display = json.find("display_id"); display != json.end() && !display->is_null())
        {
            if (!display->is_number_integer()) return UMA_ERROR_INVALID_ARGUMENT;
            auto const value = display->get<std::int64_t>();
            if (value < 0 || value > (std::numeric_limits<std::int32_t>::max)())
                return UMA_ERROR_INVALID_ARGUMENT;
            options.display_id = static_cast<std::int32_t>(value);
        }

        if (auto const surface = json.find("surface_display_id");
            surface != json.end() && !surface->is_null())
        {
            if (!surface->is_string()) return UMA_ERROR_INVALID_ARGUMENT;
            auto const value = surface->get<std::string>();
            if (value.empty() || value.size() > 20) return UMA_ERROR_INVALID_ARGUMENT;
            std::uint64_t parsed_id = 0;
            auto const parsed = std::from_chars(
                value.data(), value.data() + value.size(), parsed_id, 10);
            if (parsed.ec != std::errc{} || parsed.ptr != value.data() + value.size())
                return UMA_ERROR_INVALID_ARGUMENT;
            options.surface_display_id = std::to_string(parsed_id);
        }
        if (options.display_id && *options.display_id != 0
            && !options.surface_display_id)
            return UMA_ERROR_INVALID_ARGUMENT;
        if (options.surface_display_id && !options.display_id)
            return UMA_ERROR_INVALID_ARGUMENT;

        auto const method = json.value("screenshot_method", std::string{"auto"});
        if (method == "auto") options.screenshot_method = DeviceSessionOptions::ScreenshotMethod::automatic;
        else if (method == "raw") options.screenshot_method = DeviceSessionOptions::ScreenshotMethod::raw;
        else if (method == "gzip") options.screenshot_method = DeviceSessionOptions::ScreenshotMethod::gzip;
        else if (method == "png") options.screenshot_method = DeviceSessionOptions::ScreenshotMethod::png;
        else if (method == "shell_png") options.screenshot_method = DeviceSessionOptions::ScreenshotMethod::shell_png;
        else return UMA_ERROR_INVALID_ARGUMENT;

        if (auto const multiplier = json.find("swipe_duration_multiplier");
            multiplier != json.end())
        {
            if (!multiplier->is_number()) return UMA_ERROR_INVALID_ARGUMENT;
            auto const value = multiplier->get<double>();
            if (!std::isfinite(value) || value < 0.01 || value > 100.0)
                return UMA_ERROR_INVALID_ARGUMENT;
            options.swipe_duration_multiplier = value;
        }
        if (auto const distance = json.find("extra_swipe_distance"); distance != json.end())
        {
            if (!distance->is_number_integer()) return UMA_ERROR_INVALID_ARGUMENT;
            auto const value = distance->get<std::int64_t>();
            if (value < 0 || value > 100000) return UMA_ERROR_INVALID_ARGUMENT;
            options.extra_swipe_distance = static_cast<std::int32_t>(value);
        }
        if (auto const extra_duration = json.find("extra_swipe_duration_ms");
            extra_duration != json.end())
        {
            if (!extra_duration->is_number_integer()) return UMA_ERROR_INVALID_ARGUMENT;
            auto const value = extra_duration->get<std::int64_t>();
            if (value < 0 || value > 60000) return UMA_ERROR_INVALID_ARGUMENT;
            options.extra_swipe_duration_ms = static_cast<std::int32_t>(value);
        }

        auto const touch_backend = json.value("touch_backend", std::string{"adb"});
        if (touch_backend == "adb")
            options.touch_backend = DeviceSessionOptions::TouchBackend::adb;
        else if (touch_backend == "minitouch")
            options.touch_backend = DeviceSessionOptions::TouchBackend::minitouch;
        else if (touch_backend == "maatouch")
            options.touch_backend = DeviceSessionOptions::TouchBackend::maatouch;
        else
            return UMA_ERROR_INVALID_ARGUMENT;

        if (auto const binary = json.find("touch_binary_path");
            binary != json.end() && !binary->is_null())
        {
            if (!binary->is_string()) return UMA_ERROR_INVALID_ARGUMENT;
            auto const value = binary->get<std::string>();
            if (!value.empty())
            {
                auto path = path_from_utf8(value);
                if (!path || path->empty()) return UMA_ERROR_INVALID_ARGUMENT;
                options.touch_binary_path = std::move(*path);
            }
        }
        if (auto const remote = json.find("touch_remote_path");
            remote != json.end() && !remote->is_null())
        {
            if (!remote->is_string()) return UMA_ERROR_INVALID_ARGUMENT;
            options.touch_remote_path = remote->get<std::string>();
        }
        if (auto const event_id = json.find("touch_event_id");
            event_id != json.end() && !event_id->is_null())
        {
            if (!event_id->is_string()) return UMA_ERROR_INVALID_ARGUMENT;
            auto const value = event_id->get<std::string>();
            std::uint32_t parsed_id{};
            auto const parsed = std::from_chars(value.data(), value.data() + value.size(), parsed_id);
            if (value.empty() || value.size() > 10 || parsed.ec != std::errc{}
                || parsed.ptr != value.data() + value.size())
                return UMA_ERROR_INVALID_ARGUMENT;
            options.touch_event_id = std::to_string(parsed_id);
        }
        if (!valid_touch_remote_path(options.touch_remote_path))
            return UMA_ERROR_INVALID_ARGUMENT;
        if (options.touch_backend != DeviceSessionOptions::TouchBackend::adb
            && options.touch_binary_path.empty())
            return UMA_ERROR_INVALID_ARGUMENT;
        if (options.touch_backend == DeviceSessionOptions::TouchBackend::maatouch
            && options.touch_remote_path.find_first_of("'\" \t\r\n") != std::string::npos)
            return UMA_ERROR_INVALID_ARGUMENT;
        if (options.touch_backend == DeviceSessionOptions::TouchBackend::maatouch
            && options.display_id.value_or(0) != 0)
            return UMA_ERROR_INVALID_ARGUMENT;
        if (options.touch_backend == DeviceSessionOptions::TouchBackend::minitouch
            && options.display_id.value_or(0) != 0 && !options.touch_event_id)
            return UMA_ERROR_INVALID_ARGUMENT;

        auto state = handle->state;
        if (!state) return UMA_ERROR_INVALID_ARGUMENT;
        std::lock_guard lock(state->mutex);
        if (state->closing) return UMA_ERROR_INVALID_ARGUMENT;
        if (state->active_operation_id != 0 || state->recovering_device
            || state->active_recognition_count != 0)
            return UMA_ERROR_BUSY;
        if (state->session)
        {
            if (auto const failure = state->session->update_options(options))
                return failure->error_code;
        }
        state->device_options = std::move(options);
        return UMA_SUCCESS;
    }
    catch (...)
    {
        return UMA_ERROR_INVALID_ARGUMENT;
    }
}

namespace {

bool copy_image_view(UmaImageView const* source, UmaAssistant::Vision::GrayImage& target)
{
    if (source == nullptr || source->width <= 0 || source->height <= 0
        || source->width > 10000 || source->height > 10000)
        return false;
    auto const pixels = static_cast<std::uint64_t>(source->width)
        * static_cast<std::uint64_t>(source->height);
    if (pixels > 100'000'000u || pixels > (std::numeric_limits<std::uint64_t>::max)() / 4u)
        return false;
    auto const rgba_bytes = pixels * 4u;
    if ((source->gray_pixels == nullptr) != (source->gray_length == 0)
        || (source->rgba_pixels == nullptr) != (source->rgba_length == 0)
        || (source->gray_pixels != nullptr && source->gray_length != pixels)
        || (source->rgba_pixels != nullptr && source->rgba_length != rgba_bytes)
        || (source->gray_pixels == nullptr && source->rgba_pixels == nullptr))
        return false;

    target.width = source->width;
    target.height = source->height;
    if (source->gray_pixels != nullptr)
        target.gray.assign(source->gray_pixels, source->gray_pixels + static_cast<std::size_t>(pixels));
    else
    {
        target.gray.resize(static_cast<std::size_t>(pixels));
        for (std::size_t index = 0; index < target.gray.size(); ++index)
        {
            auto const offset = index * 4;
            target.gray[index] = static_cast<std::uint8_t>(
                (source->rgba_pixels[offset] * 299u
                    + source->rgba_pixels[offset + 1] * 587u
                    + source->rgba_pixels[offset + 2] * 114u) / 1000u);
        }
    }
    if (source->rgba_pixels != nullptr)
        target.rgba.assign(source->rgba_pixels,
            source->rgba_pixels + static_cast<std::size_t>(rgba_bytes));
    return true;
}

UmaAssistant::Vision::MatchResult copy_match(UmaTemplateMatchResult const& source) noexcept
{
    return {source.found != 0, source.score, source.x, source.y, source.width, source.height};
}

std::int32_t acquire_recognition(
    UmaHandle handle, std::shared_ptr<UmaHandleState>& state) noexcept
{
    try
    {
        if (handle == nullptr) return UMA_ERROR_INVALID_ARGUMENT;
        state = handle->state;
        if (!state) return UMA_ERROR_INVALID_ARGUMENT;
        std::lock_guard lock(state->mutex);
        if (state->closing) return UMA_ERROR_INVALID_ARGUMENT;
        if (state->active_operation_id != 0 || state->recovering_device
            || state->active_recognition_count == (std::numeric_limits<std::uint32_t>::max)())
            return UMA_ERROR_BUSY;
        ++state->active_recognition_count;
        return UMA_SUCCESS;
    }
    catch (...)
    {
        return UMA_ERROR_RECOGNITION_FAILED;
    }
}

class RecognitionLease
{
public:
    explicit RecognitionLease(std::shared_ptr<UmaHandleState> state) noexcept
        : state_(std::move(state))
    {
    }
    RecognitionLease(RecognitionLease const&) = delete;
    RecognitionLease& operator=(RecognitionLease const&) = delete;
    ~RecognitionLease()
    {
        if (!state_) return;
        try
        {
            std::lock_guard lock(state_->mutex);
            if (state_->active_recognition_count != 0)
                --state_->active_recognition_count;
            if (state_->active_recognition_count == 0)
                state_->recognition_finished.notify_all();
        }
        catch (...)
        {
        }
    }

private:
    std::shared_ptr<UmaHandleState> state_;
};

}

std::int32_t prepare_numeric_glyphs(UmaImageView const* source, int warm_gain,
    UmaColorRule const* rule, Vision::GrayImage& image) noexcept
{
    image = {};
    if (!rule || (warm_gain != 0 && warm_gain != 1)) return UMA_ERROR_INVALID_ARGUMENT;
    try
    {
        Vision::GrayImage input;
        if (!copy_image_view(source, input)) return UMA_ERROR_INVALID_ARGUMENT;
        auto prepared = Vision::prepare_numeric_glyphs(input, warm_gain != 0, *rule);
        if (prepared) image = std::move(*prepared);
        return UMA_SUCCESS;
    }
    catch (std::invalid_argument const&) { return UMA_ERROR_INVALID_ARGUMENT; }
    catch (...) { return UMA_ERROR_RECOGNITION_FAILED; }
}
std::int32_t compose_gray_images(UmaImageView const* sources, UmaImageRect const* placements,
    std::uint64_t count, int width, int height, Vision::GrayImage& image) noexcept
{
    if (width <= 0 || height <= 0 || width > 10000 || height > 10000 || count > 2048
        || (count != 0 && (!sources || !placements))) return UMA_ERROR_INVALID_ARGUMENT;
    try
    {
        std::vector<Vision::GrayImage> inputs(static_cast<std::size_t>(count));
        for (std::size_t i = 0; i < inputs.size(); ++i)
        {
            if (!copy_image_view(sources + i, inputs[i])) return UMA_ERROR_INVALID_ARGUMENT;
            auto const p = placements[i];
            if (p.x < 0 || p.y < 0 || p.width != inputs[i].width || p.height != inputs[i].height
                || static_cast<std::int64_t>(p.x) + p.width > width
                || static_cast<std::int64_t>(p.y) + p.height > height) return UMA_ERROR_INVALID_ARGUMENT;
        }
        image = Vision::compose_gray(inputs, {placements, static_cast<std::size_t>(count)}, width, height);
        return UMA_SUCCESS;
    }
    catch (...) { return UMA_ERROR_RECOGNITION_FAILED; }
}
std::int32_t count_ink_groups(UmaImageView const* source, int threshold, int* groups) noexcept
{
    if (!groups) return UMA_ERROR_INVALID_ARGUMENT;
    *groups = 0;
    if (threshold < 0 || threshold > 256) return UMA_ERROR_INVALID_ARGUMENT;
    try
    {
        Vision::GrayImage image;
        if (!copy_image_view(source, image)) return UMA_ERROR_INVALID_ARGUMENT;
        *groups = Vision::count_ink_groups(image, threshold);
        return UMA_SUCCESS;
    }
    catch (...) { return UMA_ERROR_RECOGNITION_FAILED; }
}
std::int32_t sample_image_difference(UmaImageView const* before, UmaImageView const* after,
    UmaImagePoint const* points, std::uint64_t count, std::uint64_t* difference) noexcept
{
    if (!difference) return UMA_ERROR_INVALID_ARGUMENT;
    *difference = 0;
    if (count > 1000000 || (count != 0 && !points)) return UMA_ERROR_INVALID_ARGUMENT;
    try
    {
        Vision::GrayImage a, b;
        if (!copy_image_view(before, a) || !copy_image_view(after, b)
            || a.width != b.width || a.height != b.height) return UMA_ERROR_INVALID_ARGUMENT;
        for (std::uint64_t i = 0; i < count; ++i)
            if (points[i].x < 0 || points[i].y < 0 || points[i].x >= a.width || points[i].y >= a.height)
                return UMA_ERROR_INVALID_ARGUMENT;
        *difference = Vision::sample_difference(a, b, {points, static_cast<std::size_t>(count)});
        return UMA_SUCCESS;
    }
    catch (...) { return UMA_ERROR_RECOGNITION_FAILED; }
}

std::int32_t load_template_image(UmaHandle handle, char const* path, std::int32_t preparation,
    std::shared_ptr<Resources::PreparedTemplate const>& image) noexcept
{
    image.reset();
    if (!path || !*path || preparation < UMA_TEMPLATE_ORIGINAL || preparation > UMA_TEMPLATE_PORTRAIT)
        return UMA_ERROR_INVALID_ARGUMENT;
    std::shared_ptr<UmaHandleState> state;
    auto const gate = acquire_recognition(handle, state);
    if (gate != UMA_SUCCESS) return gate;
    RecognitionLease lease{state};
    try
    {
        std::shared_ptr<Resources::ResourceSnapshot const> resources;
        { std::lock_guard lock(state->mutex); resources = state->resources; }
        if (!resources) return UMA_ERROR_DEVICE_NOT_READY;
        image = resources->load_template(path, static_cast<Resources::TemplatePreparation>(preparation));
        return image ? UMA_SUCCESS : UMA_ERROR_RECOGNITION_FAILED;
    }
    catch (...) { return UMA_ERROR_RECOGNITION_FAILED; }
}

std::int32_t refresh_resource_snapshot(UmaHandle handle) noexcept
{
    if (!handle || !handle->state) return UMA_ERROR_INVALID_ARGUMENT;
    try
    {
        auto const bundle = CoreRuntime::instance().resource_bundle();
        if (!bundle.resources) return UMA_ERROR_DEVICE_NOT_READY;
        auto const state = handle->state;
        std::lock_guard lock(state->mutex);
        if (state->closing) return UMA_ERROR_INVALID_ARGUMENT;
        if (state->active_operation_id || state->active_recognition_count || state->recovering_device)
            return UMA_ERROR_BUSY;
        state->resources = bundle.resources;
        return UMA_SUCCESS;
    }
    catch (...) { return UMA_ERROR_RECOGNITION_FAILED; }
}

std::int32_t transform_image(UmaImageView const* source, UmaImageTransformOptions const* options,
    Vision::GrayImage& image) noexcept
{
    if (!source || !options || options->mean_gray < 0 || options->mean_gray > 1
        || options->nearest < 0 || options->nearest > 1 || (options->nearest != 0 && options->mean_gray != 0))
        return UMA_ERROR_INVALID_ARGUMENT;
    try
    {
        if (!copy_image_view(source, image)) return UMA_ERROR_INVALID_ARGUMENT;
        if (options->crop_width != 0 || options->crop_height != 0)
            image = Resources::crop_image(image, options->crop_x, options->crop_y,
                options->crop_width, options->crop_height);
        else if (options->crop_x != 0 || options->crop_y != 0) return UMA_ERROR_INVALID_ARGUMENT;
        if (options->target_width != 0 || options->target_height != 0 || options->mean_gray != 0)
        {
            auto const width = options->target_width == 0 ? image.width : options->target_width;
            auto const height = options->target_height == 0 ? image.height : options->target_height;
            image = options->nearest != 0 ? Resources::resize_nearest(image, width, height)
                : Resources::resize_image(image, width, height, options->mean_gray != 0);
        }
        return UMA_SUCCESS;
    }
    catch (std::invalid_argument const&) { return UMA_ERROR_INVALID_ARGUMENT; }
    catch (...) { return UMA_ERROR_RECOGNITION_FAILED; }
}

std::int32_t binarize_color(UmaImageView const* source, UmaColorRule const* rule, Vision::GrayImage& image) noexcept
{
    if (source == nullptr || rule == nullptr) return UMA_ERROR_INVALID_ARGUMENT;
    try
    {
        Vision::GrayImage input;
        if (!copy_image_view(source, input) || input.rgba.empty()) return UMA_ERROR_INVALID_ARGUMENT;
        image = Vision::binarize_color(input, *rule);
        return UMA_SUCCESS;
    }
    catch (...) { return UMA_ERROR_RECOGNITION_FAILED; }
}

std::int32_t find_color_blobs(UmaImageView const* source, UmaColorRule const* rule,
    UmaImageRect const* region, UmaColorBlob* destination, std::uint64_t capacity,
    std::uint64_t* required_count) noexcept
{
    if (required_count == nullptr) return UMA_ERROR_INVALID_ARGUMENT;
    *required_count = 0;
    if (source == nullptr || rule == nullptr || region == nullptr || (destination == nullptr && capacity != 0))
        return UMA_ERROR_INVALID_ARGUMENT;
    try
    {
        Vision::GrayImage input;
        if (!copy_image_view(source, input) || input.rgba.empty()) return UMA_ERROR_INVALID_ARGUMENT;
        auto const blobs = Vision::find_color_blobs(input, *rule, *region);
        *required_count = blobs.size();
        if (destination == nullptr) return UMA_SUCCESS;
        if (capacity < blobs.size()) return UMA_ERROR_BUFFER_TOO_SMALL;
        std::copy(blobs.begin(), blobs.end(), destination);
        return UMA_SUCCESS;
    }
    catch (...) { return UMA_ERROR_RECOGNITION_FAILED; }
}

std::int32_t count_color(UmaImageView const* source, UmaColorRule const* rule, UmaImageRect const* region,
    UmaImageRect const* exclude, std::uint64_t* matching, std::uint64_t* sampled) noexcept
{
    if (matching == nullptr || sampled == nullptr) return UMA_ERROR_INVALID_ARGUMENT;
    *matching = 0; *sampled = 0;
    if (source == nullptr || rule == nullptr || region == nullptr) return UMA_ERROR_INVALID_ARGUMENT;
    try
    {
        Vision::GrayImage input;
        if (!copy_image_view(source, input) || input.rgba.empty()) return UMA_ERROR_INVALID_ARGUMENT;
        auto const counts = Vision::count_color(input, *rule, *region, exclude);
        *matching = counts.first; *sampled = counts.second;
        return UMA_SUCCESS;
    }
    catch (...) { return UMA_ERROR_RECOGNITION_FAILED; }
}

std::int32_t match_template(
    UmaHandle handle, UmaImageView const* screen, UmaImageView const* template_image,
    UmaTemplateMatchOptions const* options, UmaTemplateMatchResult* result) noexcept
{
    if (handle == nullptr || screen == nullptr || template_image == nullptr
        || options == nullptr || result == nullptr)
        return UMA_ERROR_INVALID_ARGUMENT;
    std::shared_ptr<UmaHandleState> state;
    auto const gate = acquire_recognition(handle, state);
    if (gate != UMA_SUCCESS) return gate;
    RecognitionLease lease{state};
    try
    {
        UmaAssistant::Vision::GrayImage screen_image;
        UmaAssistant::Vision::GrayImage template_value;
        if (!copy_image_view(screen, screen_image) || !copy_image_view(template_image, template_value)
            || options->has_roi < 0 || options->has_roi > 1
            || options->candidate_step_override < 0 || options->scale_count > 256
            || (options->scale_count != 0 && options->scales == nullptr))
            return UMA_ERROR_INVALID_ARGUMENT;

        UmaAssistant::Vision::Roi roi;
        if (options->has_roi != 0)
            roi = std::array<std::int32_t, 4>{options->roi_x, options->roi_y,
                options->roi_width, options->roi_height};

        using namespace UmaAssistant::Vision;
        MatchResult match;
        switch (options->method)
        {
        case UMA_MATCH_TEMPLATE_GRAY:
            match = find(screen_image, template_value, roi, options->threshold,
                options->reference_width, options->reference_height,
                options->candidate_step_override == 0
                    ? std::nullopt
                    : std::optional<std::int32_t>{options->candidate_step_override});
            break;
        case UMA_MATCH_TEMPLATE_COLOR:
            match = find_color(screen_image, template_value, roi, options->threshold,
                options->reference_width, options->reference_height,
                options->require_text_contrast != 0);
            break;
        case UMA_MATCH_TEMPLATE_EXHAUSTIVE_COLOR:
            match = find_exhaustive_color(screen_image, template_value, roi, options->threshold,
                options->reference_width, options->reference_height);
            break;
        case UMA_MATCH_TEMPLATE_COLORED_REGION:
            if (!roi) return UMA_ERROR_INVALID_ARGUMENT;
            match = score_colored_region(screen_image, template_value, *roi, options->threshold);
            break;
        case UMA_MATCH_TEMPLATE_BUTTON:
            match = find_button(screen_image, template_value, roi, options->threshold,
                options->reference_width, options->reference_height);
            break;
        case UMA_MATCH_TEMPLATE_MASKED_REGION:
        case UMA_MATCH_TEMPLATE_SCREENSHOT_CROP:
            if (!roi) return UMA_ERROR_INVALID_ARGUMENT;
            match = options->method == UMA_MATCH_TEMPLATE_MASKED_REGION
                ? score_masked_region(screen_image, template_value, *roi, options->threshold)
                : find_screenshot_crop(screen_image, template_value, *roi,
                    options->reference_width, options->reference_height, options->threshold);
            break;
        case UMA_MATCH_TEMPLATE_SCALED:
        case UMA_MATCH_TEMPLATE_SCALED_MASKED:
        {
            std::vector<double> scales;
            if (options->scale_count != 0)
                scales.assign(options->scales,
                    options->scales + static_cast<std::size_t>(options->scale_count));
            if (options->method == UMA_MATCH_TEMPLATE_SCALED_MASKED)
                match = find_scaled_masked(screen_image, template_value, roi, options->threshold,
                    options->reference_width, options->reference_height, scales);
            else
            {
                ScaledOptions scaled;
                scaled.scales = std::move(scales);
                scaled.candidate_step = options->candidate_step;
                scaled.sample_width = options->sample_width;
                scaled.sample_height = options->sample_height;
                scaled.full_search_on_miss = options->full_search_on_miss != 0;
                match = find_scaled(screen_image, template_value, roi, options->threshold,
                    options->reference_width, options->reference_height, scaled);
            }
            break;
        }
        default:
            return UMA_ERROR_INVALID_ARGUMENT;
        }
        *result = {match.found ? 1 : 0, match.score, match.x, match.y, match.width, match.height};
        return UMA_SUCCESS;
    }
    catch (...)
    {
        return UMA_ERROR_RECOGNITION_FAILED;
    }
}

std::int32_t recognize_text(
    UmaHandle handle, UmaImageView const* image, char const* utf8_language,
    char* json_destination, std::uint64_t capacity,
    std::uint64_t* required_length) noexcept
{
    if (handle == nullptr || image == nullptr || required_length == nullptr
        || (json_destination == nullptr && capacity != 0))
        return UMA_ERROR_INVALID_ARGUMENT;
    *required_length = 0;
    std::shared_ptr<UmaHandleState> state;
    auto const gate = acquire_recognition(handle, state);
    if (gate != UMA_SUCCESS) return gate;
    RecognitionLease lease{state};
    try
    {
        UmaAssistant::Vision::GrayImage input;
        if (!copy_image_view(image, input) || input.rgba.empty())
            return UMA_ERROR_INVALID_ARGUMENT;
        auto const recognized = state->ocr.recognize(input,
            utf8_language == nullptr ? std::string_view{} : std::string_view{utf8_language});
        nlohmann::json json = {
            {"language", recognized.language},
            {"width", recognized.width},
            {"height", recognized.height},
            {"detections", nlohmann::json::array()},
        };
        for (auto const& detection : recognized.detections)
        {
            json["detections"].push_back({
                {"text", detection.text},
                {"bounds", {{"x", detection.bounds.x}, {"y", detection.bounds.y},
                    {"width", detection.bounds.width}, {"height", detection.bounds.height}}},
                {"confidence", detection.confidence},
            });
        }
        auto const serialized = json.dump();
        auto const required = static_cast<std::uint64_t>(serialized.size()) + 1u;
        *required_length = required;
        if (json_destination == nullptr || capacity < required)
            return UMA_ERROR_BUFFER_TOO_SMALL;
        std::memcpy(json_destination, serialized.c_str(), static_cast<std::size_t>(required));
        return UMA_SUCCESS;
    }
    catch (...)
    {
        return UMA_ERROR_RECOGNITION_FAILED;
    }
}

std::int32_t compare_image_change(
    UmaHandle handle, UmaImageView const* before, UmaImageView const* after,
    UmaTemplateMatchResult const* match, std::int32_t* changed) noexcept
{
    if (handle == nullptr || before == nullptr || after == nullptr || match == nullptr || changed == nullptr)
        return UMA_ERROR_INVALID_ARGUMENT;
    std::shared_ptr<UmaHandleState> state;
    auto const gate = acquire_recognition(handle, state);
    if (gate != UMA_SUCCESS) return gate;
    RecognitionLease lease{state};
    try
    {
        UmaAssistant::Vision::GrayImage before_image;
        UmaAssistant::Vision::GrayImage after_image;
        if (!copy_image_view(before, before_image) || !copy_image_view(after, after_image))
            return UMA_ERROR_INVALID_ARGUMENT;
        *changed = UmaAssistant::Vision::has_meaningful_change(
            before_image, after_image, copy_match(*match)) ? 1 : 0;
        return UMA_SUCCESS;
    }
    catch (...)
    {
        return UMA_ERROR_RECOGNITION_FAILED;
    }
}

std::int32_t compare_persistent_image_change(
    UmaHandle handle, UmaImageView const* before, UmaImageView const* first_after,
    UmaImageView const* settled_after, UmaTemplateMatchResult const* match,
    std::int32_t* changed) noexcept
{
    if (handle == nullptr || before == nullptr || first_after == nullptr
        || settled_after == nullptr || match == nullptr || changed == nullptr)
        return UMA_ERROR_INVALID_ARGUMENT;
    std::shared_ptr<UmaHandleState> state;
    auto const gate = acquire_recognition(handle, state);
    if (gate != UMA_SUCCESS) return gate;
    RecognitionLease lease{state};
    try
    {
        UmaAssistant::Vision::GrayImage before_image;
        UmaAssistant::Vision::GrayImage first_image;
        UmaAssistant::Vision::GrayImage settled_image;
        if (!copy_image_view(before, before_image) || !copy_image_view(first_after, first_image)
            || !copy_image_view(settled_after, settled_image))
            return UMA_ERROR_INVALID_ARGUMENT;
        *changed = UmaAssistant::Vision::has_persistent_change(
            before_image, first_image, settled_image, copy_match(*match)) ? 1 : 0;
        return UMA_SUCCESS;
    }
    catch (...)
    {
        return UMA_ERROR_RECOGNITION_FAILED;
    }
}

std::int32_t validate_pipeline_json(
    char const* utf8_json,
    char* error_destination,
    std::uint64_t capacity,
    std::uint64_t* required_length) noexcept
{
    if (utf8_json == nullptr) return UMA_ERROR_INVALID_ARGUMENT;
    try
    {
        auto def = TaskEngine::HachimiPipelineLoader::LoadFromJsonString(utf8_json);
        if (def.has_value())
        {
            if (required_length) *required_length = 0;
            if (error_destination && capacity > 0) *error_destination = '\0';
            return UMA_SUCCESS;
        }

        std::string err = "Failed to parse pipeline definition or invalid schemaVersion";
        auto const needed = static_cast<std::uint64_t>(err.size() + 1);
        if (required_length) *required_length = needed;
        if (capacity < needed) return UMA_ERROR_BUFFER_TOO_SMALL;
        if (error_destination) std::memcpy(error_destination, err.c_str(), needed);
        return UMA_ERROR_INVALID_ARGUMENT;
    }
    catch (const std::exception& ex)
    {
        std::string err = ex.what();
        auto const needed = static_cast<std::uint64_t>(err.size() + 1);
        if (required_length) *required_length = needed;
        if (capacity < needed) return UMA_ERROR_BUFFER_TOO_SMALL;
        if (error_destination) std::memcpy(error_destination, err.c_str(), needed);
        return UMA_ERROR_INVALID_ARGUMENT;
    }
    catch (...)
    {
        return UMA_ERROR_INVALID_ARGUMENT;
    }
}

UmaStartResult start_run_pipeline(
    UmaHandle handle,
    std::string pipeline_json_or_path,
    std::string entry_task)
{
    if (handle == nullptr || pipeline_json_or_path.empty())
        return {0, UMA_ERROR_INVALID_ARGUMENT};

    auto state = handle->state;
    if (!state) return {0, UMA_ERROR_INVALID_ARGUMENT};

    std::shared_ptr<DeviceSession> session;
    std::shared_ptr<Resources::ResourceSnapshot const> resources;
    std::string user_directory;
    auto const operation_id = CoreRuntime::instance().allocate_operation_id();
    std::stop_token token;
    std::jthread previous_worker;

    {
        std::lock_guard lock(state->mutex);
        if (state->closing) return {0, UMA_ERROR_INVALID_ARGUMENT};
        if (state->active_operation_id != 0 || state->active_recognition_count != 0)
            return {0, UMA_ERROR_BUSY};
        session = state->session;
        if (!session) return {0, UMA_ERROR_DEVICE_NOT_READY};
        resources = state->resources;
        if (!resources)
        {
            auto const bundle = CoreRuntime::instance().resource_bundle();
            resources = bundle.resources;
            state->resources = resources;
        }
        if (!resources) return {0, UMA_ERROR_DEVICE_NOT_READY};

        // Freeze relative-path resolution with the accepted operation. A later
        // UmaSetUserDir must not redirect this run while its worker is queued.
        user_directory = CoreRuntime::instance().user_dir();

        previous_worker = std::move(handle->worker);
        state->active_operation_id = operation_id;
        state->cancellation = std::stop_source{};
        token = state->cancellation.get_token();
    }

    join_prior(previous_worker);

    try
    {
        std::lock_guard lock(state->mutex);
        if (state->closing || state->active_operation_id != operation_id)
        {
            state->active_operation_id = 0;
            return {0, UMA_ERROR_INVALID_ARGUMENT};
        }

        handle->worker = std::jthread(
            [state, operation_id, session, resources, user_directory = std::move(user_directory),
             pipeline_str = std::move(pipeline_json_or_path),
             entry = std::move(entry_task), token](std::stop_token) mutable {
                try
                {
                    std::optional<TaskEngine::HachimiPipelineDefinition> def;
                    if (std::filesystem::exists(pipeline_str))
                    {
                        def = TaskEngine::HachimiPipelineLoader::LoadFromFile(pipeline_str);
                    }
                    else
                    {
                        def = TaskEngine::HachimiPipelineLoader::LoadFromJsonString(pipeline_str, user_directory);
                    }

                    if (!def.has_value())
                    {
                        mark_terminal(*state, operation_id);
                        emit_runtime(*state, UMA_MSG_PIPELINE_FAILED, operation_id, "PipelineFailed", {
                            {"error_code", UMA_ERROR_INVALID_ARGUMENT},
                            {"message", "Could not load or parse pipeline definition"}
                        });
                        return;
                    }

                    std::string base_dir = def->baseDirectory;
                    if (base_dir.empty())
                    {
                        base_dir = user_directory;
                    }

                    TaskEngine::NativeTaskExecutionContext context(
                        session,
                        resources,
                        &state->ocr,
                        &state->sender,
                        operation_id,
                        token,
                        base_dir);

                    TaskEngine::TaskGraphRunner runner;
                    GameTasks::GameTaskActionExtensions::RegisterAll(runner.GetActionRegistry());

                    auto runResult = runner.Run(*def, entry, context);

                    mark_terminal(*state, operation_id);
                    if (runResult.status == TaskEngine::TaskGraphStatus::Success ||
                        runResult.status == TaskEngine::TaskGraphStatus::Completed)
                    {
                        emit_runtime(*state, UMA_MSG_PIPELINE_COMPLETED, operation_id, "PipelineCompleted", {
                            {"status", static_cast<int>(runResult.status)},
                            {"final_task", runResult.finalTask},
                            {"total_steps", runResult.totalStepsExecuted}
                        });
                    }
                    else
                    {
                        emit_runtime(*state, UMA_MSG_PIPELINE_FAILED, operation_id, "PipelineFailed", {
                            {"status", static_cast<int>(runResult.status)},
                            {"final_task", runResult.finalTask},
                            {"total_steps", runResult.totalStepsExecuted},
                            {"error_message", runResult.errorMessage}
                        });
                    }
                }
                catch (const std::exception& ex)
                {
                    mark_terminal(*state, operation_id);
                    emit_runtime(*state, UMA_MSG_PIPELINE_FAILED, operation_id, "PipelineFailed", {
                        {"error_code", UMA_ERROR_RECOGNITION_FAILED},
                        {"error_message", ex.what()}
                    });
                }
                catch (...)
                {
                    mark_terminal(*state, operation_id);
                    emit_runtime(*state, UMA_MSG_PIPELINE_FAILED, operation_id, "PipelineFailed", {
                        {"error_code", UMA_ERROR_RECOGNITION_FAILED},
                        {"error_message", "Unexpected failure during pipeline execution"}
                    });
                }
            });
    }
    catch (...)
    {
        std::lock_guard lock(state->mutex);
        if (state->active_operation_id == operation_id)
            state->active_operation_id = 0;
        return {0, UMA_ERROR_PROCESS_START_FAILED};
    }

    return {operation_id, UMA_SUCCESS};
}

UmaStartResult start_run_game_task(
    UmaHandle handle,
    std::string task_id,
    std::string settings_json)
{
    if (handle == nullptr || task_id.empty() || settings_json.empty())
        return {0, UMA_ERROR_INVALID_ARGUMENT};

    auto state = handle->state;
    if (!state) return {0, UMA_ERROR_INVALID_ARGUMENT};

    nlohmann::json settings;
    try
    {
        // Dumping the task ID forces nlohmann to reject malformed UTF-8 before
        // a worker is accepted. Parsing settings validates both UTF-8 and JSON.
        (void)nlohmann::json(task_id).dump();
        settings = nlohmann::json::parse(settings_json);
    }
    catch (...)
    {
        return {0, UMA_ERROR_INVALID_ARGUMENT};
    }

    if (!GameTasks::NativeGameTaskRunner::SupportsTask(task_id))
        return {0, UMA_ERROR_INVALID_ARGUMENT};
    std::string validation_error;
    try
    {
        if (!GameTasks::NativeGameTaskRunner::ValidateSettings(
                task_id, settings, validation_error))
            return {0, UMA_ERROR_INVALID_ARGUMENT};
    }
    catch (...)
    {
        return {0, UMA_ERROR_INVALID_ARGUMENT};
    }

    std::shared_ptr<DeviceSession> session;
    std::shared_ptr<Resources::ResourceSnapshot const> resources;
    auto const operation_id = CoreRuntime::instance().allocate_operation_id();
    std::stop_token token;
    std::jthread previous_worker;

    {
        std::lock_guard lock(state->mutex);
        if (state->closing) return {0, UMA_ERROR_INVALID_ARGUMENT};
        if (state->active_operation_id != 0 || state->active_recognition_count != 0)
            return {0, UMA_ERROR_BUSY};
        session = state->session;
        if (!session) return {0, UMA_ERROR_DEVICE_NOT_READY};
        resources = state->resources;
        if (!resources)
        {
            auto const bundle = CoreRuntime::instance().resource_bundle();
            resources = bundle.resources;
            state->resources = resources;
        }
        if (!resources) return {0, UMA_ERROR_DEVICE_NOT_READY};

        // Resolve the checkpoint default at acceptance time so a later global
        // user-directory update cannot redirect a queued career run.
        try
        {
            pin_career_checkpoint_directory(
                task_id, settings, CoreRuntime::instance().user_dir());
        }
        catch (...)
        {
            return {0, UMA_ERROR_INVALID_ARGUMENT};
        }

        previous_worker = std::move(handle->worker);
        state->active_operation_id = operation_id;
        state->cancellation = std::stop_source{};
        token = state->cancellation.get_token();
    }

    join_prior(previous_worker);

    try
    {
        std::lock_guard lock(state->mutex);
        if (state->closing || state->active_operation_id != operation_id)
        {
            state->active_operation_id = 0;
            return {0, UMA_ERROR_INVALID_ARGUMENT};
        }

        handle->worker = std::jthread(
            [state, operation_id, session, resources, task_id = std::move(task_id),
             settings = std::move(settings), token](std::stop_token) mutable {
                GameTasks::NativeGameTaskResult result;
                try
                {
                    TaskEngine::NativeTaskExecutionContext context(
                        session,
                        resources,
                        &state->ocr,
                        &state->sender,
                        operation_id,
                        token,
                        resources->root(),
                        task_id);
                    GameTasks::NativeGameTaskPackageOperations package_operations(session, token);
                    result = GameTasks::NativeGameTaskRunner::Run(
                        task_id,
                        settings,
                        context,
                        resources->root(),
                        &package_operations,
                        resources.get());
                }
                catch (std::exception const& exception)
                {
                    result.taskId = task_id;
                    result.succeeded = false;
                    result.errorCode = token.stop_requested()
                        ? UMA_ERROR_CANCELED
                        : UMA_ERROR_COMMAND_FAILED;
                    result.message = exception.what();
                }
                catch (...)
                {
                    result.taskId = task_id;
                    result.succeeded = false;
                    result.errorCode = token.stop_requested()
                        ? UMA_ERROR_CANCELED
                        : UMA_ERROR_COMMAND_FAILED;
                    result.message = "Unexpected failure during native game-task execution.";
                }

                result.taskId = task_id;
                if (token.stop_requested())
                {
                    std::lock_guard lock(state->mutex);
                    if (!state->closing)
                    {
                        result.succeeded = false;
                        result.errorCode = state->session == session
                            ? UMA_ERROR_CANCELED
                            : UMA_ERROR_DEVICE_DISCONNECTED;
                    }
                }
                if (!result.succeeded && result.errorCode == UMA_SUCCESS)
                    result.errorCode = UMA_ERROR_COMMAND_FAILED;
                if (result.succeeded && result.errorCode != UMA_SUCCESS)
                    result.succeeded = false;
                if (!result.succeeded
                    && (result.errorCode <= UMA_SUCCESS || result.errorCode > UMA_ERROR_RECOGNITION_FAILED))
                    result.errorCode = UMA_ERROR_COMMAND_FAILED;
                auto const completed_units = (std::max)(0, result.completedUnits);
                auto const total_units = (std::max)(
                    completed_units, (std::max)(0, result.totalUnits));
                if (!mark_terminal_once(*state, operation_id)) return;

                auto const message = result.succeeded
                    ? UMA_MSG_GAME_TASK_COMPLETED
                    : UMA_MSG_GAME_TASK_FAILED;
                auto const type = result.succeeded
                    ? "GameTaskCompleted"
                    : "GameTaskFailed";
                emit_runtime(*state, message, operation_id, type, {
                    {"task_id", result.taskId},
                    {"completed_units", completed_units},
                    {"total_units", total_units},
                    {"process_detected", result.processDetected},
                    {"error_code", result.succeeded ? UMA_SUCCESS : result.errorCode},
                    {"message", result.message}
                });
            });
    }
    catch (...)
    {
        std::lock_guard lock(state->mutex);
        if (state->active_operation_id == operation_id)
            state->active_operation_id = 0;
        return {0, UMA_ERROR_PROCESS_START_FAILED};
    }

    return {operation_id, UMA_SUCCESS};
}

UmaAppendTaskResult append_task(UmaHandle handle, char const* task_id, char const* settings_json) noexcept
{
    if (handle == nullptr || handle->state == nullptr || task_id == nullptr || *task_id == '\0')
        return {0, UMA_ERROR_INVALID_ARGUMENT};

    auto const state = handle->state;
    std::lock_guard lock(state->mutex);
    if (state->closing) return {0, UMA_ERROR_INVALID_ARGUMENT};
    if (state->active_operation_id != 0 || state->active_recognition_count != 0)
        return {0, UMA_ERROR_BUSY};

    try
    {
        return state->queue.append(
            task_id,
            settings_json != nullptr && *settings_json != '\0' ? settings_json : "{}");
    }
    catch (...)
    {
        return {0, UMA_ERROR_PROCESS_START_FAILED};
    }
}

std::int32_t set_task_params(
    UmaHandle handle, std::uint64_t task_id, char const* settings_json) noexcept
{
    if (handle == nullptr || handle->state == nullptr || task_id == 0 || settings_json == nullptr)
        return UMA_ERROR_INVALID_ARGUMENT;

    auto const state = handle->state;
    std::lock_guard lock(state->mutex);
    if (state->closing) return UMA_ERROR_INVALID_ARGUMENT;
    if (state->active_operation_id != 0 || state->active_recognition_count != 0)
        return UMA_ERROR_BUSY;

    try
    {
        return state->queue.set_params(task_id, settings_json);
    }
    catch (...)
    {
        return UMA_ERROR_INVALID_ARGUMENT;
    }
}

std::int32_t clear_tasks(UmaHandle handle) noexcept
{
    if (handle == nullptr || handle->state == nullptr)
        return UMA_ERROR_INVALID_ARGUMENT;

    auto const state = handle->state;
    std::lock_guard lock(state->mutex);
    if (state->closing) return UMA_ERROR_INVALID_ARGUMENT;
    if (state->active_operation_id != 0 || state->active_recognition_count != 0)
        return UMA_ERROR_BUSY;

    try
    {
        return state->queue.clear();
    }
    catch (...)
    {
        return UMA_ERROR_INVALID_ARGUMENT;
    }
}

UmaStartResult start_queue(UmaHandle handle)
{
    if (handle == nullptr || handle->state == nullptr)
        return {0, UMA_ERROR_INVALID_ARGUMENT};

    auto const state = handle->state;
    std::uint64_t operation_id = 0;
    std::jthread previous_worker;
    std::shared_ptr<DeviceSession> session;
    std::shared_ptr<Resources::ResourceSnapshot const> resources;
    std::stop_token token;
    std::vector<Runtime::QueuedTask> tasks;

    {
        std::lock_guard lock(state->mutex);
        if (state->closing) return {0, UMA_ERROR_INVALID_ARGUMENT};
        if (state->active_operation_id != 0 || state->active_recognition_count != 0)
            return {0, UMA_ERROR_BUSY};
        if (state->queue.empty()) return {0, UMA_ERROR_INVALID_ARGUMENT};

        session = state->session;
        if (!session) return {0, UMA_ERROR_DEVICE_NOT_READY};

        // Pin both snapshots when accepting the run. Later resource reloads,
        // user-directory changes, or device reconnects cannot redirect it.
        resources = state->resources;
        if (!resources)
        {
            auto const bundle = CoreRuntime::instance().resource_bundle();
            resources = bundle.resources;
            state->resources = resources;
        }
        if (!resources) return {0, UMA_ERROR_DEVICE_NOT_READY};

        operation_id = CoreRuntime::instance().allocate_operation_id();
        auto run_tasks = state->queue.begin_run(operation_id);
        if (!run_tasks) return {0, UMA_ERROR_INVALID_ARGUMENT};
        tasks = std::move(*run_tasks);

        try
        {
            auto const user_directory = CoreRuntime::instance().user_dir();
            for (auto& task : tasks)
            {
                auto settings = nlohmann::json::parse(task.settings_json);
                pin_career_checkpoint_directory(task.task_id, settings, user_directory);
                task.settings_json = settings.dump();
            }
        }
        catch (...)
        {
            state->queue.reject_run_start();
            return {0, UMA_ERROR_INVALID_ARGUMENT};
        }

        previous_worker = std::move(handle->worker);
        state->active_operation_id = operation_id;
        state->cancellation = std::stop_source{};
        token = state->cancellation.get_token();
    }

    join_prior(previous_worker);

    try
    {
        std::lock_guard lock(state->mutex);
        if (state->closing || state->active_operation_id != operation_id)
        {
            state->active_operation_id = 0;
            state->queue.reject_run_start();
            return {0, UMA_ERROR_INVALID_ARGUMENT};
        }

        handle->worker = std::jthread(
            [state, operation_id, session, resources, tasks = std::move(tasks), token](std::stop_token) mutable {
                auto emit = [state, operation_id](
                    std::int32_t message, std::string const& type, nlohmann::json payload) {
                    emit_runtime(*state, message, operation_id, type, std::move(payload));
                };
                auto device_available = [state, session] {
                    std::lock_guard lock(state->mutex);
                    return !state->closing && state->session == session;
                };
                auto execute = [state, operation_id, session, resources, token](
                    Runtime::QueuedTask const& task,
                    Runtime::QueueTaskProgress const& report_progress) {
                    TaskEngine::NativeTaskExecutionContext context(
                        session,
                        resources,
                        &state->ocr,
                        &state->sender,
                        operation_id,
                        token,
                        resources->root(),
                        task.task_id,
                        task.id,
                        report_progress);
                    GameTasks::NativeGameTaskPackageOperations package_operations(session, token);
                    auto const settings = nlohmann::json::parse(task.settings_json);
                    auto task_result = GameTasks::NativeGameTaskRunner::Run(
                        task.task_id,
                        settings,
                        context,
                        resources->root(),
                        &package_operations,
                        resources.get());
                    return Runtime::QueueTaskResult{
                        task_result.succeeded,
                        task_result.errorCode,
                        std::move(task_result.message),
                        task_result.completedUnits,
                        task_result.totalUnits,
                    };
                };
                auto mark_terminal = [state, operation_id] {
                    return mark_terminal_once(*state, operation_id);
                };

                try
                {
                    static_cast<void>(state->queue.run(
                        operation_id,
                        std::move(tasks),
                        token,
                        std::move(device_available),
                        std::move(execute),
                        std::move(emit),
                        std::move(mark_terminal)));
                }
                catch (std::exception const& exception)
                {
                    if (mark_terminal_once(*state, operation_id))
                    {
                        emit_runtime(*state, UMA_MSG_QUEUE_FAILED, operation_id, "QueueFailed", {
                            {"operation_id", operation_id},
                            {"error_code", UMA_ERROR_COMMAND_FAILED},
                            {"message", exception.what()},
                            {"succeeded_tasks", 0},
                            {"failed_tasks", 0},
                            {"skipped_tasks", 0},
                            {"total_tasks", 0},
                        });
                    }
                }
                catch (...)
                {
                    if (mark_terminal_once(*state, operation_id))
                    {
                        emit_runtime(*state, UMA_MSG_QUEUE_FAILED, operation_id, "QueueFailed", {
                            {"operation_id", operation_id},
                            {"error_code", UMA_ERROR_COMMAND_FAILED},
                            {"message", "Unexpected failure in queue runtime."},
                            {"succeeded_tasks", 0},
                            {"failed_tasks", 0},
                            {"skipped_tasks", 0},
                            {"total_tasks", 0},
                        });
                    }
                }
            });
    }
    catch (...)
    {
        std::lock_guard lock(state->mutex);
        if (state->active_operation_id == operation_id)
            state->active_operation_id = 0;
        state->queue.reject_run_start();
        return {0, UMA_ERROR_PROCESS_START_FAILED};
    }

    return {operation_id, UMA_SUCCESS};
}
std::int32_t get_run_snapshot_json(
    UmaHandle handle, char* destination, std::uint64_t capacity,
    std::uint64_t* required_length) noexcept
{
    if (handle == nullptr || handle->state == nullptr || required_length == nullptr)
        return UMA_ERROR_INVALID_ARGUMENT;
    *required_length = 0;
    if (destination == nullptr && capacity != 0)
        return UMA_ERROR_INVALID_ARGUMENT;

    try
    {
        nlohmann::json json;
        {
            std::lock_guard lock(handle->state->mutex);
            json = handle->state->queue.snapshot_json();
        }
        auto const dumped = json.dump();
        auto const needed = dumped.size() + 1;
        *required_length = needed;
        if (destination == nullptr || capacity < needed)
            return UMA_ERROR_BUFFER_TOO_SMALL;
        std::memcpy(destination, dumped.c_str(), needed);
        return UMA_SUCCESS;
    }
    catch (...)
    {
        return UMA_ERROR_INVALID_ARGUMENT;
    }
}

std::int32_t get_catalog_json(
    UmaHandle handle, char const* catalog_type, char* destination,
    std::uint64_t capacity, std::uint64_t* required_length) noexcept
{
    if (handle == nullptr || handle->state == nullptr || required_length == nullptr)
        return UMA_ERROR_INVALID_ARGUMENT;
    *required_length = 0;
    if (destination == nullptr && capacity != 0)
        return UMA_ERROR_INVALID_ARGUMENT;

    try
    {
        std::filesystem::path root_dir;
        {
            std::lock_guard lock(handle->state->mutex);
            if (handle->state->resources)
                root_dir = handle->state->resources->root();
        }
        if (root_dir.empty())
        {
            auto const bundle = CoreRuntime::instance().resource_bundle();
            if (bundle.resources)
                root_dir = bundle.resources->root();
        }
        if (root_dir.empty()) return UMA_ERROR_DEVICE_NOT_READY;

        std::string_view type = "all";
        if (catalog_type != nullptr && *catalog_type != 0)
            type = catalog_type;
        auto strategies = nlohmann::json::array();
        for (auto const& option : CareerDomain::UraStrategyRegistry::AvailableStrategies())
        {
            strategies.push_back({
                {"id", option.value},
                {"value", option.value},
                {"label", option.label},
                {"name", option.label},
                {"scenario", "ura"},
            });
        }
        auto const json = GameData::BuildCatalogJson(root_dir, type, std::move(strategies));
        auto const dumped = json.dump();
        auto const needed = dumped.size() + 1;
        *required_length = needed;
        if (destination == nullptr || capacity < needed)
            return UMA_ERROR_BUFFER_TOO_SMALL;
        std::memcpy(destination, dumped.c_str(), needed);
        return UMA_SUCCESS;
    }
    catch (std::invalid_argument const&)
    {
        return UMA_ERROR_INVALID_ARGUMENT;
    }
    catch (...)
    {
        return UMA_ERROR_COMMAND_FAILED;
    }
}
}
