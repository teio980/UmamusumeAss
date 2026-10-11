#pragma once
#include <exception>
#include <string>
#include <utility>

namespace UmaAssistant::TaskEngine
{
// A context may restore its application after a confirmed interruption. Its
// flow owner reconciles state before any action resumes; the engine itself
// has no knowledge of game pages or recovery policies.
struct TaskContextInterruption final : std::exception
{
    explicit TaskContextInterruption(std::string details, bool restored = false)
        : message(std::move(details)), homeRestored(restored) {}
    const char* what() const noexcept override { return message.c_str(); }
    std::string message;
    bool homeRestored{};
};
}
