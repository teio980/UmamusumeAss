#include "Common/PathEncoding.hpp"
#include "Career/CareerVisualResources.hpp"
#include "GameTasks/GameTaskActionExtensions.hpp"
#include "TaskEngine/HachimiPipelineLoader.hpp"
#include "TaskEngine/TaskGraphRunner.hpp"

#include <nlohmann/json.hpp>
#include <algorithm>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <optional>
#include <stdexcept>
#include <string>
#include <utility>

namespace {
using nlohmann::json;
using namespace UmaAssistant::TaskEngine;
namespace Paths = UmaAssistant::Paths;

std::string ReadText(std::filesystem::path const& path)
{
    if (std::filesystem::file_size(path) > 16 * 1024 * 1024)
        throw std::runtime_error("Replay input exceeds 16 MiB.");
    std::ifstream input(path, std::ios::binary);
    if (!input) throw std::runtime_error("Cannot open replay input.");
    return std::string(std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>());
}

json InspectPackage(std::filesystem::path const& manifest, std::optional<std::filesystem::path> const& requestPath)
{
    auto resourceRoot = manifest.parent_path();
    for (auto directory = manifest.parent_path(); !directory.empty(); directory = directory.parent_path())
    {
        if (directory.filename() == "resource") { resourceRoot = directory; break; }
        if (directory == directory.parent_path()) break;
    }
    std::vector<UmaAssistant::Career::CareerJsonOverride> overrides;
    if (requestPath)
    {
        auto const request = json::parse(ReadText(*requestPath));
        if (!request.is_object()) throw std::runtime_error("Package inspection request must be an object.");
        if (request.contains("resource_root"))
        {
            resourceRoot = Paths::FromUtf8(request.at("resource_root").get<std::string>());
            if (!resourceRoot.is_absolute()) throw std::runtime_error("resource_root must be absolute.");
        }
        if (request.contains("files"))
        {
            auto const& files = request.at("files");
            if (!files.is_array() || files.size() > 1024)
                throw std::runtime_error("Package inspection allows at most 1024 file overrides.");
            for (auto const& file : files)
                overrides.push_back({file.at("path").get<std::string>(), file.at("raw_json").get<std::string>()});
        }
    }
    return UmaAssistant::Career::InspectCareerPackage(manifest, resourceRoot, overrides);
}

char const* EventName(TaskEventType type)
{
    switch (type)
    {
    case TaskEventType::PipelineStarted: return "PipelineStarted";
    case TaskEventType::PipelineCompleted: return "PipelineCompleted";
    case TaskEventType::PipelineFailed: return "PipelineFailed";
    case TaskEventType::TaskEntering: return "TaskEntering";
    case TaskEventType::RecognitionEvaluated: return "RecognitionEvaluated";
    case TaskEventType::ActionExecuting: return "ActionExecuting";
    case TaskEventType::ActionExecuted: return "ActionExecuted";
    case TaskEventType::TransitionVerified: return "TransitionVerified";
    case TaskEventType::MonitorTriggered: return "MonitorTriggered";
    case TaskEventType::TaskCompleted: return "TaskCompleted";
    case TaskEventType::TaskFailed: return "TaskFailed";
    case TaskEventType::TaskRetrying: return "TaskRetrying";
    }
    return "Unknown";
}

// This is an evidence adapter, not a second flow engine or recognition
// algorithm. Scores and OCR boxes are frozen inputs in reference coordinates.
class ReplayEvidenceContext final : public ITaskExecutionContext
{
public:
    explicit ReplayEvidenceContext(json fixture) : fixture_(std::move(fixture))
    {
        if (!fixture_.is_object() || fixture_.value("version", 0) != 1
            || !fixture_.contains("frames") || !fixture_["frames"].is_array()
            || fixture_["frames"].empty() || fixture_["frames"].size() > 10000)
            throw std::runtime_error("Replay v1 requires between 1 and 10000 frames.");
        budget_ = fixture_.value("operationBudget", 10000);
        if (budget_ <= 0 || budget_ > 100000)
            throw std::runtime_error("operationBudget must be between 1 and 100000.");
    }

    bool CaptureScreen() override
    {
        if (!Consume() || captures_ >= fixture_["frames"].size())
        {
            exhausted_ = true;
            return false;
        }
        current_ = &fixture_["frames"][captures_++];
        if (!current_->is_object()) throw std::runtime_error("Replay frame must be an object.");
        cancelled_ = current_->value("cancelled", false);
        return !cancelled_ && current_->value("captureSucceeded", true);
    }

    TaskMatchResult MatchTemplate(std::string const& path, double threshold,
        std::optional<TaskRoi> const& roi, std::string const& algorithm) override
    {
        return MatchTemplateAdvanced(path, threshold, roi, algorithm, reference_.first, reference_.second, {});
    }

