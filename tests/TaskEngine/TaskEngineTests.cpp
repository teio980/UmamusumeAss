#include <catch2/catch_test_macros.hpp>

#include "UmaAssistantCore/Common/PathEncoding.hpp"
#include "UmaAssistantCore/TaskEngine/ActionRegistry.hpp"
#include "UmaAssistantCore/TaskEngine/HachimiPipelineLoader.hpp"
#include "UmaAssistantCore/TaskEngine/RecognitionPipeline.hpp"
#include "UmaAssistantCore/TaskEngine/TaskGraphRunner.hpp"
#include "MockTaskExecutionContext.hpp"

#include <chrono>
#include <algorithm>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

using namespace UmaAssistant::TaskEngine;
using namespace UmaAssistant::TaskEngine::Tests;
namespace Paths = UmaAssistant::Paths;

TEST_CASE("Optional visual probes use their fallback without reporting a task failure", "[TaskEngine][CareerParity]")
{
    HachimiPipelineDefinition definition;
    definition.name = "optional-probe";
    HachimiPipelineTask probe;
    probe.templatePath = "missing-probe.png";
    probe.required = false;
    probe.timeoutMilliseconds = 0;
    probe.onErrorNext = {"fallback"};
    definition.tasks["probe"] = probe;
    HachimiPipelineTask fallback;
    fallback.algorithm = "JustReturn";
    fallback.action = "JustReturn";
    definition.tasks["fallback"] = fallback;
    MockTaskExecutionContext context;
    auto const result = TaskGraphRunner{}.Run(definition, "probe", context);
    CHECK(result.status == TaskGraphStatus::Completed);
    CHECK(result.finalTask == "fallback");
    CHECK(std::none_of(context.emittedEvents.begin(), context.emittedEvents.end(), [](auto const& event) {
        return event.type == TaskEventType::TaskFailed;
    }));
    CHECK(std::none_of(context.logs.begin(), context.logs.end(), [](auto const& log) {
        return log.starts_with("WARN");
    }));
}

namespace
{

std::filesystem::path SourceRoot()
{
    return std::filesystem::path(UMA_SOURCE_ROOT);
}

std::filesystem::path MakeTempDirectory()
{
    const auto stamp = std::chrono::steady_clock::now().time_since_epoch().count();
    auto directory = std::filesystem::temp_directory_path() /
        ("uma-task-engine-" + std::to_string(stamp));
    std::filesystem::create_directories(directory);
    return directory;
}

} // namespace

TEST_CASE("Hachimi loader applies v1 defaults and keeps schema properties case-sensitive", "[TaskEngine]")
{
    auto definition = HachimiPipelineLoader::LoadFromJsonString(
        R"({"tasks":{"Entry":{}}})");
    REQUIRE(definition);
    CHECK(definition->schemaVersion == 1);
    CHECK(definition->referenceWidth == 900);
    CHECK(definition->referenceHeight == 1600);
    const auto* task = definition->TryGetTask("entry");
    REQUIRE(task);
    CHECK(task->action == "ClickSelf");
    CHECK(task->algorithm == "MatchTemplate");
    CHECK(task->referenceWidth == 900);
    CHECK(task->referenceHeight == 1600);
    CHECK(task->templateThreshold == 0.86);
    CHECK(task->maxTimes == 0);
    CHECK(task->required);
    CHECK(task->unique);

    CHECK_FALSE(HachimiPipelineLoader::LoadFromJsonString(
        R"({"Tasks":{"Entry":{}}})"));
    CHECK_FALSE(HachimiPipelineLoader::LoadFromJsonString(
        R"({"tasks":{"Entry":{"Action":"DoNothing"}}})"));
    CHECK_FALSE(HachimiPipelineLoader::LoadFromJsonString(
        R"({"tasks":{"Entry":{"unknownField":true}}})"));
    CHECK_FALSE(HachimiPipelineLoader::LoadFromJsonString(
        R"({"schemaVersion":2,"tasks":{}})"));
}

