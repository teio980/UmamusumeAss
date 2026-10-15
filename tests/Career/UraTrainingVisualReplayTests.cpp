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
#include "UmaAssistantCore/TaskEngine/NativeTaskExecutionContext.hpp"
#include "../TaskEngine/MockTaskExecutionContext.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <chrono>
#include <condition_variable>
#include <filesystem>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <stop_token>
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

struct OverlapTrace
{
    std::mutex mutex;
    std::condition_variable changed;
    bool blockFirst{true};
    bool holdUntilCancelled{false};
    bool releaseFirst{false};
    bool tappedWhileReading{false};
    bool tapWaitTimedOut{false};
    bool throwOnRead{false};
    int active{0};
    int maxActive{0};
    std::vector<std::string> reads;
    std::unordered_map<std::string, int> fieldCalls;
};

// A detached reader deliberately has no reference to the foreground context.
class FrozenTrainingRecognition final : public IImageRecognitionContext
{
public:
    FrozenTrainingRecognition(std::string type, FixtureValues values, std::vector<std::string> unknownFields,
        std::stop_token cancellation, std::shared_ptr<OverlapTrace> trace)
        : m_type(std::move(type)), m_values(values), m_unknownFields(std::move(unknownFields)),
          m_cancellation(cancellation), m_trace(std::move(trace)) {}

    std::vector<TaskOcrDetection> RecognizeImageText(const Vision::GrayImage&, const std::string&) override
    {
        return {};
    }

    Recognition::NumericOcrBatchResult RecognizeNumericImages(
        const std::vector<Recognition::NumericOcrBatchField>& fields) override
    {
        std::stop_callback wake(m_cancellation, [this] { m_trace->changed.notify_all(); });
        std::unique_lock lock(m_trace->mutex);
        const bool first = m_trace->reads.empty();
        m_trace->reads.push_back(m_type);
        ++m_trace->active;
        m_trace->maxActive = std::max(m_trace->maxActive, m_trace->active);
        struct Finish
        {
            OverlapTrace& trace;
            ~Finish() { --trace.active; }
        } finish{*m_trace};
        m_trace->changed.notify_all();
        if (first && m_trace->blockFirst)
        {
            const bool released = m_trace->changed.wait_for(lock, std::chrono::seconds(5), [&] {
                return IsCancelled() || (m_trace->releaseFirst && !m_trace->holdUntilCancelled);
            });
            if (!released) throw std::runtime_error("Foreground did not advance while OCR was pending.");
        }
        if (m_trace->throwOnRead) throw std::runtime_error("Injected image reader failure.");
        if (IsCancelled()) return {};
        Recognition::NumericOcrBatchResult result;
        const std::array<std::string, 6> names{"speed", "stamina", "power", "guts", "wit", "skill_points"};
        for (const auto& field : fields)
        {
            ++m_trace->fieldCalls[m_type + "." + field.key];
            if (std::find(m_unknownFields.begin(), m_unknownFields.end(), field.key) != m_unknownFields.end()) continue;
            if (field.key == "failure_rate") result[field.key].value = m_values.failure;
            else
            {
                const auto found = std::find(names.begin(), names.end(), field.key);
                if (found != names.end())
                    result[field.key].value = m_values.gains[static_cast<std::size_t>(found - names.begin())];
            }
        }
        return result;
    }

