#pragma once

#include "UmaAssistantCore/TaskEngine/ITaskExecutionContext.hpp"
#include <unordered_map>
#include <deque>
#include <string>
#include <vector>
#include <utility>

namespace UmaAssistant::TaskEngine::Tests
{

class MockTaskExecutionContext : public ITaskExecutionContext
{
public:
    bool CaptureScreen() override
    {
        captureCount++;
        if (!captureResults.empty())
        {
            const bool result = captureResults.front();
            captureResults.pop_front();
            return result;
        }
        return captureResult;
    }

    TaskMatchResult MatchTemplate(
        const std::string& templatePath,
        double threshold,
        const std::optional<TaskRoi>& /*roi*/,
        const std::string& /*algorithm*/) override
    {
        matchedTemplatesChecked.push_back(templatePath);
        auto it = mockTemplateResults.find(templatePath);
        if (it != mockTemplateResults.end() && !it->second.empty())
        {
            auto res = it->second.front();
            if (it->second.size() > 1)
            {
                it->second.pop_front();
            }
            if (res.score >= threshold)
            {
                return res;
            }
        }
        return TaskMatchResult{false, 0.0, 0, 0, 0, 0};
    }

    TaskMatchResult MatchTemplateAdvanced(
        const std::string& templatePath,
        double threshold,
        const std::optional<TaskRoi>& roi,
        const std::string& algorithm,
        int referenceWidth,
        int referenceHeight,
        const std::vector<double>& scaleCandidates) override
    {
        advancedCalls.push_back({templatePath, roi, algorithm, referenceWidth, referenceHeight, scaleCandidates});
        return MatchTemplate(templatePath, threshold, roi, algorithm);
    }

    bool CheckImageChange(const TaskMatchResult& /*match*/) override
    {
        return imageChangeResult;
    }

    std::vector<TaskOcrDetection> RecognizeText(
        const std::string& /*language*/,
        const std::optional<TaskRoi>& /*roi*/) override
    {
        if (!mockOcrSequences.empty())
        {
            auto result = std::move(mockOcrSequences.front());
            mockOcrSequences.pop_front();
            return result;
        }
        return mockOcrDetections;
    }

    bool ProbeHsv(
        int /*x*/, int /*y*/, int /*radius*/,
        double /*hMin*/, double /*hMax*/,
        double /*sMin*/, double /*sMax*/,
        double /*vMin*/, double /*vMax*/,
        double /*minRatio*/) override
    {
        ++probeCount;
        if (!probeResults.empty())
        {
            const bool result = probeResults.front();
            probeResults.pop_front();
            return result;
        }
        return probeHsvResult;
    }

    bool Tap(int x, int y) override
    {
        taps.push_back({x, y});
        if (!tapResults.empty())
        {
            const bool result = tapResults.front();
            tapResults.pop_front();
            return result;
        }
        return tapResult;
    }

    bool TapMatch(int x, int y, const TaskMatchResult& match) override
    {
        if (IsMatchRetained(match))
        {
            if (x != retainedX || y != retainedY)
                return false;
            const bool result = Tap(x, y);
            --retainedAttempts;
            if (retainedAttempts <= 0)
                retainedMatch.reset();
            return result;
        }
        return Tap(x, y);
    }

    bool RetainMatchForRetry(
        const TaskMatchResult& match,
        int maxTapAttempts = 1,
        int /*lifetimeMilliseconds*/ = 30000,
        int fixedReferenceX = -1,
        int fixedReferenceY = -1) override
    {
        if (maxTapAttempts <= 0 || maxTapAttempts > 100 ||
            (fixedReferenceX < 0) != (fixedReferenceY < 0))
            return false;
        ++retainCallCount;
        retainedMatch = match;
        retainedAttempts = maxTapAttempts;
        retainedX = fixedReferenceX < 0 ? match.CenterX() : fixedReferenceX;
        retainedY = fixedReferenceY < 0 ? match.CenterY() : fixedReferenceY;
        return true;
    }

