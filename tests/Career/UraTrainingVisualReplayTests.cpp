#include <catch2/catch_test_macros.hpp>
#include <catch2/generators/catch_generators.hpp>
#include "UmaAssistantCore/Career/Scenarios/Ura/UraTrainingVisualScanner.hpp"
#include "UmaAssistantCore/Career/Scenarios/Ura/UraTrainingFlowAdapter.hpp"
#include "UmaAssistantCore/Career/CareerActionExecutor.hpp"
#include "UmaAssistantCore/CareerDomain/Scenarios/Ura/UraSmartTrainingStrategy.hpp"
#include "UmaAssistantCore/CareerDomain/Scenarios/Ura/UraScenarioModule.hpp"
#include "UmaAssistantCore/Common/PathEncoding.hpp"
#include "UmaAssistantCore/Resources/ResourceSnapshot.hpp"
#include "UmaAssistantCore/Resources/WicImageDecoder.hpp"
#include "UmaAssistantCore/Recognition/WindowsOcrRecognizer.hpp"
#include "../TaskEngine/MockTaskExecutionContext.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <filesystem>
#include <memory>
#include <string>
#include <unordered_map>
#include <vector>

#if defined(_MSC_VER) && defined(_DEBUG)
#include <crtdbg.h>
#endif

namespace
{
using namespace UmaAssistant;
using namespace TaskEngine;
using namespace CareerDomain::Ura;

struct FixtureValues
{
    std::array<int, 6> gains;
    int failure;
};

// Values are annotated in the supplied capture README, independently of OCR.
const std::unordered_map<std::string, FixtureValues> FixtureExpected{
    {"speed", {{19, 0, 9, 0, 0, 6}, 0}},
    {"stamina", {{0, 13, 0, 7, 0, 4}, 0}},
    {"power", {{0, 10, 13, 0, 0, 5}, 0}},
    {"guts", {{12, 0, 11, 15, 0, 7}, 0}},
    {"wit", {{4, 0, 0, 0, 17, 8}, 0}},
};

class TrainingCaptureContext final : public Tests::MockTaskExecutionContext
{
public:
    explicit TrainingCaptureContext(std::string selected, bool duel = false, bool windowsOcr = false)
        : selectedType(std::move(selected)), duelWit(duel), realOcr(windowsOcr),
          resources(std::filesystem::path(CMAKE_SOURCE_DIR) / "resource") {}

    bool CaptureScreen() override
    {
        if (IsCancelled()) return false;
        const auto typeCapture = ++perTypeCaptures[selectedType];
        const auto sample = identicalSamples || typeCapture % 2 == 1 ? 1 : 2;
        const auto name = selectedType == "wit" && duelWit
            ? "wit-duel-" + std::to_string(sample) : selectedType + "-" + std::to_string(sample);
        image = Resources::WicImageDecoder::decode_file(
            std::filesystem::path(CMAKE_SOURCE_DIR) / "testdata/hachimi/ura/captures/smart_runtime_20261006" / (name + ".png"));
        if (changeWinnerSpeed && selectedType == "speed")
        {
            // Change only a color byte inside the speed ROI. Even identical
            // grayscale/prepared glyphs must not reuse the prior raw crop.
            const auto offset = (static_cast<std::size_t>(965) * image.width + 25) * 4;
            image.rgba.at(offset) ^= 1;
        }
        auto next = std::make_shared<TaskFrameSnapshot>();
        next->width = image.width;
        next->height = image.height;
        next->frameId = static_cast<std::uint64_t>(++captureCount);
        next->sessionGeneration = 1;
        next->geometryGeneration = geometryGeneration;
        next->grayPixels = image.gray;
        next->rgbaPixels = image.rgba;
        frame = std::move(next);
        capturedTypes.push_back(selectedType);
        ocrCallsThisFrame = 0;
        pendingWindowsFields.clear();
        fullLogoSearchesThisFrame.clear();
        if (cancelAtCapture == captureCount) cancelled = true;
        return !cancelled;
    }

