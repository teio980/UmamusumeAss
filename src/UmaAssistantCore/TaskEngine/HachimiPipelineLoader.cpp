#include "HachimiPipelineLoader.hpp"
#include "Common/PathEncoding.hpp"
#include "Resources/PipelineMetadataSchema.hpp"
#include <nlohmann/json.hpp>
#include <fstream>
#include <filesystem>
#include <initializer_list>
#include <string_view>

namespace UmaAssistant::TaskEngine
{

namespace
{

using Json = nlohmann::ordered_json;

bool HasOnlyKeys(const Json& value, std::initializer_list<std::string_view> allowed)
{
    if (!value.is_object())
        return false;
    for (auto it = value.begin(); it != value.end(); ++it)
    {
        if (std::find(allowed.begin(), allowed.end(), it.key()) == allowed.end())
            return false;
    }
    return true;
}

template <typename T>
std::optional<T> GetOptional(const Json& j, const std::string& key)
{
    if (j.contains(key) && !j[key].is_null())
    {
        return j[key].get<T>();
    }
    return std::nullopt;
}

template <typename T>
T GetValueOrDefault(const Json& j, const std::string& key, const T& defaultValue)
{
    if (j.contains(key) && !j[key].is_null())
    {
        return j[key].get<T>();
    }
    return defaultValue;
}

HachimiClickVerification ParseClickVerification(const Json& j)
{
    HachimiClickVerification v;
    v.preCheck = GetValueOrDefault(j, "preCheck", v.preCheck);
    v.probeRadius = GetValueOrDefault(j, "probeRadius", v.probeRadius);
    v.probeOffset = GetOptional<std::vector<int>>(j, "probeOffset");
    v.hueMin = GetValueOrDefault(j, "hueMin", v.hueMin);
    v.hueMax = GetValueOrDefault(j, "hueMax", v.hueMax);
    v.saturationMin = GetValueOrDefault(j, "saturationMin", v.saturationMin);
    v.saturationMax = GetValueOrDefault(j, "saturationMax", v.saturationMax);
    v.valueMin = GetValueOrDefault(j, "valueMin", v.valueMin);
    v.valueMax = GetValueOrDefault(j, "valueMax", v.valueMax);
    v.minimumMatchRatio = GetValueOrDefault(j, "minimumMatchRatio", v.minimumMatchRatio);
    v.settleDelayMs = GetValueOrDefault(j, "settleDelayMs", v.settleDelayMs);
    v.verifyTimeoutMs = GetValueOrDefault(j, "verifyTimeoutMs", v.verifyTimeoutMs);
    v.verifyPollIntervalMs = GetValueOrDefault(j, "verifyPollIntervalMs", v.verifyPollIntervalMs);
    v.maxRetries = GetValueOrDefault(j, "maxRetries", v.maxRetries);
    v.retryDelayMs = GetValueOrDefault(j, "retryDelayMs", v.retryDelayMs);
    return v;
}

HachimiPipelineTask ParseTask(const Json& j)
{
    HachimiPipelineTask task;
    task.outcome = GetOptional<std::string>(j, "outcome");
    task.algorithm = GetValueOrDefault<std::string>(j, "algorithm", "MatchTemplate");
    task.action = GetValueOrDefault<std::string>(j, "action", "ClickSelf");
    task.pipeline = GetOptional<std::string>(j, "pipeline");
    task.entry = GetOptional<std::string>(j, "entry");
    task.swipe = GetOptional<std::vector<int>>(j, "swipe");
    task.inputText = GetOptional<std::string>(j, "inputText");
    task.keyCode = GetOptional<std::string>(j, "keyCode");
    task.templatePath = GetOptional<std::string>(j, "template");
    task.templateCollection = GetOptional<std::string>(j, "templateCollection");
    task.referenceWidth = GetValueOrDefault(j, "referenceWidth", task.referenceWidth);
    task.referenceHeight = GetValueOrDefault(j, "referenceHeight", task.referenceHeight);
    task.templateThreshold = GetValueOrDefault(j, "templThreshold", 0.86);
    task.alternativeTemplateTasks = GetValueOrDefault<std::vector<std::string>>(j, "alternativeTemplateTasks", {});
    task.roi = GetOptional<std::vector<int>>(j, "roi");
    task.fallbackRoi = GetOptional<std::vector<int>>(j, "fallbackRoi");
    task.fallbackClickOffset = GetOptional<std::vector<int>>(j, "fallbackClickOffset");
    task.clickUntilGone = GetValueOrDefault(j, "clickUntilGone", false);
    task.reuseLastMatchOnRetry = GetValueOrDefault(j, "reuseLastMatchOnRetry", false);
    task.maxClickAttempts = GetValueOrDefault(j, "maxClickAttempts", 0);
    task.repeatTapUntilTransition = GetValueOrDefault(j, "repeatTapUntilTransition", false);
    task.goneConfirmationSamples = GetValueOrDefault(j, "goneConfirmationSamples", 3);
    task.transitionTemplates = GetValueOrDefault<std::vector<std::string>>(j, "transitionTemplates", {});
    task.pollTransitionTemplatesTogether = GetValueOrDefault(j, "pollTransitionTemplatesTogether", false);
    task.transitionRoi = GetOptional<std::vector<int>>(j, "transitionRoi");

    if (j.contains("transitionRois") && j["transitionRois"].is_array())
    {
        for (const auto& item : j["transitionRois"])
        {
            if (item.is_null())
                task.transitionRois.push_back(std::nullopt);
            else
                task.transitionRois.push_back(item.get<std::vector<int>>());
        }
    }

    task.transitionThreshold = GetValueOrDefault(j, "transitionThreshold", 0.78);
    task.transitionTimeoutMilliseconds = GetValueOrDefault(j, "transitionTimeoutMs", 2600);
    task.transitionPollIntervalMilliseconds = GetValueOrDefault(j, "transitionPollIntervalMs", 250);
    task.specificRect = GetOptional<std::vector<int>>(j, "specificRect");
    task.repeat = GetValueOrDefault(j, "repeat", 1);
    task.repeatDelayMilliseconds = GetValueOrDefault(j, "repeat_delay", 0);
    task.searchRois = GetValueOrDefault<std::vector<std::vector<int>>>(j, "searchRois", {});
    task.minimumScoreGap = GetValueOrDefault(j, "minScoreGap", 0.0);
    task.scaleCandidates = GetValueOrDefault<std::vector<double>>(j, "scaleCandidates", {});
    task.preDelay = GetValueOrDefault(j, "preDelay", 0);
    task.postDelay = GetValueOrDefault(j, "postDelay", 0);
    task.waitMilliseconds = GetValueOrDefault(j, "waitMs", 0);
    task.timeoutMilliseconds = GetValueOrDefault(j, "timeoutMs", 10000);
    task.pollIntervalMilliseconds = GetValueOrDefault(j, "pollIntervalMs", 0);
    task.monitorStableMilliseconds = GetValueOrDefault(j, "monitorStableMs", 0);
    task.retryTimes = GetValueOrDefault(j, "retryTimes", 0);
    task.retryDelayMilliseconds = GetValueOrDefault(j, "retryDelayMs", 750);

    task.next = GetValueOrDefault<std::vector<std::string>>(j, "next", {});
    task.onErrorNext = GetValueOrDefault<std::vector<std::string>>(j, "onErrorNext", {});
    task.exceededNext = GetValueOrDefault<std::vector<std::string>>(j, "exceededNext", {});
    task.sub = GetValueOrDefault<std::vector<std::string>>(j, "sub", {});
    task.monitorTasks = GetValueOrDefault<std::vector<std::string>>(j, "monitorTasks", {});
    task.successTask = GetOptional<std::string>(j, "successTask");
    task.successTasks = GetValueOrDefault<std::vector<std::string>>(j, "successTasks", {});
    task.maxTimes = GetValueOrDefault(j, "maxTimes", 0);
    task.required = GetValueOrDefault(j, "required", true);
    task.success = GetValueOrDefault(j, "success", false);
    task.countAs = GetOptional<std::string>(j, "countAs");
    task.countKey = GetOptional<std::string>(j, "countKey");
    task.targetText = GetOptional<std::string>(j, "targetText");
    task.ocrLanguage = GetOptional<std::string>(j, "ocrLanguage");
    task.fuzzyThreshold = GetValueOrDefault(j, "fuzzyThreshold", 0.86);
    task.unique = GetValueOrDefault(j, "unique", true);
    task.clickOffset = GetOptional<std::vector<int>>(j, "clickOffset");
    task.clickAnchor = GetOptional<std::vector<int>>(j, "clickAnchor");

    if (j.contains("clickVerification") && !j["clickVerification"].is_null())
    {
        task.clickVerification = ParseClickVerification(j["clickVerification"]);
    }

    task.rowExpansion = GetOptional<std::vector<int>>(j, "rowExpansion");
    task.maxScrolls = GetValueOrDefault(j, "maxScrolls", 0);
    task.ocrGroupRowHeight = GetValueOrDefault(j, "ocrGroupRowHeight", 0);
    task.ocrRowGap = GetValueOrDefault(j, "ocrRowGap", 0);
    task.ocrMatchMode = GetOptional<std::string>(j, "ocrMatchMode");
    task.ocrRequireAllTokens = GetValueOrDefault(j, "ocrRequireAllTokens", true);
    task.triggerTask = GetOptional<std::string>(j, "triggerTask");
    task.triggerChain = GetValueOrDefault<std::vector<std::string>>(j, "triggerChain", {});
    task.successConfirmDelayMs = GetValueOrDefault(j, "successConfirmDelayMs", 0);

    return task;
}

} // namespace

std::optional<HachimiPipelineDefinition> HachimiPipelineLoader::LoadFromJsonString(
    const std::string& jsonContent,
    const std::string& baseDirectory)
{
    try
    {
        auto root = Json::parse(jsonContent);
        auto metadata = Resources::ReadHachimiV1PipelineMetadata(root);
        if (!metadata)
            return std::nullopt;

        HachimiPipelineDefinition def;
        def.schemaVersion = metadata->schemaVersion;
        def.name = std::move(metadata->name);
        def.start = std::move(metadata->start);
        def.description = std::move(metadata->description);
        def.referenceWidth = metadata->referenceWidth;
        def.referenceHeight = metadata->referenceHeight;
        def.timing.pollIntervalMilliseconds = metadata->pollIntervalMilliseconds;
        def.baseDirectory = baseDirectory;

        if (root.contains("tasks"))
        {
            if (!root["tasks"].is_object())
                return std::nullopt;
            for (const auto& [taskName, taskJson] : root["tasks"].items())
            {
                if (!HasOnlyKeys(taskJson, {"outcome", "algorithm", "action", "pipeline", "entry", "swipe",
                    "inputText", "keyCode", "template", "templateCollection", "referenceWidth", "referenceHeight",
                    "templThreshold", "alternativeTemplateTasks", "roi", "fallbackRoi", "fallbackClickOffset",
                    "clickUntilGone", "reuseLastMatchOnRetry", "maxClickAttempts", "repeatTapUntilTransition",
                    "goneConfirmationSamples", "transitionTemplates", "pollTransitionTemplatesTogether",
                    "transitionRoi", "transitionRois", "transitionThreshold", "transitionTimeoutMs",
                    "transitionPollIntervalMs", "specificRect", "repeat", "repeat_delay", "searchRois", "minScoreGap", "scaleCandidates",
                    "preDelay", "postDelay", "waitMs", "timeoutMs", "pollIntervalMs", "monitorStableMs",
                    "retryTimes", "retryDelayMs", "next", "onErrorNext", "exceededNext", "sub", "monitorTasks",
                    "successTask", "successTasks", "maxTimes", "required", "success", "countAs", "countKey",
                    "targetText", "ocrLanguage", "fuzzyThreshold", "unique", "clickOffset", "clickAnchor",
                    "clickVerification", "rowExpansion", "maxScrolls", "ocrGroupRowHeight", "ocrRowGap",
                    "ocrMatchMode", "ocrRequireAllTokens", "triggerTask", "triggerChain", "successConfirmDelayMs"}))
                    return std::nullopt;

                auto task = ParseTask(taskJson);
                if (task.repeat < 1 || task.repeat > 100 || task.repeatDelayMilliseconds < 0
                    || task.repeatDelayMilliseconds > 10000)
                    return std::nullopt;
                if (task.repeat > 1 && !CaseInsensitiveEqual{}(task.action, "ClickRect")
                    && !CaseInsensitiveEqual{}(task.action, "ClickSpecificRect"))
                    return std::nullopt;
                if (task.repeat > 1 && (!task.specificRect || task.specificRect->size() != 4))
                    return std::nullopt;
                if (!taskJson.contains("referenceWidth"))
                    task.referenceWidth = def.referenceWidth;
                if (!taskJson.contains("referenceHeight"))
                    task.referenceHeight = def.referenceHeight;
                if (task.clickVerification &&
                    !HasOnlyKeys(taskJson["clickVerification"], {"preCheck", "probeRadius", "probeOffset", "hueMin",
                        "hueMax", "saturationMin", "saturationMax", "valueMin", "valueMax", "minimumMatchRatio",
                        "settleDelayMs", "verifyTimeoutMs", "verifyPollIntervalMs", "maxRetries", "retryDelayMs"}))
                    return std::nullopt;
                if (def.tasks.find(taskName) != def.tasks.end())
                    return std::nullopt;
                def.taskOrder.push_back(taskName);
                def.tasks.emplace(taskName, std::move(task));
            }
        }

        return def;
    }
    catch (const std::exception&)
    {
        return std::nullopt;
    }
}

std::optional<HachimiPipelineDefinition> HachimiPipelineLoader::LoadFromFile(
    const std::string& filePath)
{
    try
    {
        const auto path = Paths::FromUtf8(filePath);
        std::ifstream file(path);
        if (!file.is_open())
            return std::nullopt;

        std::string content((std::istreambuf_iterator<char>(file)),
                            std::istreambuf_iterator<char>());
        std::string baseDir = Paths::ToUtf8(path.parent_path());

        return LoadFromJsonString(content, baseDir);
    }
    catch (const std::exception&)
    {
        return std::nullopt;
    }
}

} // namespace UmaAssistant::TaskEngine
