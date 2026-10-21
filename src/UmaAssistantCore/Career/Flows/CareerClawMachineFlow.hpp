#pragma once

#include "UmaAssistantCore/Career/CareerVisualResources.hpp"
#include "UmaAssistantCore/Career/ICareerActionExecutor.hpp"
#include "UmaAssistantCore/Vision/ColorAnalysis.hpp"
#include <nlohmann/json.hpp>

namespace UmaAssistant::Career
{

struct ClawBlobRule
{
    double minimumAreaRatio{}, maximumAreaRatio{1};
    double minimumWidthRatio{}, maximumWidthRatio{1};
    double minimumHeightRatio{}, maximumHeightRatio{1};
    double minimumAspectRatio{}, maximumAspectRatio{1000000};
};

struct CareerClawMachineSettings
{
    double markerSearchScale{.25}, markerCandidateThreshold{.8};
    double creditLabelThreshold{.94}, ocrScale{2}, creditOcrPaddingLabelRatio{.25};
    double controlBorderPaddingRatio{.10}, stableToleranceControlRatio{.08}, controlPinkPixelRatio{.08};
    int readyTimeoutMs{12000}, nextAttemptTimeoutMs{45000}, resultTimeoutMs{25000}, exitTimeoutMs{20000};
    int pollIntervalMs{200}, holdDurationMs{1000}, stableSamples{2};
    UmaColorRule whiteControlColor, pinkControlColor, resultButtonColor, ocrTextColor;
    ClawBlobRule controlShape{.01, .15, .18, .5, .10, .30, .65, 1.5};
    ClawBlobRule resultButtonShape{.005, .10, .15, .65, .025, .15, 2, 8};

    CareerClawMachineSettings();
    static CareerClawMachineSettings FromJson(const nlohmann::json& options);
};

class CareerClawVision
{
public:
    static Vision::MatchResult MatchMarker(const Vision::GrayImage& frame,
        const Vision::GrayImage& marker, const CareerClawMachineSettings& settings,
        double threshold, bool colorText = true, int referenceWidth = 900, int referenceHeight = 1600);
    static std::optional<UmaImageRect> FindControl(const Vision::GrayImage& frame,
        const Vision::MatchResult& marker, const CareerClawMachineSettings& settings);
    static std::vector<UmaImageRect> FindResultButtons(const Vision::GrayImage& frame,
        const Vision::MatchResult& marker, const CareerClawMachineSettings& settings);
};

struct CareerClawFlowResult
{
    ActionExecutionStatus status{ActionExecutionStatus::Failed};
    std::string error;
};

class CareerClawMachineFlow
{
public:
    explicit CareerClawMachineFlow(std::shared_ptr<CareerVisualResources> resources);
    CareerClawFlowResult Run(TaskEngine::ITaskExecutionContext& context, bool resultScreen = false) const;
    static std::optional<int> ParseCredit(const std::vector<TaskEngine::TaskOcrDetection>& text);
    // Exposed for replay hosts; input is always bound to the recognized source.
    static CareerClawFlowResult PressControl(TaskEngine::ITaskExecutionContext& context,
        const TaskEngine::TaskMatchResult& control, int holdDurationMs);

private:
    std::shared_ptr<CareerVisualResources> m_resources;
};

} // namespace UmaAssistant::Career