    TaskMatchResult MatchTemplateAdvanced(const std::string& path, double threshold,
        const std::optional<TaskRoi>& roi, const std::string& algorithm,
        int referenceWidth, int referenceHeight, const std::vector<double>& scales) override
    {
        if (path.find("training_selection_logos") != std::string::npos && roi)
        {
            if (roi->height < 200 && !fullLogoSearchesThisFrame[path])
            {
                ++smallLogoSearches;
                if (failSmallLogoSearch) return {};
            }
            else if (roi->height >= 200)
            {
                ++fullLogoSearches;
                fullLogoSearchesThisFrame[path] = true;
            }
        }
        const auto templ = LoadTemplateImage(path);
        if (!templ || !frame) return {};
        Vision::Roi search;
        if (roi) search = std::array<std::int32_t, 4>{roi->x, roi->y, roi->width, roi->height};
        Vision::MatchResult match;
        if (algorithm == "MatchTemplateExhaustiveColor")
            match = Vision::find_exhaustive_color(image, *templ, search, threshold, referenceWidth, referenceHeight);
        else if (algorithm == "MatchTemplateColor" || algorithm == "MatchTemplateColorText")
            match = Vision::find_color(image, *templ, search, threshold, referenceWidth, referenceHeight,
                algorithm == "MatchTemplateColorText");
        else if (!scales.empty())
        {
            Vision::ScaledOptions options;
            options.scales = scales;
            match = Vision::find_scaled(image, *templ, search, threshold, referenceWidth, referenceHeight, options);
        }
        else
            match = Vision::find(image, *templ, search, threshold, referenceWidth, referenceHeight);
        auto scale = [](int value, int source, int target) {
            return static_cast<int>(std::nearbyint(static_cast<double>(value) * target / source));
        };
        return {match.found, match.score,
            scale(match.x, image.width, referenceWidth), scale(match.y, image.height, referenceHeight),
            scale(match.width, image.width, referenceWidth), scale(match.height, image.height, referenceHeight),
            referenceWidth, referenceHeight, frame->frameId, frame->sessionGeneration, frame->geometryGeneration};
    }

    std::shared_ptr<const Vision::GrayImage> LoadTemplateImage(const std::string& path) override
    {
        auto prepared = resources.load_template(path, Resources::TemplatePreparation::original);
        return prepared ? std::shared_ptr<const Vision::GrayImage>(prepared, &prepared->image) : nullptr;
    }
    std::shared_ptr<const TaskFrameSnapshot> GetCurrentFrame() const override { return frame; }

    bool TapMatch(int x, int y, const TaskMatchResult& match) override
    {
        REQUIRE(frame != nullptr);
        REQUIRE(match.frameId == frame->frameId);
        REQUIRE(match.sessionGeneration == frame->sessionGeneration);
        REQUIRE(match.geometryGeneration == frame->geometryGeneration);
        const std::array<std::string, 5> types{"speed", "stamina", "power", "guts", "wit"};
        const std::array<int, 5> columns{110, 270, 430, 590, 750};
        auto nearest = std::min_element(columns.begin(), columns.end(), [x](int a, int b) {
            return std::abs(a - x) < std::abs(b - x);
        });
        const auto targetType = types[static_cast<std::size_t>(nearest - columns.begin())];
        CAPTURE(x, y, match.x, match.y, match.referenceWidth, match.referenceHeight, selectedType, targetType);
        if (targetType == selectedType)
        {
            finalTrainingTypes.push_back(selectedType);
            return true;
        }
        // Preview/selection taps use the lower, unselected card.
        CHECK(y > 1300);
        selectedType = targetType;
        previewTypes.push_back(selectedType);
        return true;
    }

