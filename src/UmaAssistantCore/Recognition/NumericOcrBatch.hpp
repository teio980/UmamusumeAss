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
}

namespace UmaAssistant::Recognition {

struct NumericOcrBatchField
{
    std::string key;
    Vision::GrayImage crop;
    int maxValue{999};
    bool validateGainGlyphs{};
};

struct NumericOcrBatchReading
{
    std::optional<int> value;
    std::string rawText;
    bool glyphMismatch{};
};

using NumericOcrBatchResult = std::map<std::string, NumericOcrBatchReading, std::less<>>;

/// Optional, bounded Tesseract fallback for isolated numeric image crops.
/// OCR failures leave readings unknown; cancellation is reported as
/// std::errc::operation_canceled so callers can stop the surrounding task.
class NumericOcrBatch
{
public:
    NumericOcrBatch();
    NumericOcrBatch(IWin32Process& process, std::filesystem::path executable);
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
    std::unique_ptr<IWin32Process> owned_process_;
    IWin32Process* process_{};
    std::filesystem::path executable_;
};

} // namespace UmaAssistant::Recognition
