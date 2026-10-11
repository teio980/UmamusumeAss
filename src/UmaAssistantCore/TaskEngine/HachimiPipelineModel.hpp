#pragma once

#include <string>
#include <vector>
#include <unordered_map>
#include <optional>
#include <algorithm>
#include <cctype>

namespace UmaAssistant::TaskEngine
{

struct CaseInsensitiveHash
{
    std::size_t operator()(const std::string& str) const noexcept
    {
        std::size_t hash = 2166136261u;
        for (char c : str)
        {
            hash ^= static_cast<unsigned char>(std::tolower(static_cast<unsigned char>(c)));
            hash *= 16777619u;
        }
        return hash;
    }
};

struct CaseInsensitiveEqual
{
    bool operator()(const std::string& lhs, const std::string& rhs) const noexcept
    {
        if (lhs.size() != rhs.size())
            return false;
        return std::equal(lhs.begin(), lhs.end(), rhs.begin(),
            [](char a, char b) {
                return std::tolower(static_cast<unsigned char>(a)) ==
                       std::tolower(static_cast<unsigned char>(b));
            });
    }
};

struct HachimiClickVerification
{
    bool preCheck{true};
    int probeRadius{8};
    std::optional<std::vector<int>> probeOffset;
    double hueMin{70.0};
    double hueMax{170.0};
    double saturationMin{0.25};
    double saturationMax{1.0};
    double valueMin{0.20};
    double valueMax{1.0};
    double minimumMatchRatio{0.20};
    int settleDelayMs{150};
    int verifyTimeoutMs{1000};
    int verifyPollIntervalMs{100};
    int maxRetries{0};
    int retryDelayMs{300};
};

struct HachimiPipelineTask
{
    std::optional<std::string> outcome;
    std::string algorithm{"MatchTemplate"};
    std::string action{"ClickSelf"};
    std::optional<std::string> pipeline;
    std::optional<std::string> entry;
    std::optional<std::vector<int>> swipe;
    std::optional<std::string> inputText;
    std::optional<std::string> keyCode;
    std::optional<std::string> templatePath;
    std::optional<std::string> templateCollection;
    int referenceWidth{900};
    int referenceHeight{1600};
    double templateThreshold{0.86};
    std::vector<std::string> alternativeTemplateTasks;
    std::optional<std::vector<int>> roi;
    std::optional<std::vector<int>> fallbackRoi;
    std::optional<std::vector<int>> fallbackClickOffset;
    bool clickUntilGone{false};
    bool reuseLastMatchOnRetry{false};
    int maxClickAttempts{0};
    bool repeatTapUntilTransition{false};
    int goneConfirmationSamples{3};
    std::vector<std::string> transitionTemplates;
    bool pollTransitionTemplatesTogether{false};
    std::optional<std::vector<int>> transitionRoi;
    std::vector<std::optional<std::vector<int>>> transitionRois;
    double transitionThreshold{0.78};
    int transitionTimeoutMilliseconds{2600};
    int transitionPollIntervalMilliseconds{250};
    std::optional<std::vector<int>> specificRect;
    std::vector<std::vector<int>> searchRois;
    double minimumScoreGap{0.0};
    std::vector<double> scaleCandidates;
    int preDelay{0};
    int postDelay{0};
    int waitMilliseconds{0};
    int timeoutMilliseconds{10000};
    int pollIntervalMilliseconds{0};
    int monitorStableMilliseconds{0};
    int retryTimes{0};
    int retryDelayMilliseconds{750};

    std::vector<std::string> next;
    std::vector<std::string> onErrorNext;
    std::vector<std::string> exceededNext;
    std::vector<std::string> sub;
    std::vector<std::string> monitorTasks;
    std::optional<std::string> successTask;
    std::vector<std::string> successTasks;
    int maxTimes{0};
    bool required{true};
    bool success{false};
    std::optional<std::string> countAs;
    std::optional<std::string> countKey;
    std::optional<std::string> targetText;
    std::optional<std::string> ocrLanguage;
    double fuzzyThreshold{0.86};
    bool unique{true};
    std::optional<std::vector<int>> clickOffset;
    std::optional<std::vector<int>> clickAnchor;
    std::optional<HachimiClickVerification> clickVerification;
    std::optional<std::vector<int>> rowExpansion;
    int maxScrolls{0};
    int ocrGroupRowHeight{0};
    int ocrRowGap{0};
    std::optional<std::string> ocrMatchMode;
    bool ocrRequireAllTokens{true};
    std::optional<std::string> triggerTask;
    std::vector<std::string> triggerChain;
    int successConfirmDelayMs{0};
};

struct HachimiPipelineTiming
{
    int pollIntervalMilliseconds{300};
};

struct HachimiPipelineDefinition
{
    std::string name;
    std::optional<std::string> start;
    int schemaVersion{1};
    std::string description;
    int referenceWidth{900};
    int referenceHeight{1600};
    std::vector<std::string> taskOrder;
    std::unordered_map<std::string, HachimiPipelineTask, CaseInsensitiveHash, CaseInsensitiveEqual> tasks;
    HachimiPipelineTiming timing;
    std::string baseDirectory;

    const HachimiPipelineTask* TryGetTask(const std::string& taskName) const noexcept
    {
        auto it = tasks.find(taskName);
        if (it != tasks.end())
        {
            return &it->second;
        }
        return nullptr;
    }
};

} // namespace UmaAssistant::TaskEngine
