#pragma once

#include "Resources/ResourceSnapshot.hpp"
#include "TaskEngine/ITaskExecutionContext.hpp"

namespace UmaAssistant::GameTasks
{
enum class GameRecoveryDialogKind { none, dateChanged, connectionError, sessionError, missingTemplates };

struct GameRecoveryDialogMatch
{
    GameRecoveryDialogKind kind{GameRecoveryDialogKind::none};
    TaskEngine::TaskMatchResult match;
    int tapX{-1};
    int tapY{-1};

    [[nodiscard]] int TargetX() const noexcept { return tapX < 0 ? match.CenterX() : tapX; }
    [[nodiscard]] int TargetY() const noexcept { return tapY < 0 ? match.CenterY() : tapY; }
};

// Evidence and its derived button point stay in reference coordinates and
// belong to the supplied frame. Session Error accepts any one marker immediately.
[[nodiscard]] GameRecoveryDialogMatch DetectGameRecoveryDialog(
    const TaskEngine::TaskFrameSnapshot* frame,
    const Resources::ResourceSnapshot* resources,
    const std::filesystem::path& resourceRoot);
}
