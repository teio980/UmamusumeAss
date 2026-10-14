#pragma once

#include "ICareerObserver.hpp"
#include "CareerVisualResources.hpp"
#include <memory>
#include <string>
#include <optional>
#include <vector>
#include <cstdint>
#include <cstddef>
#include <unordered_map>
#include <cmath>
#include <cstring>

namespace UmaAssistant::Career
{

struct EnergyBarMeasurement
{
    int percent{0};
    double confidence{0.0};
};

struct ParsedTurnPosition
{
    std::string label;
    int turnIndex{0};
    std::string year;   // "junior", "classic", "senior"
    std::string phase;  // "early", "late", "pre-debut"
    std::optional<int> month;
};

class CareerObservationReader
{
public:
    static std::optional<EnergyBarMeasurement> MeasureEnergyBar(
        const uint8_t* rgbaPixels,
        int imageWidth,
        int imageHeight,
        const std::vector<int>& roi,
        const std::vector<int>& innerInset = {},
        int referenceWidth = 900,
        int referenceHeight = 1600,
        double fillSaturationMin = 0.18,
        double fillValueMin = 0.18,
        double columnMatchRatio = 0.55,
        std::size_t pixelBytes = 0);

    static std::optional<std::string> ParseMood(const std::string& ocrText);

    static std::optional<ParsedTurnPosition> ParseTurnPosition(const std::string& text);
    static std::optional<int> ParseObjectiveTurnsLeft(const std::string& text);
    static std::optional<int> ParseFansToGoal(const std::string& text);
};

struct CachedMainRegion
{
    int width{0};
    int height{0};
    int frameWidth{0};
    int frameHeight{0};
    int x{0};
    int y{0};
    double confidence{0.0};
    std::uint64_t sessionGeneration{0};
    std::uint64_t geometryGeneration{0};
    std::vector<uint8_t> grayPixels;
    std::vector<uint8_t> rgbaPixels;
    std::string text;
};

class CareerMainTextCache
{
public:
    void Clear() noexcept { m_entries.clear(); }

    void Remember(const std::string& field, const TaskEngine::TaskFrameSnapshot& frame,
                  const TaskEngine::TaskRoi& roi, const std::string& text, double confidence = 1.0)
    {
        if (text.empty() || !std::isfinite(confidence) || confidence <= 0.0 || confidence > 1.0)
            return;
        CachedMainRegion entry;
        entry.width = roi.width;
        entry.height = roi.height;
        entry.text = text;
        entry.x = roi.x;
        entry.y = roi.y;
        entry.frameWidth = frame.width;
        entry.frameHeight = frame.height;
        entry.confidence = confidence;
        entry.sessionGeneration = frame.sessionGeneration;
        entry.geometryGeneration = frame.geometryGeneration;
        if (!CropPixels(frame, roi, entry.grayPixels, entry.rgbaPixels)) return;
        m_entries[field] = std::move(entry);
    }

    bool TryGet(const std::string& field, const TaskEngine::TaskFrameSnapshot& frame,
                const TaskEngine::TaskRoi& roi, std::string& outText, double* outConfidence = nullptr) const
    {
        auto it = m_entries.find(field);
        if (it == m_entries.end())
            return false;
        const auto& cached = it->second;
        if (cached.width != roi.width || cached.height != roi.height || cached.x != roi.x || cached.y != roi.y
            || cached.frameWidth != frame.width || cached.frameHeight != frame.height
            || cached.sessionGeneration != frame.sessionGeneration || cached.geometryGeneration != frame.geometryGeneration)
            return false;
        std::vector<uint8_t> currentGray;
        std::vector<uint8_t> currentRgba;
        if (!CropPixels(frame, roi, currentGray, currentRgba)) return false;
        if (currentGray != cached.grayPixels)
            return false;
        if (currentRgba != cached.rgbaPixels)
            return false;
        outText = cached.text;
        if (outConfidence) *outConfidence = cached.confidence;
        return true;
    }

private:
    static bool CropPixels(const TaskEngine::TaskFrameSnapshot& frame, const TaskEngine::TaskRoi& roi,
                           std::vector<uint8_t>& grayOut, std::vector<uint8_t>& rgbaOut)
    {
        if (frame.width <= 0 || frame.height <= 0 || roi.width <= 0 || roi.height <= 0
            || frame.grayPixels.size() != static_cast<std::size_t>(frame.width) * frame.height
            || (!frame.rgbaPixels.empty() && frame.rgbaPixels.size() != frame.grayPixels.size() * 4)) return false;
        const int x0 = static_cast<int>(std::lround(roi.x * frame.width / 900.0));
        const int y0 = static_cast<int>(std::lround(roi.y * frame.height / 1600.0));
        const int x1 = x0 + static_cast<int>(std::lround(roi.width * frame.width / 900.0));
        const int y1 = y0 + static_cast<int>(std::lround(roi.height * frame.height / 1600.0));
        const int w = x1 - x0;
        const int h = y1 - y0;
        if (w <= 0 || h <= 0 || x0 < 0 || y0 < 0 || x1 > frame.width || y1 > frame.height) return false;
        grayOut.resize(static_cast<std::size_t>(w) * h);
        for (int row = 0; row < h; ++row)
        {
            const auto srcOffset = static_cast<std::size_t>(y0 + row) * frame.width + x0;
            if (srcOffset + w <= frame.grayPixels.size())
            {
                std::memcpy(&grayOut[static_cast<std::size_t>(row) * w],
                            &frame.grayPixels[srcOffset], w);
            }
        }
        if (!frame.rgbaPixels.empty())
        {
            rgbaOut.resize(static_cast<std::size_t>(w) * h * 4);
            for (int row = 0; row < h; ++row)
            {
                const auto srcOffset = (static_cast<std::size_t>(y0 + row) * frame.width + x0) * 4;
                if (srcOffset + static_cast<std::size_t>(w) * 4 <= frame.rgbaPixels.size())
                {
                    std::memcpy(&rgbaOut[static_cast<std::size_t>(row) * w * 4],
                                &frame.rgbaPixels[srcOffset], static_cast<std::size_t>(w) * 4);
                }
            }
        }
        return true;
    }

