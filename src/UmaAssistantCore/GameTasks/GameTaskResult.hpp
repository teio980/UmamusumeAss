#pragma once

#include <string>

namespace UmaAssistant::GameTasks
{

struct NativeGameTaskResult
{
    std::string taskId;
    bool succeeded{false};
    int completedUnits{0};
    int totalUnits{0};
    bool processDetected{false};
    int errorCode{0};
    std::string message;
};

struct GameTaskPackageResult
{
    bool succeeded{false};
    bool processDetected{false};
    bool cancelled{false};
    int errorCode{0};
    std::string message;
};

} // namespace UmaAssistant::GameTasks
