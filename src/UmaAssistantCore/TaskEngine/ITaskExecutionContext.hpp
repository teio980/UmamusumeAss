#pragma once

#include "TaskEvents.hpp"
#include "TaskFrameSnapshot.hpp"
#include <string>
#include <vector>
#include <optional>
#include <cstdint>
#include <memory>
#include <string_view>
#include <algorithm>
#include <utility>
#include "Vision/TemplateMatcher.hpp"
#include "Recognition/NumericOcrBatch.hpp"

namespace UmaAssistant::TaskEngine
{

struct TaskRoi
{
    int x{0};
    int y{0};
    int width{0};
    int height{0};
};

struct TaskMatchResult
{
    bool found{false};
    double score{0.0};
    int x{0};
    int y{0};
    int width{0};
    int height{0};
    int referenceWidth{900};
    int referenceHeight{1600};
    std::uint64_t frameId{0};
    std::uint64_t sessionGeneration{0};
    std::uint64_t geometryGeneration{0};

    [[nodiscard]] int CenterX() const noexcept { return x + width / 2; }
    [[nodiscard]] int CenterY() const noexcept { return y + height / 2; }
};

struct TaskTemplateMatchCandidate
{
    std::string templatePath;
    TaskMatchResult match;
};

struct TaskTemplateCandidatesResult
{
    std::vector<TaskTemplateMatchCandidate> candidates;
    double scoreGap{0.0};
    bool ambiguous{false};
};

struct TaskOcrDetection
{
    std::string text;
    int x{0};
    int y{0};
    int width{0};
    int height{0};
    double confidence{0.0};

    [[nodiscard]] int CenterX() const noexcept { return x + width / 2; }
    [[nodiscard]] int CenterY() const noexcept { return y + height / 2; }
};

class ITaskExecutionContext
{
public:
    virtual ~ITaskExecutionContext() = default;

    virtual bool CaptureScreen() = 0;

    // An observational capture must preserve the selected frame and any input
    // lease. Its pixels can be inspected, but must not be used for input.
    [[nodiscard]] virtual std::shared_ptr<const TaskFrameSnapshot> CaptureInspectionFrame() { return {}; }

    // Persistence can bind checkpoints to the connected device without
    // trusting a serial supplied by task settings or depending on DeviceSession.
    [[nodiscard]] virtual std::string GetDeviceSerial() const { return {}; }

    virtual TaskMatchResult MatchTemplate(
        const std::string& templatePath,
        double threshold,
        const std::optional<TaskRoi>& roi = std::nullopt,
        const std::string& algorithm = "MatchTemplate") = 0;

    virtual TaskMatchResult MatchTemplateAdvanced(
        const std::string& templatePath,
        double threshold,
        const std::optional<TaskRoi>& roi,
        const std::string& algorithm,
        int referenceWidth,
        int referenceHeight,
        const std::vector<double>& scaleCandidates)
    {
        (void)referenceWidth;
        (void)referenceHeight;
        (void)scaleCandidates;
        return MatchTemplate(templatePath, threshold, roi, algorithm);
    }

    virtual TaskTemplateCandidatesResult MatchTemplateCandidates(
        const std::vector<std::string>& templatePaths,
        double threshold,
        const std::optional<TaskRoi>& roi,
        const std::string& algorithm,
        int referenceWidth,
        int referenceHeight,
        const std::vector<double>& scaleCandidates,
        double minimumScoreGap)
    {
        TaskTemplateCandidatesResult result;
        for (const auto& path : templatePaths)
        {
            auto match = MatchTemplateAdvanced(path, threshold, roi, algorithm,
                                               referenceWidth, referenceHeight, scaleCandidates);
            if (match.found)
                result.candidates.push_back({path, std::move(match)});
        }
        std::stable_sort(result.candidates.begin(), result.candidates.end(),
            [](const auto& left, const auto& right) {
                return left.match.score > right.match.score;
            });
        if (result.candidates.size() > 1)
        {
            result.scoreGap = result.candidates[0].match.score - result.candidates[1].match.score;
            result.ambiguous = result.scoreGap < minimumScoreGap;
        }
        return result;
    }