TEST_CASE("Hachimi loader accepts legacy metadata and uses its poll default only", "[TaskEngine][metadata]")
{
    auto const legacy = HachimiPipelineLoader::LoadFromJsonString(R"({
        "schemaVersion": 1,
        "name": "legacy",
        "start": "Entry",
        "templates": {"raceResult": "race.png", "randomShop": "shop.png"},
        "uma": {"charaId": "1001", "cardDeckId": "2001"},
        "timing": {"navigationMs": 1200, "raceTimeoutMs": 60000, "pollIntervalMs": 425},
        "tasks": {"Entry": {"action": "DoNothing"}}
    })");
    REQUIRE(legacy.has_value());
    CHECK(legacy->name == "legacy");
    CHECK(legacy->start == "Entry");
    CHECK(legacy->timing.pollIntervalMilliseconds == 425);
    CHECK(legacy->TryGetTask("Entry") != nullptr);

    auto const defaults = HachimiPipelineLoader::LoadFromJsonString(
        R"({"tasks":{"Entry":{"action":"DoNothing"}}})");
    REQUIRE(defaults.has_value());
    CHECK(defaults->timing.pollIntervalMilliseconds == 300);

    CHECK_FALSE(HachimiPipelineLoader::LoadFromJsonString(
        R"({"templates":{"raceResult":false},"tasks":{}})"));
    CHECK_FALSE(HachimiPipelineLoader::LoadFromJsonString(
        R"({"uma":{"cardDeckId":2},"tasks":{}})"));
    CHECK_FALSE(HachimiPipelineLoader::LoadFromJsonString(
        R"({"timing":{"raceTimeoutMs":"never"},"tasks":{}})"));
    CHECK_FALSE(HachimiPipelineLoader::LoadFromJsonString(
        R"({"timing":{"unexpectedMs":1},"tasks":{}})"));
    CHECK_FALSE(HachimiPipelineLoader::LoadFromJsonString(
        R"({"unknownRootKey":true,"tasks":{}})"));
}

TEST_CASE("All shipped Hachimi definitions and their graph references load", "[TaskEngine]")
{
    const auto pipelineDirectory = SourceRoot() / "resource" / "hachimi" / "pipelines";
    const std::vector<std::string> names{
        "daily_race.json", "mail_collection.json", "mission_collection.json",
        "shop.json", "shop_task.json", "start_game.json", "team_race.json"};
    REQUIRE(std::filesystem::exists(pipelineDirectory));

    for (const auto& name : names)
    {
        const auto path = pipelineDirectory / name;
        CAPTURE(name);
        auto definition = HachimiPipelineLoader::LoadFromFile(Paths::ToUtf8(path));
        REQUIRE(definition);
        CHECK(!definition->tasks.empty());
        CHECK(Paths::FromUtf8(definition->baseDirectory) == path.parent_path());
        if (definition->start)
            CHECK(definition->TryGetTask(*definition->start) != nullptr);

        for (const auto& [taskName, task] : definition->tasks)
        {
            CAPTURE(taskName);
            for (const auto* links : {&task.next, &task.onErrorNext, &task.exceededNext,
                                      &task.sub, &task.monitorTasks, &task.alternativeTemplateTasks,
                                      &task.triggerChain})
            {
                for (const auto& target : *links)
                    CHECK(definition->TryGetTask(target) != nullptr);
            }
            if (task.triggerTask)
                CHECK(definition->TryGetTask(*task.triggerTask) != nullptr);
            if (task.successTask)
                CHECK(definition->TryGetTask(*task.successTask) != nullptr);
            for (const auto& target : task.successTasks)
                CHECK(definition->TryGetTask(target) != nullptr);
            if (task.pipeline)
            {
                auto nestedPath = Paths::FromUtf8(*task.pipeline);
                if (nestedPath.is_relative())
                    nestedPath = path.parent_path() / nestedPath;
                CHECK(std::filesystem::exists(nestedPath));
            }
        }
    }
}