    void ReleaseRetainedMatch() override
    {
        retainedMatch.reset();
        retainedAttempts = 0;
    }

    bool HasRetainedMatch() const override
    {
        return retainedMatch.has_value() && retainedAttempts > 0;
    }

    bool IsMatchRetained(const TaskMatchResult& match) const override
    {
        return HasRetainedMatch() && retainedMatch->x == match.x && retainedMatch->y == match.y &&
            retainedMatch->width == match.width && retainedMatch->height == match.height;
    }

    bool Swipe(int x1, int y1, int x2, int y2, int durationMs) override
    {
        swipes.push_back({x1, y1, x2, y2, durationMs});
        return swipeResult;
    }

    bool InputText(const std::string& text) override
    {
        inputs.push_back(text);
        return true;
    }

    bool SendKey(const std::string& keyCode) override
    {
        keys.push_back(keyCode);
        return true;
    }

    void Sleep(int milliseconds) override
    {
        totalSleepMs += milliseconds;
        if (cancelOnSleep)
            cancelled = true;
    }

    bool IsCancelled() const override
    {
        return cancelled;
    }

    void SetCoordinateReference(int width, int height) override
    {
        coordinateReference = {width, height};
    }

    std::pair<int, int> GetCoordinateReference() const override
    {
        return coordinateReference;
    }

    std::string GetTemplateBaseDirectory() const override
    {
        return templateBaseDirectory;
    }

    void SetTemplateBaseDirectory(const std::string& directory) override
    {
        templateBaseDirectory = directory;
        templateBaseDirectoryHistory.push_back(directory);
    }

    void Log(const std::string& level, const std::string& message) override
    {
        logs.push_back(level + ": " + message);
    }

    void EmitEvent(const TaskExecutionEvent& event) override
    {
        emittedEvents.push_back(event);
    }

    // Test helpers
    void SetMockMatch(const std::string& templatePath, const TaskMatchResult& result)
    {
        mockTemplateResults[templatePath] = {result};
    }

    void SetMockMatchSequence(const std::string& templatePath, const std::vector<TaskMatchResult>& sequence)
    {
        mockTemplateResults[templatePath] = std::deque<TaskMatchResult>(sequence.begin(), sequence.end());
    }

    int captureCount{0};
    int totalSleepMs{0};
    bool cancelled{false};
    bool cancelOnSleep{false};
    bool captureResult{true};
    std::deque<bool> captureResults;
    bool tapResult{true};
    std::deque<bool> tapResults;
    bool swipeResult{true};
    bool imageChangeResult{true};
    bool probeHsvResult{true};
    std::deque<bool> probeResults;
    int probeCount{0};
    std::pair<int, int> coordinateReference{900, 1600};
    std::string templateBaseDirectory;
    std::vector<std::string> templateBaseDirectoryHistory;
    std::optional<TaskMatchResult> retainedMatch;
    int retainedAttempts{0};
    int retainedX{0};
    int retainedY{0};
    int retainCallCount{0};

    std::unordered_map<std::string, std::deque<TaskMatchResult>> mockTemplateResults;
    std::vector<TaskOcrDetection> mockOcrDetections;
    std::deque<std::vector<TaskOcrDetection>> mockOcrSequences;
    std::vector<std::string> matchedTemplatesChecked;
    std::vector<std::pair<int, int>> taps;
    struct SwipeRecord { int x1, y1, x2, y2, duration; };
    struct AdvancedCall
    {
        std::string templatePath;
        std::optional<TaskRoi> roi;
        std::string algorithm;
        int referenceWidth;
        int referenceHeight;
        std::vector<double> scales;
    };
    std::vector<AdvancedCall> advancedCalls;
    std::vector<SwipeRecord> swipes;
    std::vector<std::string> inputs;
    std::vector<std::string> keys;
    std::vector<std::string> logs;
    std::vector<TaskExecutionEvent> emittedEvents;
};

} // namespace UmaAssistant::TaskEngine::Tests
