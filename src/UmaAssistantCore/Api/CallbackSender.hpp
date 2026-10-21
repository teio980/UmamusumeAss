#pragma once

#include "UmaAssistant/UmaCaller.h"

#include <cstdint>
#include <deque>
#include <mutex>
#include <string>

#include <nlohmann/json.hpp>

namespace UmaAssistant::Api {

class CallbackSender final
{
public:
    CallbackSender(UmaApiCallback callback, void* custom_arg) noexcept;
    CallbackSender(CallbackSender const&) = delete;
    CallbackSender& operator=(CallbackSender const&) = delete;

    void send_connection(
        std::int32_t message,
        std::uint64_t operation_id,
        std::string type,
        nlohmann::json payload) noexcept;
    void send_runtime(
        std::int32_t message,
        std::uint64_t operation_id,
        std::string type,
        nlohmann::json payload) noexcept;
    void close() noexcept;

private:
    struct Pending
    {
        std::int32_t message{};
        std::uint64_t operation_id{};
        std::uint64_t sequence{};
        bool runtime{};
        std::string type;
        nlohmann::json payload;
    };

    void enqueue(Pending pending) noexcept;
    void drain() noexcept;

    UmaApiCallback callback_{};
    void* custom_arg_{};
    std::mutex mutex_;
    std::deque<Pending> pending_;
    std::uint64_t next_sequence_{1};
    bool dispatching_{};
    bool closed_{};
};

}