    std::vector<TaskOcrDetection> RecognizeImageText(const Vision::GrayImage& prepared, const std::string& language) override
    {
        ++ocrCallsThisFrame;
        if (realOcr)
        {
            std::vector<TaskOcrDetection> results;
            for (const auto& detection : ocr.recognize(prepared, language).detections)
                results.push_back({detection.text, detection.bounds.x, detection.bounds.y,
                    detection.bounds.width, detection.bounds.height, detection.confidence});
            return results;
        }
        // Frozen OCR words are associated with supplied numeric rows; actual
        // WIC pixels, glyph preprocessing, blank detection and validation run.
        // The auxiliary level crop is a separate OCR call, not the gain batch.
        if (prepared.height <= 100) return {};
        if (ocrCallsThisFrame != 1) return {};
        std::vector<std::string> words;
        for (const auto& field : pendingWindowsFields)
            words.push_back(field == "failure_rate" ? std::to_string(FieldValue(field)) + "%"
                : "+" + std::to_string(FieldValue(field)));
        if (words.empty()) return {};
        REQUIRE(prepared.height % static_cast<int>(words.size()) == 0);
        const auto rowHeight = prepared.height / static_cast<int>(words.size());
        std::vector<TaskOcrDetection> results;
        for (std::size_t i = 0; i < words.size(); ++i)
        {
            if (failFirstSpeed && selectedType == "speed" && perTypeCaptures[selectedType] == 1
                && pendingWindowsFields[i] == "speed") continue;
            const double confidence = lowConfidenceFirstSpeed && selectedType == "speed"
                && perTypeCaptures[selectedType] == 1 && pendingWindowsFields[i] == "speed" ? 0.79 : 1.0;
            results.push_back({words[i], 24, static_cast<int>(i) * rowHeight + 24,
                50, 20, confidence});
        }
        return results;
    }

    void Log(const std::string& level, const std::string& message) override
    {
        Tests::MockTaskExecutionContext::Log(level, message);
        if (changeWinnerAfterScan && message.starts_with("Smart training candidate "))
            changeWinnerSpeed = true;
    }

    void Sleep(int) override {}
    Recognition::NumericOcrBatchResult RecognizeNumericImages(
        const std::vector<Recognition::NumericOcrBatchField>& fields) override
    {
        ++numericBatchCalls;
        Recognition::NumericOcrBatchResult result;
        for (const auto& field : fields)
        {
            ++numericFieldCalls[selectedType + "." + field.key];
            const bool unreadable = selectedType == "speed" && field.key == "speed"
                && perTypeCaptures[selectedType] == 1 && (failFirstSpeed || lowConfidenceFirstSpeed);
            if (frozenNumeric && !unreadable)
                result[field.key].value = FieldValue(field.key);
            else
                pendingWindowsFields.push_back(field.key);
        }
        if (realOcr)
        {
            auto actual = numericOcr.read(fields);
            for (const auto& [key, reading] : actual)
                numericDiagnostics.push_back(selectedType + "." + key + ": "
                    + (reading.value ? std::to_string(*reading.value) : "unknown")
                    + "; raw=" + reading.rawText + "; failure=" + reading.failureReason);
            return actual;
        }
        return result;
    }
    bool IsCancelled() const override { return cancelled; }
    std::string selectedType;
    std::vector<std::string> capturedTypes;
    std::vector<std::string> previewTypes;
    std::vector<std::string> finalTrainingTypes;
    int cancelAtCapture{-1};
    bool identicalSamples{false};
    bool frozenNumeric{false};
    bool changeWinnerSpeed{false};
    bool changeWinnerAfterScan{false};
    bool failSmallLogoSearch{false};
    bool lowConfidenceFirstSpeed{false};
    bool failFirstSpeed{false};
    std::uint64_t geometryGeneration{1};
    int numericBatchCalls{0};
    int smallLogoSearches{0};
    int fullLogoSearches{0};
    std::unordered_map<std::string, int> numericFieldCalls;
    std::vector<std::string> numericDiagnostics;

private:
    int FieldValue(const std::string& field) const
    {
        auto values = FixtureExpected.at(selectedType);
        if (selectedType == "wit" && duelWit) values.gains = {4, 0, 0, 0, 13, 6};
        if (selectedType == "speed" && changeWinnerSpeed) values.gains[0] = 20;
        if (field == "failure_rate") return values.failure;
        const std::array<std::string, 6> names{"speed", "stamina", "power", "guts", "wit", "skill_points"};
        const auto found = std::find(names.begin(), names.end(), field);
        REQUIRE(found != names.end());
        return values.gains[static_cast<std::size_t>(found - names.begin())];
    }