TEST_CASE("Action registry normalizes action names and requires evidence for ClickSelf", "[TaskEngine]")
{
    ActionRegistry registry;
    MockTaskExecutionContext context;
    HachimiPipelineTask task;
    task.action = "cLiCkSeLf";
    TaskMatchResult match{true, 1.0, 100, 200, 60, 40};
    CHECK(registry.ExecuteAction(task, match, context) == ActionResult::Success);
    REQUIRE(context.taps.size() == 1);
    CHECK((context.taps.front() == std::pair<int, int>{130, 220}));

    task.action = "ClickSelf";
    match.found = false;
    CHECK(registry.ExecuteAction(task, match, context) == ActionResult::Failed);
    CHECK(context.taps.size() == 1);
    CHECK_FALSE(registry.HasHandler("StartupMonitor"));
    CHECK_FALSE(registry.HasHandler("TapToStart"));

    task.action = "Input";
    CHECK(registry.ExecuteAction(task, match, context) == ActionResult::Failed);
    task.action = "Key";
    CHECK(registry.ExecuteAction(task, match, context) == ActionResult::Failed);

    task.action = "ClickText";
    task.rowExpansion = std::vector<int>{4, 6, 8, 10};
    task.clickOffset = std::vector<int>{3, -2};
    match = TaskMatchResult{true, 0.9, 10, 20, 20, 10};
    CHECK(registry.ExecuteAction(task, match, context) == ActionResult::Success);
    REQUIRE(context.taps.size() == 2);
    CHECK((context.taps.back() == std::pair<int, int>{25, 25}));
}

TEST_CASE("Layout ClickRect works without a template and template-less ClickSelf fails", "[TaskEngine]")
{
    auto definition = HachimiPipelineLoader::LoadFromJsonString(R"({
      "tasks": {
        "Layout": {"action":"ClickRect","specificRect":[100,200,300,80],"success":true},
        "NoEvidence": {"action":"ClickSelf","success":true}
      }
    })");
    REQUIRE(definition);
    TaskGraphRunner runner;

    MockTaskExecutionContext layoutContext;
    auto layoutResult = runner.Run(*definition, "Layout", layoutContext);
    CHECK(layoutResult.status == TaskGraphStatus::Success);
    REQUIRE(layoutContext.taps.size() == 1);
    CHECK((layoutContext.taps.front() == std::pair<int, int>{250, 240}));

    MockTaskExecutionContext noEvidenceContext;
    auto noEvidenceResult = runner.Run(*definition, "NoEvidence", noEvidenceContext);
    CHECK(noEvidenceResult.status == TaskGraphStatus::Failed);
    CHECK(noEvidenceContext.taps.empty());
}

TEST_CASE("Recognition applies scaled-template defaults and search ROI score-gap policy", "[TaskEngine]")
{
    auto definition = HachimiPipelineLoader::LoadFromJsonString(R"({
      "tasks": {
        "Scaled": {"algorithm":"MatchTemplateScaled","template":"scaled.png"},
        "Search": {"template":"search.png","searchRois":[[0,0,20,20],[40,0,20,20]],"minScoreGap":0.1}
      }
    })");
    REQUIRE(definition);
    MockTaskExecutionContext context;
    context.SetMockMatch("scaled.png", TaskMatchResult{true, 0.9, 10, 20, 30, 40});
    auto scaled = RecognitionPipeline::EvaluateTask(
        *definition, "Scaled", *definition->TryGetTask("Scaled"), context);
    CHECK(scaled.matched);
    REQUIRE(context.advancedCalls.size() == 1);
    CHECK((context.advancedCalls.front().scales ==
        std::vector<double>{0.80, 0.85, 0.90, 0.95, 1.00, 1.05, 1.10}));

    context.SetMockMatchSequence("search.png", {
        TaskMatchResult{true, 0.91, 10, 10, 5, 5},
        TaskMatchResult{true, 0.86, 50, 10, 5, 5}});
    auto ambiguous = RecognitionPipeline::EvaluateTask(
        *definition, "Search", *definition->TryGetTask("Search"), context);
    CHECK_FALSE(ambiguous.matched);
    REQUIRE(context.advancedCalls[1].roi);
    CHECK(context.advancedCalls[1].roi->x == 0);
    CHECK(context.advancedCalls[1].roi->width == 20);
    REQUIRE(context.advancedCalls[2].roi);
    CHECK(context.advancedCalls[2].roi->x == 40);
    CHECK(context.advancedCalls[2].roi->width == 20);
}

