#include "CareerSkillLearningFlow.hpp"
#include "../CareerPersistenceStore.hpp"

#include <algorithm>
#include <cctype>
#include <chrono>
#include <cmath>
#include <limits>
#include <regex>
#include <cstdint>
#include <vector>
#include <unordered_set>
#include <utility>

namespace UmaAssistant::Career
{

namespace
{

struct CoordinateReferenceScope
{
    TaskEngine::ITaskExecutionContext& context;
    std::pair<int, int> prior;
    explicit CoordinateReferenceScope(TaskEngine::ITaskExecutionContext& value)
        : context(value), prior(value.GetCoordinateReference())
    { context.SetCoordinateReference(900, 1600); }
    ~CoordinateReferenceScope() { context.SetCoordinateReference(prior.first, prior.second); }
};

std::string Fold(const std::string& value)
{
    std::string folded;
    folded.reserve(value.size());
    for (unsigned char c : value)
        folded.push_back(static_cast<char>(std::tolower(c)));
    return folded;
}

std::optional<TaskEngine::TaskRoi> ReadRegionRoi(
    const CareerVisualResources& resources,
    const std::string& regionId)
{
    const auto* region = resources.FindRegion(regionId);
    if (!region || !region->is_object() || !region->contains("roi") || !(*region)["roi"].is_array()
        || (*region)["roi"].size() < 4)
        return std::nullopt;
    try
    {
        return TaskEngine::TaskRoi{
            (*region)["roi"][0].get<int>(), (*region)["roi"][1].get<int>(),
            (*region)["roi"][2].get<int>(), (*region)["roi"][3].get<int>()};
    }
    catch (...) { return std::nullopt; }
}

std::optional<int> ReadRegionMetadataInt(
    const CareerVisualResources& resources,
    const std::string& regionId,
    const std::string& key)
{
    const auto* region = resources.FindRegion(regionId);
    if (!region || !region->is_object() || !region->contains("metadata")
        || !(*region)["metadata"].is_object() || !(*region)["metadata"].contains(key))
        return std::nullopt;
    try { return (*region)["metadata"][key].get<int>(); }
    catch (...) { return std::nullopt; }
}

std::optional<TaskEngine::TaskMatchResult> MatchSkillAsset(
    const CareerVisualResources& resources,
    TaskEngine::ITaskExecutionContext& context,
    const std::string& assetId,
    const std::string& regionId)
{
    const auto* asset = resources.FindAsset(assetId);
    const auto roi = ReadRegionRoi(resources, regionId);
    const auto frame = context.GetCurrentFrame();
    if (!asset || !roi || !frame)
        return std::nullopt;
    const auto match = context.MatchTemplateAdvanced(
        asset->path, asset->threshold, roi, "MatchTemplateColor", 900, 1600, {});
    const auto after = context.GetCurrentFrame();
    if (!after || after.get() != frame.get() || after->frameId != frame->frameId
        || (match.frameId != 0 && frame->frameId != 0 && match.frameId != frame->frameId))
        return std::nullopt;
    return match;
}

bool TapSkillMatch(TaskEngine::ITaskExecutionContext& context, const TaskEngine::TaskMatchResult& match)
{
    return match.found && match.frameId != 0
        && context.TapMatch(match.CenterX(), match.CenterY(), match);
}

std::optional<TaskEngine::TaskMatchResult> WaitForSkillAsset(
    const CareerVisualResources& resources,
    TaskEngine::ITaskExecutionContext& context,
    const std::string& assetId,
    const std::string& regionId,
    int timeoutMs = 5000,
    int pollMs = 250)
{
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeoutMs);
    int stable = 0;
    std::optional<TaskEngine::TaskMatchResult> previous;
    const int maxPolls = (std::max)(1, (timeoutMs + pollMs - 1) / pollMs + 1);
    for (int poll = 0; poll < maxPolls && !context.IsCancelled()
        && std::chrono::steady_clock::now() < deadline; ++poll)
    {
        if (context.CaptureScreen())
        {
            auto match = MatchSkillAsset(resources, context, assetId, regionId);
            if (match && match->found && previous
                && std::abs(match->CenterX() - previous->CenterX()) <= 12
                && std::abs(match->CenterY() - previous->CenterY()) <= 12)
            {
                if (++stable >= 2) return match;
            }
            else stable = match && match->found ? 1 : 0;
            previous = match;
        }
        context.Sleep(pollMs);
    }
    return std::nullopt;
}

std::string NormalizeSkillName(std::string text)
{
    auto marker = [&]() -> std::string {
        for (const auto* suffix : {"\xE2\x97\x8B", "\xE2\x97\x8E", "\xC3\x97"})
        {
            const std::string value(suffix);
            if (text.size() >= value.size()
                && text.compare(text.size() - value.size(), value.size(), value) == 0)
            {
                text.resize(text.size() - value.size());
                return value;
            }
        }
        return {};
    }();
    std::string result;
    for (unsigned char ch : text)
        if (std::isalnum(ch)) result.push_back(static_cast<char>(std::tolower(ch)));
    return result + marker;
}

double SkillNameSimilarity(const std::string& expected, const std::string& observed)
{
    const auto left = NormalizeSkillName(expected);
    const auto right = NormalizeSkillName(observed);
    if (left.empty() || right.empty() || right.size() > left.size() * 1.5 + 3)
        return 0.0;
    std::vector<int> prior(right.size() + 1), current(right.size() + 1);
    for (std::size_t j = 0; j <= right.size(); ++j) prior[j] = static_cast<int>(j);
    for (std::size_t i = 1; i <= left.size(); ++i)
    {
        current[0] = static_cast<int>(i);
        for (std::size_t j = 1; j <= right.size(); ++j)
        {
            const int substitution = left[i - 1] == right[j - 1] ? 0 : 1;
            current[j] = (std::min)({prior[j] + 1, current[j - 1] + 1, prior[j - 1] + substitution});
        }
        std::swap(prior, current);
    }
    return 1.0 - static_cast<double>(prior[right.size()])
        / static_cast<double>((std::max)(left.size(), right.size()));
}

std::optional<int> ReadNumberFromRegion(
    TaskEngine::ITaskExecutionContext& context,
    const TaskEngine::TaskRoi& roi)
{
    const auto frame = context.GetCurrentFrame();
    if (!frame) return std::nullopt;
    // Segment the numeric region itself; full-screen OCR can omit the balance
    // or merge it into an unrelated line outside the region.
    const auto detections = context.RecognizeText("en-US", roi);
    const auto after = context.GetCurrentFrame();
    if (!after || after.get() != frame.get() || after->frameId != frame->frameId)
        return std::nullopt;
    static const std::regex number(R"(\d{1,4})");
    std::vector<int> values;
    for (const auto& detection : detections)
    {
        const bool inside = detection.CenterX() >= roi.x && detection.CenterX() <= roi.x + roi.width
            && detection.CenterY() >= roi.y && detection.CenterY() <= roi.y + roi.height;
        if (inside)
        {
            for (std::sregex_iterator it(detection.text.begin(), detection.text.end(), number), end; it != end; ++it)
            {
                try { values.push_back(std::stoi(it->str())); } catch (...) {}
            }
        }
    }
    if (values.empty())
    {
        for (const auto& detection : detections)
        {
            if (detection.x >= roi.x || detection.x + detection.width < roi.x
                || detection.x + detection.width > roi.x + roi.width
                || detection.CenterY() < roi.y || detection.CenterY() > roi.y + roi.height)
                continue;
            std::smatch match;
            static const std::regex trailingNumber(R"((\d{1,4})\s*$)");
            if (std::regex_search(detection.text, match, trailingNumber))
            {
                try { values.push_back(std::stoi(match.str(1))); } catch (...) {}
            }
        }
    }
    std::sort(values.begin(), values.end());
    values.erase(std::unique(values.begin(), values.end()), values.end());
    return values.size() == 1 ? std::optional<int>(values.front()) : std::nullopt;
}

int EstimateSkillCost(const GameData::UmaIndependentSkillRecord& skill, const std::vector<GameData::UmaIndependentSkillRecord>& all)
{
    int threshold = skill.needSkillPoint;
    std::vector<std::string> tiers;
    std::size_t start = 0;
    while (start < skill.otherTiers.size())
    {
        const auto slash = skill.otherTiers.find('/', start);
        auto tier = skill.otherTiers.substr(start, slash == std::string::npos ? slash : slash - start);
        const auto first = tier.find_first_not_of(" \t");
        const auto last = tier.find_last_not_of(" \t");
        if (first != std::string::npos) tiers.push_back(tier.substr(first, last - first + 1));
        if (slash == std::string::npos) break;
        start = slash + 1;
    }
    std::optional<std::string> prerequisite;
    if (skill.rarity == 2)
    {
        for (const auto& name : tiers)
        {
            auto it = std::find_if(all.begin(), all.end(), [&](const auto& candidate) {
                return Fold(candidate.skillName) == Fold(name) && candidate.rarity == 1 && candidate.needSkillPoint > 0;
            });
            if (it != all.end()) { prerequisite = name; break; }
        }
    }
    else if (skill.rarity == 1 && skill.skillName.size() >= 3
        && skill.skillName.compare(skill.skillName.size() - 3, 3, "\xE2\x97\x8E") == 0)
    {
        auto lower = skill.skillName.substr(0, skill.skillName.size() - 3) + "\xE2\x97\x8B";
        if (std::any_of(tiers.begin(), tiers.end(), [&](const auto& name) { return Fold(name) == Fold(lower); }))
            prerequisite = std::move(lower);
    }
    if (prerequisite)
    {
        auto it = std::find_if(all.begin(), all.end(), [&](const auto& candidate) {
            return Fold(candidate.skillName) == Fold(*prerequisite);
        });
        if (it != all.end() && it->needSkillPoint > 0
            && threshold <= (std::numeric_limits<int>::max)() - it->needSkillPoint)
            threshold += it->needSkillPoint;
    }
    return threshold;
}

struct SkillScanResult
{
    enum class Kind { Unknown, NotFound, Obtained, Purchasable } kind{Kind::Unknown};
    std::optional<TaskEngine::TaskMatchResult> plus;
    std::optional<TaskEngine::TaskOcrDetection> rowName;
};

#if defined(__APPLE__)
bool VerifySelectedSkillRow(const CareerVisualResources& resources,
    TaskEngine::ITaskExecutionContext& context, const std::string& skillName,
    const TaskEngine::TaskOcrDetection& priorName)
{
    const auto names = ReadRegionRoi(resources, "career.skill.row_name");
    const auto statuses = ReadRegionRoi(resources, "career.skill.row_status");
    if (!names || !statuses) return false;
    auto nameRoi = *names;
    nameRoi.y = (std::max)(names->y, priorName.y - 12);
    nameRoi.height = (std::min)(priorName.height + 24, names->y + names->height - nameRoi.y);
    if (nameRoi.height <= 0) return false;
    double minSimilarity = .84;
    try { minSimilarity = resources.FindRegion("career.skill.row_name")->at("metadata").value("minSimilarity", .84); }
    catch (...) {}
    for (int attempt = 0; attempt < 3 && !context.IsCancelled(); ++attempt)
    {
        if (!context.CaptureScreen()) return false;
        const auto frame = context.GetCurrentFrame();
        if (!frame) return false;
        const auto rowNames = context.RecognizeText("en-US", nameRoi);
        const auto afterNames = context.GetCurrentFrame();
        if (!afterNames || afterNames.get() != frame.get() || afterNames->frameId != frame->frameId)
            return false;
        std::vector<TaskEngine::TaskOcrDetection> matches;
        for (const auto& name : rowNames)
            if (name.CenterX() >= nameRoi.x && name.CenterX() <= nameRoi.x + nameRoi.width
                && name.CenterY() >= nameRoi.y && name.CenterY() <= nameRoi.y + nameRoi.height
                && SkillNameSimilarity(skillName, name.text) >= minSimilarity)
                matches.push_back(name);
        if (matches.size() == 1)
        {
            auto status = *statuses;
            status.y = (std::max)(status.y, matches.front().y
                + ReadRegionMetadataInt(resources, "career.skill.row_status", "rowTopOffset").value_or(-15));
            status.width = ReadRegionMetadataInt(resources, "career.skill.row_status", "width").value_or(status.width);
            status.height = ReadRegionMetadataInt(resources, "career.skill.row_status", "height").value_or(status.height);
            const auto rowStatus = context.RecognizeText("en-US", status);
            const auto afterStatus = context.GetCurrentFrame();
            if (!afterStatus || afterStatus.get() != frame.get() || afterStatus->frameId != frame->frameId)
                return false;
            if (std::any_of(rowStatus.begin(), rowStatus.end(), [&](const auto& item) {
                return item.CenterX() >= status.x && item.CenterX() <= status.x + status.width
                    && item.CenterY() >= status.y && item.CenterY() <= status.y + status.height
                    && Fold(item.text).find("obtained") != std::string::npos;
            })) return true;
        }
        if (attempt < 2)
        {
            context.Log("DEBUG", "Selected skill row is not settled; rechecking the original row without scrolling.");
            context.Sleep(200);
        }
    }
    return false;
}
#endif

SkillScanResult ScanForSkill(
    const CareerVisualResources& resources,
    TaskEngine::ITaskExecutionContext& context,
    const std::string& skillName)
{
    const auto list = ReadRegionRoi(resources, "career.skill.list");
    const auto nameRoi = ReadRegionRoi(resources, "career.skill.row_name");
    const auto statusRegion = ReadRegionRoi(resources, "career.skill.row_status");
    const auto plusRegion = ReadRegionRoi(resources, "career.skill.plus_search");
    const auto scroll = resources.FindRegion("career.skill.scroll");
    const int pages = ReadRegionMetadataInt(resources, "career.skill.list", "maxPages").value_or(60);
    const auto* plusAsset = resources.FindAsset("career.skill.plus");
    if (!list || !nameRoi || !statusRegion || !plusRegion || !scroll || !plusAsset)
        return {};
    const int xMin = ReadRegionMetadataInt(resources, "career.skill.row_name", "xMin").value_or(nameRoi->x);
    const int xMax = ReadRegionMetadataInt(resources, "career.skill.row_name", "xMax").value_or(nameRoi->x + nameRoi->width);
    const int yMin = ReadRegionMetadataInt(resources, "career.skill.row_name", "yMin").value_or(nameRoi->y);
    const int yMax = ReadRegionMetadataInt(resources, "career.skill.row_name", "yMax").value_or(nameRoi->y + nameRoi->height);
    const double minSimilarity = [&]() {
        const auto* region = resources.FindRegion("career.skill.row_name");
        if (!region) return .84;
        try { return region->at("metadata").value("minSimilarity", .84); }
        catch (...) { return .84; }
    }();
    std::uint64_t priorSignature = 0;
    int unchanged = 0;
    for (int page = 0; page <= pages && !context.IsCancelled(); ++page)
    {
        if (!context.CaptureScreen()) return {};
        const auto frame = context.GetCurrentFrame();
        if (!frame) return {};
        const auto detections = context.RecognizeText("en-US", *list);
        const auto after = context.GetCurrentFrame();
        if (!after || after.get() != frame.get() || after->frameId != frame->frameId) return {};
        std::vector<TaskEngine::TaskOcrDetection> matches;
        for (const auto& item : detections)
        {
            if (item.x < xMin || item.x > xMax || item.y < yMin || item.y > yMax
                || item.CenterX() < nameRoi->x || item.CenterX() > nameRoi->x + nameRoi->width
                || item.CenterY() < nameRoi->y || item.CenterY() > nameRoi->y + nameRoi->height)
                continue;
            if (SkillNameSimilarity(skillName, item.text) >= minSimilarity)
                matches.push_back(item);
        }
        if (matches.size() > 1) return {};
        if (matches.size() == 1)
        {
            const auto statusTop = (std::max)(statusRegion->y,
                matches.front().y + ReadRegionMetadataInt(resources, "career.skill.row_status", "rowTopOffset").value_or(-15));
            auto status = *statusRegion;
            status.y = statusTop;
            status.width = ReadRegionMetadataInt(resources, "career.skill.row_status", "width").value_or(status.width);
            status.height = ReadRegionMetadataInt(resources, "career.skill.row_status", "height").value_or(status.height);
            const auto rowStatus = context.RecognizeText("en-US", status);
            const auto current = context.GetCurrentFrame();
            if (!current || current.get() != frame.get() || current->frameId != frame->frameId) return {};
            if (std::any_of(rowStatus.begin(), rowStatus.end(), [](const auto& item) {
                return Fold(item.text).find("obtained") != std::string::npos;
            }))
                return {SkillScanResult::Kind::Obtained, std::nullopt};

            const int yOffset = ReadRegionMetadataInt(resources, "career.skill.plus_search", "yOffset").value_or(20);
            const int minY = ReadRegionMetadataInt(resources, "career.skill.plus_search", "minimumY").value_or(595);
            const int maxY = ReadRegionMetadataInt(resources, "career.skill.plus_search", "maximumY").value_or(1190);
            const int heightFromBottom = ReadRegionMetadataInt(resources, "career.skill.plus_search", "heightFromBottom").value_or(1280);
            const int maxHeight = ReadRegionMetadataInt(resources, "career.skill.plus_search", "maxHeight").value_or(155);
            const int minHeight = ReadRegionMetadataInt(resources, "career.skill.plus_search", "minimumHeight").value_or(1);
            auto plusRoi = *plusRegion;
            plusRoi.y = (std::clamp)(matches.front().y + yOffset, minY, maxY);
            plusRoi.height = (std::max)(minHeight, (std::min)(maxHeight, heightFromBottom - plusRoi.y));
            const auto plus = context.MatchTemplateAdvanced(
                plusAsset->path, plusAsset->threshold, plusRoi, "MatchTemplateColor", 900, 1600, {});
            const auto plusFrame = context.GetCurrentFrame();
            if (!plusFrame || plusFrame.get() != frame.get() || plusFrame->frameId != frame->frameId) return {};
            if (plus.found) return {SkillScanResult::Kind::Purchasable, plus, matches.front()};
        }

        // Hash the visible list at a fixed sparse interval to stop safely at the end.
        std::uint64_t signature = 14695981039346656037ull;
        const int left = (std::clamp)(list->x * frame->width / 900, 0, frame->width);
        const int top = (std::clamp)(list->y * frame->height / 1600, 0, frame->height);
        const int right = (std::clamp)((list->x + list->width) * frame->width / 900, left, frame->width);
        const int bottom = (std::clamp)((list->y + list->height) * frame->height / 1600, top, frame->height);
        for (int y = top; y < bottom; y += 8)
            for (int x = left; x < right; x += 8)
            {
                const auto index = static_cast<std::size_t>(y) * frame->width + x;
                if (index < frame->grayPixels.size())
                { signature ^= frame->grayPixels[index]; signature *= 1099511628211ull; }
            }
        unchanged = signature == priorSignature ? unchanged + 1 : 0;
        if (unchanged >= 2 || page >= pages)
            return {SkillScanResult::Kind::NotFound, std::nullopt};
        priorSignature = signature;
        std::vector<int> swipe;
        try { swipe = scroll->at("metadata").at("swipe").get<std::vector<int>>(); }
        catch (...) { return {}; }
        if (swipe.size() != 5 || !context.Swipe(swipe[0], swipe[1], swipe[2], swipe[3], swipe[4]))
            return {};
        context.Sleep(ReadRegionMetadataInt(resources, "career.skill.scroll", "scrollDelayMs").value_or(500));
    }
    return {};
}

bool ReturnToRaceDayScreen(const CareerVisualResources& resources, TaskEngine::ITaskExecutionContext& context)
{
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(15);
    int stableRaceDay = 0;
    bool backSubmitted = false;
    constexpr int maxPolls = 90;
    for (int poll = 0; poll < maxPolls && !context.IsCancelled()
        && std::chrono::steady_clock::now() < deadline; ++poll)
    {
        if (!context.CaptureScreen()) { context.Sleep(200); continue; }
        const auto frame = context.GetCurrentFrame();
        if (!frame) return false;
        if (auto overlay = MatchSkillAsset(resources, context, "career.skill.learned.title", "career.skill.learned.title"); overlay && overlay->found)
        {
            if (auto close = MatchSkillAsset(resources, context, "career.skill.learned.close", "career.skill.learned.close"); close && close->found)
                TapSkillMatch(context, *close);
            context.Sleep(350); continue;
        }
        if (auto overlay = MatchSkillAsset(resources, context, "career.skill.exit.title", "career.skill.exit.title"); overlay && overlay->found)
        {
            if (auto close = MatchSkillAsset(resources, context, "career.skill.exit.ok", "career.skill.exit.ok"); close && close->found)
                TapSkillMatch(context, *close);
            context.Sleep(350); continue;
        }
        if (auto overlay = MatchSkillAsset(resources, context, "career.skill.confirmation.title", "career.skill.confirmation.title"); overlay && overlay->found)
        {
            if (auto cancel = MatchSkillAsset(resources, context, "career.skill.confirmation.cancel", "career.skill.confirmation.cancel"); cancel && cancel->found)
                TapSkillMatch(context, *cancel);
            else return false;
            context.Sleep(350); continue;
        }
        const auto* raceDay = resources.FindScreen("race_day");
        bool raceFound = false;
        if (raceDay)
            for (const auto& templ : raceDay->templates)
            {
                const auto match = context.MatchTemplateAdvanced(templ.path, templ.threshold, templ.roi,
                    templ.requiresAlphaMatching ? "MatchTemplateColor" : templ.algorithm, 900, 1600, {});
                const auto current = context.GetCurrentFrame();
                if (!current || current.get() != frame.get() || current->frameId != frame->frameId) return false;
                if (match.found) { raceFound = true; break; }
            }
        stableRaceDay = raceFound ? stableRaceDay + 1 : 0;
        if (stableRaceDay >= 2) return true;
        if (raceFound)
        {
            context.Sleep(120);
            continue;
        }
        if (auto back = MatchSkillAsset(resources, context, "career.skill.back", "career.skill.back"); back && back->found)
        {
            if (backSubmitted)
            {
                context.Sleep(200);
                continue;
            }
            if (!TapSkillMatch(context, *back)) return false;
            backSubmitted = true;
            context.Sleep(350);
            stableRaceDay = 0;
            continue;
        }
        context.Sleep(250);
    }
    return false;
}

} // namespace