    bool duelWit;
    bool realOcr;
    bool cancelled{false};
    int ocrCallsThisFrame{0};
    std::vector<std::string> pendingWindowsFields;
    std::unordered_map<std::string, bool> fullLogoSearchesThisFrame;
    std::unordered_map<std::string, int> perTypeCaptures;
    Resources::ResourceSnapshot resources;
    Vision::GrayImage image;
    std::shared_ptr<const TaskFrameSnapshot> frame;
    Recognition::WindowsOcrRecognizer ocr;
    Recognition::NumericOcrBatch numericOcr;
};

std::shared_ptr<Career::CareerVisualResources> TrainingResources()
{
    Career::CareerRuntimeConfig config;
    config.resourceDirectory = std::filesystem::path(CMAKE_SOURCE_DIR) / "resource";
    config.manifestPath = Paths::ToUtf8(config.resourceDirectory / "hachimi/ura/manifest.json");
    auto resources = std::make_shared<Career::CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    return resources;
}
}

TEST_CASE("URA visual scan reads captured five-card evidence for scoring", "[Career][Vision][Replay]")
{
    const auto initial = GENERATE(std::string{"speed"}, std::string{"guts"}, std::string{"wit"});
    TrainingCaptureContext context(initial);
    Career::Ura::UraTrainingVisualScanner visual(TrainingResources());
    UraTrainingScanner scanner;
    auto scan = visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 17);
    CAPTURE(initial, scan.detail);
    REQUIRE(scan.IsReadyToSelect());
    REQUIRE(scanner.Candidates().size() == 5);
    CHECK(context.capturedTypes.front() == initial);
    for (const auto& candidate : scanner.Candidates())
    {
        const auto expected = FixtureExpected.at(candidate.trainingType);
        CHECK((std::array<std::optional<int>, 6>{candidate.speedGain, candidate.staminaGain,
            candidate.powerGain, candidate.gutsGain, candidate.witGain, candidate.skillPointGain}
            == std::array<std::optional<int>, 6>{expected.gains[0], expected.gains[1], expected.gains[2],
                expected.gains[3], expected.gains[4], expected.gains[5]}));
        CHECK(candidate.failureRatePercent == expected.failure);
        CHECK(candidate.HasReliableCoreValues());
        CHECK(candidate.HasReliableFailureRate());
        CHECK(std::count(context.capturedTypes.begin(), context.capturedTypes.end(), candidate.trainingType) == 2);
    }
    CHECK(context.capturedTypes.size() == 10);
    CHECK(context.previewTypes.size() == 4);
    CHECK_FALSE(scanner.CanTapTraining());
}

