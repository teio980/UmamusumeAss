#include "RecognitionPipeline.hpp"
#include <sstream>
#include <algorithm>
#include <cmath>
#include <limits>
#include <cctype>
#include <utility>

namespace UmaAssistant::TaskEngine
{

namespace
{

std::vector<std::string> SplitTokens(const std::string& str)
{
    std::vector<std::string> tokens;
    std::istringstream iss(str);
    std::string token;
    while (iss >> token)
    {
        tokens.push_back(token);
    }
    return tokens;
}

std::string Normalize(const std::string& text);
double Similarity(const std::string& left, const std::string& right);

std::vector<TaskOcrDetection> GroupOcrDetections(
    const std::vector<TaskOcrDetection>& detections,
    int rowHeight,
    int rowGap)
{
    std::vector<TaskOcrDetection> ordered;
    for (const auto& detection : detections)
    {
        if (!detection.text.empty())
            ordered.push_back(detection);
    }
    std::stable_sort(ordered.begin(), ordered.end(), [](const auto& left, const auto& right) {
        return left.y == right.y ? left.x < right.x : left.y < right.y;
    });
    if (ordered.empty())
        return {};

    rowHeight = (std::max)(0, rowHeight);
    rowGap = (std::max)(0, rowGap);
    if (rowHeight == 0)
        rowHeight = rowGap;
    if (rowGap == 0)
        rowGap = rowHeight;

    std::vector<TaskOcrDetection> groups;
    for (const auto& detection : ordered)
    {
        if (groups.empty())
        {
            groups.push_back(detection);
            continue;
        }
        auto& group = groups.back();
        const int groupBottom = group.y + group.height;
        const int projectedHeight = (std::max)(groupBottom, detection.y + detection.height) - group.y;
        const int verticalGap = detection.y > groupBottom ? detection.y - groupBottom : 0;
        if (projectedHeight <= rowHeight && verticalGap <= rowGap)
        {
            group.text += " " + detection.text;
            const int left = (std::min)(group.x, detection.x);
            const int top = (std::min)(group.y, detection.y);
            const int right = (std::max)(group.x + group.width, detection.x + detection.width);
            const int bottom = (std::max)(group.y + group.height, detection.y + detection.height);
            group.x = left;
            group.y = top;
            group.width = right - left;
            group.height = bottom - top;
            group.confidence = (group.confidence + detection.confidence) / 2.0;
        }
        else
        {
            groups.push_back(detection);
        }
    }
    return groups;
}

double TokenSimilarity(const std::string& expected, const std::string& actual)
{
    if (expected == actual)
        return 1.0;
    return Similarity(expected, actual);
}

double TokenCoverageSimilarity(const std::string& expected, const std::string& actual, bool requireAllTokens)
{
    const auto expectedTokens = SplitTokens(Normalize(expected));
    const auto actualTokens = SplitTokens(Normalize(actual));
    if (expectedTokens.empty() || actualTokens.empty())
        return 0.0;

    double sum = 0.0;
    for (const auto& expectedToken : expectedTokens)
    {
        double best = 0.0;
        for (const auto& actualToken : actualTokens)
            best = (std::max)(best, TokenSimilarity(expectedToken, actualToken));
        if (requireAllTokens && best < 0.60)
            return 0.0;
        sum += best;
    }
    return sum / static_cast<double>(expectedTokens.size());
}

std::string Normalize(const std::string& text)
{
    std::string value = text;
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });
    return value;
}

double Similarity(const std::string& left, const std::string& right)
{
    if (left.empty() || right.empty())
        return left.empty() && right.empty() ? 1.0 : 0.0;
    const auto a = Normalize(left);
    const auto b = Normalize(right);
    std::vector<std::size_t> prior(b.size() + 1), current(b.size() + 1);
    for (std::size_t j = 0; j <= b.size(); ++j)
        prior[j] = j;
    for (std::size_t i = 1; i <= a.size(); ++i)
    {
        current[0] = i;
        for (std::size_t j = 1; j <= b.size(); ++j)
        {
            const auto substitution = prior[j - 1] + (a[i - 1] == b[j - 1] ? 0u : 1u);
            current[j] = (std::min)({prior[j] + 1, current[j - 1] + 1, substitution});
        }
        prior.swap(current);
    }
    const auto denominator = (std::max)(a.size(), b.size());
    return denominator == 0 ? 1.0 : 1.0 - static_cast<double>(prior[b.size()]) / denominator;
}

} // namespace

