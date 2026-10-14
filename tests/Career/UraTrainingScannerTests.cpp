#include "UmaAssistantCore/CareerDomain/Scenarios/Ura/UraTrainingScanner.hpp"

#include <catch2/catch_test_macros.hpp>
#include <algorithm>
#include <string>
#include <vector>

using namespace UmaAssistant::CareerDomain;
using namespace UmaAssistant::CareerDomain::Ura;

namespace
{

TrainingCandidate Candidate(const std::string& type, int speedGain, int failureRate = 0)
{
    TrainingCandidate value;
    value.trainingType = type;
    value.speedGain = speedGain;
    value.staminaGain = 8;
    value.powerGain = 6;
    value.gutsGain = 2;
    value.witGain = 4;
    value.skillPointGain = 3;
    value.failureRatePercent = failureRate;
    value.trainingLevel = 2;
    value.unbondedSupportCount = 1;
    value.hintCount = 0;
    value.coreConfidence = 0.94;
    value.failureRateConfidence = 0.93;
    value.auxiliaryConfidence = 0.91;
    value.coreSource = ObservationSource::Ocr;
    value.failureRateSource = ObservationSource::Ocr;
    value.auxiliarySource = ObservationSource::TemplateMatch;
    return value;
}

UraTrainingMarkerEvidence Markers(std::optional<std::string> raised = std::nullopt)
{
    UraTrainingMarkerEvidence evidence;
    evidence.trainingSelectionScreen = true;
    evidence.raisedTrainingType = std::move(raised);
    for (const auto* type : {"speed", "stamina", "power", "guts", "wit"})
        evidence.matches.push_back({type, true, 0.96});
    return evidence;
}

void SubmitStableFullCardSet(UraTrainingScanner& scanner, int speedOverride = 30)
{
    const std::vector<TrainingCandidate> first = {
        Candidate("speed", speedOverride), Candidate("stamina", 10), Candidate("power", 12),
        Candidate("guts", 8), Candidate("wit", 5)};
    for (const auto& candidate : first)
        REQUIRE(scanner.SubmitCandidateSample(candidate));
    for (const auto& candidate : first)
        REQUIRE(scanner.SubmitCandidateSample(candidate));
}

TEST_CASE("URA scan requires stable double samples for all five cards", "[UraTrainingScanner]")
{
    UraTrainingScanner scanner;
    auto markers = Markers();
    REQUIRE(scanner.BeginCandidateScan(markers, RaceDistanceBand::Mile, "Classic Year", 60, 11) ==
           UraTrainingScannerStage::SamplingCandidates);
    SubmitStableFullCardSet(scanner);
    REQUIRE(scanner.Stage() == UraTrainingScannerStage::ReadyToSelect);
    REQUIRE(scanner.Candidates().size() == 5);
    REQUIRE(scanner.Decision().IsSafe());
    REQUIRE(scanner.SelectedTrainingType() == std::optional<std::string>{"speed"});
    REQUIRE(!scanner.CanTapTraining());

    // A scan with one changed OCR value excludes that card while preserving other safe candidates.
    UraTrainingScanner unstable;
    REQUIRE(unstable.BeginCandidateScan(markers, RaceDistanceBand::Mile, "Classic Year", 60) ==
           UraTrainingScannerStage::SamplingCandidates);
    const auto cards = std::vector<TrainingCandidate>{Candidate("speed", 30), Candidate("stamina", 10),
        Candidate("power", 12), Candidate("guts", 8), Candidate("wit", 5)};
    for (const auto& candidate : cards)
        REQUIRE(unstable.SubmitCandidateSample(candidate));
    for (const auto& candidate : cards)
    {
        auto second = candidate;
        if (second.trainingType == "speed")
            second.speedGain = 31;
        REQUIRE(unstable.SubmitCandidateSample(second));
    }
    REQUIRE(unstable.Stage() == UraTrainingScannerStage::ReadyToSelect);
    REQUIRE(unstable.Candidates().size() == 5);
    REQUIRE(unstable.Decision().IsSafe());
    REQUIRE(unstable.SelectedTrainingType() != std::optional<std::string>{"speed"});
    REQUIRE(!unstable.CanTapTraining());
    const auto unstableSpeed = std::find_if(
        unstable.Decision().allEvaluations.begin(), unstable.Decision().allEvaluations.end(),
        [](const auto& evaluation) { return evaluation.first.trainingType == "speed"; });
    REQUIRE(unstableSpeed != unstable.Decision().allEvaluations.end());
    REQUIRE(unstableSpeed->second.IsExcluded());
}

TEST_CASE("URA scored winner is ready for execution without further samples", "[UraTrainingScanner]")
{
    UraTrainingScanner scanner;
    REQUIRE_FALSE(scanner.PrepareTrainingTap());
    REQUIRE(scanner.BeginCandidateScan(Markers("power"), RaceDistanceBand::Middle, "Senior Year", 72, 20) ==
           UraTrainingScannerStage::SamplingCandidates);
    REQUIRE_FALSE(scanner.PrepareTrainingTap());
    SubmitStableFullCardSet(scanner);
    REQUIRE(scanner.SelectedTrainingType() == std::optional<std::string>{"speed"});
    REQUIRE(scanner.PrepareTrainingTap());
    REQUIRE(scanner.Stage() == UraTrainingScannerStage::ReadyToTap);
    REQUIRE(scanner.CanTapTraining());
    REQUIRE_FALSE(scanner.PrepareTrainingTap());
    REQUIRE(scanner.MarkTrainingTapSent());
    REQUIRE_FALSE(scanner.CanTapTraining());
    REQUIRE_FALSE(scanner.MarkTrainingTapSent());
    REQUIRE_FALSE(scanner.PrepareTrainingTap());
}

TEST_CASE("URA training commits only after observed outcome and only once", "[UraTrainingScanner]")
{
    UraTrainingScanner scanner;
    REQUIRE(scanner.BeginCandidateScan(Markers(), RaceDistanceBand::Middle, "Classic Year", 70, 3) ==
           UraTrainingScannerStage::SamplingCandidates);
    SubmitStableFullCardSet(scanner);
    REQUIRE(scanner.PrepareTrainingTap());
    REQUIRE(scanner.MarkTrainingTapSent());

    auto sameTurn = ObservedValue<int>::Make(3, 0.99, ObservationSource::Ocr);
    REQUIRE(!scanner.ObserveOutcome(CareerScreenKind::MainTurn, sameTurn));
    REQUIRE(!scanner.ObserveOutcome(CareerScreenKind::Unknown, ObservedValue<int>::Unknown()));
    const auto resetTurn = ObservedValue<int>::Make(2, 0.96, ObservationSource::Ocr);
    REQUIRE(!scanner.ObserveOutcome(CareerScreenKind::MainTurn, resetTurn));

    auto nextTurn = ObservedValue<int>::Make(4, 0.95, ObservationSource::Ocr);
    const auto commit = scanner.ObserveOutcome(CareerScreenKind::MainTurn, nextTurn);
    REQUIRE(commit.has_value());
    REQUIRE(commit->trainingType == "speed");
    REQUIRE(commit->turnIndex == 4);
    REQUIRE(scanner.Stage() == UraTrainingScannerStage::Committed);
    REQUIRE(!scanner.ObserveOutcome(CareerScreenKind::TrainingResult, nextTurn));

    UraTrainingScanner resultScreen;
    REQUIRE(resultScreen.BeginCandidateScan(Markers(), RaceDistanceBand::Mile, "Junior Year", 60) ==
           UraTrainingScannerStage::SamplingCandidates);
    SubmitStableFullCardSet(resultScreen);
    REQUIRE(resultScreen.PrepareTrainingTap());
    REQUIRE(resultScreen.MarkTrainingTapSent());
    const auto resultCommit = resultScreen.ObserveOutcome(
        CareerScreenKind::TrainingResult, ObservedValue<int>::Unknown());
    REQUIRE(resultCommit.has_value());
    REQUIRE(resultCommit->trainingType == "speed");
    REQUIRE(!resultCommit->turnIndex.has_value());
}

TEST_CASE("URA incomplete or unsafe scans cannot authorize a training tap", "[UraTrainingScanner]")
{
    UraTrainingScanner scanner;
    auto incomplete = Markers();
    incomplete.matches.pop_back();
    REQUIRE(scanner.BeginCandidateScan(incomplete, RaceDistanceBand::Mile, "Junior", 80) ==
           UraTrainingScannerStage::Fallback);
    REQUIRE(!scanner.CanTapTraining());
    REQUIRE_FALSE(scanner.PrepareTrainingTap());

    UraTrainingScanner risky;
    REQUIRE(risky.BeginCandidateScan(Markers(), RaceDistanceBand::Mile, "Junior", 80) ==
           UraTrainingScannerStage::SamplingCandidates);
    const std::vector<TrainingCandidate> cards = {
        Candidate("speed", 30, 8), Candidate("stamina", 10, 10), Candidate("power", 12, 6),
        Candidate("guts", 8, 15), Candidate("wit", 5, 6)};
    for (const auto& candidate : cards)
        REQUIRE(risky.SubmitCandidateSample(candidate));
    for (const auto& candidate : cards)
        REQUIRE((risky.SubmitCandidateSample(candidate) ||
            risky.Stage() == UraTrainingScannerStage::Fallback));
    REQUIRE(risky.Stage() == UraTrainingScannerStage::Fallback);
    REQUIRE(!risky.CanTapTraining());
    REQUIRE_FALSE(risky.PrepareTrainingTap());
}

} // namespace


