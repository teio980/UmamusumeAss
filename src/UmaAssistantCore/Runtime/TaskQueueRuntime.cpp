#include "TaskQueueRuntime.hpp"

#include <algorithm>
#include <exception>
#include <limits>
#include <stdexcept>
#include <utility>

namespace UmaAssistant::Runtime {
namespace {

void emit_safely(
    QueueEventSink const& emit,
    std::int32_t message,
    char const* type,
    nlohmann::json payload) noexcept
{
    if (!emit) return;
    try
    {
        emit(message, type, std::move(payload));
    }
    catch (...)
    {
        // Event delivery is observational and must not strand the queue.
    }
}

[[nodiscard]] bool device_is_available(std::function<bool()> const& probe) noexcept
{
    if (!probe) return false;
    try
    {
        return probe();
    }
    catch (...)
    {
        return false;
    }
}

[[nodiscard]] bool is_device_loss_error(std::int32_t error_code) noexcept
{
    return error_code == UMA_ERROR_DEVICE_UNAUTHORIZED
        || error_code == UMA_ERROR_DEVICE_OFFLINE
        || error_code == UMA_ERROR_DEVICE_UNAVAILABLE
        || error_code == UMA_ERROR_DEVICE_DISCONNECTED;
}

}

UmaAppendTaskResult TaskQueueRuntime::append(std::string task_id, std::string settings_json)
{
    if (task_id.empty()) return {0, UMA_ERROR_INVALID_ARGUMENT};
    if (settings_json.empty()) settings_json = "{}";

    try
    {
        // The task ID is also exposed in JSON events and snapshots, so reject
        // malformed UTF-8 before accepting it into the queue.
        (void)nlohmann::json(task_id).dump();
        auto const settings = nlohmann::json::parse(settings_json);
        if (!settings.is_object()) return {0, UMA_ERROR_INVALID_ARGUMENT};
    }
    catch (...)
    {
        return {0, UMA_ERROR_INVALID_ARGUMENT};
    }

    std::lock_guard lock(mutex_);
    if (running_) return {0, UMA_ERROR_BUSY};
    if (next_task_id_ == 0) return {0, UMA_ERROR_PROCESS_START_FAILED};
    try
    {
        QueuedTask task;
        task.id = next_task_id_;
        next_task_id_ = next_task_id_ == (std::numeric_limits<std::uint64_t>::max)()
            ? 0 : next_task_id_ + 1;
        task.task_id = std::move(task_id);
        task.settings_json = std::move(settings_json);
        tasks_.push_back(std::move(task));
        return {tasks_.back().id, UMA_SUCCESS};
    }
    catch (...)
    {
        return {0, UMA_ERROR_PROCESS_START_FAILED};
    }
}

std::int32_t TaskQueueRuntime::set_params(
    std::uint64_t task_id, std::string settings_json)
{
    if (task_id == 0) return UMA_ERROR_INVALID_ARGUMENT;
    try
    {
        if (settings_json.empty()) return UMA_ERROR_INVALID_ARGUMENT;
        auto const settings = nlohmann::json::parse(settings_json);
        if (!settings.is_object()) return UMA_ERROR_INVALID_ARGUMENT;
    }
    catch (...)
    {
        return UMA_ERROR_INVALID_ARGUMENT;
    }

    std::lock_guard lock(mutex_);
    if (running_) return UMA_ERROR_BUSY;
    for (auto& task : tasks_)
    {
        if (task.id == task_id)
        {
            task.settings_json = std::move(settings_json);
            return UMA_SUCCESS;
        }
    }
    return UMA_ERROR_INVALID_ARGUMENT;
}

std::int32_t TaskQueueRuntime::clear()
{
    std::lock_guard lock(mutex_);
    if (running_) return UMA_ERROR_BUSY;
    tasks_.clear();
    snapshot_ = {};
    return UMA_SUCCESS;
}

bool TaskQueueRuntime::empty() const noexcept
{
    std::lock_guard lock(mutex_);
    return tasks_.empty();
}

std::optional<std::vector<QueuedTask>> TaskQueueRuntime::begin_run(
    std::uint64_t operation_id)
{
    std::lock_guard lock(mutex_);
    if (operation_id == 0 || tasks_.empty() || running_) return std::nullopt;

    QueueSnapshot next;
    next.operation_id = operation_id;
    next.state = "running";
    auto run_tasks = tasks_;
    next.tasks = run_tasks;
    for (auto& task : next.tasks)
    {
        task.status = "pending";
        task.error_code = UMA_SUCCESS;
        task.message.clear();
        task.completed_units = 0;
        task.total_units = 0;
    }
    snapshot_ = std::move(next);
    running_ = true;
    return run_tasks;
}

void TaskQueueRuntime::reject_run_start() noexcept
{
    try
    {
        std::lock_guard lock(mutex_);
        running_ = false;
        snapshot_ = {};
    }
    catch (...)
    {
    }
}

void TaskQueueRuntime::update_progress(
    std::uint64_t task_id,
    std::int32_t completed_units,
    std::int32_t total_units) noexcept
{
    try
    {
        completed_units = (std::max)(0, completed_units);
        total_units = (std::max)(completed_units, total_units);
        std::lock_guard lock(mutex_);
        if (!running_ || snapshot_.current_task_id != task_id) return;
        // Generic task events often carry 0/0 to mean "no progress data".
        // Once the active task has reported real units, those events must not
        // erase the latest known progress.
        if (completed_units == 0 && total_units == 0
            && (snapshot_.current_completed_units > 0 || snapshot_.current_total_units > 0))
            return;
        snapshot_.current_completed_units = completed_units;
        snapshot_.current_total_units = total_units;
        for (auto& task : snapshot_.tasks)
        {
            if (task.id != task_id) continue;
            task.completed_units = completed_units;
            task.total_units = total_units;
            break;
        }
    }
    catch (...)
    {
    }
}

QueueSnapshot TaskQueueRuntime::snapshot() const
{
    std::lock_guard lock(mutex_);
    return snapshot_;
}

nlohmann::json TaskQueueRuntime::snapshot_json() const
{
    auto const current = snapshot();
    auto tasks_json = nlohmann::json::array();
    for (auto const& task : current.tasks)
    {
        tasks_json.push_back({
            {"id", task.id},
            {"name", task.task_id},
            {"status", task.status},
            {"error_code", task.error_code},
            {"message", task.message},
            {"completed_units", task.completed_units},
            {"total_units", task.total_units},
        });
    }

    return {
        {"operation_id", current.operation_id},
        {"state", current.state},
        {"current_task_id", current.current_task_id},
        {"current_task_name", current.current_task_name},
        {"current_completed_units", current.current_completed_units},
        {"current_total_units", current.current_total_units},
        {"succeeded_count", current.succeeded_count},
        {"failed_count", current.failed_count},
        {"skipped_count", current.skipped_count},
        {"tasks", std::move(tasks_json)},
    };
}

void TaskQueueRuntime::publish_task_result(
    std::size_t index, std::string status, QueueTaskResult const& result)
{
    std::lock_guard lock(mutex_);
    if (index >= snapshot_.tasks.size()) return;
    auto& task = snapshot_.tasks[index];
    if (task.status == "succeeded" && snapshot_.succeeded_count > 0)
        --snapshot_.succeeded_count;
    else if (task.status == "failed" && snapshot_.failed_count > 0)
        --snapshot_.failed_count;
    else if (task.status == "skipped" && snapshot_.skipped_count > 0)
        --snapshot_.skipped_count;
    task.status = std::move(status);
    if (task.status == "succeeded") ++snapshot_.succeeded_count;
    else if (task.status == "failed") ++snapshot_.failed_count;
    else if (task.status == "skipped") ++snapshot_.skipped_count;
    task.error_code = result.error_code;
    task.message = result.message;
    if (result.completed_units != 0 || result.total_units != 0
        || (task.completed_units == 0 && task.total_units == 0))
    {
        task.completed_units = (std::max)(0, result.completed_units);
        task.total_units = (std::max)(task.completed_units, result.total_units);
    }
    snapshot_.current_completed_units = task.completed_units;
    snapshot_.current_total_units = task.total_units;
}

void TaskQueueRuntime::publish_skipped(
    std::size_t first_index,
    std::vector<QueuedTask> const& tasks,
    std::int32_t error_code,
    std::string const& message)
{
    std::lock_guard lock(mutex_);
    for (std::size_t index = first_index; index < tasks.size(); ++index)
    {
        if (index >= snapshot_.tasks.size()) continue;
        auto& task = snapshot_.tasks[index];
        if (task.status == "succeeded" || task.status == "failed" || task.status == "skipped")
            continue;
        task.status = "skipped";
        task.error_code = error_code;
        task.message = message;
        ++snapshot_.skipped_count;
    }
}

void TaskQueueRuntime::publish_final(QueueRunResult const& result)
{
    std::lock_guard lock(mutex_);
    running_ = false;
    snapshot_.succeeded_count = result.succeeded_count;
    snapshot_.failed_count = result.failed_count;
    snapshot_.skipped_count = result.skipped_count;
    snapshot_.state = result.aborted
        ? (result.error_code == UMA_ERROR_CANCELED ? "canceled" : "failed")
        : "completed";
}

QueueRunResult TaskQueueRuntime::run(
    std::uint64_t operation_id,
    std::vector<QueuedTask> tasks,
    std::stop_token cancellation,
    std::function<bool()> device_available,
    QueueTaskExecutor execute,
    QueueEventSink emit,
    QueueTerminalGuard mark_terminal)
{
    QueueRunResult result;
    result.total_count = static_cast<std::int32_t>(tasks.size());

    auto abort_remaining = [&](std::size_t first_index, std::int32_t error_code, std::string message) {
        result.aborted = true;
        result.error_code = error_code;
        result.message = std::move(message);
        publish_skipped(first_index, tasks, result.error_code, result.message);
        auto const current = snapshot();
        result.skipped_count = static_cast<std::int32_t>(std::count_if(
            current.tasks.begin(), current.tasks.end(), [](QueuedTask const& task) {
                return task.status == "skipped";
            }));
    };

    for (std::size_t index = 0; index < tasks.size(); ++index)
    {
        if (cancellation.stop_requested())
        {
            abort_remaining(index, UMA_ERROR_CANCELED, "Queue execution was canceled.");
            break;
        }
        if (!device_is_available(device_available))
        {
            abort_remaining(index, UMA_ERROR_DEVICE_DISCONNECTED,
                "Device disconnected during queue execution.");
            break;
        }

        auto const& task = tasks[index];
        {
            std::lock_guard lock(mutex_);
            snapshot_.current_task_id = task.id;
            snapshot_.current_task_name = task.task_id;
            snapshot_.current_completed_units = 0;
            snapshot_.current_total_units = 0;
            if (index < snapshot_.tasks.size()) snapshot_.tasks[index].status = "running";
        }

        emit_safely(emit, UMA_MSG_TASK_STARTED, "TaskStarted", {
            {"operation_id", operation_id},
            {"task_id", task.id},
            {"task_instance_id", task.id},
            {"name", task.task_id},
            {"index", index},
            {"total_tasks", tasks.size()},
        });

        QueueTaskResult task_result;
        if (cancellation.stop_requested())
        {
            task_result.error_code = UMA_ERROR_CANCELED;
            task_result.message = "Queue execution was canceled.";
        }
        else if (!device_is_available(device_available))
        {
            task_result.error_code = UMA_ERROR_DEVICE_DISCONNECTED;
            task_result.message = "Device disconnected during queue execution.";
        }
        else
        {
            try
            {
                if (!execute)
                    throw std::runtime_error("Queue task executor is unavailable.");
                QueueTaskProgress report_progress = [this, task_id = task.id](
                    std::int32_t completed_units, std::int32_t total_units) {
                    update_progress(task_id, completed_units, total_units);
                };
                task_result = execute(task, report_progress);
            }
            catch (std::exception const& exception)
            {
                task_result.error_code = cancellation.stop_requested()
                    ? UMA_ERROR_CANCELED : UMA_ERROR_COMMAND_FAILED;
                task_result.message = exception.what();
            }
            catch (...)
            {
                task_result.error_code = cancellation.stop_requested()
                    ? UMA_ERROR_CANCELED : UMA_ERROR_COMMAND_FAILED;
                task_result.message = "Unexpected failure in queued task.";
            }
        }

        if (cancellation.stop_requested())
        {
            task_result.succeeded = false;
            task_result.error_code = UMA_ERROR_CANCELED;
            task_result.message = "Queue execution was canceled.";
        }
        else if (!device_is_available(device_available))
        {
            task_result.succeeded = false;
            task_result.error_code = UMA_ERROR_DEVICE_DISCONNECTED;
            task_result.message = "Device disconnected during queue execution.";
        }

        if (task_result.succeeded)
        {
            if (task_result.error_code != UMA_SUCCESS)
            {
                task_result.succeeded = false;
                if (task_result.error_code <= UMA_SUCCESS
                    || task_result.error_code > UMA_ERROR_RECOGNITION_FAILED)
                    task_result.error_code = UMA_ERROR_COMMAND_FAILED;
            }
        }
        else if (task_result.error_code <= UMA_SUCCESS
            || task_result.error_code > UMA_ERROR_RECOGNITION_FAILED)
        {
            task_result.error_code = UMA_ERROR_COMMAND_FAILED;
        }

        if (task_result.succeeded)
        {
            ++result.succeeded_count;
            publish_task_result(index, "succeeded", task_result);
            emit_safely(emit, UMA_MSG_TASK_COMPLETED, "TaskCompleted", {
                {"operation_id", operation_id},
                {"task_id", task.id},
                {"task_instance_id", task.id},
                {"name", task.task_id},
                {"completed_units", task_result.completed_units},
                {"total_units", task_result.total_units},
                {"message", task_result.message},
            });
            continue;
        }

        ++result.failed_count;
        publish_task_result(index, "failed", task_result);
        emit_safely(emit, UMA_MSG_TASK_FAILED, "TaskFailed", {
            {"operation_id", operation_id},
            {"task_id", task.id},
            {"task_instance_id", task.id},
            {"name", task.task_id},
            {"error_code", task_result.error_code},
            {"message", task_result.message},
        });

        if (task_result.error_code == UMA_ERROR_CANCELED
            || is_device_loss_error(task_result.error_code))
        {
            abort_remaining(index + 1, task_result.error_code, task_result.message);
            break;
        }
    }

    publish_final(result);
    bool terminal_allowed = true;
    if (mark_terminal)
    {
        try
        {
            terminal_allowed = mark_terminal();
        }
        catch (...)
        {
            terminal_allowed = false;
        }
    }

    if (terminal_allowed && result.aborted)
    {
        emit_safely(emit, UMA_MSG_QUEUE_FAILED, "QueueFailed", {
            {"operation_id", operation_id},
            {"error_code", result.error_code},
            {"message", result.message},
            {"succeeded_tasks", result.succeeded_count},
            {"failed_tasks", result.failed_count},
            {"skipped_tasks", result.skipped_count},
            {"total_tasks", result.total_count},
        });
    }
    else if (terminal_allowed)
    {
        emit_safely(emit, UMA_MSG_QUEUE_COMPLETED, "QueueCompleted", {
            {"operation_id", operation_id},
            {"succeeded_tasks", result.succeeded_count},
            {"failed_tasks", result.failed_count},
            {"skipped_tasks", result.skipped_count},
            {"total_tasks", result.total_count},
        });
    }
    return result;
}

}
