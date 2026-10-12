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

    void RememberObservation(const CareerDomain::CareerObservation& observation);

    void ResetForEntry() noexcept;
    void ResetForRecovery() noexcept override { ResetForEntry(); }

    [[nodiscard]] const std::shared_ptr<CareerVisualResources>& GetResources() const noexcept
    {
        return m_resources;
    }

private:
    std::shared_ptr<CareerVisualResources> m_resources;
    bool m_diagnosticsLogged{false};
    bool m_careerStarted{false};
    bool m_settlementInProgress{false};
    bool m_returningHome{false};
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
};

} // namespace UmaAssistant::Career