bool RecognitionPipeline::CheckOcrText(
    const std::string& targetText,
    const std::vector<TaskOcrDetection>& detections,
    double fuzzyThreshold,
    const std::optional<std::string>& matchMode,
    bool requireAllTokens,
    bool unique,
    int groupRowHeight,
    int rowGap,
    TaskMatchResult& outMatch)
{
    if (detections.empty() || targetText.empty())
        return false;

    const bool isTokenCoverage = matchMode.has_value() && Normalize(*matchMode) == "tokencoverage";
    auto candidates = isTokenCoverage && groupRowHeight > 0
        ? GroupOcrDetections(detections, groupRowHeight, rowGap)
        : detections;
    std::vector<std::pair<const TaskOcrDetection*, double>> matches;

    if (isTokenCoverage)
    {
        for (const auto& d : candidates)
        {
            const double score = TokenCoverageSimilarity(targetText, d.text, requireAllTokens);
            if (score >= fuzzyThreshold)
                matches.emplace_back(&d, score);
        }
    }
    else
    {
        // Standard substring or line matching
        for (const auto& d : candidates)
        {
            const auto text = Normalize(d.text);
            const auto target = Normalize(targetText);
            const auto similarity = text.find(target) != std::string::npos
                ? 1.0 : Similarity(targetText, d.text);
            if (similarity >= fuzzyThreshold)
                matches.emplace_back(&d, similarity);
        }
    }

    if (matches.empty() || (unique && matches.size() > 1))
        return false;
    const auto* candidate = matches.front().first;
    outMatch.found = true;
    outMatch.score = matches.front().second;
    outMatch.x = candidate->x;
    outMatch.y = candidate->y;
    outMatch.width = candidate->width;
    outMatch.height = candidate->height;
    return true;
}

std::optional<TaskMatchResult> RecognitionPipeline::DetectSingleFrame(
    const HachimiPipelineDefinition& definition,
    const HachimiPipelineTask& task,
    ITaskExecutionContext& context,
    std::string& outMethod)
{
    const int referenceWidth = task.referenceWidth > 0 ? task.referenceWidth : definition.referenceWidth;
    const int referenceHeight = task.referenceHeight > 0 ? task.referenceHeight : definition.referenceHeight;
    context.SetCoordinateReference(referenceWidth, referenceHeight);
    auto stampMatch = [&](TaskMatchResult match) {
        match.referenceWidth = referenceWidth;
        match.referenceHeight = referenceHeight;
        if (const auto frame = context.GetCurrentFrame())
        {
            match.frameId = frame->frameId;
            match.sessionGeneration = frame->sessionGeneration;
            match.geometryGeneration = frame->geometryGeneration;
        }
        return match;
    };

    std::optional<TaskRoi> roi;
    if (task.roi && task.roi->size() >= 4)
    {
        roi = TaskRoi{(*task.roi)[0], (*task.roi)[1], (*task.roi)[2], (*task.roi)[3]};
    }

    // 1. Primary template check
    const auto algorithm = Normalize(task.algorithm);
    const auto scales = !task.scaleCandidates.empty()
        ? task.scaleCandidates
        : (algorithm == "matchtemplatescaled"
            ? std::vector<double>{0.80, 0.85, 0.90, 0.95, 1.00, 1.05, 1.10}
            : std::vector<double>{});
    if (task.templatePath && !task.templatePath->empty())
    {
        TaskMatchResult res;
        if (task.searchRois.empty())
        {
            res = context.MatchTemplateAdvanced(
                *task.templatePath, task.templateThreshold, roi, task.algorithm,
                referenceWidth, referenceHeight, scales);
            if (res.found)
                res = stampMatch(std::move(res));
        }
        else
        {
            std::vector<TaskMatchResult> foundMatches;
            for (const auto& searchRoi : task.searchRois)
            {
                if (searchRoi.size() < 4)
                    continue;
                auto candidate = context.MatchTemplateAdvanced(
                    *task.templatePath, task.templateThreshold,
                    TaskRoi{searchRoi[0], searchRoi[1], searchRoi[2], searchRoi[3]},
                    task.algorithm, referenceWidth, referenceHeight, scales);
                if (candidate.found)
                    foundMatches.push_back(stampMatch(std::move(candidate)));
            }
            std::stable_sort(foundMatches.begin(), foundMatches.end(), [](const auto& left, const auto& right) {
                return left.score > right.score;
            });
            if (!foundMatches.empty())
            {
                const double scoreGap = foundMatches.size() > 1
                    ? foundMatches[0].score - foundMatches[1].score
                    : std::numeric_limits<double>::infinity();
                if (scoreGap >= task.minimumScoreGap)
                    res = foundMatches.front();
            }
        }
        if (res.found)
        {
            outMethod = "Template";
            return res;
        }
    }

    // 2. AlternativeTemplateTasks check
    for (const auto& altTaskName : task.alternativeTemplateTasks)
    {
        const auto* altTask = definition.TryGetTask(altTaskName);
        if (!altTask || !altTask->templatePath || altTask->templatePath->empty())
            continue;

        std::optional<TaskRoi> altRoi;
        if (altTask->roi && altTask->roi->size() >= 4)
        {
            altRoi = TaskRoi{(*altTask->roi)[0], (*altTask->roi)[1], (*altTask->roi)[2], (*altTask->roi)[3]};
        }

        const int altReferenceWidth = altTask->referenceWidth > 0
            ? altTask->referenceWidth : definition.referenceWidth;
        const int altReferenceHeight = altTask->referenceHeight > 0
            ? altTask->referenceHeight : definition.referenceHeight;
        auto altScales = altTask->scaleCandidates;
        if (altScales.empty() && Normalize(altTask->algorithm) == "matchtemplatescaled")
            altScales = {0.80, 0.85, 0.90, 0.95, 1.00, 1.05, 1.10};
        TaskMatchResult res = context.MatchTemplateAdvanced(
            *altTask->templatePath,
            altTask->templateThreshold,
            altRoi,
            altTask->algorithm,
            altReferenceWidth,
            altReferenceHeight,
            altScales);
        if (res.found)
        {
            res.referenceWidth = altReferenceWidth;
            res.referenceHeight = altReferenceHeight;
            if (const auto frame = context.GetCurrentFrame())
            {
                res.frameId = frame->frameId;
                res.sessionGeneration = frame->sessionGeneration;
                res.geometryGeneration = frame->geometryGeneration;
            }
            outMethod = "AlternativeTemplate:" + altTaskName;
            return res;
        }
    }

    // 3. OCR check
    if (task.targetText && !task.targetText->empty())
    {
        std::string lang = task.ocrLanguage.value_or("ja");
        auto detections = context.RecognizeText(lang, roi);
        TaskMatchResult match;
        if (CheckOcrText(*task.targetText, detections, task.fuzzyThreshold,
                         task.ocrMatchMode, task.ocrRequireAllTokens, task.unique,
                         task.ocrGroupRowHeight, task.ocrRowGap, match))
        {
            match = stampMatch(std::move(match));
            outMethod = "Ocr";
            return match;
        }
    }

    // 4. Non-visual actions (e.g. DoNothing, pure input)
    if (!task.templatePath && !task.templateCollection && task.alternativeTemplateTasks.empty() && !task.targetText)
    {
        outMethod = "Direct";
        TaskMatchResult direct;
        // Direct actions do not have a recognition hit. Coordinate actions
        // may still run from specificRect; ClickSelf must require evidence.
        direct.found = false;
        direct.referenceWidth = referenceWidth;
        direct.referenceHeight = referenceHeight;
        return direct;
    }

    return std::nullopt;
}