    TaskMatchResult MatchTemplateAdvanced(std::string const& path, double threshold,
        std::optional<TaskRoi> const& roi, std::string const&, int width, int height,
        std::vector<double> const&) override
    {
        TaskMatchResult result;
        result.referenceWidth = width;
        result.referenceHeight = height;
        result.frameId = captures_;
        result.sessionGeneration = result.geometryGeneration = 1;
        if (!Consume() || current_ == nullptr || !current_->contains("matches")) return result;
        auto const& matches = current_->at("matches");
        if (!matches.is_object()) throw std::runtime_error("Frame matches must be an object.");
        auto found = matches.find(path);
        if (found == matches.end()) return result;
        result.score = found->value("score", 1.0);
        if (!std::isfinite(result.score) || result.score < 0.0 || result.score > 1.0)
            throw std::runtime_error("Match score must be finite and between 0 and 1.");
        result.x = found->value("x", 0);
        result.y = found->value("y", 0);
        result.width = found->value("width", 1);
        result.height = found->value("height", 1);
        result.found = found->value("found", true) && result.score >= threshold;
        if (roi && (result.x < roi->x || result.y < roi->y
            || static_cast<long long>(result.x) + result.width > static_cast<long long>(roi->x) + roi->width
            || static_cast<long long>(result.y) + result.height > static_cast<long long>(roi->y) + roi->height))
            result.found = false;
        return result;
    }

    std::vector<TaskOcrDetection> RecognizeText(std::string const&,
        std::optional<TaskRoi> const& roi) override
    {
        std::vector<TaskOcrDetection> detections;
        if (!Consume() || current_ == nullptr || !current_->contains("ocr")) return detections;
        for (auto const& item : current_->at("ocr"))
        {
            TaskOcrDetection detection{item.at("text").get<std::string>(), item.value("x", 0),
                item.value("y", 0), item.value("width", 1), item.value("height", 1), item.value("confidence", 1.0)};
            if (!roi || (detection.CenterX() >= roi->x && detection.CenterY() >= roi->y
                && static_cast<long long>(detection.CenterX()) < static_cast<long long>(roi->x) + roi->width
                && static_cast<long long>(detection.CenterY()) < static_cast<long long>(roi->y) + roi->height))
                detections.push_back(std::move(detection));
        }
        return detections;
    }