    bool IsCancelled() const override { return m_cancellation.stop_requested(); }

private:
    std::string m_type;
    FixtureValues m_values;
    std::vector<std::string> m_unknownFields;
    std::stop_token m_cancellation;
    std::shared_ptr<OverlapTrace> m_trace;
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
        if (geometryChangeAtCapture == captureCount + 1) ++geometryGeneration;
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
        if (cancelAtCapture == captureCount)
        {
            cancelled = true;
            recognitionCancellation.request_stop();
        }
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
        if (overlap)
        {
            std::unique_lock lock(overlap->mutex);
            if (!overlap->releaseFirst && overlap->blockFirst)
            {
                const bool started = overlap->changed.wait_for(lock, std::chrono::seconds(5), [&] {
                    return overlap->active > 0;
                });
                overlap->tapWaitTimedOut = !started;
                overlap->tappedWhileReading = started;
                overlap->releaseFirst = true;
                overlap->changed.notify_all();
            }
            if (failOverlapPreview) return false;
        }
        // Preview/selection taps use the lower, unselected card.
        CHECK(y > 1300);
        selectedType = targetType;
        previewTypes.push_back(selectedType);
        return true;
    }

    std::vector<TaskOcrDetection> RecognizeImageText(const Vision::GrayImage& prepared, const std::string& language) override
    {
        if (prepared.width == 470 && prepared.height == 75) ++unusedLevelOcrCalls;
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

    void Sleep(int) override {}
    std::shared_ptr<IImageRecognitionContext> CreateImageRecognitionContext() override
    {
        if (realOcr && detachedOcr)
        {
            if (!detachedReader)
            {
                NativeTaskExecutionContext native({}, {}, &ocr, nullptr, 0,
                    recognitionCancellation.get_token(), {});
                detachedReader = native.CreateImageRecognitionContext();
            }
            return detachedReader;
        }
        if (!overlap) return {};
        std::vector<std::string> unknown;
        if (selectedType == "speed" && ((perTypeCaptures[selectedType] == 1 && failFirstSpeed) || alwaysUnreadableSpeed))
            unknown.emplace_back("speed");
        if (selectedType == "speed" && perTypeCaptures[selectedType] >= 2 && missingRecheckedFailure)
            unknown.emplace_back("failure_rate");
        return std::make_shared<FrozenTrainingRecognition>(selectedType, CurrentValues(), std::move(unknown),
            recognitionCancellation.get_token(), overlap);
    }
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
    bool failSmallLogoSearch{false};
    bool lowConfidenceFirstSpeed{false};
    bool failFirstSpeed{false};
    std::uint64_t geometryGeneration{1};
    int numericBatchCalls{0};
    int unusedLevelOcrCalls{0};
    int smallLogoSearches{0};
    int fullLogoSearches{0};
    std::unordered_map<std::string, int> numericFieldCalls;
    std::vector<std::string> numericDiagnostics;
    std::shared_ptr<OverlapTrace> overlap;
    bool failOverlapPreview{false};
    bool detachedOcr{false};
    std::optional<int> recheckedSpeedGain;
    std::optional<int> recheckedSpeedFailure;
    bool oscillatingSpeed{false};
    bool missingRecheckedFailure{false};
    bool alwaysUnreadableSpeed{false};
    int geometryChangeAtCapture{-1};

private:
    FixtureValues CurrentValues() const
    {
        auto values = FixtureExpected.at(selectedType);
        if (selectedType == "wit" && duelWit) values.gains = {4, 0, 0, 0, 13, 6};
        const auto captured = perTypeCaptures.find(selectedType);
        if (selectedType == "speed" && captured != perTypeCaptures.end() && captured->second >= 2)
        {
            if (recheckedSpeedGain) values.gains[0] = *recheckedSpeedGain;
            if (oscillatingSpeed) values.gains[0] = captured->second % 2 == 0 ? 20 : 19;
            if (recheckedSpeedFailure) values.failure = *recheckedSpeedFailure;
        }
        return values;
    }

    int FieldValue(const std::string& field) const
    {
        const auto values = CurrentValues();
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
    std::stop_source recognitionCancellation;
    std::shared_ptr<IImageRecognitionContext> detachedReader;
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

TEST_CASE("URA single-frame scan reads five cards and freshly rechecks only its winner", "[Career][Vision][Replay][TrainingSingleFrame]")
{
    const auto initial = GENERATE(std::string{"speed"}, std::string{"guts"}, std::string{"wit"});
    TrainingCaptureContext context(initial);
    Career::Ura::UraTrainingVisualScanner visual(TrainingResources());
    UraTrainingScanner scanner;
    const auto scan = visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 17);
    CAPTURE(initial, scan.detail);
    REQUIRE(scan.IsReadyToSelect());
    REQUIRE(scanner.Candidates().size() == 5);
    CHECK(context.capturedTypes.front() == initial);
    CHECK(context.selectedType == "speed");
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
        CHECK_FALSE(candidate.trainingLevel.has_value());
        CHECK(std::count(context.capturedTypes.begin(), context.capturedTypes.end(), candidate.trainingType)
            == (candidate.trainingType == "speed" ? 2 : 1));
    }
    CHECK(context.capturedTypes.size() == 6);
    CHECK(context.unusedLevelOcrCalls == 0);
    CHECK(context.previewTypes.size() == 5);
    CHECK(context.finalTrainingTypes.empty());
    CHECK_FALSE(scanner.CanTapTraining());
    REQUIRE_FALSE(context.logs.empty());
    CHECK(context.logs.back().find("fresh_captures=6") != std::string::npos);
    CHECK(context.logs.back().find("retries=0, winner_checks=1") != std::string::npos);
}

