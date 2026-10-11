#include "Runtime/TaskQueueRuntime.hpp"

#include <catch2/catch_test_macros.hpp>

#include <algorithm>
#include <stop_token>
#include <string>
#include <vector>

using UmaAssistant::Runtime::QueueTaskResult;
using UmaAssistant::Runtime::TaskQueueRuntime;

namespace {

struct CapturedEvent
{
    std::int32_t message;
    std::string type;
    nlohmann::json payload;
};

}

TEST_CASE("Runtime queue continues after ordinary task failures and preserves instance IDs")
{
    TaskQueueRuntime queue;
    auto const first = queue.append("mail-collection", "{}");
    auto const second = queue.append("mail-collection", "{}");
    REQUIRE(first.error_code == UMA_SUCCESS);
    REQUIRE(second.error_code == UMA_SUCCESS);
    REQUIRE(first.task_id != second.task_id);

    auto tasks = queue.begin_run(81);
    REQUIRE(tasks.has_value());
    REQUIRE(queue.append("mission-collection", "{}").error_code == UMA_ERROR_BUSY);
    REQUIRE(queue.set_params(first.task_id, "{}") == UMA_ERROR_BUSY);
    REQUIRE(queue.clear() == UMA_ERROR_BUSY);

    std::vector<CapturedEvent> events;
    int terminal_mark_count = 0;
    bool live_progress_snapshot_matches = false;
    bool reentrant_snapshot_query_succeeded = false;
    auto result = queue.run(
        81,
        std::move(*tasks),
        std::stop_token{},
        [] { return true; },
        [&](UmaAssistant::Runtime::QueuedTask const& task,
            UmaAssistant::Runtime::QueueTaskProgress const& report_progress) {
            if (task.id == second.task_id)
            {
                report_progress(1, 3);
                report_progress(0, 0); // Context events without progress must not erase it.
                auto const live = queue.snapshot_json();
                live_progress_snapshot_matches =
                    live.at("current_task_id") == second.task_id
                    && live.at("current_completed_units") == 1
                    && live.at("current_total_units") == 3
                    && live.at("failed_count") == 1
                    && live.at("succeeded_count") == 0
                    && live.at("tasks").at(1).at("completed_units") == 1;
            }
            if (task.id == first.task_id)
                return QueueTaskResult{false, UMA_ERROR_RECOGNITION_FAILED, "missed", 0, 2};
            return QueueTaskResult{true, UMA_SUCCESS, "done", 0, 0};
        },
        [&](std::int32_t message, std::string const& type, nlohmann::json payload) {
            if (type == "TaskStarted")
            {
                auto const reentrant = queue.snapshot_json();
                reentrant_snapshot_query_succeeded = reentrant.at("state") == "running";
            }
            events.push_back({message, type, std::move(payload)});
        },
        [&] { ++terminal_mark_count; return true; });

    REQUIRE_FALSE(result.aborted);
    REQUIRE(result.succeeded_count == 1);
    REQUIRE(result.failed_count == 1);
    REQUIRE(result.skipped_count == 0);
    REQUIRE(terminal_mark_count == 1);
    REQUIRE(live_progress_snapshot_matches);
    REQUIRE(reentrant_snapshot_query_succeeded);
    REQUIRE(events.size() == 5);
    REQUIRE(events[0].type == "TaskStarted");
    REQUIRE(events[0].payload.at("task_instance_id") == first.task_id);
    REQUIRE(events[1].type == "TaskFailed");
    REQUIRE(events[2].type == "TaskStarted");
    REQUIRE(events[2].payload.at("task_instance_id") == second.task_id);
    REQUIRE(events[3].type == "TaskCompleted");
    REQUIRE(events[4].type == "QueueCompleted");

    auto const snapshot = queue.snapshot_json();
    REQUIRE(snapshot.at("state") == "completed");
    REQUIRE(snapshot.at("tasks").at(0).at("id") == first.task_id);
    REQUIRE(snapshot.at("tasks").at(0).at("name") == "mail-collection");
    REQUIRE(snapshot.at("tasks").at(0).at("status") == "failed");
    REQUIRE(snapshot.at("tasks").at(1).at("status") == "succeeded");
    REQUIRE(snapshot.at("tasks").at(1).at("completed_units") == 1);
    REQUIRE(snapshot.at("tasks").at(1).at("total_units") == 3);
    REQUIRE(snapshot.at("current_completed_units") == 1);
    REQUIRE(snapshot.at("current_total_units") == 3);
}