TEST_CASE("Smart scan evidence expires when pre-debut countdown or committed actions change",
    "[Career][Vision][Replay][SmartRestRegression]")
{
    TrainingCaptureContext context("speed");
    Career::Ura::UraTrainingFlowAdapter flow(TrainingResources());
    CareerDomain::CareerState state;
    state.energy = CareerDomain::ObservedValue<int>::Make(100);
    state.turnIndexEvidence = CareerDomain::ObservedValue<int>::Make(0);
    state.turnsToGoal = CareerDomain::ObservedValue<int>::Make(8);
    state.turnPositionText = "Junior Year Pre-Debut";
    CareerDomain::UraScenarioModule scenario;
    CareerDomain::UraSmartTrainingStrategy strategy(RaceDistanceBand::Unknown);
    auto const result = flow.Select(context, state, scenario, strategy);
    CAPTURE(result.error);
    REQUIRE(result.status == Career::CareerTrainingSelectionStatus::Ready);
    REQUIRE(flow.HasCandidatesForCurrentTurn(state));
    state.confirmedTurnActions = 1;
    CHECK_FALSE(flow.HasCandidatesForCurrentTurn(state));
    state.confirmedTurnActions = 0;
    state.turnsToGoal = CareerDomain::ObservedValue<int>::Make(7);
    CHECK_FALSE(flow.HasCandidatesForCurrentTurn(state));
    state.turnsToGoal = CareerDomain::ObservedValue<int>::Make(8);
    CHECK(flow.HasCandidatesForCurrentTurn(state));
    state.turnIndexEvidence = CareerDomain::ObservedValue<int>::Make(1);
    CHECK_FALSE(flow.HasCandidatesForCurrentTurn(state));
    flow.Reset();
    CHECK_FALSE(flow.HasCandidatesForCurrentTurn(state));
}

TEST_CASE("URA scan reuses identical OCR crops while retaining fresh two-frame evidence",
    "[Career][Vision][Replay][TrainingScanCache]")
{
    TrainingCaptureContext context("speed");
    context.identicalSamples = true;
    context.frozenNumeric = GENERATE(false, true);
    Career::Ura::UraTrainingVisualScanner visual(TrainingResources());
    UraTrainingScanner scanner;
    const auto scan = visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 17);
    CAPTURE(scan.detail, context.frozenNumeric);
    REQUIRE(scan.IsReadyToSelect());
    REQUIRE(scanner.SelectedTrainingType() == std::optional<std::string>{"speed"});
    CHECK(context.capturedTypes.size() == 10);
    CHECK(context.numericBatchCalls == 5);
    CHECK(context.numericFieldCalls.at("speed.speed") == 1);
    CHECK(context.smallLogoSearches > 0);
}

TEST_CASE("URA flow hands the scored training directly to execution without winner OCR or preview taps",
    "[Career][Vision][Replay][TrainingDirectExecution]")
{
    const auto initial = GENERATE(std::string{"speed"}, std::string{"guts"}, std::string{"wit"});
    TrainingCaptureContext context(initial);
    context.identicalSamples = true;
    context.frozenNumeric = true;
    context.changeWinnerAfterScan = true;
    Career::Ura::UraTrainingFlowAdapter flow(TrainingResources());
    CareerDomain::CareerState state;
    state.energy = CareerDomain::ObservedValue<int>::Make(58);
    state.turnIndexEvidence = CareerDomain::ObservedValue<int>::Make(17);
    state.turnPositionText = "Classic Year";
    CareerDomain::UraScenarioModule scenario;
    CareerDomain::UraSmartTrainingStrategy strategy(RaceDistanceBand::Middle);
    const auto result = flow.Select(context, state, scenario, strategy);
    CAPTURE(initial, result.error);
    REQUIRE(result.status == Career::CareerTrainingSelectionStatus::Ready);
    CHECK(result.intent.kind == CareerDomain::ActionIntentKind::Training);
    CHECK(result.intent.trainingType == "speed");
    CHECK(flow.HasPendingConfirmedTap());
    CHECK(context.changeWinnerSpeed);
    CHECK(context.capturedTypes.size() == 10);
    CHECK(context.previewTypes.size() == 4);
    CHECK(context.selectedType == (initial == "wit" ? "guts" : "wit"));
    CHECK(context.numericBatchCalls == 5);
    CHECK(context.numericFieldCalls.at("speed.speed") == 1);
    Career::CareerActionExecutor executor(TrainingResources());
    CareerDomain::CareerObservation observation;
    observation.screenId = "training_selection";
    REQUIRE(executor.ExecuteForObservation(result.intent, observation, context) == Career::ActionExecutionStatus::Success);
    CHECK(context.finalTrainingTypes == std::vector<std::string>{"speed"});
    CHECK(context.previewTypes.size() == 5);
    CHECK(context.previewTypes.back() == "speed");
    CHECK(context.numericBatchCalls == 5);
    REQUIRE(flow.MarkTrainingTapSent());
    CHECK(flow.HasPendingOutcome());
    CHECK_FALSE(flow.HasPendingConfirmedTap());
}

