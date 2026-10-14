#pragma once

#include "UmaAssistantCore/Career/CareerVisualResources.hpp"
#include "UmaAssistantCore/CareerDomain/Scenarios/Ura/UraTrainingScanner.hpp"
#include "UmaAssistantCore/TaskEngine/ITaskExecutionContext.hpp"

#include <memory>
#include <optional>
#include <string>

namespace UmaAssistant::Career::Ura
{

struct UraTrainingVisualScanResult
{
    CareerDomain::Ura::UraTrainingScannerStage stage{CareerDomain::Ura::UraTrainingScannerStage::Idle};
    bool screenChanged{false};
    std::string detail;

    [[nodiscard]] bool IsReadyToSelect() const noexcept
    {
        return stage == CareerDomain::Ura::UraTrainingScannerStage::ReadyToSelect;
    }
    [[nodiscard]] bool IsReadyToTap() const noexcept
    {
        return stage == CareerDomain::Ura::UraTrainingScannerStage::ReadyToTap;
    }
};

// Runtime adapter for the pure UraTrainingScanner. It reads the configured
// training screen, captures stable OCR evidence, and performs only reversible
// preview taps; the action executor owns the confirmed training tap.
class UraTrainingVisualScanner
{
public:
    explicit UraTrainingVisualScanner(std::shared_ptr<CareerVisualResources> resources);

    [[nodiscard]] UraTrainingVisualScanResult ScanCandidates(
        TaskEngine::ITaskExecutionContext& context,
        CareerDomain::Ura::UraTrainingScanner& scanner,
        CareerDomain::Ura::RaceDistanceBand distance,
        const std::string& careerStage,
        std::optional<int> energyPercent,
        std::optional<int> turnIndex);

    [[nodiscard]] UraTrainingVisualScanResult ConfirmWinner(
        TaskEngine::ITaskExecutionContext& context,
        CareerDomain::Ura::UraTrainingScanner& scanner);

private:
    struct SelectionFrame;
    struct CandidateRead;
    struct ScanCache;

    [[nodiscard]] std::shared_ptr<const TaskEngine::TaskFrameSnapshot> CurrentFrame(
        TaskEngine::ITaskExecutionContext& context,
        std::string& error) const;
    [[nodiscard]] std::shared_ptr<const TaskEngine::TaskFrameSnapshot> CaptureFreshFrame(
        TaskEngine::ITaskExecutionContext& context,
        const std::shared_ptr<const TaskEngine::TaskFrameSnapshot>& previous,
        std::string& error) const;
    [[nodiscard]] SelectionFrame DetectSelection(
        TaskEngine::ITaskExecutionContext& context,
        const std::shared_ptr<const TaskEngine::TaskFrameSnapshot>& frame) const;
    [[nodiscard]] bool SelectPreview(
        TaskEngine::ITaskExecutionContext& context,
        SelectionFrame& current,
        const std::string& trainingType,
        std::string& error) const;
    [[nodiscard]] CandidateRead ReadCandidate(
        TaskEngine::ITaskExecutionContext& context,
        const std::shared_ptr<const TaskEngine::TaskFrameSnapshot>& frame,
        const std::string& trainingType,
        const TaskEngine::TaskMatchResult& selectedLogo) const;
    [[nodiscard]] CareerDomain::Ura::TrainingCandidate UnknownCandidate(
        const std::string& trainingType) const;

    std::shared_ptr<CareerVisualResources> m_resources;
    std::shared_ptr<ScanCache> m_scanCache;
};

} // namespace UmaAssistant::Career::Ura