TEST_CASE("Runtime queue cancellation stops work and marks remaining tasks skipped")
{
    TaskQueueRuntime queue;
    auto const first = queue.append("mail-collection", "{}");
    auto const second = queue.append("mission-collection", "{}");
    auto const third = queue.append("shop-purchase", "{}");
    REQUIRE(first.error_code == UMA_SUCCESS);
    REQUIRE(second.error_code == UMA_SUCCESS);
    REQUIRE(third.error_code == UMA_SUCCESS);

    auto tasks = queue.begin_run(82);
    REQUIRE(tasks.has_value());
    std::stop_source cancellation;
    std::vector<CapturedEvent> events;
    int execution_count = 0;
    int terminal_mark_count = 0;
    auto const result = queue.run(
        82,
        std::move(*tasks),
        cancellation.get_token(),
        [] { return true; },
        [&](UmaAssistant::Runtime::QueuedTask const&,
            UmaAssistant::Runtime::QueueTaskProgress const&) {
            ++execution_count;
            cancellation.request_stop();
            return QueueTaskResult{false, UMA_ERROR_CANCELED, "stopped", 0, 0};
        },
        [&](std::int32_t message, std::string const& type, nlohmann::json payload) {
            events.push_back({message, type, std::move(payload)});
        },
        [&] { ++terminal_mark_count; return true; });

    REQUIRE(result.aborted);
    REQUIRE(result.error_code == UMA_ERROR_CANCELED);
    REQUIRE(result.failed_count == 1);
    REQUIRE(result.skipped_count == 2);
    REQUIRE(execution_count == 1);
    REQUIRE(terminal_mark_count == 1);
    REQUIRE(std::count_if(events.begin(), events.end(), [](CapturedEvent const& event) {
        return event.type == "QueueCompleted" || event.type == "QueueFailed";
    }) == 1);
    auto const snapshot = queue.snapshot_json();
    REQUIRE(snapshot.at("state") == "canceled");
    REQUIRE(snapshot.at("tasks").at(0).at("status") == "failed");
    REQUIRE(snapshot.at("tasks").at(1).at("status") == "skipped");
    REQUIRE(snapshot.at("tasks").at(2).at("status") == "skipped");
}

TEST_CASE("Runtime queue marks all work skipped when the pinned device session disconnects")
{
    TaskQueueRuntime queue;
    REQUIRE(queue.append("mail-collection", "{}").error_code == UMA_SUCCESS);
    REQUIRE(queue.append("mission-collection", "{}").error_code == UMA_SUCCESS);
    auto tasks = queue.begin_run(83);
    REQUIRE(tasks.has_value());

    std::vector<CapturedEvent> events;
    int executions = 0;
    auto const result = queue.run(
        83,
        std::move(*tasks),
        std::stop_token{},
        [] { return false; },
        [&](UmaAssistant::Runtime::QueuedTask const&,
            UmaAssistant::Runtime::QueueTaskProgress const&) {
            ++executions;
            return QueueTaskResult{true, UMA_SUCCESS, {}, 1, 1};
        },
        [&](std::int32_t message, std::string const& type, nlohmann::json payload) {
            events.push_back({message, type, std::move(payload)});
        },
        [] { return true; });

    REQUIRE(result.aborted);
    REQUIRE(result.error_code == UMA_ERROR_DEVICE_DISCONNECTED);
    REQUIRE(result.skipped_count == 2);
    REQUIRE(executions == 0);
    REQUIRE(events.size() == 1);
    REQUIRE(events[0].type == "QueueFailed");
    auto const snapshot = queue.snapshot_json();
    REQUIRE(snapshot.at("tasks").at(0).at("status") == "skipped");
    REQUIRE(snapshot.at("tasks").at(1).at("status") == "skipped");
}