    std::unordered_map<std::string, CachedMainRegion> m_entries;
};

class CareerScreenObserver : public ICareerObserver
{
public:
    CareerScreenObserver();
    explicit CareerScreenObserver(std::shared_ptr<CareerVisualResources> resources);
    explicit CareerScreenObserver(const CareerRuntimeConfig& config);

    CareerDomain::CareerObservation Observe(
        TaskEngine::ITaskExecutionContext& context) override;

    CareerDomain::CareerObservation ObserveScreens(
        TaskEngine::ITaskExecutionContext& context, const std::vector<std::string>& screenIds,
        bool allowResume = false) override;

    CareerDomain::CareerObservation ObserveCareer(TaskEngine::ITaskExecutionContext& context) override;
    CareerDomain::CareerObservation ObserveTransition(TaskEngine::ITaskExecutionContext& context,
        const CareerTransitionContext& transition,
        std::shared_ptr<const TaskEngine::TaskFrameSnapshot> seed = {}) override;

    void RememberObservation(const CareerDomain::CareerObservation& observation);

    void ResetForEntry() noexcept;
    void ResetForRecovery() noexcept override { ResetForEntry(); }
    void SetGoalCompletionProbeArmed(bool armed) noexcept override { m_goalCompletionProbeArmed = armed; }
    void SetRaceReplayFlowCompleted(bool completed) noexcept override { m_raceReplayFlowCompleted = completed; }
    void SetRaceRunnerResultExpected(bool expected) noexcept override { m_raceRunnerResultExpected = expected; }
    void ClearMainTextCache() noexcept override { m_mainTextCache.Clear(); }

    [[nodiscard]] const std::shared_ptr<CareerVisualResources>& GetResources() const noexcept
    {
        return m_resources;
    }

private:
    CareerDomain::CareerObservation ObserveScreensCore(TaskEngine::ITaskExecutionContext& context,
        const std::vector<std::string>& screens, bool allowResume,
        std::shared_ptr<const TaskEngine::TaskFrameSnapshot> seed, bool minimumDetails);
    std::shared_ptr<CareerVisualResources> m_resources;
    bool m_diagnosticsLogged{false};
    bool m_careerStarted{false};
    bool m_settlementInProgress{false};
    bool m_returningHome{false};
    bool m_goalCompletionProbeArmed{false};
    bool m_raceReplayFlowCompleted{false};
    bool m_raceRunnerResultExpected{false};
    CareerMainTextCache m_mainTextCache;
    std::shared_ptr<const TaskEngine::TaskFrameSnapshot> m_pendingConfirmationFrame;
    std::string m_lastScreenId;
    std::unordered_map<std::string, std::string> m_eventIdsByNormalizedTitle;
    struct EventTitleRecognition
    {
        std::string eventId;
        std::string title;
        std::string screenId;
        std::string actionId;
    };
    std::vector<EventTitleRecognition> m_eventTitles;
    struct MainAnchorHint
    {
        std::string path;
        TaskEngine::TaskMatchResult match;
        int width{0};
        int height{0};
        std::uint64_t session{0};
        std::uint64_t geometry{0};
    };
    std::optional<MainAnchorHint> m_mainAnchorHint;
};

} // namespace UmaAssistant::Career