    [[nodiscard]] virtual std::shared_ptr<const TaskFrameSnapshot> GetCurrentFrame() const { return {}; }
    [[nodiscard]] virtual std::shared_ptr<const TaskFrameSnapshot> GetPreviousFrame() const { return {}; }
    [[nodiscard]] virtual std::shared_ptr<const Vision::GrayImage> LoadTemplateImage(const std::string& path)
    {
        (void)path;
        return {};
    }
    virtual void SetCoordinateReference(int width, int height) { (void)width; (void)height; }
    [[nodiscard]] virtual std::pair<int, int> GetCoordinateReference() const { return {900, 1600}; }
    [[nodiscard]] virtual std::string GetTemplateBaseDirectory() const { return {}; }
    virtual void SetTemplateBaseDirectory(const std::string& directory) { (void)directory; }
    // Coordinates are expressed in match.referenceWidth/referenceHeight space.
    // Retained matches accept taps only at their frozen reference point.
    [[nodiscard]] virtual bool TapMatch(int x, int y, const TaskMatchResult& match)
    {
        (void)match;
        return Tap(x, y);
    }
    [[nodiscard]] virtual bool RetainMatchForRetry(
        const TaskMatchResult& match,
        int maxTapAttempts = 1,
        int lifetimeMilliseconds = 30000,
        int fixedReferenceX = -1,
        int fixedReferenceY = -1)
    {
        (void)match;
        (void)maxTapAttempts;
        (void)lifetimeMilliseconds;
        (void)fixedReferenceX;
        (void)fixedReferenceY;
        return false;
    }
    virtual void ReleaseRetainedMatch() {}
    [[nodiscard]] virtual bool HasRetainedMatch() const { return false; }
    [[nodiscard]] virtual bool IsMatchRetained(const TaskMatchResult& match) const
    {
        (void)match;
        return false;
    }

    virtual bool CheckImageChange(const TaskMatchResult& match) = 0;

    virtual std::vector<TaskOcrDetection> RecognizeText(
        const std::string& language,
        const std::optional<TaskRoi>& roi = std::nullopt) = 0;

    // Processed crops remain native images. Offline hosts can inject fixed OCR
    // through this same interface without replacing the flow under test.
    virtual std::vector<TaskOcrDetection> RecognizeImageText(
        const Vision::GrayImage& image, const std::string& language)
    {
        (void)image; (void)language;
        return {};
    }

    // Optional numeric recognition consumes prepared crops from the same
    // captured frame. Unavailable local OCR leaves values unknown.
    virtual Recognition::NumericOcrBatchResult RecognizeNumericImages(
        const std::vector<Recognition::NumericOcrBatchField>& fields)
    {
        (void)fields;
        return {};
    }

    virtual bool PrepareHeldTouch() { return false; }
    virtual bool TouchDownMatch(int x, int y, const TaskMatchResult& source)
    {
        (void)x; (void)y; (void)source;
        return false;
    }
    virtual bool TouchUp() { return false; }
    // Cancellation must release an active press even after IsCancelled().
    virtual bool TouchCancel() { return false; }

    virtual bool ProbeHsv(
        int x, int y, int radius,
        double hMin, double hMax,
        double sMin, double sMax,
        double vMin, double vMax,
        double minRatio) = 0;

    virtual bool Tap(int x, int y) = 0;

    virtual bool Swipe(int x1, int y1, int x2, int y2, int durationMs) = 0;

    virtual bool InputText(const std::string& text) = 0;

    virtual bool SendKey(const std::string& keyCode) = 0;

    virtual void Sleep(int milliseconds) = 0;

    virtual bool IsCancelled() const = 0;
    [[nodiscard]] virtual int GetLastErrorCode() const { return 0; }

    virtual void Log(const std::string& level, const std::string& message) = 0;

    virtual void EmitEvent(const TaskExecutionEvent& event) = 0;
};

} // namespace UmaAssistant::TaskEngine
