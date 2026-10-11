#pragma once

#include "UmaAssistant/UmaCaller.h"

#include <cstdint>
#include <functional>
#include <mutex>
#include <optional>
#include <stop_token>
#include <string>
#include <vector>

#include <nlohmann/json.hpp>

namespace UmaAssistant::Runtime {

struct QueuedTask
{
    std::uint64_t id{0};
    std::string task_id;
    std::string settings_json{"{}"};
    std::string status{"pending"};
    std::int32_t error_code{0};
    std::string message;
    std::int32_t completed_units{0};
    std::int32_t total_units{0};
};

struct QueueSnapshot
{
    std::uint64_t operation_id{0};
    std::string state{"idle"};
    std::uint64_t current_task_id{0};
    std::string current_task_name;
    std::int32_t current_completed_units{0};
    std::int32_t current_total_units{0};
    std::int32_t succeeded_count{0};
    std::int32_t failed_count{0};
    std::int32_t skipped_count{0};
    std::vector<QueuedTask> tasks;
};

struct QueueTaskResult
{
    bool succeeded{false};
    std::int32_t error_code{UMA_ERROR_COMMAND_FAILED};
    std::string message;
    std::int32_t completed_units{0};
    std::int32_t total_units{0};
};

struct QueueRunResult
{
    bool aborted{false};
    std::int32_t error_code{UMA_SUCCESS};
    std::string message;
    std::int32_t succeeded_count{0};
    std::int32_t failed_count{0};
    std::int32_t skipped_count{0};
    std::int32_t total_count{0};
};

using QueueTaskProgress = std::function<void(std::int32_t, std::int32_t)>;
using QueueTaskExecutor = std::function<QueueTaskResult(
    QueuedTask const&, QueueTaskProgress const&)>;
using QueueEventSink = std::function<void(
    std::int32_t message, std::string const& type, nlohmann::json payload)>;
using QueueTerminalGuard = std::function<bool()>;

/// Owns the mutable queue and its run snapshot. Execution is deliberately
/// generic: Runtime schedules immutable task records and receives the concrete
/// game-task executor from the composition layer.
class TaskQueueRuntime final
{
public:
    [[nodiscard]] UmaAppendTaskResult append(std::string task_id, std::string settings_json);
    [[nodiscard]] std::int32_t set_params(std::uint64_t task_id, std::string settings_json);
    [[nodiscard]] std::int32_t clear();
    [[nodiscard]] bool empty() const noexcept;
    [[nodiscard]] std::optional<std::vector<QueuedTask>> begin_run(std::uint64_t operation_id);
    void reject_run_start() noexcept;
    void update_progress(
        std::uint64_t task_id,
        std::int32_t completed_units,
        std::int32_t total_units) noexcept;
    [[nodiscard]] QueueSnapshot snapshot() const;
    [[nodiscard]] nlohmann::json snapshot_json() const;

    [[nodiscard]] QueueRunResult run(
        std::uint64_t operation_id,
        std::vector<QueuedTask> tasks,
        std::stop_token cancellation,
        std::function<bool()> device_available,
        QueueTaskExecutor execute,
        QueueEventSink emit,
        QueueTerminalGuard mark_terminal);

private:
    void publish_task_result(
        std::size_t index, std::string status, QueueTaskResult const& result);
    void publish_skipped(
        std::size_t first_index, std::vector<QueuedTask> const& tasks,
        std::int32_t error_code, std::string const& message);
    void publish_final(QueueRunResult const& result);

    mutable std::mutex mutex_;
    std::vector<QueuedTask> tasks_;
    std::uint64_t next_task_id_{1};
    bool running_{false};
    QueueSnapshot snapshot_;
};

}
