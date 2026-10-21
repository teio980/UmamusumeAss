#pragma once

#include "GameTaskResult.hpp"
#include <string>

namespace UmaAssistant::GameTasks
{

/// <summary>
/// Package operations are kept behind a game-task boundary so startup behavior
/// can be replayed with a fake and the generic task engine stays device-agnostic.
/// </summary>
class IGameTaskPackageOperations
{
public:
    virtual ~IGameTaskPackageOperations() = default;

    virtual GameTaskPackageResult Start(
        const std::string& packageId,
        const std::string& activityName) = 0;

    virtual GameTaskPackageResult Stop(const std::string& packageId) = 0;

    virtual GameTaskPackageResult Query(const std::string& packageId) = 0;
};

} // namespace UmaAssistant::GameTasks
