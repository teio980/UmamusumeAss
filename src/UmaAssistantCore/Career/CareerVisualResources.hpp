#pragma once

#include "CareerRuntimeConfig.hpp"
#include "UmaAssistantCore/TaskEngine/HachimiPipelineModel.hpp"
#include "UmaAssistantCore/TaskEngine/ActionRegistry.hpp"
#include "UmaAssistantCore/TaskEngine/TaskGraphRunner.hpp"
#include <nlohmann/json.hpp>

#include <filesystem>
#include <cstdint>
#include <memory>
#include <optional>
#include <string>
#include <unordered_map>
#include <vector>

namespace UmaAssistant::Career
{

struct CareerTemplateDefinition
{
    std::string path;
    // UraScreenRecognition on main defaults both screen markers to 0.78.
    double threshold{0.78};
    std::optional<TaskEngine::TaskRoi> roi;
    std::string algorithm{"MatchTemplate"};
    bool requiresAlphaMatching{false};
};

struct CareerOcrRegionDefinition
{
    std::string semanticId;
    TaskEngine::TaskRoi roi;
    std::optional<TaskEngine::TaskRoi> numericRoi;
};

struct CareerEnergyBarDefinition
{
    std::vector<int> roi;
    std::vector<int> innerInset;
    double minimumConfidence{0.7};
    double fillSaturationMin{0.18};
    double fillValueMin{0.18};
    double columnMatchRatio{0.55};
};

struct CareerVisualActionDefinition
{
    std::string semanticId;
    std::string taskName;
    std::string error;
    bool valid{false};
};

struct CareerVisualAssetDefinition
{
    std::string semanticId;
    std::string path;
    double threshold{0.86};
    std::optional<TaskEngine::TaskRoi> roi;
};

struct CareerVisualScreenDefinition
{
    std::string screenId;
    std::string flow;
    int priority{0};
    bool stable{false};
    std::vector<std::string> requiredText;
    bool verifyRequiredText{false};
    std::optional<TaskEngine::TaskRoi> stateSignatureRoi;
    std::vector<CareerTemplateDefinition> templates;
    std::vector<CareerTemplateDefinition> requiredTemplates;
    std::vector<CareerOcrRegionDefinition> ocrRegions;
    std::optional<CareerEnergyBarDefinition> energyBar;
    std::unordered_map<std::string, CareerVisualActionDefinition> actions;
    std::filesystem::path profilePath;
};

struct CareerJsonOverride
{
    std::string path;
    std::string rawJson;
};

struct CareerSourceJsonFile
{
    std::string path;
    std::string rawJson;
};

class CareerVisualResources
{
public:
    explicit CareerVisualResources(
        const CareerRuntimeConfig& config,
        TaskEngine::ActionRegistry actionRegistry = {},
        std::vector<CareerJsonOverride> overrides = {});