TEST_CASE("Template candidate matching preserves same-frame ranking and reports ambiguity", "[TaskEngine]")
{
    MockTaskExecutionContext context;
    context.SetMockMatch("first.png", TaskMatchResult{true, 0.80, 0, 0, 10, 10});
    context.SetMockMatch("best.png", TaskMatchResult{true, 0.95, 20, 0, 10, 10});
    context.SetMockMatch("second.png", TaskMatchResult{true, 0.88, 40, 0, 10, 10});
    const std::vector<std::string> paths{"first.png", "best.png", "second.png"};
    const auto candidates = context.MatchTemplateCandidates(paths, 0.7, std::nullopt,
        "MatchTemplate", 900, 1600, {}, 0.05);
    REQUIRE(candidates.candidates.size() == 3);
    CHECK(candidates.candidates[0].templatePath == "best.png");
    CHECK(candidates.candidates[1].templatePath == "second.png");
    CHECK(candidates.candidates[2].templatePath == "first.png");
    CHECK_FALSE(candidates.ambiguous);

    const auto ambiguous = context.MatchTemplateCandidates(paths, 0.7, std::nullopt,
        "MatchTemplate", 900, 1600, {}, 0.1);
    CHECK(ambiguous.ambiguous);
    CHECK(std::abs(ambiguous.scoreGap - 0.07) < 0.001);
}

TEST_CASE("OCR unique matching rejects duplicates and token coverage groups one row", "[TaskEngine]")
{
    auto definition = HachimiPipelineLoader::LoadFromJsonString(R"({
      "tasks": {
        "Unique": {"algorithm":"OCR","targetText":"Ready","fuzzyThreshold":0.8},
        "Grouped": {"algorithm":"OCR","targetText":"support runner","ocrMatchMode":"TokenCoverage","ocrRequireAllTokens":true,"ocrGroupRowHeight":30,"ocrRowGap":5,"fuzzyThreshold":0.9}
      }
    })");
    REQUIRE(definition);
    MockTaskExecutionContext context;
    context.mockOcrDetections = {
        TaskOcrDetection{"Ready", 10, 10, 40, 20, 0.9},
        TaskOcrDetection{"Ready", 60, 10, 40, 20, 0.9}};
    const auto ambiguous = RecognitionPipeline::EvaluateTask(
        *definition, "Unique", *definition->TryGetTask("Unique"), context);
    CHECK_FALSE(ambiguous.matched);

    context.mockOcrDetections = {
        TaskOcrDetection{"support", 10, 10, 50, 18, 0.9},
        TaskOcrDetection{"runner", 65, 11, 50, 18, 0.9}};
    const auto grouped = RecognitionPipeline::EvaluateTask(
        *definition, "Grouped", *definition->TryGetTask("Grouped"), context);
    CHECK(grouped.matched);
    CHECK(grouped.methodUsed == "Ocr");
    CHECK(grouped.matchResult.x == 10);
    CHECK(grouped.matchResult.width == 105);
}

TEST_CASE("Task graph honors explicit zero maxTimes override and failure routes", "[TaskEngine]")
{
    auto definition = HachimiPipelineLoader::LoadFromJsonString(R"({
      "tasks": {
        "Entry": {"action":"ClickSpecificRect","specificRect":[0,0,10,10],"exceededNext":["Done"]},
        "Parent": {"sub":["BrokenChild"],"action":"ClickSpecificRect","specificRect":[0,0,10,10],"onErrorNext":["Done"]},
        "BrokenChild": {"action":"missing"},
        "Done": {"action":"DoNothing","success":true}
      }
    })");
    REQUIRE(definition);
    TaskGraphRunner runner;

    MockTaskExecutionContext zeroContext;
    TaskGraphRunOptions options;
    options.maxTimesOverrides["entry"] = 0;
    auto zeroResult = runner.Run(*definition, "Entry", zeroContext, options);
    CHECK(zeroResult.status == TaskGraphStatus::Success);
    CHECK(zeroResult.taskExecutionCounts.find("Entry") == zeroResult.taskExecutionCounts.end());
    CHECK(zeroContext.taps.empty());

    MockTaskExecutionContext failureContext;
    auto failureResult = runner.Run(*definition, "Parent", failureContext);
    CHECK(failureResult.status == TaskGraphStatus::Success);
    CHECK(failureResult.finalTask == "Done");
    CHECK(failureContext.taps.empty());
    CHECK(failureResult.taskExecutionCounts.at("Parent") == 1);
    CHECK(failureResult.taskExecutionCounts.at("BrokenChild") == 1);
}