TEST_CASE("URA scan switches cards while detached OCR reads the preceding snapshot",
    "[Career][Vision][Replay][TrainingScanOverlap]")
{
    const auto initial = GENERATE(std::string{"speed"}, std::string{"guts"}, std::string{"wit"});
    TrainingCaptureContext context(initial);
    context.overlap = std::make_shared<OverlapTrace>();
    Career::Ura::UraTrainingVisualScanner visual(TrainingResources());
    UraTrainingScanner scanner;
    const auto scan = visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 17);
    CAPTURE(initial, scan.detail);
    REQUIRE(scan.IsReadyToSelect());
    CHECK(context.overlap->tappedWhileReading);
    CHECK_FALSE(context.overlap->tapWaitTimedOut);
    CHECK(context.overlap->active == 0);
    CHECK(context.overlap->maxActive == 1);
    CHECK(context.overlap->reads.size() == 6);
    CHECK(context.capturedTypes.size() == 6);
    CHECK(context.previewTypes.size() == 5);
    CHECK(context.numericBatchCalls == 0);
    CHECK(context.finalTrainingTypes.empty());
    CHECK(scanner.SelectedTrainingType() == std::optional<std::string>{"speed"});
    for (const auto& candidate : scanner.Candidates())
    {
        const auto expected = FixtureExpected.at(candidate.trainingType);
        CHECK(candidate.speedGain == expected.gains[0]);
        CHECK(candidate.staminaGain == expected.gains[1]);
        CHECK(candidate.powerGain == expected.gains[2]);
        CHECK(candidate.gutsGain == expected.gains[3]);
        CHECK(candidate.witGain == expected.gains[4]);
        CHECK(candidate.skillPointGain == expected.gains[5]);
        CHECK(candidate.failureRatePercent == expected.failure);
        CHECK(context.overlap->fieldCalls.at(candidate.trainingType + ".failure_rate")
            == (candidate.trainingType == "speed" ? 2 : 1));
    }
}

TEST_CASE("URA scan joins pending recognition when preview fails or capture cancels",
    "[Career][Vision][Replay][TrainingScanOverlap]")
{
    const bool cancel = GENERATE(false, true);
    TrainingCaptureContext context("speed");
    context.overlap = std::make_shared<OverlapTrace>();
    context.overlap->holdUntilCancelled = cancel;
    context.failOverlapPreview = !cancel;
    if (cancel) context.cancelAtCapture = 2;
    Career::Ura::UraTrainingVisualScanner visual(TrainingResources());
    UraTrainingScanner scanner;
    const auto scan = visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 17);
    CHECK_FALSE(scan.IsReadyToSelect());
    CHECK_FALSE(scanner.CanTapTraining());
    CHECK(context.overlap->tappedWhileReading);
    CHECK(context.overlap->active == 0);
    CHECK(scanner.Candidates().empty());
    CHECK(context.finalTrainingTypes.empty());
}

TEST_CASE("URA scan reports a detached reader exception without authorizing training",
    "[Career][Vision][Replay][TrainingScanOverlap]")
{
    TrainingCaptureContext context("speed");
    context.overlap = std::make_shared<OverlapTrace>();
    context.overlap->throwOnRead = true;
    Career::Ura::UraTrainingVisualScanner visual(TrainingResources());
    UraTrainingScanner scanner;
    const auto scan = visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 17);
    CHECK_FALSE(scan.IsReadyToSelect());
    CHECK(scan.detail.find("Injected image reader failure") != std::string::npos);
    CHECK(context.overlap->active == 0);
    CHECK_FALSE(scanner.CanTapTraining());
    CHECK(context.finalTrainingTypes.empty());
}

TEST_CASE("URA winner recheck updates scores and restores and verifies the new winner",
    "[Career][Vision][Replay][TrainingSingleFrame]")
{
    TrainingCaptureContext context("speed");
    context.overlap = std::make_shared<OverlapTrace>();
    context.recheckedSpeedGain = 1;
    Career::Ura::UraTrainingVisualScanner visual(TrainingResources());
    UraTrainingScanner scanner;
    const auto scan = visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 17);
    CAPTURE(scan.detail);
    REQUIRE(scan.IsReadyToSelect());
    CHECK(scanner.SelectedTrainingType() == std::optional<std::string>{"guts"});
    CHECK(context.selectedType == "guts");
    CHECK(context.capturedTypes.size() == 7);
    CHECK(context.overlap->fieldCalls.at("speed.speed") == 2);
    CHECK(context.overlap->fieldCalls.at("guts.guts") == 2);
    const auto speed = std::find_if(scanner.Candidates().begin(), scanner.Candidates().end(),
        [](const auto& candidate) { return candidate.trainingType == "speed"; });
    REQUIRE(speed != scanner.Candidates().end());
    CHECK(speed->speedGain == 1);
    CHECK(context.finalTrainingTypes.empty());
}