    bool CheckImageChange(TaskMatchResult const&) override
    {
        return Consume() && current_ != nullptr && current_->value("imageChanged", false);
    }
    bool ProbeHsv(int, int, int, double, double, double, double, double, double, double) override
    {
        return Consume() && current_ != nullptr && current_->value("hsvMatched", false);
    }
    bool Tap(int x, int y) override { return Record({{"type", "tap"}, {"x", x}, {"y", y}}); }
    bool TapMatch(int x, int y, TaskMatchResult const& match) override
    {
        if (IsMatchRetained(match))
        {
            if (x != retainedX_ || y != retainedY_) return false;
            --retainedAttempts_;
        }
        else if (match.frameId != 0 && match.frameId != captures_) return false;
        return Tap(x, y);
    }
    bool RetainMatchForRetry(TaskMatchResult const& match, int attempts, int,
        int x, int y) override
    {
        if (!Consume() || attempts <= 0 || attempts > 100 || match.frameId != captures_)
            return false;
        retained_ = match;
        retainedAttempts_ = attempts;
        retainedX_ = x < 0 ? match.CenterX() : x;
        retainedY_ = y < 0 ? match.CenterY() : y;
        return true;
    }
    void ReleaseRetainedMatch() override { retained_.reset(); retainedAttempts_ = 0; }
    bool HasRetainedMatch() const override { return retained_.has_value() && retainedAttempts_ > 0; }
    bool IsMatchRetained(TaskMatchResult const& match) const override
    {
        return HasRetainedMatch() && match.frameId == retained_->frameId;
    }
    bool Swipe(int x1, int y1, int x2, int y2, int duration) override
    {
        return Record({{"type", "swipe"}, {"x1", x1}, {"y1", y1}, {"x2", x2}, {"y2", y2}, {"duration", duration}});
    }
    bool InputText(std::string const& value) override { return Record({{"type", "text"}, {"value", value}}); }
    bool SendKey(std::string const& value) override { return Record({{"type", "key"}, {"value", value}}); }
    void Sleep(int milliseconds) override
    {
        if (Consume()) sleepMs_ += (std::max)(0, milliseconds);
    }
    bool IsCancelled() const override { return cancelled_ || exhausted_ || budget_ <= 0; }
    void SetCoordinateReference(int width, int height) override { reference_ = {width, height}; }
    std::pair<int, int> GetCoordinateReference() const override { return reference_; }
    std::string GetTemplateBaseDirectory() const override { return baseDirectory_; }
    void SetTemplateBaseDirectory(std::string const& value) override { baseDirectory_ = value; }
    void Log(std::string const& level, std::string const& message) override
    {
        if (Consume()) logs.push_back({{"level", level}, {"message", message}});
    }
    void EmitEvent(TaskExecutionEvent const& event) override
    {
        if (events.size() >= 100000) { budget_ = 0; return; }
        events.push_back({{"type", EventName(event.type)}, {"task", event.taskName},
            {"pipeline", event.pipelineName}, {"step", event.stepIndex}, {"succeeded", event.succeeded},
            {"score", event.score}, {"x", event.targetX}, {"y", event.targetY}, {"message", event.message}});
    }
    json Summary(TaskGraphRunResult const& result) const
    {
        bool const succeeded = !IsCancelled() && (result.status == TaskGraphStatus::Success
            || result.status == TaskGraphStatus::Completed);
        return {{"version", 1}, {"success", succeeded}, {"status", static_cast<int>(result.status)},
            {"finalTask", result.finalTask}, {"totalSteps", result.totalStepsExecuted},
            {"error", result.errorMessage}, {"captures", captures_}, {"exhausted", exhausted_},
            {"budgetExceeded", budget_ <= 0}, {"sleepMilliseconds", sleepMs_},
            {"taskCounts", result.taskExecutionCounts}, {"actions", actions}, {"events", events}, {"logs", logs}};
    }

private:
    bool Consume() { if (IsCancelled()) return false; --budget_; return budget_ > 0; }
    bool Record(json value)
    {
        if (!Consume() || current_ == nullptr) return false;
        bool const succeeded = current_->value("actionSucceeded", true);
        value["frame"] = captures_;
        value["succeeded"] = succeeded;
        actions.push_back(std::move(value));
        return succeeded;
    }
    json fixture_;
    json const* current_{nullptr};
    std::size_t captures_{0};
    int budget_{0};
    bool exhausted_{false};
    bool cancelled_{false};
    long long sleepMs_{0};
    std::pair<int, int> reference_{900, 1600};
    std::string baseDirectory_;
    std::optional<TaskMatchResult> retained_;
    int retainedAttempts_{0};
    int retainedX_{0};
    int retainedY_{0};
    json actions = json::array();
    json events = json::array();
    json logs = json::array();
};
}

#ifdef _WIN32
int wmain(int argc, wchar_t* argv[])
#else
int main(int argc, char* argv[])
#endif
{
    try
    {
        if (argc < 3 || argc > 4)
            throw std::runtime_error("Usage: uma_pipeline_replay <pipeline.json> <replay.json> [entry-task] | --inspect-career-package <manifest.json> [request.json]");
#ifdef _WIN32
        auto const command = Paths::ToUtf8(std::filesystem::path(argv[1]));
        auto const path = std::filesystem::absolute(std::filesystem::path(argv[1]));
        auto const replayPath = std::filesystem::path(argv[2]);
        auto const entry = argc == 4 ? Paths::ToUtf8(std::filesystem::path(argv[3])) : std::string{};
#else
        auto const command = std::string(argv[1]);
        auto const path = std::filesystem::absolute(Paths::FromUtf8(argv[1]));
        auto const replayPath = Paths::FromUtf8(argv[2]);
        auto const entry = argc == 4 ? std::string(argv[3]) : std::string{};
#endif
        if (command == "--inspect-career-package")
        {
            auto const summary = InspectPackage(std::filesystem::absolute(replayPath),
                argc == 4 ? std::optional<std::filesystem::path>{Paths::FromUtf8(entry)} : std::nullopt);
            std::cout << summary.dump() << '\n';
            return summary.at("valid").get<bool>() ? 0 : 1;
        }
        auto const definition = HachimiPipelineLoader::LoadFromJsonString(
            ReadText(path), Paths::ToUtf8(path.parent_path()));
        if (!definition) throw std::runtime_error("Core rejected the Hachimi pipeline.");
        ReplayEvidenceContext context(json::parse(ReadText(replayPath)));
        ActionRegistry registry;
        UmaAssistant::GameTasks::GameTaskActionExtensions::RegisterAll(registry);
        TaskGraphRunner runner(std::move(registry));
        auto const result = runner.Run(*definition,
            argc == 4 ? entry : definition->start.value_or(""), context);
        auto const summary = context.Summary(result);
        std::cout << summary.dump() << '\n';
        return summary.at("success").get<bool>() ? 0 : 1;
    }
    catch (std::exception const& exception)
    {
        std::cout << json({{"version", 1}, {"success", false}, {"valid", false}, {"error", exception.what()}}).dump() << '\n';
        return 1;
    }
}
