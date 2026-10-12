#pragma once

#include "CareerRuntimeConfig.hpp"
#include <nlohmann/json.hpp>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <optional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

namespace UmaAssistant::Career
{

inline std::string ComputeDeviceHash(const std::string& deviceKey)
{
    // FNV-1a 64-bit hash converted to 16-hex-digit lowercase string
    std::uint64_t hash = 14695981039346656037ull;
    for (unsigned char c : deviceKey)
    {
        hash ^= c;
        hash *= 1099511628211ull;
    }
    std::ostringstream ss;
    ss << std::hex << std::setfill('0') << std::setw(16) << hash;
    return ss.str();
}

class NormalCareerSkillCache
{
public:
    static std::filesystem::path GetPath(
        const std::filesystem::path& checkpointDir,
        int traineeId,
        const std::string& deviceKey)
    {
        const auto hash = ComputeDeviceHash(deviceKey);
        return checkpointDir / ("normal-skills-" + std::to_string(traineeId) + "-" + hash + ".json");
    }

    static std::vector<int> Load(
        const std::filesystem::path& checkpointDir,
        int traineeId,
        const std::string& deviceKey)
    {
        const auto path = GetPath(checkpointDir, traineeId, deviceKey);
        if (!std::filesystem::exists(path))
            return {};
        try
        {
            std::ifstream file(path);
            if (!file.is_open()) return {};
            nlohmann::json j;
            file >> j;
            if (j.value("version", 0) != 1 || j.value("traineeId", 0) != traineeId
                || j.value("deviceKey", std::string{}) != deviceKey)
                return {};
            auto ids = j.value("skillIds", std::vector<int>{});
            std::erase_if(ids, [](int id) { return id <= 0; });
            std::sort(ids.begin(), ids.end());
            ids.erase(std::unique(ids.begin(), ids.end()), ids.end());
            return ids;
        }
        catch (...)
        {
            return {};
        }
    }

    static void Save(
        const std::filesystem::path& checkpointDir,
        int traineeId,
        const std::string& deviceKey,
        const std::vector<int>& skillIds)
    {
        try
        {
            std::filesystem::create_directories(checkpointDir);
            const auto target = GetPath(checkpointDir, traineeId, deviceKey);
            auto temp = target;
            temp += ".tmp";
            nlohmann::json j;
            j["version"] = 1;
            j["traineeId"] = traineeId;
            j["deviceKey"] = deviceKey;
            j["skillIds"] = skillIds;
            {
                std::ofstream file(temp);
                if (!file.is_open()) return;
                file << j.dump(2);
                file.flush();
                if (!file) throw std::runtime_error("Could not write learned skill cache.");
            }
            std::filesystem::rename(temp, target);
        }
        catch (...)
        {
        }
    }

    static void Clear(
        const std::filesystem::path& checkpointDir,
        int traineeId,
        const std::string& deviceKey)
    {
        try
        {
            const auto path = GetPath(checkpointDir, traineeId, deviceKey);
            if (std::filesystem::exists(path))
                std::filesystem::remove(path);
        }
        catch (...)
        {
        }
    }
};

struct SmartTrainingPendingRecord
{
    int traineeId{0};
    std::string trainingType;
    int turnIndex{0};
    std::string recordedAt;
};

class SmartTrainingConfirmationStore
{
public:
    static std::filesystem::path GetPath(
        const std::filesystem::path& checkpointDir,
        int traineeId,
        const std::string& deviceKey)
    {
        const auto hash = ComputeDeviceHash(deviceKey + "|" + std::to_string(traineeId));
        return checkpointDir / ("smart-training-pending-" + hash + ".json");
    }

    static std::optional<SmartTrainingPendingRecord> Load(
        const std::filesystem::path& checkpointDir,
        int traineeId,
        const std::string& deviceKey)
    {
        const auto path = GetPath(checkpointDir, traineeId, deviceKey);
        if (!std::filesystem::exists(path))
            return std::nullopt;
        try
        {
            std::ifstream file(path);
            if (!file.is_open()) throw std::runtime_error("Could not open pending training guard.");
            nlohmann::json j;
            file >> j;
            SmartTrainingPendingRecord record;
            record.traineeId = j.value("traineeId", 0);
            record.trainingType = j.value("trainingType", "");
            record.turnIndex = j.value("turnIndex", 0);
            record.recordedAt = j.value("recordedAt", "");
            const bool knownTraining = record.trainingType == "speed" || record.trainingType == "stamina"
                || record.trainingType == "power" || record.trainingType == "guts" || record.trainingType == "wit";
            if (record.traineeId != traineeId || !knownTraining || record.turnIndex < 0
                || j.value("version", 1) != 1 || j.value("deviceKey", deviceKey) != deviceKey)
                throw std::runtime_error("Pending training guard contains invalid identity or training data.");
            return record;
        }
        catch (...)
        {
            throw std::runtime_error("Existing pending training guard is unreadable; input remains blocked.");
        }
    }

    static bool Save(
        const std::filesystem::path& checkpointDir,
        int traineeId,
        const std::string& deviceKey,
        const std::string& trainingType,
        int turnIndex)
    {
        try
        {
            std::filesystem::create_directories(checkpointDir);
            const auto target = GetPath(checkpointDir, traineeId, deviceKey);
            auto temp = target;
            temp += ".tmp";
            nlohmann::json j;
            j["version"] = 1;
            j["deviceKey"] = deviceKey;
            j["traineeId"] = traineeId;
            j["trainingType"] = trainingType;
            j["turnIndex"] = turnIndex;
            j["recordedAt"] = std::to_string(std::chrono::system_clock::now().time_since_epoch().count());
            {
                std::ofstream file(temp);
                if (!file.is_open()) return false;
                file << j.dump(2);
                file.flush();
                if (!file) return false;
            }
            std::filesystem::rename(temp, target);
            return true;
        }
        catch (...)
        {
            return false;
        }
    }

    static void Clear(
        const std::filesystem::path& checkpointDir,
        int traineeId,
        const std::string& deviceKey)
    {
        try
        {
            const auto path = GetPath(checkpointDir, traineeId, deviceKey);
            if (std::filesystem::exists(path))
                std::filesystem::remove(path);
        }
        catch (...)
        {
        }
    }
};

} // namespace UmaAssistant::Career