TEST_CASE("URA changed winner values are reread even when identical pictures would have hit the old OCR cache",
    "[Career][Vision][Replay][TrainingSingleFrame]")
{
    TrainingCaptureContext context("speed");
    context.identicalSamples = true;
    context.frozenNumeric = true;
    context.recheckedSpeedGain = 20;
    Career::Ura::UraTrainingVisualScanner visual(TrainingResources());
    UraTrainingScanner scanner;
    const auto scan = visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 17);
    CAPTURE(scan.detail);
    REQUIRE(scan.IsReadyToSelect());
    CHECK(scanner.SelectedTrainingType() == std::optional<std::string>{"speed"});
    CHECK(scanner.Decision().chosenCandidate->speedGain == 20);
    CHECK(context.numericFieldCalls.at("speed.speed") == 3);
    CHECK(context.capturedTypes.size() == 7);
    CHECK(context.logs.back().find("winner_checks=2") != std::string::npos);
}

TEST_CASE("URA winner recheck excludes unreadable or unsafe failure rates",
    "[Career][Vision][Replay][TrainingSingleFrame]")
{
    const bool unreadable = GENERATE(false, true);
    TrainingCaptureContext context("speed");
    context.overlap = std::make_shared<OverlapTrace>();
    context.missingRecheckedFailure = unreadable;
    if (!unreadable) context.recheckedSpeedFailure = 6;
    Career::Ura::UraTrainingVisualScanner visual(TrainingResources());
    UraTrainingScanner scanner;
    const auto scan = visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 17);
    CAPTURE(scan.detail, unreadable);
    REQUIRE(scan.IsReadyToSelect());
    CHECK(scanner.SelectedTrainingType() == std::optional<std::string>{"guts"});
    CHECK(context.selectedType == "guts");
    CHECK(context.overlap->fieldCalls.at("guts.failure_rate") == 2);
    CHECK(context.finalTrainingTypes.empty());
}

TEST_CASE("URA unstable winner checks are bounded and cannot authorize training",
    "[Career][Vision][Replay][TrainingSingleFrame]")
{
    TrainingCaptureContext context("speed");
    context.frozenNumeric = true;
    context.oscillatingSpeed = true;
    Career::Ura::UraTrainingVisualScanner visual(TrainingResources());
    UraTrainingScanner scanner;
    const auto scan = visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 17);
    CHECK_FALSE(scan.IsReadyToSelect());
    CHECK(scan.detail.find("five winner checks") != std::string::npos);
    CHECK(context.capturedTypes.size() == 10);
    CHECK_FALSE(scanner.PrepareTrainingTap());
    CHECK(context.finalTrainingTypes.empty());
}

TEST_CASE("URA unreadable or low confidence cards get one fresh retry before scoring",
    "[Career][Vision][Replay][TrainingSingleFrame]")
{
    TrainingCaptureContext context("speed");
    context.frozenNumeric = true;
    context.lowConfidenceFirstSpeed = GENERATE(false, true);
    context.failFirstSpeed = !context.lowConfidenceFirstSpeed;
    Career::Ura::UraTrainingVisualScanner visual(TrainingResources());
    UraTrainingScanner scanner;
    const auto scan = visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 17);
    CAPTURE(scan.detail, context.lowConfidenceFirstSpeed);
    REQUIRE(scan.IsReadyToSelect());
    CHECK(scanner.SelectedTrainingType() == std::optional<std::string>{"speed"});
    CHECK(context.numericFieldCalls.at("speed.speed") == 3);
    CHECK(context.capturedTypes.size() == 7);
    CHECK(context.logs.back().find("retries=1, winner_checks=1") != std::string::npos);
}

TEST_CASE("URA persistently unreadable cards are retried once and excluded",
    "[Career][Vision][Replay][TrainingSingleFrame]")
{
    TrainingCaptureContext context("speed");
    context.overlap = std::make_shared<OverlapTrace>();
    context.alwaysUnreadableSpeed = true;
    Career::Ura::UraTrainingVisualScanner visual(TrainingResources());
    UraTrainingScanner scanner;
    const auto scan = visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 17);
    CAPTURE(scan.detail);
    REQUIRE(scan.IsReadyToSelect());
    CHECK(scanner.SelectedTrainingType() == std::optional<std::string>{"guts"});
    CHECK(context.overlap->fieldCalls.at("speed.speed") == 2);
    CHECK(context.logs.back().find("retries=1") != std::string::npos);
}