TEST_CASE("Task graph fails closed for unresolved collections and missing RunPipeline paths", "[TaskEngine]")
{
    auto definition = HachimiPipelineLoader::LoadFromJsonString(R"({
      "tasks": {
        "Collection": {"templateCollection":"shop-items","timeoutMs":0,"onErrorNext":["Done"]},
        "MissingNested": {"action":"RunPipeline","success":true},
        "Done": {"action":"DoNothing","success":true}
      }
    })");
    REQUIRE(definition);
    TaskGraphRunner runner;

    MockTaskExecutionContext collectionContext;
    auto collectionResult = runner.Run(*definition, "Collection", collectionContext);
    CHECK(collectionResult.status == TaskGraphStatus::Success);
    CHECK(collectionResult.finalTask == "Done");
    CHECK(collectionContext.captureCount == 0);

    MockTaskExecutionContext nestedContext;
    auto nestedResult = runner.Run(*definition, "MissingNested", nestedContext);
    CHECK(nestedResult.status == TaskGraphStatus::Failed);
    CHECK(nestedContext.captureCount == 0);
}

TEST_CASE("Bare DoNothing needs no frame while template-gated DoNothing still recognizes", "[TaskEngine]")
{
    auto definition = HachimiPipelineLoader::LoadFromJsonString(R"({
      "tasks": {
        "Bare": {"action":"DoNothing","success":true},
        "Visible": {"template":"ready.png","action":"DoNothing","success":true}
      }
    })");
    REQUIRE(definition);
    TaskGraphRunner runner;

    MockTaskExecutionContext bareContext;
    CHECK(runner.Run(*definition, "Bare", bareContext).status == TaskGraphStatus::Success);
    CHECK(bareContext.captureCount == 0);

    MockTaskExecutionContext visualContext;
    visualContext.SetMockMatch("ready.png", TaskMatchResult{true, 0.95, 20, 30, 40, 50});
    CHECK(runner.Run(*definition, "Visible", visualContext).status == TaskGraphStatus::Success);
    CHECK(visualContext.captureCount == 1);
}

TEST_CASE("Parallel monitor prioritizes a same-frame success over a click candidate", "[TaskEngine]")
{
    auto definition = HachimiPipelineLoader::LoadFromJsonString(R"({
      "tasks": {
        "Monitor": {"algorithm":"ParallelMonitor","successTask":"SuccessPage","monitorTasks":["ActionButton"],"timeoutMs":0},
        "SuccessPage": {"template":"success.png","action":"DoNothing","success":true},
        "ActionButton": {"template":"button.png","action":"ClickSelf"}
      }
    })");
    REQUIRE(definition);
    MockTaskExecutionContext context;
    context.SetMockMatch("success.png", TaskMatchResult{true, 0.97, 10, 20, 30, 40});
    context.SetMockMatch("button.png", TaskMatchResult{true, 0.99, 100, 200, 30, 40});
    TaskGraphRunner runner;

    const auto result = runner.Run(*definition, "Monitor", context);
    CHECK(result.status == TaskGraphStatus::Success);
    CHECK(context.taps.empty());
}