TEST_CASE("Runtime queue stops on explicit ADB offline, unauthorized, and unavailable failures")
{
    constexpr std::int32_t device_loss_errors[] = {
        UMA_ERROR_DEVICE_UNAUTHORIZED,
        UMA_ERROR_DEVICE_OFFLINE,
        UMA_ERROR_DEVICE_UNAVAILABLE,
        UMA_ERROR_DEVICE_DISCONNECTED,
    };

    for (auto const error_code : device_loss_errors)
    {
        TaskQueueRuntime queue;
        REQUIRE(queue.append("first", "{}").error_code == UMA_SUCCESS);
        REQUIRE(queue.append("second", "{}").error_code == UMA_SUCCESS);
        auto tasks = queue.begin_run(85);
        REQUIRE(tasks.has_value());
        int execution_count = 0;

        auto const result = queue.run(
            85,
            std::move(*tasks),
            std::stop_token{},
            [] { return true; },
            [error_code, &execution_count](UmaAssistant::Runtime::QueuedTask const&,
                UmaAssistant::Runtime::QueueTaskProgress const&) {
                ++execution_count;
                return QueueTaskResult{false, error_code, "device lost", 0, 0};
            },
            [](std::int32_t, std::string const&, nlohmann::json) {},
            [] { return true; });

        REQUIRE(result.aborted);
        REQUIRE(result.error_code == error_code);
        REQUIRE(result.failed_count == 1);
        REQUIRE(result.skipped_count == 1);
        REQUIRE(execution_count == 1);
        auto const snapshot = queue.snapshot_json();
        REQUIRE(snapshot.at("state") == "failed");
        REQUIRE(snapshot.at("tasks").at(1).at("status") == "skipped");
    }
}

TEST_CASE("Runtime queue does not classify DeviceNotReady as an implicit disconnect")
{
    TaskQueueRuntime queue;
    REQUIRE(queue.append("first", "{}").error_code == UMA_SUCCESS);
    REQUIRE(queue.append("second", "{}").error_code == UMA_SUCCESS);
    auto tasks = queue.begin_run(86);
    REQUIRE(tasks.has_value());
    int execution_count = 0;

    auto const result = queue.run(
        86,
        std::move(*tasks),
        std::stop_token{},
        [] { return true; },
        [&execution_count](UmaAssistant::Runtime::QueuedTask const&,
            UmaAssistant::Runtime::QueueTaskProgress const&) {
            ++execution_count;
            return execution_count == 1
                ? QueueTaskResult{false, UMA_ERROR_DEVICE_NOT_READY, "not ready", 0, 0}
                : QueueTaskResult{true, UMA_SUCCESS, {}, 1, 1};
        },
        [](std::int32_t, std::string const&, nlohmann::json) {},
        [] { return true; });

    REQUIRE_FALSE(result.aborted);
    REQUIRE(result.failed_count == 1);
    REQUIRE(result.succeeded_count == 1);
    REQUIRE(execution_count == 2);
}

TEST_CASE("Runtime queue rejects malformed task settings synchronously")
{
    TaskQueueRuntime queue;
    REQUIRE(queue.append("mail-collection", "{").error_code == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(queue.append("mail-collection", "[]").error_code == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(queue.append("", "{}").error_code == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(queue.empty());
}

TEST_CASE("Runtime queue task IDs are monotonic across clear and reject stale parameter updates")
{
    TaskQueueRuntime queue;
    auto const first = queue.append("mail-collection", "{}");
    REQUIRE(first.error_code == UMA_SUCCESS);
    REQUIRE(queue.clear() == UMA_SUCCESS);

    auto const second = queue.append("mission-collection", "{}");
    REQUIRE(second.error_code == UMA_SUCCESS);
    REQUIRE(second.task_id > first.task_id);
    REQUIRE(queue.set_params(first.task_id, R"({"stale":true})") == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(queue.set_params(second.task_id, R"({"current":true})") == UMA_SUCCESS);

    auto tasks = queue.begin_run(84);
    REQUIRE(tasks.has_value());
    REQUIRE(tasks->size() == 1);
    REQUIRE(tasks->front().id == second.task_id);
    REQUIRE(tasks->front().settings_json == R"({"current":true})");
}