TEST_CASE("URA executor selects normal training cards and clicks raised cards directly", "[Career][Vision][Replay][CareerTrainingAction]")
{
    const auto type = GENERATE(std::string{"speed"}, std::string{"stamina"}, std::string{"power"},
        std::string{"guts"}, std::string{"wit"});
    const bool alreadyRaised = GENERATE(false, true);
    TrainingCaptureContext context(alreadyRaised ? type : (type == "speed" ? "wit" : "speed"));
    REQUIRE(context.CaptureScreen());
    Career::CareerActionExecutor executor(TrainingResources());
    CareerDomain::CareerObservation observation;
    observation.screenId = "training_selection";
    REQUIRE(executor.ExecuteForObservation(CareerDomain::ActionIntent::MakeTraining(type), observation, context)
        == Career::ActionExecutionStatus::Success);
    if (alreadyRaised)
        CHECK(context.previewTypes.empty());
    else
        CHECK(context.previewTypes == std::vector<std::string>{type});
    CHECK(context.finalTrainingTypes == std::vector<std::string>{type});
    CHECK(context.numericBatchCalls == 0);
}

TEST_CASE("URA OCR cache retries unknown and low confidence readings",
    "[Career][Vision][Replay][TrainingScanCache]")
{
    TrainingCaptureContext context("speed");
    context.identicalSamples = true;
    context.frozenNumeric = true;
    context.lowConfidenceFirstSpeed = GENERATE(false, true);
    context.failFirstSpeed = !context.lowConfidenceFirstSpeed;
    Career::Ura::UraTrainingVisualScanner visual(TrainingResources());
    UraTrainingScanner scanner;
    const auto scan = visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 17);
    CAPTURE(scan.detail, context.lowConfidenceFirstSpeed);
    REQUIRE(scan.IsReadyToSelect());
    CHECK(context.numericFieldCalls.at("speed.speed") == 2);
    CHECK(context.numericFieldCalls.at("speed.power") == 1);
    REQUIRE(scanner.SelectedTrainingType().has_value());
    CHECK(*scanner.SelectedTrainingType() != "speed");
}

TEST_CASE("URA OCR cache starts empty on a new scan even with identical rendered values",
    "[Career][Vision][Replay][TrainingScanCache]")
{
    TrainingCaptureContext context("speed");
    context.identicalSamples = true;
    context.frozenNumeric = true;
    Career::Ura::UraTrainingVisualScanner visual(TrainingResources());
    UraTrainingScanner scanner;
    REQUIRE(visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 17).IsReadyToSelect());
    CHECK(context.numericBatchCalls == 5);
    REQUIRE(visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 18).IsReadyToSelect());
    CHECK(context.numericBatchCalls == 10);
    CHECK(context.numericFieldCalls.at("speed.speed") == 2);
}

TEST_CASE("URA local logo search falls back to the authored region on a miss",
    "[Career][Vision][Replay][TrainingScanCache]")
{
    TrainingCaptureContext context("speed");
    context.identicalSamples = true;
    context.frozenNumeric = true;
    context.failSmallLogoSearch = true;
    Career::Ura::UraTrainingVisualScanner visual(TrainingResources());
    UraTrainingScanner scanner;
    const auto scan = visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 17);
    CAPTURE(scan.detail);
    REQUIRE(scan.IsReadyToSelect());
    CHECK(context.smallLogoSearches > 0);
    CHECK(context.fullLogoSearches >= context.smallLogoSearches + 5);
}

