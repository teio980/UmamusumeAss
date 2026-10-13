#include "CareerVisualResources.hpp"

#include "Common/PathEncoding.hpp"
#include "UmaAssistantCore/TaskEngine/HachimiPipelineLoader.hpp"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <cctype>
#include <fstream>
#include <iterator>
#include <map>
#include <sstream>
#include <stdexcept>

namespace UmaAssistant::Career
{

namespace
{

using Json = nlohmann::json;

std::string Key(const std::string& value)
{
    std::string result;
    result.reserve(value.size());
    for (unsigned char c : value)
        result.push_back(static_cast<char>(std::tolower(c)));
    return result;
}

std::string PathKey(const std::filesystem::path& path)
{
    const auto normalized = std::filesystem::absolute(path).lexically_normal().generic_u8string();
    std::string result(reinterpret_cast<const char*>(normalized.data()), normalized.size());
    std::transform(result.begin(), result.end(), result.begin(), [](unsigned char value) {
        return static_cast<char>(std::tolower(value));
    });
    return result;
}

std::filesystem::path AbsoluteNormalized(const std::filesystem::path& path)
{
    return std::filesystem::absolute(path).lexically_normal();
}

bool IsWithinPath(const std::filesystem::path& path, const std::filesystem::path& root)
{
    std::error_code canonicalError;
    auto canonicalRoot = std::filesystem::weakly_canonical(root, canonicalError);
    if (canonicalError)
        canonicalRoot = AbsoluteNormalized(root);
    canonicalError.clear();
    auto canonicalPath = std::filesystem::weakly_canonical(path, canonicalError);
    if (canonicalError)
        canonicalPath = AbsoluteNormalized(path);
    const auto pathKey = PathKey(canonicalPath);
    auto rootKey = PathKey(canonicalRoot);
    while (rootKey.size() > 1 && rootKey.back() == '/')
        rootKey.pop_back();
    if (rootKey == "/")
        return !pathKey.empty() && pathKey.front() == '/';
    return pathKey == rootKey
        || (pathKey.size() > rootKey.size()
            && pathKey.compare(0, rootKey.size(), rootKey) == 0
            && pathKey[rootKey.size()] == '/');
}

std::optional<TaskEngine::TaskRoi> ParseRoi(const Json& value)
{
    if (!value.is_array() || value.size() < 4)
        return std::nullopt;
    try
    {
        return TaskEngine::TaskRoi{
            value[0].get<int>(), value[1].get<int>(), value[2].get<int>(), value[3].get<int>()};
    }
    catch (...)
    {
        return std::nullopt;
    }
}

std::filesystem::path ResolveInsideCareerRoot(
    const std::filesystem::path& allowedRoot,
    const std::filesystem::path& profileDirectory,
    const std::string& relativePath)
{
    auto path = Paths::FromUtf8(relativePath);
    if (path.is_relative())
        path = profileDirectory / path;
    path = path.lexically_normal();
    const auto root = std::filesystem::absolute(allowedRoot).lexically_normal();
    const auto absolute = std::filesystem::absolute(path).lexically_normal();
    auto relative = absolute.lexically_relative(root);
    if (relative.empty() || (!relative.empty() && *relative.begin() == ".."))
        return {};
    return absolute;
}

bool ContainsText(const std::vector<TaskEngine::TaskOcrDetection>& detections, const std::string& expected)
{
    auto normalize = [](const std::string& text) {
        std::string result;
        for (unsigned char c : text)
        {
            if (std::isalnum(c))
                result.push_back(static_cast<char>(std::tolower(c)));
        }
        return result;
    };
    const auto wanted = normalize(expected);
    if (wanted.empty())
        return true;
    for (const auto& detection : detections)
    {
        if (normalize(detection.text).find(wanted) != std::string::npos)
            return true;
    }
    return false;
}

} // namespace

CareerVisualResources::CareerVisualResources(
    const CareerRuntimeConfig& config,
    TaskEngine::ActionRegistry actionRegistry,
    std::vector<CareerJsonOverride> overrides)
    : m_actionRegistry(std::move(actionRegistry))
{
    m_resourceRoot = AbsoluteNormalized(config.resourceDirectory);
    for (auto& entry : overrides)
    {
        if (entry.path.find('\0') != std::string::npos || entry.rawJson.find('\0') != std::string::npos)
            throw std::invalid_argument("Career JSON override path and content must not contain NUL bytes.");
        auto path = Paths::FromUtf8(entry.path);
        if (!path.is_absolute() || !IsWithinPath(path, m_resourceRoot))
            throw std::invalid_argument("Career JSON override path must be absolute and remain inside resourceRoot.");
        const auto key = PathKey(path);
        if (!m_sourceOverrides.emplace(key, std::move(entry.rawJson)).second)
            throw std::invalid_argument("Career JSON override paths must be unique.");
        try
        {
            (void)Json::parse(m_sourceOverrides.at(key));
        }
        catch (const std::exception& exception)
        {
            throw std::invalid_argument(std::string("Career JSON override is invalid JSON: ") + exception.what());
        }
    }
    m_careerRoot = m_resourceRoot / "hachimi" / "career";
    LoadProfiles(config);
}

std::optional<std::string> CareerVisualResources::ReadSourceText(const std::filesystem::path& path)
{
    const auto absolutePath = AbsoluteNormalized(path);
    const auto key = PathKey(absolutePath);
    std::optional<std::string> content;
    if (const auto override = m_sourceOverrides.find(key); override != m_sourceOverrides.end())
    {
        content = override->second;
    }
    else
    {
        try
        {
            std::ifstream file(absolutePath, std::ios::binary);
            if (!file.is_open())
                return std::nullopt;
            content.emplace(std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>());
        }
        catch (...)
        {
            return std::nullopt;
        }
    }

    const auto position = m_sourceFileIndices.find(key);
    if (position == m_sourceFileIndices.end())
    {
        m_sourceFileIndices.emplace(key, m_sourceJsonFiles.size());
        m_sourceJsonFiles.push_back({Paths::ToUtf8(absolutePath), *content});
    }
    else
    {
        m_sourceJsonFiles[position->second].rawJson = *content;
    }
    return content;
}

std::optional<nlohmann::json> CareerVisualResources::ReadSourceJson(const std::filesystem::path& path)
{
    const auto content = ReadSourceText(path);
    if (!content)
        return std::nullopt;
    try
    {
        return Json::parse(*content);
    }
    catch (...)
    {
        return std::nullopt;
    }
}

const CareerVisualScreenDefinition* CareerVisualResources::FindScreen(
    const std::string& screenId) const noexcept
{
    auto it = std::find_if(m_screens.begin(), m_screens.end(), [&](const auto& screen) {
        return Key(screen.screenId) == Key(screenId);
    });
    return it == m_screens.end() ? nullptr : &*it;
}

const CareerVisualActionDefinition* CareerVisualResources::FindAction(
    const std::string& screenId,
    const std::string& semanticId) const noexcept
{
    const auto* screen = FindScreen(screenId);
    if (!screen)
        return nullptr;
    auto it = screen->actions.find(Key(semanticId));
    return it == screen->actions.end() ? nullptr : &it->second;
}

const CareerVisualAssetDefinition* CareerVisualResources::FindAsset(
    const std::string& semanticId) const noexcept
{
    const auto it = m_assets.find(Key(semanticId));
    return it == m_assets.end() ? nullptr : &it->second;
}

std::optional<CareerVisualAssetDefinition> CareerVisualResources::ResolveCollectionAsset(
    const std::string& semanticId) const noexcept
{
    try
    {
        const auto separator = semanticId.find(':');
        if (separator == std::string::npos || separator == 0 || separator + 1 >= semanticId.size())
            return std::nullopt;
        const auto collectionId = Key(semanticId.substr(0, separator));
        const auto itemName = semanticId.substr(separator + 1);
        if (itemName.find_first_of("/\\:") != std::string::npos || itemName == "." || itemName == "..")
            return std::nullopt;
        const auto collection = m_collections.find(collectionId);
        if (collection == m_collections.end() || !collection->second.is_object())
            return std::nullopt;
        const auto& definition = collection->second;
        if (!definition.contains("root") || !definition["root"].is_string()
            || !definition.contains("pattern") || !definition["pattern"].is_string())
            return std::nullopt;

        const auto resolvePattern = [&](const std::string& pattern) -> std::optional<std::filesystem::path> {
            const auto open = pattern.find('{');
            const auto close = open == std::string::npos ? std::string::npos : pattern.find('}', open + 1);
            if (open == std::string::npos || close == std::string::npos)
                return std::nullopt;
            auto relative = pattern;
            relative.replace(open, close - open + 1, itemName);
            auto combined = (std::filesystem::path(definition["root"].get<std::string>()) / relative).generic_string();
            auto resolved = ResolveInsideCareerRoot(m_resourceRoot, m_catalogDirectory, combined);
            if (resolved.empty() || !std::filesystem::is_regular_file(resolved))
                return std::nullopt;
            return resolved;
        };

        const auto primaryPattern = definition["pattern"].get<std::string>();
        auto path = resolvePattern(primaryPattern);
        if (!path && definition.contains("legacyPatterns") && definition["legacyPatterns"].is_array())
        {
            for (const auto& pattern : definition["legacyPatterns"])
            {
                if (pattern.is_string())
                    path = resolvePattern(pattern.get<std::string>());
                if (path) break;
            }
        }
        if (!path)
            return std::nullopt;
        CareerVisualAssetDefinition asset;
        asset.semanticId = semanticId;
        asset.path = Paths::ToUtf8(*path);
        return asset;
    }
    catch (...)
    {
        return std::nullopt;
    }
}

const nlohmann::json* CareerVisualResources::FindRegion(const std::string& regionId) const noexcept
{
    const auto it = m_regions.find(Key(regionId));
    return it == m_regions.end() ? nullptr : &it->second;
}

const TaskEngine::HachimiPipelineTask* CareerVisualResources::FindExecutionTask(
    const std::string& taskName) const noexcept
{
    return m_execution.TryGetTask(taskName);
}

TaskEngine::TaskMatchResult CareerVisualResources::MatchAsset(
    const std::string& semanticId,
    TaskEngine::ITaskExecutionContext& context,
    std::uint64_t requiredFrameId,
    const std::string& algorithm) const
{
    const auto* asset = FindAsset(semanticId);
    if (!asset)
        return {};
    const auto frame = context.GetCurrentFrame();
    if (requiredFrameId != 0 && frame && frame->frameId != 0 && frame->frameId != requiredFrameId)
        return {};
    auto match = context.MatchTemplateAdvanced(
        asset->path, asset->threshold, asset->roi, algorithm, 900, 1600, {});
    if (requiredFrameId != 0 && match.frameId != 0 && match.frameId != requiredFrameId)
        return {};
    return match;
}

TaskEngine::TaskGraphRunResult CareerVisualResources::RunAction(
    const std::string& screenId,
    const std::string& semanticId,
    TaskEngine::ITaskExecutionContext& context,
    const TaskEngine::TaskGraphRunOptions& options) const
{
    TaskEngine::TaskGraphRunResult failed;
    failed.status = TaskEngine::TaskGraphStatus::Failed;
    const auto* screen = FindScreen(screenId);
    const auto* action = FindAction(screenId, semanticId);
    if (!m_ready || !screen || !action)
    {
        failed.errorMessage = "Career visual action is not registered: " + screenId + "." + semanticId;
        return failed;
    }
    if (!action->valid)
    {
        failed.errorMessage = action->error.empty()
            ? "Career visual action resource is invalid: " + screenId + "." + semanticId
            : action->error;
        return failed;
    }
    if (!m_executionLoaded)
    {
        failed.errorMessage = "Career execution definitions are unavailable for " + screenId;
        return failed;
    }

    TaskEngine::TaskGraphRunner runner(m_actionRegistry);
    return runner.Run(m_execution, action->taskName, context, options);
}

void CareerVisualResources::LoadProfiles(const CareerRuntimeConfig& config)
{
    const auto resourceRoot = m_resourceRoot;
    auto manifestPath = config.manifestPath.empty()
        ? (resourceRoot / "hachimi" / "ura" / "manifest.json")
        : Paths::FromUtf8(config.manifestPath);
    if (manifestPath.is_relative())
        manifestPath = resourceRoot / manifestPath;
    manifestPath = std::filesystem::absolute(manifestPath).lexically_normal();
    const auto manifest = ReadSourceJson(manifestPath);
    if (!manifest || !manifest->is_object() || manifest->value("schemaVersion", 0) != 2
        || !manifest->contains("scenarioId") || !(*manifest)["scenarioId"].is_string()
        || !manifest->contains("screens") || !(*manifest)["screens"].is_array()
        || !manifest->contains("execution") || !(*manifest)["execution"].is_array()
        || !manifest->contains("resourceCatalog") || !(*manifest)["resourceCatalog"].is_string())
    {
        m_diagnostics.push_back("Career manifest is missing, not schema v2, or malformed: "
            + Paths::ToUtf8(manifestPath));
        return;
    }
    m_manifestDocument = *manifest;
    m_scenarioId = (*manifest)["scenarioId"].get<std::string>();
    if (manifest->contains("dataVersion") && (*manifest)["dataVersion"].is_string())
        m_resourceVersion = (*manifest)["dataVersion"].get<std::string>();
    if (m_scenarioId.empty())
    {
        m_diagnostics.push_back("Career manifest has an empty scenarioId.");
        return;
    }

    const auto manifestDirectory = manifestPath.parent_path();
    for (const auto* name : {"definition", "objectives", "races", "events"})
    {
        const auto reference = manifest->find(name);
        if (reference == manifest->end()) continue;
        if (!reference->is_string())
        {
            m_diagnostics.push_back(std::string("Career scenario document reference is invalid: ") + name);
            return;
        }
        const auto path = ResolveInsideCareerRoot(resourceRoot, manifestDirectory, reference->get<std::string>());
        const auto document = path.empty() ? std::optional<Json>{} : ReadSourceJson(path);
        if (!document || !document->is_object() ||
            document->value("scenarioId", std::string{}) != m_scenarioId)
        {
            m_diagnostics.push_back(std::string("Career scenario document is missing or mismatched: ") + name);
            return;
        }
        m_scenarioDocuments.emplace(name, *document);
    }
    const auto catalogPath = ResolveInsideCareerRoot(
        resourceRoot, manifestDirectory, (*manifest)["resourceCatalog"].get<std::string>());
    if (catalogPath.empty())
    {
        m_diagnostics.push_back("Career manifest resourceCatalog path leaves the configured resource root.");
        return;
    }
    m_catalogDirectory = catalogPath.parent_path();
    const auto rootJson = ReadSourceJson(catalogPath);
    if (!rootJson || !rootJson->is_object()
        || rootJson->value("schemaVersion", 0) != 1
        || !rootJson->contains("assets") || !(*rootJson)["assets"].is_object())
    {
        m_diagnostics.push_back("Career catalog is missing or has an unsupported schema: "
            + Paths::ToUtf8(catalogPath));
        return;
    }

    for (auto it = (*rootJson)["assets"].begin(); it != (*rootJson)["assets"].end(); ++it)
    {
        if (!it.value().is_object() || !it.value().contains("path") || !it.value()["path"].is_string())
            continue;
        CareerVisualAssetDefinition asset;
        asset.semanticId = it.key();
        const auto resolved = ResolveInsideCareerRoot(
            resourceRoot, catalogPath.parent_path(), it.value()["path"].get<std::string>());
        if (resolved.empty())
        {
            m_diagnostics.push_back(asset.semanticId + ": catalog asset escapes fixed career resource root");
            continue;
        }
        asset.path = Paths::ToUtf8(resolved);
        asset.threshold = it.value().value("threshold", 0.86);
        if (it.value().contains("roi"))
            asset.roi = ParseRoi(it.value()["roi"]);
        if (!std::filesystem::exists(resolved))
        {
            m_diagnostics.push_back(asset.semanticId + ": missing catalog asset: " + asset.path);
            continue;
        }
        m_assets.emplace(Key(asset.semanticId), std::move(asset));
    }

    if (rootJson->contains("collections") && (*rootJson)["collections"].is_object())
    {
        for (auto it = (*rootJson)["collections"].begin(); it != (*rootJson)["collections"].end(); ++it)
            if (it.value().is_object())
                m_collections.emplace(Key(it.key()), it.value());
    }

    if (rootJson->contains("regions") && (*rootJson)["regions"].is_object())
    {
        for (auto it = (*rootJson)["regions"].begin(); it != (*rootJson)["regions"].end(); ++it)
            m_regions.emplace(Key(it.key()), it.value());
    }

    std::vector<std::filesystem::path> profileFiles;
    for (const auto& reference : (*manifest)["screens"])
    {
        if (!reference.is_string())
            continue;
        const auto resolved = ResolveInsideCareerRoot(resourceRoot, manifestDirectory, reference.get<std::string>());
        if (resolved.empty())
        {
            m_diagnostics.push_back("Career screen profile path leaves the configured resource root.");
            return;
        }
        profileFiles.push_back(resolved);
    }
    if (profileFiles.empty() || (*manifest)["execution"].empty())
    {
        m_diagnostics.push_back("Career manifest requires ordered non-empty screen and execution fragment arrays.");
        return;
    }

    std::unordered_map<std::string, std::string> taskErrors;
    for (const auto& reference : (*manifest)["execution"])
    {
        if (!reference.is_string())
            continue;
        const auto executionPath = ResolveInsideCareerRoot(
            resourceRoot, manifestDirectory, reference.get<std::string>());
        if (executionPath.empty())
        {
            m_diagnostics.push_back("Career execution fragment path leaves the configured resource root.");
            return;
        }
        const auto fragmentContent = ReadSourceText(executionPath);
        auto fragment = fragmentContent
            ? TaskEngine::HachimiPipelineLoader::LoadFromJsonString(
                *fragmentContent, Paths::ToUtf8(executionPath.parent_path()))
            : std::nullopt;
        if (!fragment)
        {
            m_diagnostics.push_back("Could not load career execution fragment: " + Paths::ToUtf8(executionPath));
            return;
        }
        Json fragmentDocument;
        try
        {
            fragmentDocument = Json::parse(*fragmentContent);
        }
        catch (const std::exception& exception)
        {
            m_diagnostics.push_back("Could not parse career execution fragment "
                + Paths::ToUtf8(executionPath) + ": " + exception.what());
            return;
        }
        m_executionFragmentPaths.push_back(Paths::ToUtf8(executionPath));
        if (m_execution.taskOrder.empty())
        {
            m_execution.name = fragment->name;
            m_execution.schemaVersion = fragment->schemaVersion;
            m_execution.start = fragment->start;
            m_execution.description = fragment->description;
            m_execution.referenceWidth = fragment->referenceWidth;
            m_execution.referenceHeight = fragment->referenceHeight;
            m_execution.timing = fragment->timing;
            m_executionDocument = fragmentDocument;
            m_executionDocument["tasks"] = Json::object();
        }
        else if (m_execution.referenceWidth != fragment->referenceWidth
            || m_execution.referenceHeight != fragment->referenceHeight)
        {
            m_diagnostics.push_back("Career execution fragments use different reference resolutions.");
            return;
        }

        const auto sourceDirectory = Paths::FromUtf8(fragment->baseDirectory);
        for (const auto& taskName : fragment->taskOrder)
        {
            auto taskPosition = fragment->tasks.find(taskName);
            if (taskPosition == fragment->tasks.end())
                continue;
            auto& task = taskPosition->second;
            auto rebase = [&](std::string& path, const std::string& label) {
                if (path.empty())
                    return;
                auto resolved = Paths::FromUtf8(path);
                if (resolved.is_relative())
                    resolved = sourceDirectory / resolved;
                resolved = std::filesystem::absolute(resolved).lexically_normal();
                const auto relative = resolved.lexically_relative(resourceRoot);
                if (relative.empty() || *relative.begin() == "..")
                {
                    taskErrors[Key(taskName)] = "Resource path escapes the configured resource root for task "
                        + taskName + " (" + label + "): " + path;
                    return;
                }
                if (!std::filesystem::exists(resolved))
                    taskErrors[Key(taskName)] = "Resource file is missing for task " + taskName + ": "
                        + Paths::ToUtf8(resolved);
                path = Paths::ToUtf8(resolved);
            };
            if (task.templatePath)
                rebase(*task.templatePath, "template");
            for (auto& transitionTemplate : task.transitionTemplates)
                rebase(transitionTemplate, "transition template");
            if (task.pipeline)
            {
                auto resolved = Paths::FromUtf8(*task.pipeline);
                if (resolved.is_relative())
                    resolved = sourceDirectory / resolved;
                *task.pipeline = Paths::ToUtf8(std::filesystem::absolute(resolved).lexically_normal());
            }

            if (m_execution.tasks.find(taskName) != m_execution.tasks.end())
            {
                m_diagnostics.push_back("Duplicate task name across career execution fragments: " + taskName);
                return;
            }
            m_execution.taskOrder.push_back(taskName);
            m_execution.tasks.emplace(taskName, task);

            Json projectedTask = fragmentDocument["tasks"].value(taskName, Json::object());
            if (!projectedTask.is_object())
            {
                m_diagnostics.push_back("Career execution task must be an object: " + taskName);
                return;
            }
            if (task.templatePath)
                projectedTask["template"] = *task.templatePath;
            else
                projectedTask.erase("template");
            projectedTask["transitionTemplates"] = task.transitionTemplates;
            if (task.pipeline)
                projectedTask["pipeline"] = *task.pipeline;
            else
                projectedTask.erase("pipeline");
            projectedTask["referenceWidth"] = task.referenceWidth;
            projectedTask["referenceHeight"] = task.referenceHeight;
            m_executionDocument["tasks"][taskName] = std::move(projectedTask);
        }
    }
    m_execution.baseDirectory = Paths::ToUtf8(resourceRoot);
    m_executionLoaded = !m_execution.tasks.empty();

    for (const auto& profilePath : profileFiles)
    {
        const auto profile = ReadSourceJson(profilePath);
        if (!profile || !profile->is_object() || !profile->contains("screens")
            || !(*profile)["screens"].is_array())
        {
            m_diagnostics.push_back("Career screen profile could not be loaded: " + Paths::ToUtf8(profilePath));
            continue;
        }
        if (!profile->contains("scenarioId") || !(*profile)["scenarioId"].is_string()
            || Key((*profile)["scenarioId"].get<std::string>()) != Key(m_scenarioId))
        {
            m_diagnostics.push_back("Career screen profile scenarioId does not match manifest: "
                + Paths::ToUtf8(profilePath));
            return;
        }

        for (auto option = profile->begin(); option != profile->end(); ++option)
        {
            if (option.key() != "screens")
                m_profileOptions[option.key()] = option.value();
        }

        const auto profileDirectory = profilePath.parent_path();
        for (const auto& item : (*profile)["screens"])
        {
            if (!item.is_object() || !item.contains("screenId") || !item["screenId"].is_string())
                continue;
            CareerVisualScreenDefinition screen;
            screen.screenId = item["screenId"].get<std::string>();
            screen.flow = item.value("flow", std::string{});
            screen.profilePath = profilePath;

            const auto recognition = item.value("recognition", Json::object());
            if (recognition.is_object())
            {
                screen.priority = recognition.value("priority", 0);
                screen.stable = recognition.value("stable", true);
                screen.verifyRequiredText = recognition.value("verifyRequiredText", false);
                if (recognition.contains("stateSignatureRoi"))
                    screen.stateSignatureRoi = ParseRoi(recognition["stateSignatureRoi"]);
                if (recognition.contains("requiredText") && recognition["requiredText"].is_array())
                {
                    for (const auto& text : recognition["requiredText"])
                    {
                        if (text.is_string())
                            screen.requiredText.push_back(text.get<std::string>());
                    }
                }

                std::vector<std::string> relativeTemplates;
                if (recognition.contains("template") && recognition["template"].is_string())
                    relativeTemplates.push_back(recognition["template"].get<std::string>());
                if (recognition.contains("templates") && recognition["templates"].is_array())
                {
                    for (const auto& path : recognition["templates"])
                    {
                        if (path.is_string())
                            relativeTemplates.push_back(path.get<std::string>());
                    }
                }

                const auto roi = recognition.contains("roi") ? ParseRoi(recognition["roi"]) : std::nullopt;
                std::string algorithm = "MatchTemplate";
                if (recognition.value("matchColorText", false))
                    algorithm = "MatchTemplateColorText";
                else if (recognition.value("matchColor", false))
                    algorithm = "MatchTemplateColor";
                for (const auto& relativeTemplate : relativeTemplates)
                {
                    CareerTemplateDefinition templ;
                    const auto resolved = ResolveInsideCareerRoot(resourceRoot, profileDirectory, relativeTemplate);
                    if (resolved.empty())
                    {
                        m_diagnostics.push_back(screen.screenId + ": template escapes fixed career resource root: "
                            + relativeTemplate);
                        continue;
                    }
                    templ.path = Paths::ToUtf8(resolved);
                    templ.threshold = recognition.value("templThreshold", templ.threshold);
                    templ.roi = roi;
                    templ.algorithm = algorithm;
                    templ.requiresAlphaMatching = recognition.value("matchAlphaTemplate", false);
                    if (!std::filesystem::exists(resolved))
                    {
                        m_diagnostics.push_back(screen.screenId + ": missing template: " + templ.path);
                        continue;
                    }
                    screen.templates.push_back(std::move(templ));
                }

                std::vector<std::string> requiredTemplatePaths;
                if (recognition.contains("requiredTemplate") && recognition["requiredTemplate"].is_string())
                    requiredTemplatePaths.push_back(recognition["requiredTemplate"].get<std::string>());
                if (recognition.contains("requiredTemplates") && recognition["requiredTemplates"].is_array())
                {
                    for (const auto& path : recognition["requiredTemplates"])
                    {
                        if (path.is_string())
                            requiredTemplatePaths.push_back(path.get<std::string>());
                    }
                }
                const auto requiredRoi = recognition.contains("requiredTemplateRoi")
                    ? ParseRoi(recognition["requiredTemplateRoi"])
                    : std::nullopt;
                for (const auto& requiredPath : requiredTemplatePaths)
                {
                    CareerTemplateDefinition required;
                    const auto resolved = ResolveInsideCareerRoot(resourceRoot, profileDirectory, requiredPath);
                    if (resolved.empty())
                    {
                        m_diagnostics.push_back(screen.screenId + ": required template escapes configured resource root: "
                            + requiredPath);
                        continue;
                    }
                    required.path = Paths::ToUtf8(resolved);
                    required.threshold = recognition.value("requiredTemplateThreshold", required.threshold);
                    required.roi = requiredRoi;
                    required.algorithm = algorithm;
                    required.requiresAlphaMatching = recognition.value("requiredTemplateMatchAlphaTemplate", false);
                    if (!std::filesystem::exists(resolved))
                    {
                        m_diagnostics.push_back(screen.screenId + ": missing required template: " + required.path);
                        continue;
                    }
                    screen.requiredTemplates.push_back(std::move(required));
                }
            }

            if (item.contains("ocrRegions") && item["ocrRegions"].is_array())
            {
                for (const auto& region : item["ocrRegions"])
                {
                    if (!region.is_object() || !region.contains("semanticId")
                        || !region["semanticId"].is_string() || !region.contains("bounds"))
                        continue;
                    const auto bounds = region["bounds"];
                    if (!bounds.is_object())
                        continue;
                    try
                    {
                        screen.ocrRegions.push_back({
                            region["semanticId"].get<std::string>(),
                            TaskEngine::TaskRoi{
                                bounds.at("x").get<int>(), bounds.at("y").get<int>(),
                                bounds.at("width").get<int>(), bounds.at("height").get<int>()},
                            region.contains("numericRoi") ? ParseRoi(region["numericRoi"]) : std::nullopt});
                    }
                    catch (...)
                    {
                        m_diagnostics.push_back(screen.screenId + ": malformed OCR region in profile");
                    }
                }
            }

            if (item.contains("observations") && item["observations"].is_object()
                && item["observations"].contains("energyBar"))
            {
                const auto bar = item["observations"]["energyBar"];
                if (bar.is_object() && bar.contains("roi") && bar["roi"].is_array())
                {
                    CareerEnergyBarDefinition energy;
                    energy.roi = bar["roi"].get<std::vector<int>>();
                    if (bar.contains("innerInset") && bar["innerInset"].is_array())
                        energy.innerInset = bar["innerInset"].get<std::vector<int>>();
                    energy.minimumConfidence = bar.value("minimumConfidence", 0.7);
                    energy.fillSaturationMin = bar.value("fillSaturationMin", 0.18);
                    energy.fillValueMin = bar.value("fillValueMin", 0.18);
                    energy.columnMatchRatio = bar.value("columnMatchRatio", 0.55);
                    screen.energyBar = std::move(energy);
                }
            }

            if (item.contains("actions") && item["actions"].is_array())
            {
                for (const auto& actionJson : item["actions"])
                {
                    if (!actionJson.is_object() || !actionJson.contains("semanticId")
                        || !actionJson.contains("task") || !actionJson["semanticId"].is_string()
                        || !actionJson["task"].is_string())
                        continue;
                    CareerVisualActionDefinition action;
                    action.semanticId = actionJson["semanticId"].get<std::string>();
                    action.taskName = actionJson["task"].get<std::string>();
                    action.valid = m_executionLoaded && m_execution.TryGetTask(action.taskName) != nullptr;
                    if (!action.valid)
                    {
                        action.error = "Action task is missing from the composed career execution graph: "
                            + action.taskName;
                    }
                    else if (const auto error = taskErrors.find(Key(action.taskName)); error != taskErrors.end())
                    {
                        action.valid = false;
                        action.error = error->second;
                    }
                    screen.actions.emplace(Key(action.semanticId), std::move(action));
                }
            }

            const auto existing = std::find_if(m_screens.begin(), m_screens.end(), [&](const auto& current) {
                return Key(current.screenId) == Key(screen.screenId);
            });
            if (existing == m_screens.end())
            {
                m_screens.push_back(std::move(screen));
                continue;
            }
            existing->priority = (std::min)(existing->priority, screen.priority);
            existing->stable = existing->stable || screen.stable;
            existing->verifyRequiredText = existing->verifyRequiredText || screen.verifyRequiredText;
            if (!existing->stateSignatureRoi && screen.stateSignatureRoi)
                existing->stateSignatureRoi = screen.stateSignatureRoi;
            if (existing->flow.empty())
                existing->flow = screen.flow;
            for (auto& templ : screen.templates)
            {
                if (std::none_of(existing->templates.begin(), existing->templates.end(), [&](const auto& current) {
                        return current.path == templ.path;
                    }))
                    existing->templates.push_back(std::move(templ));
            }
            for (auto& templ : screen.requiredTemplates)
            {
                if (std::none_of(existing->requiredTemplates.begin(), existing->requiredTemplates.end(), [&](const auto& current) {
                        return current.path == templ.path;
                    }))
                    existing->requiredTemplates.push_back(std::move(templ));
            }
            for (auto& region : screen.ocrRegions)
            {
                if (std::none_of(existing->ocrRegions.begin(), existing->ocrRegions.end(), [&](const auto& current) {
                        return Key(current.semanticId) == Key(region.semanticId);
                    }))
                    existing->ocrRegions.push_back(std::move(region));
            }
            if (!existing->energyBar && screen.energyBar)
                existing->energyBar = std::move(screen.energyBar);
            if (existing->requiredText.empty())
                existing->requiredText = std::move(screen.requiredText);
            for (auto& [semanticKey, action] : screen.actions)
            {
                const auto previous = existing->actions.find(semanticKey);
                if (previous == existing->actions.end())
                    existing->actions.emplace(semanticKey, std::move(action));
                else if (previous->second.taskName != action.taskName)
                {
                    previous->second.valid = false;
                    previous->second.error = "Conflicting action bindings for " + existing->screenId + "."
                        + previous->second.semanticId;
                }
            }
        }
    }

    std::stable_sort(m_screens.begin(), m_screens.end(), [](const auto& left, const auto& right) {
        return left.priority < right.priority;
    });
    m_ready = m_executionLoaded && std::any_of(m_screens.begin(), m_screens.end(), [](const auto& screen) {
        return screen.screenId == "career_main" && !screen.templates.empty();
    });
    if (!m_ready)
        m_diagnostics.push_back("Career runtime has no usable career_main screen definition.");
}

nlohmann::json InspectCareerPackage(
    const std::filesystem::path& manifestPath,
    const std::filesystem::path& resourceRoot,
    const std::vector<CareerJsonOverride>& overrides)
{
    using Json = nlohmann::json;

    if (Paths::ToUtf8(manifestPath).find('\0') != std::string::npos
        || Paths::ToUtf8(resourceRoot).find('\0') != std::string::npos)
    {
        return Json{
            {"version", 1}, {"valid", false}, {"ready", false},
            {"manifest_path", ""}, {"resource_root", ""}, {"manifest", nullptr},
            {"files", Json::array()},
            {"execution", Json{{"definition", Json::object()}, {"task_order", Json::array()},
                {"fragment_paths", Json::array()}, {"base_directory", ""}}},
            {"diagnostics", Json::array({"Career manifest and resourceRoot paths must not contain NUL bytes."})}};
    }

    const auto absoluteRoot = AbsoluteNormalized(resourceRoot);
    const auto absoluteManifest = AbsoluteNormalized(manifestPath);
    Json result{
        {"version", 1},
        {"valid", false},
        {"ready", false},
        {"manifest_path", Paths::ToUtf8(absoluteManifest)},
        {"resource_root", Paths::ToUtf8(absoluteRoot)},
        {"manifest", nullptr},
        {"files", Json::array()},
        {"execution", Json{
            {"definition", Json::object()},
            {"task_order", Json::array()},
            {"fragment_paths", Json::array()},
            {"base_directory", Paths::ToUtf8(absoluteRoot)}}},
        {"diagnostics", Json::array()},
    };
    auto addDiagnostic = [&](std::string message) {
        result["diagnostics"].push_back(std::move(message));
    };

    std::unordered_map<std::string, std::string> overridesByPath;
    std::map<std::string, CareerSourceJsonFile> filesByPath;
    std::size_t invalidOverrideIndex = 0;
    for (const auto& overrideFile : overrides)
    {
        if (overrideFile.path.find('\0') != std::string::npos)
        {
            filesByPath.emplace("\xffinvalid-override-" + std::to_string(invalidOverrideIndex++),
                CareerSourceJsonFile{overrideFile.path, overrideFile.rawJson});
            addDiagnostic("Career JSON override path and content must not contain NUL bytes.");
            continue;
        }
        const auto normalizedPath = Paths::FromUtf8(overrideFile.path);
        const auto absolutePath = AbsoluteNormalized(normalizedPath);
        const auto normalizedPathUtf8 = Paths::ToUtf8(absolutePath);
        const auto pathKey = PathKey(absolutePath);
        if (overrideFile.rawJson.find('\0') != std::string::npos)
        {
            filesByPath.insert_or_assign(pathKey, CareerSourceJsonFile{normalizedPathUtf8, overrideFile.rawJson});
            addDiagnostic("Career JSON override path and content must not contain NUL bytes.");
            continue;
        }
        filesByPath.insert_or_assign(pathKey, CareerSourceJsonFile{normalizedPathUtf8, overrideFile.rawJson});
        if (!normalizedPath.is_absolute())
        {
            addDiagnostic("Career JSON override paths must be absolute: " + overrideFile.path);
            continue;
        }
        if (!IsWithinPath(absolutePath, absoluteRoot))
        {
            addDiagnostic("Career JSON override path leaves resourceRoot: " + normalizedPathUtf8);
            continue;
        }
        if (!overridesByPath.emplace(pathKey, overrideFile.rawJson).second)
        {
            addDiagnostic("Career JSON override paths must be unique: " + normalizedPathUtf8);
            continue;
        }
        try
        {
            (void)Json::parse(overrideFile.rawJson);
        }
        catch (const std::exception& exception)
        {
            addDiagnostic("Career JSON override is invalid JSON at " + normalizedPathUtf8 + ": " + exception.what());
        }
    }

    if (!IsWithinPath(absoluteManifest, absoluteRoot))
        addDiagnostic("Career manifest path must remain inside resourceRoot: " + Paths::ToUtf8(absoluteManifest));
    if (!std::filesystem::is_directory(absoluteRoot))
        addDiagnostic("Career resourceRoot is not an existing directory: " + Paths::ToUtf8(absoluteRoot));

    if (!result["diagnostics"].empty())
    {
        for (const auto& [key, file] : filesByPath)
        {
            (void)key;
            result["files"].push_back({{"path", file.path}, {"raw_json", file.rawJson}});
        }
        return result;
    }

    try
    {
        CareerRuntimeConfig config;
        config.resourceDirectory = absoluteRoot;
        config.manifestPath = Paths::ToUtf8(absoluteManifest);
        CareerVisualResources resources(config, {}, overrides);

        if (resources.GetManifestDocument().is_object())
            result["manifest"] = resources.GetManifestDocument();

        Json definition = resources.GetExecutionDocument();
        if (!definition.is_object())
            definition = Json::object();
        definition["tasks"] = resources.GetExecutionDocument().value("tasks", Json::object());
        result["execution"]["definition"] = std::move(definition);
        result["execution"]["task_order"] = resources.GetExecutionDefinition().taskOrder;
        result["execution"]["fragment_paths"] = resources.GetExecutionFragmentPaths();

        auto addFile = [&](const CareerSourceJsonFile& file) {
            filesByPath.insert_or_assign(PathKey(Paths::FromUtf8(file.path)), file);
        };
        for (const auto& file : resources.GetSourceJsonFiles())
            addFile(file);

        // Developer Tools has historically shown every JSON document next to a
        // scenario manifest, including optional metadata such as events and
        // localization. File discovery is presentation-only; package semantics
        // above still come exclusively from CareerVisualResources.
        std::error_code iterationError;
        std::filesystem::recursive_directory_iterator iterator(
            absoluteManifest.parent_path(), std::filesystem::directory_options::skip_permission_denied,
            iterationError);
        const std::filesystem::recursive_directory_iterator end;
        for (; !iterationError && iterator != end; iterator.increment(iterationError))
        {
            std::error_code typeError;
            if (iterator->is_symlink(typeError) || typeError || !iterator->is_regular_file(typeError)
                || typeError || iterator->path().extension() != ".json")
                continue;

            const auto path = AbsoluteNormalized(iterator->path());
            const auto key = PathKey(path);
            if (filesByPath.contains(key))
                continue;

            std::optional<std::string> content;
            if (const auto override = overridesByPath.find(key); override != overridesByPath.end())
                content = override->second;
            else
            {
                std::ifstream file(path, std::ios::binary);
                if (file.is_open())
                    content.emplace(std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>());
            }
            if (!content)
            {
                addDiagnostic("Could not read Career package JSON file: " + Paths::ToUtf8(path));
                continue;
            }
            try
            {
                (void)Json::parse(*content);
            }
            catch (const std::exception& exception)
            {
                addDiagnostic("Career package JSON is invalid at " + Paths::ToUtf8(path) + ": " + exception.what());
            }
            addFile({Paths::ToUtf8(path), std::move(*content)});
        }
        if (iterationError)
            addDiagnostic("Could not enumerate Career package JSON files: " + iterationError.message());

        for (const auto& overrideFile : overrides)
        {
            const auto path = AbsoluteNormalized(Paths::FromUtf8(overrideFile.path));
            addFile({Paths::ToUtf8(path), overrideFile.rawJson});
        }

        result["ready"] = resources.IsReady();
        for (const auto& diagnostic : resources.GetLoadDiagnostics())
            addDiagnostic(diagnostic);
        for (const auto& screen : resources.GetScreens())
        {
            for (const auto& [semanticId, action] : screen.actions)
            {
                (void)semanticId;
                if (action.valid || action.error.empty())
                    continue;
                const auto message = screen.screenId + ": " + action.error;
                const bool alreadyReported = std::any_of(
                    result["diagnostics"].begin(), result["diagnostics"].end(),
                    [&](const Json& current) {
                        return current.is_string() && current.get<std::string>() == message;
                    });
                if (!alreadyReported)
                    addDiagnostic(message);
            }
        }
        result["valid"] = resources.IsReady() && resources.GetLoadDiagnostics().empty()
            && result["diagnostics"].empty();
    }
    catch (const std::exception& exception)
    {
        addDiagnostic(std::string("Career package inspection failed: ") + exception.what());
    }

    for (const auto& [key, file] : filesByPath)
    {
        (void)key;
        result["files"].push_back({{"path", file.path}, {"raw_json", file.rawJson}});
    }
    return result;
}

} // namespace UmaAssistant::Career