TEST_CASE("URA winner recheck rejects cancellation or changed display geometry",
    "[Career][Vision][Replay][TrainingSingleFrame]")
{
    const bool cancel = GENERATE(false, true);
    TrainingCaptureContext context("speed");
    context.frozenNumeric = true;
    if (cancel) context.cancelAtCapture = 6;
    else context.geometryChangeAtCapture = 6;
    Career::Ura::UraTrainingVisualScanner visual(TrainingResources());
    UraTrainingScanner scanner;
    const auto scan = visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 17);
    CHECK_FALSE(scan.IsReadyToSelect());
    CHECK_FALSE(scanner.PrepareTrainingTap());
    CHECK(context.finalTrainingTypes.empty());
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
    const auto result = flow.Select(context, state, scenario, strategy);
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

TEST_CASE("URA flow publishes the rechecked winner and the executor confirms it once",
    "[Career][Vision][Replay][TrainingDirectExecution]")
{
    const bool changeWinner = GENERATE(false, true);
    TrainingCaptureContext context("speed");
    context.frozenNumeric = true;
    if (changeWinner) context.recheckedSpeedGain = 1;
    Career::Ura::UraTrainingFlowAdapter flow(TrainingResources());
    CareerDomain::CareerState state;
    state.energy = CareerDomain::ObservedValue<int>::Make(58);
    state.turnIndexEvidence = CareerDomain::ObservedValue<int>::Make(17);
    state.turnPositionText = "Classic Year";
    CareerDomain::UraScenarioModule scenario;
    CareerDomain::UraSmartTrainingStrategy strategy(RaceDistanceBand::Middle);
    const auto result = flow.Select(context, state, scenario, strategy);
    CAPTURE(result.error, changeWinner);
    REQUIRE(result.status == Career::CareerTrainingSelectionStatus::Ready);
    const std::string expected = changeWinner ? "guts" : "speed";
    CHECK(result.intent.trainingType == expected);
    CHECK(context.selectedType == expected);
    CHECK(flow.HasPendingConfirmedTap());
    CHECK(context.capturedTypes.size() == (changeWinner ? 7 : 6));
    const auto reads = context.numericBatchCalls;
    const auto previews = context.previewTypes.size();
    Career::CareerActionExecutor executor(TrainingResources());
    CareerDomain::CareerObservation observation;
    observation.screenId = "training_selection";
    REQUIRE(executor.ExecuteForObservation(result.intent, observation, context) == Career::ActionExecutionStatus::Success);
    CHECK(context.finalTrainingTypes == std::vector<std::string>{expected});
    CHECK(context.previewTypes.size() == previews);
    CHECK(context.numericBatchCalls == reads);
    REQUIRE(flow.MarkTrainingTapSent());
    CHECK(flow.HasPendingOutcome());
    CHECK_FALSE(flow.HasPendingConfirmedTap());
}

TEST_CASE("URA single-frame scan rereads all values on a subsequent turn",
    "[Career][Vision][Replay][TrainingSingleFrame]")
{
    TrainingCaptureContext context("speed");
    context.frozenNumeric = true;
    Career::Ura::UraTrainingVisualScanner visual(TrainingResources());
    UraTrainingScanner scanner;
    REQUIRE(visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 17).IsReadyToSelect());
    CHECK(context.numericBatchCalls == 6);
    REQUIRE(visual.ScanCandidates(context, scanner, RaceDistanceBand::Middle, "career", 58, 18).IsReadyToSelect());
    CHECK(context.numericBatchCalls == 12);
    CHECK(context.numericFieldCalls.at("speed.speed") == 4);
    CHECK(context.logs.back().find("fresh_captures=5") != std::string::npos);
}

TEST_CASE("URA local logo search falls back to the authored region on a miss",
    "[Career][Vision][Replay][TrainingSingleFrame]")
{
    TrainingCaptureContext context("speed");
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

TEST_CASE("URA visual scan uses selected chevron when a Duel badge distracts icon matching", "[Career][Vision][Replay][TrainingSingleFrame]")
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
    CHECK(wit->witGain == 13);
    CHECK(wit->skillPointGain == 6);
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
    context.detachedOcr = GENERATE(false, true);
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
    CAPTURE(context.numericDiagnostics, context.detachedOcr);
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