TEST_CASE("URA visual scan uses selected chevron when a Duel badge distracts icon matching", "[Career][Vision][Replay]")
{
    TrainingCaptureContext context("wit", true);
    Career::Ura::UraTrainingVisualScanner visual(TrainingResources());
    UraTrainingScanner scanner;
    const auto scan = visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 17);
    CAPTURE(scan.detail);
    REQUIRE(scan.IsReadyToSelect());
    const auto wit = std::find_if(scanner.Candidates().begin(), scanner.Candidates().end(), [](const auto& candidate) {
        return candidate.trainingType == "wit";
    });
    REQUIRE(wit != scanner.Candidates().end());
    CHECK(wit->speedGain == 4);
    CHECK(wit->witGain == 13);
    CHECK(wit->skillPointGain == 6);
}

TEST_CASE("URA visual scan cancellation prevents a final training authorization", "[Career][Vision][Replay]")
{
    TrainingCaptureContext context("speed");
    context.cancelAtCapture = 2;
    Career::Ura::UraTrainingVisualScanner visual(TrainingResources());
    UraTrainingScanner scanner;
    const auto scan = visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 17);
    CHECK_FALSE(scan.IsReadyToSelect());
    CHECK_FALSE(scanner.CanTapTraining());
    CHECK(context.previewTypes.empty());
}

TEST_CASE("URA visual scan preserves actual numeric OCR on the captured five-card set", "[Career][Vision][WindowsOcr]")
{
#if defined(_MSC_VER) && defined(_DEBUG)
    // Unattended Debug replays must report CRT assertions to the test log,
    // instead of blocking indefinitely on a modal assertion dialog.
    struct AssertionReporting
    {
        int mode{_CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_FILE)};
        _HFILE file{_CrtSetReportFile(_CRT_ASSERT, _CRTDBG_FILE_STDERR)};
        ~AssertionReporting()
        {
            _CrtSetReportFile(_CRT_ASSERT, file);
            _CrtSetReportMode(_CRT_ASSERT, mode);
        }
    } assertionReporting;
#endif
    TrainingCaptureContext context("speed", false, true);
    Career::Ura::UraTrainingVisualScanner visual(TrainingResources());
    UraTrainingScanner scanner;
    const auto scan = visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 17);
    std::vector<std::string> evidence;
    for (const auto& candidate : scanner.Candidates())
    {
        auto number = [](const std::optional<int>& value) { return value ? std::to_string(*value) : "unknown"; };
        evidence.push_back(candidate.trainingType + ": " + number(candidate.speedGain) + "/"
            + number(candidate.staminaGain) + "/" + number(candidate.powerGain) + "/"
            + number(candidate.gutsGain) + "/" + number(candidate.witGain) + "/"
            + number(candidate.skillPointGain) + "; failure=" + number(candidate.failureRatePercent));
    }
    CAPTURE(evidence);
    CAPTURE(context.numericDiagnostics);
    CAPTURE(scan.detail);
    REQUIRE(scan.IsReadyToSelect());
    REQUIRE(scanner.Candidates().size() == 5);
    for (const auto& candidate : scanner.Candidates())
    {
        CAPTURE(candidate.trainingType);
        const auto expected = FixtureExpected.at(candidate.trainingType);
        CHECK((std::array<std::optional<int>, 6>{candidate.speedGain, candidate.staminaGain,
            candidate.powerGain, candidate.gutsGain, candidate.witGain, candidate.skillPointGain}
            == std::array<std::optional<int>, 6>{expected.gains[0], expected.gains[1], expected.gains[2],
                expected.gains[3], expected.gains[4], expected.gains[5]}));
        CHECK(candidate.failureRatePercent == expected.failure);
        CHECK(candidate.HasReliableCoreValues());
        CHECK(candidate.HasReliableFailureRate());
    }
    CHECK_FALSE(scanner.CanTapTraining());
}
