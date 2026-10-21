#pragma once

#include "TemplateMatcher.hpp"

#include <filesystem>
#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <span>
#include <stop_token>
#include <string>
#include <vector>

namespace UmaAssistant {
class IWin32Process;
class IWin32InteractiveSession;
}

namespace UmaAssistant::Recognition {

enum class NumericOcrFormat { TrainingValue, UnsignedInteger };

struct NumericOcrBatchField
{
    std::string key;
    Vision::GrayImage crop;
    int maxValue{999};
    bool validateGainGlyphs{};
    NumericOcrFormat format{NumericOcrFormat::TrainingValue};
};

struct NumericOcrBatchReading
{
    std::optional<int> value;
    std::string rawText;
    bool glyphMismatch{};
    std::string failureReason;
};

using NumericOcrBatchResult = std::map<std::string, NumericOcrBatchReading, std::less<>>;

/// Optional, bounded Tesseract fallback for isolated numeric image crops.
/// Default instances reuse an isolated model worker with in-memory images,
/// falling back to the CLI when the worker or installed C API is unavailable.
/// OCR failures leave readings unknown; cancellation is reported as
/// std::errc::operation_canceled so callers can stop the surrounding task.
class NumericOcrBatch
{
public:
    using WorkerFactory = std::function<std::unique_ptr<IWin32InteractiveSession>()>;

    NumericOcrBatch();
    NumericOcrBatch(IWin32Process& process, std::filesystem::path executable);
    NumericOcrBatch(IWin32Process& process, std::filesystem::path executable, WorkerFactory workerFactory);
    ~NumericOcrBatch();

    NumericOcrBatch(NumericOcrBatch const&) = delete;
    NumericOcrBatch& operator=(NumericOcrBatch const&) = delete;
    NumericOcrBatch(NumericOcrBatch&&) noexcept;
    NumericOcrBatch& operator=(NumericOcrBatch&&) noexcept;

    [[nodiscard]] NumericOcrBatchResult read(
        std::span<NumericOcrBatchField const> fields,
        std::stop_token cancellation = {}) const;

    [[nodiscard]] static std::filesystem::path locate_executable();

private:
    struct WorkerState;
    std::unique_ptr<IWin32Process> owned_process_;
    IWin32Process* process_{};
    std::filesystem::path executable_;
    std::unique_ptr<WorkerState> worker_;
};

} // namespace UmaAssistant::Recognition