RecognitionEvaluation RecognitionPipeline::EvaluateTask(
    const HachimiPipelineDefinition& definition,
    const std::string& taskName,
    const HachimiPipelineTask& task,
    ITaskExecutionContext& context)
{
    RecognitionEvaluation eval;
    eval.matchedTaskName = taskName;

    std::string method;
    auto match = DetectSingleFrame(definition, task, context, method);
    if (match.has_value())
    {
        eval.matched = true;
        eval.matchResult = *match;
        eval.methodUsed = method;
        eval.scrollCount = 0;
        return eval;
    }

    // Scroll-to-find loop if requested
    if (task.maxScrolls > 0)
    {
        int scrollX1 = task.referenceWidth / 2;
        int scrollY1 = static_cast<int>(task.referenceHeight * 0.70);
        int scrollX2 = scrollX1;
        int scrollY2 = static_cast<int>(task.referenceHeight * 0.35);

        for (int s = 1; s <= task.maxScrolls; ++s)
        {
            if (context.IsCancelled())
                break;

            context.Log("INFO", "Executing scroll-to-find #" + std::to_string(s) +
                                " for task: " + taskName);
            context.Swipe(scrollX1, scrollY1, scrollX2, scrollY2, 350);
            context.Sleep(600); // Settle after scroll

            if (!context.CaptureScreen())
                continue;

            match = DetectSingleFrame(definition, task, context, method);
            if (match.has_value())
            {
                eval.matched = true;
                eval.matchResult = *match;
                eval.methodUsed = method + "+Scrolled";
                eval.scrollCount = s;
                return eval;
            }
        }
    }

    eval.matched = false;
    return eval;
}

std::optional<RecognitionEvaluation> RecognitionPipeline::EvaluateSameFrameMonitors(
    const HachimiPipelineDefinition& definition,
    const std::vector<std::string>& monitorTasks,
    ITaskExecutionContext& context)
{
    for (const auto& monName : monitorTasks)
    {
        const auto* monTask = definition.TryGetTask(monName);
        if (!monTask)
            continue;

        std::string method;
        auto match = DetectSingleFrame(definition, *monTask, context, method);
        if (match.has_value())
        {
            RecognitionEvaluation eval;
            eval.matched = true;
            eval.matchedTaskName = monName;
            eval.matchResult = *match;
            eval.methodUsed = method;
            eval.scrollCount = 0;
            return eval;
        }
    }
    return std::nullopt;
}

} // namespace UmaAssistant::TaskEngine