TEST_CASE("Nested pipelines use UTF-8 relative paths, restore context, and support skip options", "[TaskEngine]")
{
    const auto temporaryRoot = MakeTempDirectory();
    const auto unicodeName = Paths::FromUtf8("\xE5\xAD\x90\xE7\xAE\xA1\xE7\xBA\xBF.json");
    const auto childDirectory = temporaryRoot / "nested";
    std::filesystem::create_directories(childDirectory);
    const auto childPath = childDirectory / unicodeName;
    {
        std::ofstream child(childPath);
        REQUIRE(child.is_open());
        child << R"({"name":"Child","tasks":{"Leaf":{"action":"DoNothing","success":true}}})";
    }

    const std::string parentJson = "{\"name\":\"Parent\",\"tasks\":{\"Enter\":{\"action\":\"RunPipeline\",\"pipeline\":\"nested/" +
        Paths::ToUtf8(unicodeName) + "\",\"success\":true}}}";
    auto parent = HachimiPipelineLoader::LoadFromJsonString(parentJson, Paths::ToUtf8(temporaryRoot));
    REQUIRE(parent);
    TaskGraphRunner runner;

    MockTaskExecutionContext context;
    const auto result = runner.Run(*parent, "Enter", context);
    CHECK(result.status == TaskGraphStatus::Success);
    CHECK(context.GetTemplateBaseDirectory().empty());
    CHECK(std::find(context.templateBaseDirectoryHistory.begin(), context.templateBaseDirectoryHistory.end(),
                    Paths::ToUtf8(childPath.parent_path())) != context.templateBaseDirectoryHistory.end());

    MockTaskExecutionContext skippedContext;
    TaskGraphRunOptions skipOptions;
    skipOptions.skipNestedPipelines.insert(Paths::ToUtf8(unicodeName));
    const auto skippedResult = runner.Run(*parent, "Enter", skippedContext, skipOptions);
    CHECK(skippedResult.status == TaskGraphStatus::Success);
    CHECK(std::find(skippedContext.templateBaseDirectoryHistory.begin(),
                    skippedContext.templateBaseDirectoryHistory.end(), Paths::ToUtf8(childPath.parent_path()))
          == skippedContext.templateBaseDirectoryHistory.end());

    std::filesystem::remove_all(temporaryRoot);
}

TEST_CASE("Task graph guards recursion, counts failed entries, and handles cancellation", "[TaskEngine]")
{
    auto recursive = HachimiPipelineLoader::LoadFromJsonString(
        R"({"tasks":{"Loop":{"sub":["Loop"]}}})");
    REQUIRE(recursive);
    MockTaskExecutionContext recursiveContext;
    TaskGraphRunner runner;
    CHECK(runner.Run(*recursive, "Loop", recursiveContext).status == TaskGraphStatus::GuardLimitExceeded);
    CHECK(recursiveContext.taps.empty());

    auto failed = HachimiPipelineLoader::LoadFromJsonString(
        R"({"tasks":{"Entry":{"action":"missing"}}})");
    REQUIRE(failed);
    MockTaskExecutionContext failedContext;
    const auto failedResult = runner.Run(*failed, "Entry", failedContext);
    CHECK(failedResult.status == TaskGraphStatus::Failed);
    CHECK(failedResult.taskExecutionCounts.at("Entry") == 1);

    auto delayed = HachimiPipelineLoader::LoadFromJsonString(
        R"({"tasks":{"Entry":{"action":"ClickSpecificRect","specificRect":[0,0,10,10],"preDelay":1}}})");
    REQUIRE(delayed);
    MockTaskExecutionContext canceledContext;
    canceledContext.cancelOnSleep = true;
    CHECK(runner.Run(*delayed, "Entry", canceledContext).status == TaskGraphStatus::Cancelled);
    CHECK(canceledContext.taps.empty());
}

TEST_CASE("Transition click failure stops retries and capture failure never causes a blind tap", "[TaskEngine]")
{
    auto definition = HachimiPipelineLoader::LoadFromJsonString(R"({
      "tasks": {
        "Gone": {"template":"button.png","action":"ClickSelf","clickUntilGone":true,"maxClickAttempts":3,"timeoutMs":1},
        "Repeated": {"template":"button.png","action":"ClickSelfUntilTransition","repeatTapUntilTransition":true,"transitionTemplates":["next.png"],"maxClickAttempts":3,"transitionTimeoutMs":1}
      }
    })");
    REQUIRE(definition);
    TaskGraphRunner runner;

    MockTaskExecutionContext failedTapContext;
    failedTapContext.SetMockMatch("button.png", TaskMatchResult{true, 0.9, 10, 20, 20, 20});
    failedTapContext.tapResults = {false, true, true};
    const auto failedTapResult = runner.Run(*definition, "Gone", failedTapContext);
    CHECK(failedTapResult.status == TaskGraphStatus::Failed);
    REQUIRE(failedTapContext.taps.size() == 1);

    MockTaskExecutionContext failedCaptureContext;
    failedCaptureContext.SetMockMatch("button.png", TaskMatchResult{true, 0.9, 10, 20, 20, 20});
    failedCaptureContext.SetMockMatch("next.png", TaskMatchResult{false, 0.0, 0, 0, 0, 0});
    failedCaptureContext.captureResults = {true, false};
    const auto failedCaptureResult = runner.Run(*definition, "Repeated", failedCaptureContext);
    CHECK(failedCaptureResult.status == TaskGraphStatus::Failed);
    REQUIRE(failedCaptureContext.taps.size() == 1);
}

