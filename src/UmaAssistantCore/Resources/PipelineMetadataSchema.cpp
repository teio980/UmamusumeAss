#include "PipelineMetadataSchema.hpp"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <exception>
#include <initializer_list>
#include <string_view>

namespace UmaAssistant::Resources
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
std::optional<T> GetOptional(const Json& value, const std::string& key)
{
    if (!value.contains(key) || value[key].is_null())
        return std::nullopt;
    return value[key].get<T>();
}

template <typename T>
T GetValueOrDefault(const Json& value, const std::string& key, const T& defaultValue)
{
    if (!value.contains(key) || value[key].is_null())
        return defaultValue;
    return value[key].get<T>();
}

bool ValidateOptionalStringFields(const Json& value, std::initializer_list<std::string_view> keys)
{
    for (const auto key : keys)
    {
        const auto it = value.find(std::string(key));
        if (it != value.end() && !it->is_null() && !it->is_string())
            return false;
    }
    return true;
}

bool ValidateTiming(const Json& value, int& pollIntervalMilliseconds)
{
    const std::initializer_list<std::string_view> timingKeys = {
        "navigationMs", "mailboxLoadMs", "collectionSettleMs", "homeTimeoutMs",
        "homeRetryTimeoutMs", "homeVerifyTimeoutMs", "backAttempts", "backSettleMs",
        "pollIntervalMs", "teamDownloadMs", "nextRaceLoadMs", "playbackLoadMs",
        "skipSettleMs", "raceTimeoutMs", "shopProbeMs", "betweenRacesMs"};
    if (!HasOnlyKeys(value, timingKeys))
        return false;

    // Preserve the old loader's typed int conversions for every accepted v1
    // timing field, including business timing fields that the graph never uses.
    for (const auto key : timingKeys)
        (void)GetValueOrDefault<int>(value, std::string(key), 0);
    pollIntervalMilliseconds = GetValueOrDefault(value, "pollIntervalMs", 300);
    return true;
}

} // namespace

std::optional<PipelineMetadata> ReadHachimiV1PipelineMetadata(const Json& root)
{
    try
    {
        if (!HasOnlyKeys(root, {"name", "start", "schemaVersion", "description", "referenceWidth",
                "referenceHeight", "templates", "uma", "tasks", "timing"}))
            return std::nullopt;

        PipelineMetadata metadata;
        metadata.schemaVersion = GetValueOrDefault(root, "schemaVersion", 1);
        if (metadata.schemaVersion != 1)
            return std::nullopt;
        metadata.name = GetValueOrDefault<std::string>(root, "name", "");
        metadata.start = GetOptional<std::string>(root, "start");
        metadata.description = GetValueOrDefault<std::string>(root, "description", "");
        metadata.referenceWidth = GetValueOrDefault(root, "referenceWidth", 900);
        metadata.referenceHeight = GetValueOrDefault(root, "referenceHeight", 1600);

        if (root.contains("templates"))
        {
            const auto& templates = root["templates"];
            if (!HasOnlyKeys(templates, {"raceResult", "randomShop"})
                || !ValidateOptionalStringFields(templates, {"raceResult", "randomShop"}))
                return std::nullopt;
        }

        if (root.contains("uma"))
        {
            const auto& uma = root["uma"];
            if (!HasOnlyKeys(uma, {"charaId", "cardDeckId"})
                || !ValidateOptionalStringFields(uma, {"charaId", "cardDeckId"}))
                return std::nullopt;
        }

        if (root.contains("timing")
            && !ValidateTiming(root["timing"], metadata.pollIntervalMilliseconds))
            return std::nullopt;

        return metadata;
    }
    catch (const std::exception&)
    {
        return std::nullopt;
    }
}

} // namespace UmaAssistant::Resources
