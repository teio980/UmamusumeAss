#include "GameData/UmaGameDatabase.hpp"
#include "Common/PathEncoding.hpp"

#include <nlohmann/json.hpp>
#include <algorithm>
#include <fstream>
#include <stdexcept>
#include <utility>

namespace UmaAssistant::GameData
{

namespace
{

using Json = nlohmann::json;

std::string ReadString(const Json& object, const char* key)
{
    auto const value = object.find(key);
    if (value == object.end() || value->is_null()) return {};
    return value->get<std::string>();
}

std::string ReadId(const Json& value, const char* fieldName)
{
    if (value.is_string())
        return value.get<std::string>();
    if (value.is_number_integer())
        return value.dump();
    throw std::runtime_error(std::string("UMA database field '") + fieldName
        + "' must be a string or integer ID.");
}

int ReadIntOrDefault(const Json& object, const char* key, int fallback = 0)
{
    auto const value = object.find(key);
    if (value == object.end() || value->is_null()) return fallback;
    if (!value->is_number_integer())
        throw std::runtime_error(std::string("UMA database field '") + key + "' must be an integer.");
    return value->get<int>();
}

int ReadInt(const Json& object, const char* key)
{
    auto const value = object.find(key);
    if (value == object.end() || !value->is_number_integer())
        throw std::runtime_error(std::string("UMA trainee record is missing integer field '") + key + "'.");
    return value->get<int>();
}

bool ReadBool(const Json& object, const char* key)
{
    auto const value = object.find(key);
    return value != object.end() && value->is_boolean() && value->get<bool>();
}

std::map<std::string, std::string, std::less<>> ReadAptitudes(
    const Json& object,
    const char* key)
{
    std::map<std::string, std::string, std::less<>> result;
    auto const aptitudes = object.find(key);
    if (aptitudes == object.end() || !aptitudes->is_object()) return result;

    for (auto const& [name, grade] : aptitudes->items())
    {
        if (grade.is_string())
            result.emplace(name, grade.get<std::string>());
    }
    return result;
}

void AddIfPresent(
    std::vector<std::filesystem::path>& paths,
    std::filesystem::path path)
{
    std::error_code error;
    if (std::filesystem::is_regular_file(path, error)
        && std::find(paths.begin(), paths.end(), path) == paths.end())
    {
        paths.push_back(std::move(path));
    }
}

} // namespace

UmaGameDatabase::UmaGameDatabase(std::filesystem::path resourceRoot)
    : m_resourceRoot(std::move(resourceRoot))
{
}

bool UmaGameDatabase::Load(std::string& errorMessage)
{
    m_trainees.clear();
    m_careerRaces.clear();
    auto const databasePath = m_resourceRoot / "uma" / "database" / "global" / "trainees.json";
    try
    {
        std::ifstream input(databasePath, std::ios::binary);
        if (!input)
        {
            errorMessage = "UMA trainee database was not found: " + Paths::ToUtf8(databasePath);
            return false;
        }

        auto const records = Json::parse(input);
        if (!records.is_array())
        {
            errorMessage = "UMA trainee database must contain a JSON array.";
            return false;
        }

        for (auto const& record : records)
        {
            if (!record.is_object())
                continue;
            UmaTraineeRecord trainee;
            trainee.traineeId = ReadInt(record, "trainee_id");
            trainee.baseCharacterId = ReadInt(record, "base_character_id");
            trainee.nameEn = ReadString(record, "name_en");
            trainee.available = ReadBool(record, "available");

            auto const aptitudes = record.find("aptitudes");
            if (aptitudes != record.end() && aptitudes->is_object())
            {
                trainee.surfaceAptitudes = ReadAptitudes(*aptitudes, "surface");
                trainee.distanceAptitudes = ReadAptitudes(*aptitudes, "distance");
                trainee.strategyAptitudes = ReadAptitudes(*aptitudes, "strategy");
            }

            auto const objectives = record.find("career_objectives");
            if (objectives != record.end() && !objectives->is_null())
            {
                if (!objectives->is_array())
                    throw std::runtime_error("UMA trainee career_objectives must be an array.");
                for (auto const& objectiveJson : *objectives)
                {
                    if (!objectiveJson.is_object()) continue;
                    UmaCareerObjectiveRecord objective;
                    objective.objectiveId = ReadString(objectiveJson, "objective_id");
                    objective.order = ReadIntOrDefault(objectiveJson, "order");
                    objective.kind = ReadString(objectiveJson, "kind");
                    objective.turn = ReadIntOrDefault(objectiveJson, "turn");
                    auto const raceIds = objectiveJson.find("race_ids");
                    if (raceIds != objectiveJson.end() && !raceIds->is_null())
                    {
                        if (!raceIds->is_array())
                            throw std::runtime_error("UMA trainee objective race_ids must be an array.");
                        for (auto const& raceId : *raceIds)
                            objective.raceIds.push_back(ReadId(raceId, "race_ids"));
                    }
                    trainee.careerObjectives.push_back(std::move(objective));
                }
            }

            if (!m_trainees.emplace(trainee.traineeId, std::move(trainee)).second)
            {
                errorMessage = "UMA trainee database contains a duplicate trainee ID.";
                m_trainees.clear();
                return false;
            }
        }

        // Older/minimal data snapshots may only package trainee records. In
        // that case callers retain the previous behavior and distance stays
        // unknown; packaged global data includes the full race catalog.
        auto const racesPath = m_resourceRoot / "uma" / "database" / "global" / "races.json";
        std::error_code fileError;
        if (std::filesystem::is_regular_file(racesPath, fileError))
        {
            std::ifstream racesInput(racesPath, std::ios::binary);
            if (!racesInput)
                throw std::runtime_error("could not open career race database");
            auto const raceRecords = Json::parse(racesInput);
            if (!raceRecords.is_array())
                throw std::runtime_error("UMA career race database must contain a JSON array.");
            for (auto const& raceJson : raceRecords)
            {
                if (!raceJson.is_object()) continue;
                UmaCareerRaceRecord race;
                auto const raceId = raceJson.find("race_id");
                if (raceId == raceJson.end() || raceId->is_null())
                    throw std::runtime_error("UMA career race record is missing race_id.");
                race.raceId = ReadId(*raceId, "race_id");
                race.nameEn = ReadString(raceJson, "name_en");
                race.grade = ReadString(raceJson, "grade");
                race.distanceMeters = ReadIntOrDefault(raceJson, "distance");
                race.distanceBand = ReadString(raceJson, "distance_band");
                if (!m_careerRaces.emplace(race.raceId, std::move(race)).second)
                {
                    errorMessage = "UMA career race database contains a duplicate race ID.";
                    m_trainees.clear();
                    m_careerRaces.clear();
                    return false;
                }
            }
        }
        return true;
    }
    catch (std::exception const& exception)
    {
        errorMessage = "Could not read UMA trainee database: " + std::string(exception.what());
        m_trainees.clear();
        m_careerRaces.clear();
        return false;
    }
}

UmaTraineeRecord const* UmaGameDatabase::TryGetTrainee(int traineeId) const noexcept
{
    auto const position = m_trainees.find(traineeId);
    return position == m_trainees.end() ? nullptr : &position->second;
}

UmaCareerRaceRecord const* UmaGameDatabase::TryGetCareerRace(
    std::string const& raceId) const noexcept
{
    auto const position = m_careerRaces.find(raceId);
    return position == m_careerRaces.end() ? nullptr : &position->second;
}

std::vector<std::filesystem::path> UmaGameDatabase::GetTraineeImagePaths(
    UmaTraineeRecord const& trainee) const
{
    auto const id = std::to_string(trainee.traineeId);
    auto const baseId = std::to_string(trainee.baseCharacterId);
    auto const umaRoot = m_resourceRoot / "uma";
    std::vector<std::filesystem::path> paths;
    paths.reserve(5);

    // Preserve the existing source priority: identity crops win over generic
    // image assets, followed by live-outfit references and their full images.
    AddIfPresent(paths, umaRoot / "system_reference" / (id + ".webp"));
    AddIfPresent(paths, umaRoot / "assets" / "images" / "global" / "trainees" / (id + ".webp"));
    AddIfPresent(paths, umaRoot / "system_reference" / (baseId + "_live.webp"));
    AddIfPresent(paths, umaRoot / "assets" / "images" / "global" / "live_outfits" / (baseId + ".png"));
    return paths;
}

} // namespace UmaAssistant::GameTasks