    [[nodiscard]] const std::vector<CareerVisualScreenDefinition>& GetScreens() const noexcept
    {
        return m_screens;
    }
    [[nodiscard]] const CareerVisualScreenDefinition* FindScreen(
        const std::string& screenId) const noexcept;
    [[nodiscard]] const CareerVisualActionDefinition* FindAction(
        const std::string& screenId,
        const std::string& semanticId) const noexcept;
    [[nodiscard]] const CareerVisualAssetDefinition* FindAsset(
        const std::string& semanticId) const noexcept;
    // Resolves an authored catalog collection reference such as
    // `career.runtime_frame:debut_race_result_wait` beneath the fixed package root.
    [[nodiscard]] std::optional<CareerVisualAssetDefinition> ResolveCollectionAsset(
        const std::string& semanticId) const noexcept;
    [[nodiscard]] const nlohmann::json* FindRegion(
        const std::string& regionId) const noexcept;
    [[nodiscard]] const TaskEngine::HachimiPipelineTask* FindExecutionTask(
        const std::string& taskName) const noexcept;
    [[nodiscard]] TaskEngine::TaskMatchResult MatchAsset(
        const std::string& semanticId,
        TaskEngine::ITaskExecutionContext& context,
        std::uint64_t requiredFrameId = 0,
        const std::string& algorithm = "MatchTemplate") const;
    [[nodiscard]] const std::filesystem::path& GetCareerResourceRoot() const noexcept
    {
        return m_careerRoot;
    }
    [[nodiscard]] const std::vector<std::string>& GetLoadDiagnostics() const noexcept
    {
        return m_diagnostics;
    }
    [[nodiscard]] bool IsReady() const noexcept { return m_ready; }
    [[nodiscard]] const std::string& GetScenarioId() const noexcept { return m_scenarioId; }
    [[nodiscard]] const std::string& GetResourceVersion() const noexcept { return m_resourceVersion; }
    [[nodiscard]] const nlohmann::json& GetProfileOptions() const noexcept { return m_profileOptions; }
    [[nodiscard]] const nlohmann::json& GetManifestDocument() const noexcept { return m_manifestDocument; }
    [[nodiscard]] const nlohmann::json& GetExecutionDocument() const noexcept { return m_executionDocument; }
    [[nodiscard]] const TaskEngine::HachimiPipelineDefinition& GetExecutionDefinition() const noexcept
    {
        return m_execution;
    }
    [[nodiscard]] const std::vector<std::string>& GetExecutionFragmentPaths() const noexcept
    {
        return m_executionFragmentPaths;
    }
    [[nodiscard]] const std::vector<CareerSourceJsonFile>& GetSourceJsonFiles() const noexcept
    {
        return m_sourceJsonFiles;
    }
    [[nodiscard]] const nlohmann::json* GetScenarioDocument(const std::string& name) const noexcept
    {
        const auto document = m_scenarioDocuments.find(name);
        return document == m_scenarioDocuments.end() ? nullptr : &document->second;
    }

    [[nodiscard]] TaskEngine::TaskGraphRunResult RunAction(
        const std::string& screenId,
        const std::string& semanticId,
        TaskEngine::ITaskExecutionContext& context,
        const TaskEngine::TaskGraphRunOptions& options = {}) const;

private:
    void LoadProfiles(const CareerRuntimeConfig& config);
    [[nodiscard]] std::optional<std::string> ReadSourceText(const std::filesystem::path& path);
    [[nodiscard]] std::optional<nlohmann::json> ReadSourceJson(const std::filesystem::path& path);

    std::filesystem::path m_careerRoot;
    std::filesystem::path m_resourceRoot;
    std::filesystem::path m_catalogDirectory;
    std::string m_scenarioId;
    std::string m_resourceVersion;
    std::unordered_map<std::string, std::string> m_sourceOverrides;
    std::unordered_map<std::string, std::size_t> m_sourceFileIndices;
    std::vector<CareerSourceJsonFile> m_sourceJsonFiles;
    nlohmann::json m_manifestDocument = nlohmann::json::object();
    nlohmann::json m_executionDocument = nlohmann::json::object();
    std::vector<std::string> m_executionFragmentPaths;
    nlohmann::json m_profileOptions;
    std::unordered_map<std::string, nlohmann::json> m_scenarioDocuments;
    bool m_ready{false};
    bool m_executionLoaded{false};
    std::vector<CareerVisualScreenDefinition> m_screens;
    std::unordered_map<std::string, CareerVisualAssetDefinition> m_assets;
    std::unordered_map<std::string, nlohmann::json> m_collections;
    std::unordered_map<std::string, nlohmann::json> m_regions;
    std::vector<std::string> m_diagnostics;
    TaskEngine::HachimiPipelineDefinition m_execution;
    TaskEngine::ActionRegistry m_actionRegistry;
};

} // namespace UmaAssistant::Career

namespace UmaAssistant::Career
{

// Runs the same production Career resource parser used by native execution and
// returns an editor-friendly snapshot. Overrides are keyed by absolute UTF-8
// paths and are accepted only beneath resourceRoot.
[[nodiscard]] nlohmann::json InspectCareerPackage(
    const std::filesystem::path& manifestPath,
    const std::filesystem::path& resourceRoot,
    const std::vector<CareerJsonOverride>& overrides = {});

} // namespace UmaAssistant::Career