TEST_CASE("Transition click counts its first tap and accepts a same-frame transition", "[TaskEngine]")
{
    auto definition = HachimiPipelineLoader::LoadFromJsonString(R"({
      "tasks": {
        "Repeated": {"template":"button.png","action":"ClickSelfUntilTransition","repeatTapUntilTransition":true,"transitionTemplates":["next.png"],"maxClickAttempts":2,"transitionTimeoutMs":1500},
        "Unused": {"action":"DoNothing"}
      }
    })");
    REQUIRE(definition);
    MockTaskExecutionContext context;
    context.SetMockMatch("button.png", TaskMatchResult{true, 0.9, 10, 20, 20, 20});
    context.SetMockMatchSequence("next.png", {
        TaskMatchResult{false, 0.0, 0, 0, 0, 0},
        TaskMatchResult{true, 0.95, 50, 60, 20, 20}});
    TaskGraphRunner runner;

    const auto result = runner.Run(*definition, "Repeated", context);
    CHECK(result.status == TaskGraphStatus::Completed);
    CHECK(context.taps.size() == 2);
}

TEST_CASE("Scroll-to-find succeeds after a swipe and exhausts the configured swipe count", "[TaskEngine]")
{
    auto definition = HachimiPipelineLoader::LoadFromJsonString(R"({
      "tasks": {"Search":{"template":"target.png","action":"ClickSelf","maxScrolls":2,"timeoutMs":0,"success":true}}
    })");
    REQUIRE(definition);
    TaskGraphRunner runner;

    MockTaskExecutionContext foundContext;
    foundContext.SetMockMatchSequence("target.png", {
        TaskMatchResult{false, 0.0, 0, 0, 0, 0},
        TaskMatchResult{true, 0.95, 100, 200, 40, 30}});
    const auto found = runner.Run(*definition, "Search", foundContext);
    CHECK(found.status == TaskGraphStatus::Success);
    CHECK(foundContext.swipes.size() == 1);
    REQUIRE(foundContext.taps.size() == 1);
    CHECK((foundContext.taps.front() == std::pair<int, int>{120, 215}));

    MockTaskExecutionContext exhaustedContext;
    exhaustedContext.SetMockMatchSequence("target.png", {
        TaskMatchResult{false, 0.0, 0, 0, 0, 0},
        TaskMatchResult{false, 0.0, 0, 0, 0, 0},
        TaskMatchResult{false, 0.0, 0, 0, 0, 0}});
    const auto exhausted = runner.Run(*definition, "Search", exhaustedContext);
    CHECK(exhausted.status == TaskGraphStatus::TimedOut);
    CHECK(exhaustedContext.swipes.size() == 2);
    CHECK(exhaustedContext.taps.empty());
}

TEST_CASE("Transient match retry keeps one frozen click target and lease budget across recapture", "[TaskEngine]")
{
    auto definition = HachimiPipelineLoader::LoadFromJsonString(R"({
      "tasks": {
        "Flash": {"template":"flash.png","action":"ClickSelf","clickOffset":[5,-5],"reuseLastMatchOnRetry":true,"maxTimes":2,"next":["Flash"],"exceededNext":["Done"]},
        "Done": {"action":"DoNothing","success":true}
      }
    })");
    REQUIRE(definition);
    MockTaskExecutionContext context;
    context.SetMockMatchSequence("flash.png", {
        TaskMatchResult{true, 0.92, 300, 400, 60, 30},
        TaskMatchResult{false, 0.0, 0, 0, 0, 0}});
    TaskGraphRunner runner;

    const auto result = runner.Run(*definition, "Flash", context);
    CHECK(result.status == TaskGraphStatus::Success);
    CHECK(result.taskExecutionCounts.at("Flash") == 2);
    REQUIRE(context.taps.size() == 2);
    CHECK((context.taps[0] == std::pair<int, int>{335, 410}));
    CHECK(context.taps[1] == context.taps[0]);
    CHECK(context.retainCallCount == 1);
}

