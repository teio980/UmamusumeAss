#include <catch2/catch_test_macros.hpp>
#include <catch2/generators/catch_generators.hpp>
#include "UmaAssistantCore/Career/Scenarios/Ura/UraTrainingVisualScanner.hpp"
#include "UmaAssistantCore/Career/Scenarios/Ura/UraTrainingFlowAdapter.hpp"
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
        const auto sample = (++perTypeCaptures[selectedType] % 2 == 1) ? 1 : 2;
        const auto name = selectedType == "wit" && duelWit
            ? "wit-duel-" + std::to_string(sample) : selectedType + "-" + std::to_string(sample);
        image = Resources::WicImageDecoder::decode_file(
            std::filesystem::path(CMAKE_SOURCE_DIR) / "testdata/hachimi/ura/captures/smart_runtime_20261006" / (name + ".png"));
        auto next = std::make_shared<TaskFrameSnapshot>();
        next->width = image.width;
        next->height = image.height;
        next->frameId = static_cast<std::uint64_t>(++captureCount);
        next->sessionGeneration = 1;
        next->geometryGeneration = 1;
        next->grayPixels = image.gray;
        next->rgbaPixels = image.rgba;
        frame = std::move(next);
        capturedTypes.push_back(selectedType);
        ocrCallsThisFrame = 0;
        if (cancelAtCapture == captureCount) cancelled = true;
        return !cancelled;
    }

    TaskMatchResult MatchTemplateAdvanced(const std::string& path, double threshold,
        const std::optional<TaskRoi>& roi, const std::string& algorithm,
        int referenceWidth, int referenceHeight, const std::vector<double>& scales) override
    {
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
        // Preview taps use the lower, unselected card. Final confirmation is
        // deliberately absent from this visual-only flow.
        CHECK(y > 1300);
        const std::array<std::string, 5> types{"speed", "stamina", "power", "guts", "wit"};
        const std::array<int, 5> columns{110, 270, 430, 590, 750};
        auto nearest = std::min_element(columns.begin(), columns.end(), [x](int a, int b) {
            return std::abs(a - x) < std::abs(b - x);
        });
        selectedType = types[static_cast<std::size_t>(nearest - columns.begin())];
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
        if (ocrCallsThisFrame != 1) return {};
        auto values = FixtureExpected.at(selectedType);
        if (selectedType == "wit" && duelWit) values.gains = {4, 0, 0, 0, 13, 6};
        std::vector<std::string> words;
        for (int value : values.gains)
            if (value > 0) words.push_back("+" + std::to_string(value));
        words.push_back(std::to_string(values.failure) + "%");
        REQUIRE(prepared.height % static_cast<int>(words.size()) == 0);
        const auto rowHeight = prepared.height / static_cast<int>(words.size());
        std::vector<TaskOcrDetection> results;
        for (std::size_t i = 0; i < words.size(); ++i)
            results.push_back({words[i], 24, static_cast<int>(i) * rowHeight + 24,
                50, 20, 1.0});
        return results;
    }

    void Sleep(int) override {}
    Recognition::NumericOcrBatchResult RecognizeNumericImages(
        const std::vector<Recognition::NumericOcrBatchField>& fields) override
    {
        return realOcr ? numericOcr.read(fields) : Recognition::NumericOcrBatchResult{};
    }
    bool IsCancelled() const override { return cancelled; }
    std::string selectedType;
    std::vector<std::string> capturedTypes;
    std::vector<std::string> previewTypes;
    int cancelAtCapture{-1};

private:
    bool duelWit;
    bool realOcr;
    bool cancelled{false};
    int ocrCallsThisFrame{0};
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

TEST_CASE("URA visual scan reads captured five-card evidence and rechecks its winner", "[Career][Vision][Replay]")
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
    const auto confirmation = visual.ConfirmWinner(context, scanner);
    CAPTURE(confirmation.detail);
    CHECK(confirmation.IsReadyToTap());
    CHECK(scanner.CanTapTraining());
    CHECK(context.previewTypes.size() <= 5);
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
}
