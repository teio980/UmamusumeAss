#include "CallbackSender.hpp"

#include <algorithm>
#include <cstddef>
#include <string_view>
#include <utility>

#include <nlohmann/json.hpp>

namespace UmaAssistant::Api {
namespace {

constexpr std::size_t kMaximumCallbackBytes = 60 * 1024;

[[nodiscard]] std::string valid_utf8(std::string_view text, std::size_t maximum_bytes = 4096)
{
    std::string normalized;
    normalized.reserve((std::min)(text.size(), maximum_bytes));
    bool truncated = false;
    for (std::size_t index = 0; index < text.size();)
    {
        auto const first = static_cast<unsigned char>(text[index]);
        std::size_t length = 0;
        if (first <= 0x7f) length = 1;
        else if (first >= 0xc2 && first <= 0xdf && index + 1 < text.size()
                 && (static_cast<unsigned char>(text[index + 1]) & 0xc0) == 0x80)
            length = 2;
        else if (first >= 0xe0 && first <= 0xef && index + 2 < text.size())
        {
            auto const second = static_cast<unsigned char>(text[index + 1]);
            auto const third = static_cast<unsigned char>(text[index + 2]);
            bool const second_valid = (second & 0xc0) == 0x80
                && (first != 0xe0 || second >= 0xa0)
                && (first != 0xed || second <= 0x9f);
            if (second_valid && (third & 0xc0) == 0x80) length = 3;
        }
        else if (first >= 0xf0 && first <= 0xf4 && index + 3 < text.size())
        {
            auto const second = static_cast<unsigned char>(text[index + 1]);
            auto const third = static_cast<unsigned char>(text[index + 2]);
            auto const fourth = static_cast<unsigned char>(text[index + 3]);
            bool const second_valid = (second & 0xc0) == 0x80
                && (first != 0xf0 || second >= 0x90)
                && (first != 0xf4 || second <= 0x8f);
            if (second_valid && (third & 0xc0) == 0x80 && (fourth & 0xc0) == 0x80)
                length = 4;
        }

        auto const* bytes = length == 0 ? "\xef\xbf\xbd" : text.data() + index;
        auto const count = length == 0 ? 3 : length;
        if (normalized.size() + count > maximum_bytes)
        {
            truncated = true;
            break;
        }
        normalized.append(bytes, count);
        index += length == 0 ? 1 : length;
    }
    if (truncated && normalized.size() + 3 <= maximum_bytes)
        normalized += "...";
    return normalized;
}

void sanitize_json_strings(nlohmann::json& value)
{
    if (value.is_string())
    {
        value = valid_utf8(value.get_ref<std::string const&>());
        return;
    }
    if (value.is_array())
    {
        for (auto& item : value) sanitize_json_strings(item);
        return;
    }
    if (value.is_object())
    {
        for (auto& [_, item] : value.items()) sanitize_json_strings(item);
    }
}

[[nodiscard]] std::string serialize(
    bool runtime,
    std::uint64_t operation_id,
    std::uint64_t sequence,
    std::string const& type,
    nlohmann::json payload)
{
    sanitize_json_strings(payload);
    nlohmann::json envelope = {
        {"version", runtime ? 2 : 1},
        {"operation_id", operation_id},
        {"type", type},
        {"payload", std::move(payload)},
    };
    if (runtime) envelope["sequence"] = sequence;
    return envelope.dump();
}

[[nodiscard]] std::string bounded_connection(
    std::uint64_t operation_id,
    std::int32_t& message,
    std::string const& type,
    nlohmann::json const& payload)
{
    auto details = serialize(false, operation_id, 0, type, payload);
    if (details.size() <= kMaximumCallbackBytes) return details;

    if (type == "ConnectionSucceeded")
    {
        message = UMA_MSG_CONNECTION_SUCCEEDED;
        return serialize(false, operation_id, 0, type, nlohmann::json{
            {"serial", valid_utf8(payload.value("serial", std::string{}), 256)},
            {"android_id", valid_utf8(payload.value("android_id", std::string{}), 256)},
            {"android_version", valid_utf8(payload.value("android_version", std::string{}), 256)},
            {"width", payload.value("width", 1)},
            {"height", payload.value("height", 1)},
            {"physical_width", payload.value("physical_width", 1)},
            {"physical_height", payload.value("physical_height", 1)},
            {"size_source", payload.value("size_source", std::string{"physical"})},
        });
    }
    if (type == "ConnectionStarted")
    {
        message = UMA_MSG_CONNECTION_STARTED;
        return serialize(false, operation_id, 0, type, nlohmann::json::object());
    }
    if (type == "ConnectionProgress")
    {
        message = UMA_MSG_CONNECTION_PROGRESS;
        return serialize(false, operation_id, 0, type, nlohmann::json{{"phase", "adb_devices"}});
    }
    message = UMA_MSG_CONNECTION_FAILED;
    return serialize(false, operation_id, 0, "ConnectionFailed", nlohmann::json{
        {"error_code", UMA_ERROR_INVALID_DEVICE_RESPONSE},
        {"phase", "callback_serialization"},
        {"message", "Connection event exceeded the protocol size limit."},
        {"attempt", 1},
        {"max_attempts", 1},
    });
}

[[nodiscard]] std::string bounded_runtime(
    std::int32_t original_message,
    std::uint64_t operation_id,
    std::uint64_t sequence,
    std::string const& type,
    nlohmann::json const& payload,
    std::int32_t& message)
{
    auto details = serialize(true, operation_id, sequence, type, payload);
    if (details.size() <= kMaximumCallbackBytes) return details;

    if (original_message == UMA_MSG_DEVICE_DISCONNECTED)
    {
        message = UMA_MSG_DEVICE_DISCONNECTED;
        return serialize(true, 0, sequence, "DeviceDisconnected", nlohmann::json{
            {"error_code", UMA_ERROR_DEVICE_DISCONNECTED},
            {"session_generation", payload.value("session_generation", std::uint64_t{1})},
        });
    }
    if (original_message == UMA_MSG_DEVICE_RECONNECTED)
    {
        message = UMA_MSG_DEVICE_RECONNECTED;
        return serialize(true, 0, sequence, "DeviceReconnected", nlohmann::json{
            {"previous_session_generation", payload.value(
                "previous_session_generation", std::uint64_t{1})},
            {"session_generation", payload.value("session_generation", std::uint64_t{2})},
            {"serial", valid_utf8(payload.value("serial", std::string{}), 256)},
            {"width", payload.value("width", 1)},
            {"height", payload.value("height", 1)},
            {"physical_width", payload.value("physical_width", 1)},
            {"physical_height", payload.value("physical_height", 1)},
            {"size_source", payload.value("size_source", std::string{"physical"})},
        });
    }

    message = UMA_MSG_OPERATION_FAILED;
    return serialize(true, operation_id, sequence, "OperationFailed", nlohmann::json{
        {"error_code", UMA_ERROR_INVALID_DEVICE_RESPONSE},
        {"phase", "callback_serialization"},
        {"message", "Native event exceeded the protocol size limit."},
    });
}

}

CallbackSender::CallbackSender(UmaApiCallback callback, void* custom_arg) noexcept
    : callback_{callback}, custom_arg_{custom_arg}
{
}

void CallbackSender::send_connection(
    std::int32_t message,
    std::uint64_t operation_id,
    std::string type,
    nlohmann::json payload) noexcept
{
    enqueue(Pending{
        .message = message,
        .operation_id = operation_id,
        .runtime = false,
        .type = std::move(type),
        .payload = std::move(payload),
    });
}

void CallbackSender::send_runtime(
    std::int32_t message,
    std::uint64_t operation_id,
    std::string type,
    nlohmann::json payload) noexcept
{
    enqueue(Pending{
        .message = message,
        .operation_id = operation_id,
        .runtime = true,
        .type = std::move(type),
        .payload = std::move(payload),
    });
}

void CallbackSender::close() noexcept
{
    try
    {
        std::lock_guard lock(mutex_);
        closed_ = true;
        pending_.clear();
    }
    catch (...) {}
}

void CallbackSender::enqueue(Pending pending) noexcept
{
    bool dispatch = false;
    try
    {
        {
            std::lock_guard lock(mutex_);
            if (closed_ || callback_ == nullptr) return;
            if (pending.runtime) pending.sequence = next_sequence_++;
            pending_.push_back(std::move(pending));
            if (!dispatching_)
            {
                dispatching_ = true;
                dispatch = true;
            }
        }
        if (dispatch) drain();
    }
    catch (...)
    {
        if (dispatch)
        {
            try
            {
                std::lock_guard lock(mutex_);
                dispatching_ = false;
            }
            catch (...) {}
        }
    }
}

void CallbackSender::drain() noexcept
{
    for (;;)
    {
        Pending pending;
        {
            std::lock_guard lock(mutex_);
            if (closed_)
            {
                pending_.clear();
                dispatching_ = false;
                return;
            }
            if (pending_.empty())
            {
                dispatching_ = false;
                return;
            }
            pending = std::move(pending_.front());
            pending_.pop_front();
        }

        auto message = pending.message;
        try
        {
            auto details = pending.runtime
                ? bounded_runtime(
                    pending.message,
                    pending.operation_id,
                    pending.sequence,
                    pending.type,
                    pending.payload,
                    message)
                : bounded_connection(pending.operation_id, message, pending.type, pending.payload);
            if (details.size() > kMaximumCallbackBytes)
            {
                // All fallback envelopes are deliberately small and bounded.
                // Drop only if an invariant is violated; continue draining.
                continue;
            }
            callback_(message, details.c_str(), custom_arg_);
        }
        catch (...)
        {
            // A serialization or callback exception must not strand queued events.
        }
    }
}

}