TEST_CASE("Fallback ROI retry uses its explicit click offset", "[TaskEngine]")
{
    auto definition = HachimiPipelineLoader::LoadFromJsonString(R"({
      "tasks": {
        "Entry": {"template":"entry.png","action":"ClickSelf","clickOffset":[3,4],"fallbackRoi":[100,200,80,60],"fallbackClickOffset":[5,6],"transitionTemplates":["next.png"],"transitionTimeoutMs":1,"transitionPollIntervalMs":1,"success":true}
      }
    })");
    REQUIRE(definition);
    MockTaskExecutionContext context;
    context.SetMockMatchSequence("entry.png", {
        TaskMatchResult{true, 0.90, 10, 20, 20, 30},
        TaskMatchResult{true, 0.92, 100, 200, 20, 30}});
    context.SetMockMatchSequence("next.png", {
        TaskMatchResult{false, 0.0, 0, 0, 0, 0},
        TaskMatchResult{true, 0.96, 400, 500, 20, 20}});
    context.captureResults = {true, false};
    TaskGraphRunner runner;

    const auto result = runner.Run(*definition, "Entry", context);
    CHECK(result.status == TaskGraphStatus::Success);
    REQUIRE(context.taps.size() == 2);
    CHECK((context.taps[0] == std::pair<int, int>{23, 39}));
    CHECK((context.taps[1] == std::pair<int, int>{115, 221}));
}

TEST_CASE("A missing click-verification confirmation is a failed transition", "[TaskEngine]")
{
    auto definition = HachimiPipelineLoader::LoadFromJsonString(R"({
      "tasks":{"Entry":{"action":"ClickSpecificRect","specificRect":[0,0,10,10],
        "clickVerification":{"verifyTimeoutMs":0,"maxRetries":0},"success":true}}
    })");
    REQUIRE(definition);
    MockTaskExecutionContext context;
    context.probeHsvResult = false;
    TaskGraphRunner runner;
    const auto result = runner.Run(*definition, "Entry", context);
    CHECK(result.status == TaskGraphStatus::Failed);
    CHECK(result.taskExecutionCounts.at("Entry") == 1);
    CHECK(context.taps.size() == 1);
}

TEST_CASE("HSV retries are capped at five and use fresh OCR evidence before another tap", "[TaskEngine]")
{
    auto cappedDefinition = HachimiPipelineLoader::LoadFromJsonString(R"({
      "tasks":{"Entry":{"action":"ClickSpecificRect","specificRect":[0,0,10,10],
        "clickVerification":{"preCheck":false,"verifyTimeoutMs":0,"settleDelayMs":0,"retryDelayMs":0,"maxRetries":9},"success":true}}
    })");
    REQUIRE(cappedDefinition);
    MockTaskExecutionContext cappedContext;
    cappedContext.probeHsvResult = false;
    TaskGraphRunner runner;
    const auto capped = runner.Run(*cappedDefinition, "Entry", cappedContext);
    CHECK(capped.status == TaskGraphStatus::Failed);
    CHECK(cappedContext.taps.size() == 6); // Initial click + five bounded retries.
    CHECK(cappedContext.probeCount == 6);

    auto ocrDefinition = HachimiPipelineLoader::LoadFromJsonString(R"({
      "tasks":{"Select":{"algorithm":"OCR","action":"ClickSelf","targetText":"Open",
        "clickVerification":{"preCheck":false,"verifyTimeoutMs":0,"settleDelayMs":0,"retryDelayMs":0,"maxRetries":1},"success":true}}
    })");
    REQUIRE(ocrDefinition);
    MockTaskExecutionContext ocrContext;
    ocrContext.mockOcrSequences = {
        {TaskOcrDetection{"Open", 10, 20, 20, 20, 0.9}},
        {TaskOcrDetection{"Open", 100, 200, 20, 20, 0.95}}};
    ocrContext.probeResults = {false, false, true};
    const auto ocrResult = runner.Run(*ocrDefinition, "Select", ocrContext);
    CHECK(ocrResult.status == TaskGraphStatus::Success);
    REQUIRE(ocrContext.taps.size() == 2);
    CHECK((ocrContext.taps[0] == std::pair<int, int>{20, 30}));
    CHECK((ocrContext.taps[1] == std::pair<int, int>{110, 210}));
}