CareerSkillLearningFlow::CareerSkillLearningFlow(
    CareerRuntimeConfig config,
    std::shared_ptr<CareerVisualResources> resources)
    : m_config(std::move(config))
    , m_resources(std::move(resources))
    , m_catalog(m_config.resourceDirectory)
{
}

bool CareerSkillLearningFlow::EnsureCatalogLoaded(TaskEngine::ITaskExecutionContext& context)
{
    if (m_catalogLoaded)
        return m_catalogError.empty();
    m_catalogLoaded = true;
    if (!m_catalog.Load(m_catalogError))
    {
        context.Log("ERROR", "Global career selection catalog is unavailable: " + m_catalogError);
        return false;
    }
    return true;
}

bool CareerSkillLearningFlow::ReturnToRaceDay(TaskEngine::ITaskExecutionContext& context)
{
    CoordinateReferenceScope coordinates(context);
    return m_resources && ReturnToRaceDayScreen(*m_resources, context);
}

ActionExecutionStatus CareerSkillLearningFlow::RunBeforeRace(TaskEngine::ITaskExecutionContext& context)
{
    const auto& config = m_config;
    const auto& resources = m_resources;
    if (config.normalSkillIds.empty()) return ActionExecutionStatus::Success;
    if (!resources || !resources->IsReady()) return ActionExecutionStatus::Failed;
    if (!m_skillsLoadedFromDisk)
    {
        m_skillsLoadedFromDisk = true;
        if (m_config.traineeId)
        {
            const auto loaded = NormalCareerSkillCache::Load(
                m_config.checkpointDirectory, *m_config.traineeId, context.GetDeviceSerial());
            for (const int loadedId : loaded)
                m_confirmedLearnedSkillIds.insert(loadedId);
        }
    }
    const auto saveLearnedSkills = [&]() {
        if (m_config.traineeId)
        {
            std::vector<int> skillIds(m_confirmedLearnedSkillIds.begin(), m_confirmedLearnedSkillIds.end());
            std::sort(skillIds.begin(), skillIds.end());
            NormalCareerSkillCache::Save(
                m_config.checkpointDirectory, *m_config.traineeId, context.GetDeviceSerial(), skillIds);
        }
    };
    CoordinateReferenceScope coordinates(context);
    if (!EnsureCatalogLoaded(context)) return ActionExecutionStatus::Failed;
    const auto* catalog = &m_catalog.Skills();
    const auto pointsRegion = ReadRegionRoi(*resources, "career.skill.points");
    if (!pointsRegion) { context.Log("ERROR", "Skill-point OCR region is missing."); return ActionExecutionStatus::Failed; }
    std::vector<int> ids;
    ids.reserve(config.normalSkillIds.size());
    std::unordered_set<int> seen;
    for (const int id : config.normalSkillIds)
        if (seen.insert(id).second)
            ids.push_back(id);
    for (const int id : ids)
    {
        if (context.IsCancelled()) return ActionExecutionStatus::Cancelled;
        if (m_confirmedLearnedSkillIds.contains(id))
        {
            context.Log("INFO", "Skipping already confirmed configured skill " + std::to_string(id) + ".");
            continue;
        }
        const auto skill = std::find_if(catalog->begin(), catalog->end(), [id](const auto& item) {
            return item.skillId == id;
        });
        if (skill == catalog->end() || !skill->availableInGlobal || !skill->singleModeEnabled || skill->needSkillPoint <= 0)
        {
            context.Log("ERROR", "Configured normal skill is unavailable in the fixed global catalog: " + std::to_string(id));
            return ActionExecutionStatus::Failed;
        }
        if (!context.CaptureScreen()) return ActionExecutionStatus::Failed;
        auto points = ReadNumberFromRegion(context, *pointsRegion);
        if (!points)
        {
            const auto* region = resources->FindRegion("career.skill.points");
            try
            {
                const auto focused = region->at("focusedRoi").get<std::vector<int>>();
                if (focused.size() >= 4)
                    points = ReadNumberFromRegion(context, {focused[0], focused[1], focused[2], focused[3]});
            }
            catch (...) {}
        }
        if (!points)
        {
            context.Log("ERROR", "Could not unambiguously read current skill points; stopping before race entry.");
            return ActionExecutionStatus::Failed;
        }
        const int threshold = EstimateSkillCost(*skill, *catalog);
        if (*points < threshold)
        {
            context.Log("INFO", "Saving " + std::to_string(*points) + " Skill Pts for "
                + skill->skillName + " (estimated " + std::to_string(threshold)
                + "); proceeding to race.");
            break;
        }

        const auto open = resources->RunAction("race_day", "skills.open", context);
        if (open.status != TaskEngine::TaskGraphStatus::Success
            && open.status != TaskEngine::TaskGraphStatus::Completed)
        {
            context.Log("ERROR", "Could not run the profile-defined skills.open action: " + open.errorMessage);
            return context.IsCancelled() ? ActionExecutionStatus::Cancelled : ActionExecutionStatus::Failed;
        }
        if (!WaitForSkillAsset(*resources, context, "career.skill.back", "career.skill.back"))
        {
            context.Log("ERROR", "Skills page did not appear after the profile action.");
            return context.IsCancelled() ? ActionExecutionStatus::Cancelled : ActionExecutionStatus::Failed;
        }
        const auto scan = ScanForSkill(*resources, context, skill->skillName);
        if (scan.kind == SkillScanResult::Kind::Unknown)
        {
            if (!ReturnToRaceDayScreen(*resources, context)) return ActionExecutionStatus::Failed;
            context.Log("WARNING", "Could not uniquely find " + skill->skillName
                + " on the Skills list; returned to Race Day and skipped it.");
            continue;
        }
        if (scan.kind == SkillScanResult::Kind::Obtained)
        {
            m_confirmedLearnedSkillIds.insert(id);
            saveLearnedSkills();
        }
        if (scan.kind == SkillScanResult::Kind::Purchasable && scan.plus)
        {
            const auto priceRegion = ReadRegionRoi(*resources, "career.skill.price");
            if (!priceRegion) return ActionExecutionStatus::Failed;
            auto priceRoi = *priceRegion;
            const int yOffset = ReadRegionMetadataInt(*resources, "career.skill.price", "centerYOffset").value_or(-40);
            priceRoi.y = (std::max)(priceRoi.y + priceRoi.height / 2, scan.plus->CenterY() + yOffset);
            const auto price = ReadNumberFromRegion(context, priceRoi);
            if (!price || *price <= 0)
            {
                if (!ReturnToRaceDayScreen(*resources, context)) return ActionExecutionStatus::Failed;
                context.Log("ERROR", "Displayed skill price could not be read; stopping before race entry.");
                return ActionExecutionStatus::Failed;
            }
            if (*price > *points)
            {
                if (!ReturnToRaceDayScreen(*resources, context)) return ActionExecutionStatus::Failed;
                context.Log("INFO", "Saving " + std::to_string(*points) + " Skill Pts for "
                    + skill->skillName + " (displayed price " + std::to_string(*price)
                    + "); proceeding to race.");
                break;
            }
            if (!TapSkillMatch(context, *scan.plus)) return ActionExecutionStatus::Failed;
            context.Sleep(ReadRegionMetadataInt(*resources, "career.skill.plus_search", "tapDelayMs").value_or(350));
#if defined(__APPLE__)
            const bool selected = scan.rowName
                && VerifySelectedSkillRow(*resources, context, skill->skillName, *scan.rowName);
#else
            const bool selected = ScanForSkill(*resources, context, skill->skillName).kind
                == SkillScanResult::Kind::Obtained;
#endif
            if (!selected)
            {
                if (!ReturnToRaceDayScreen(*resources, context)) return ActionExecutionStatus::Failed;
                context.Log("ERROR", "Selected skill row was not confirmed as obtained after tapping its plus marker.");
                return ActionExecutionStatus::Failed;
            }
            const auto confirm = WaitForSkillAsset(*resources, context, "career.skill.confirm", "career.skill.confirm");
            if (!confirm || !TapSkillMatch(context, *confirm)) return ActionExecutionStatus::Failed;
            const auto dialog = WaitForSkillAsset(*resources, context,
                "career.skill.confirmation.title", "career.skill.confirmation.title");
            if (!dialog) return ActionExecutionStatus::Failed;
            const auto nameRoi = ReadRegionRoi(*resources, "career.skill.confirmation.name");
            if (!nameRoi) return ActionExecutionStatus::Failed;
            const auto nameRegion = resources->FindRegion("career.skill.confirmation.name");
            if (!nameRegion)
            {
                context.Log("ERROR", "Skill confirmation name metadata is missing.");
                return ActionExecutionStatus::Failed;
            }
            double nameThreshold = .84;
            try { nameThreshold = nameRegion->at("metadata").value("minSimilarity", .84); } catch (...) {}
            const auto nameFrame = context.GetCurrentFrame();
            if (!nameFrame) return ActionExecutionStatus::Failed;
            const auto dialogNames = context.RecognizeText("en-US", *nameRoi);
            const auto afterNames = context.GetCurrentFrame();
            if (!afterNames || afterNames.get() != nameFrame.get()
                || afterNames->frameId != nameFrame->frameId)
            {
                context.Log("ERROR", "Skill confirmation OCR did not retain its source frame.");
                return ActionExecutionStatus::Failed;
            }
            if (std::none_of(dialogNames.begin(), dialogNames.end(), [&](const auto& item) {
                return SkillNameSimilarity(skill->skillName, item.text) >= nameThreshold;
            }))
            {
                context.Log("ERROR", "Skill confirmation dialog did not identify the requested skill.");
                return ActionExecutionStatus::Failed;
            }
            const auto learn = WaitForSkillAsset(*resources, context,
                "career.skill.confirmation.learn", "career.skill.confirmation.learn");
            if (!learn || !TapSkillMatch(context, *learn)) return ActionExecutionStatus::Failed;
            if (!WaitForSkillAsset(*resources, context, "career.skill.learned.title", "career.skill.learned.title"))
            {
                context.Log("ERROR", "Skill purchase completion dialog was not confirmed.");
                return ActionExecutionStatus::Failed;
            }
            m_confirmedLearnedSkillIds.insert(id);
            saveLearnedSkills();
        }
        else if (scan.kind == SkillScanResult::Kind::NotFound)
        {
            if (!ReturnToRaceDayScreen(*resources, context)) return ActionExecutionStatus::Failed;
            context.Log("WARNING", "Could not uniquely find " + skill->skillName
                + " on the Skills list; returned to Race Day and skipped it.");
            continue;
        }
        if (!ReturnToRaceDayScreen(*resources, context))
        {
            context.Log("ERROR", "Could not return from the skills flow to Race Day.");
            return ActionExecutionStatus::Failed;
        }
    }
    return ActionExecutionStatus::Success;
}

void CareerSkillLearningFlow::ClearLearnedSkills(const std::string& deviceKey)
{
    m_confirmedLearnedSkillIds.clear();
    m_skillsLoadedFromDisk = false;
    if (m_config.traineeId)
    {
        NormalCareerSkillCache::Clear(
            m_config.checkpointDirectory, *m_config.traineeId, deviceKey);
    }
}

} // namespace UmaAssistant::Career
