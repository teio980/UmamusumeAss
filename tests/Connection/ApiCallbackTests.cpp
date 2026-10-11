#include "Api/CallbackSender.hpp"

#include <catch2/catch_test_macros.hpp>
#include <nlohmann/json.hpp>

#include <string>
#include <utility>
#include <vector>

namespace {

using UmaAssistant::Api::CallbackSender;

struct CapturedCallback
{
    struct Event
    {
        std::int32_t message{};
        nlohmann::json envelope;
        std::string bytes;
    };

    CallbackSender* sender{};
    bool enqueue_followup{};
    std::vector<Event> events;
};

void UMA_CALL capture_callback(int32_t message, char const* details, void* context)
{
    auto& captured = *static_cast<CapturedCallback*>(context);
    auto bytes = std::string{details};
    captured.events.push_back(CapturedCallback::Event{
        .message = message,
        .envelope = nlohmann::json::parse(bytes),
        .bytes = std::move(bytes),
    });
    if (std::exchange(captured.enqueue_followup, false))
    {
        captured.sender->send_runtime(
            UMA_MSG_OPERATION_FAILED,
            202,
            "OperationFailed",
            nlohmann::json{
                {"error_code", UMA_ERROR_COMMAND_FAILED},
                {"phase", "capture"},
                {"message", "failed"},
            });
    }
}

nlohmann::json oversized_fields()
{
    auto fields = nlohmann::json::array();
    auto const control_heavy = std::string(4096, '\x01');
    fields.push_back(control_heavy);
    fields.push_back(control_heavy);
    fields.push_back(control_heavy);
    return fields;
}

}

TEST_CASE("API sender bounds complete runtime envelopes and preserves session generations", "[ApiCallback]")
{
    CapturedCallback captured;
    CallbackSender sender{capture_callback, &captured};
    captured.sender = &sender;

    sender.send_runtime(
        UMA_MSG_DEVICE_RECONNECTED,
        0,
        "DeviceReconnected",
        nlohmann::json{
            {"previous_session_generation", 42},
            {"session_generation", 43},
            {"serial", "serial"},
            {"width", 900},
            {"height", 1600},
            {"physical_width", 900},
            {"physical_height", 1600},
            {"size_source", "physical"},
            {"diagnostics", oversized_fields()},
        });

    auto const& event = captured.events.at(0);
    REQUIRE(event.bytes.size() <= 60 * 1024);
    REQUIRE(event.message == UMA_MSG_DEVICE_RECONNECTED);
    REQUIRE(event.envelope.at("version") == 2);
    REQUIRE(event.envelope.at("operation_id") == 0);
    REQUIRE(event.envelope.at("sequence") == 1);
    REQUIRE(event.envelope.at("type") == "DeviceReconnected");
    REQUIRE(event.envelope.at("payload").at("previous_session_generation") == 42);
    REQUIRE(event.envelope.at("payload").at("session_generation") == 43);
}

TEST_CASE("API sender turns oversized operation terminals into valid bounded failures", "[ApiCallback]")
{
    CapturedCallback captured;
    CallbackSender sender{capture_callback, &captured};

    sender.send_runtime(
        UMA_MSG_INPUT_FAILED,
        303,
        "InputFailed",
        nlohmann::json{
            {"error_code", UMA_ERROR_COMMAND_FAILED},
            {"phase", "swipe"},
            {"message", "failed"},
            {"diagnostics", oversized_fields()},
        });

    auto const& event = captured.events.at(0);
    REQUIRE(event.bytes.size() <= 60 * 1024);
    REQUIRE(event.message == UMA_MSG_OPERATION_FAILED);
    REQUIRE(event.envelope.at("version") == 2);
    REQUIRE(event.envelope.at("operation_id") == 303);
    REQUIRE(event.envelope.at("sequence") == 1);
    REQUIRE(event.envelope.at("type") == "OperationFailed");
    REQUIRE(event.envelope.at("payload").at("phase") == "callback_serialization");
}

TEST_CASE("API sender bounds version one success without changing the connection result", "[ApiCallback]")
{
    CapturedCallback captured;
    CallbackSender sender{capture_callback, &captured};

    sender.send_connection(
        UMA_MSG_CONNECTION_SUCCEEDED,
        404,
        "ConnectionSucceeded",
        nlohmann::json{
            {"serial", "serial"},
            {"android_id", "0123456789abcdef"},
            {"android_version", "14"},
            {"width", 900},
            {"height", 1600},
            {"physical_width", 900},
            {"physical_height", 1600},
            {"size_source", "physical"},
            {"diagnostics", oversized_fields()},
        });

    auto const& event = captured.events.at(0);
    REQUIRE(event.bytes.size() <= 60 * 1024);
    REQUIRE(event.message == UMA_MSG_CONNECTION_SUCCEEDED);
    REQUIRE(event.envelope.at("version") == 1);
    REQUIRE(event.envelope.at("operation_id") == 404);
    REQUIRE(event.envelope.at("type") == "ConnectionSucceeded");
    REQUIRE(event.envelope.at("payload").at("android_id") == "0123456789abcdef");
    REQUIRE(event.envelope.at("payload").at("width") == 900);
}

TEST_CASE("API sender serializes reentrant runtime events in sequence order", "[ApiCallback]")
{
    CapturedCallback captured;
    CallbackSender sender{capture_callback, &captured};
    captured.sender = &sender;
    captured.enqueue_followup = true;

    sender.send_runtime(
        UMA_MSG_DEVICE_DISCONNECTED,
        0,
        "DeviceDisconnected",
        nlohmann::json{
            {"error_code", UMA_ERROR_DEVICE_DISCONNECTED},
            {"session_generation", 51},
        });

    REQUIRE(captured.events.size() == 2);
    REQUIRE(captured.events[0].envelope.at("sequence") == 1);
    REQUIRE(captured.events[1].envelope.at("sequence") == 2);
    REQUIRE(captured.events[0].envelope.at("payload").at("session_generation") == 51);
    REQUIRE(captured.events[1].envelope.at("operation_id") == 202);
}
