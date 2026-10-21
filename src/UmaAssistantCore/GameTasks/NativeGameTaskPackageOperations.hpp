#pragma once

#include "IGameTaskPackageOperations.hpp"
#include "Session/DeviceSession.hpp"

#include <memory>
#include <stop_token>

namespace UmaAssistant::GameTasks
{

/// <summary>DeviceSession-backed package operations for production game tasks.</summary>
class NativeGameTaskPackageOperations final : public IGameTaskPackageOperations
{
public:
    NativeGameTaskPackageOperations(
        std::shared_ptr<DeviceSession> session,
        std::stop_token cancellation);

    GameTaskPackageResult Start(
        const std::string& packageId,
        const std::string& activityName) override;

    GameTaskPackageResult Stop(const std::string& packageId) override;

    GameTaskPackageResult Query(const std::string& packageId) override;

private:
    GameTaskPackageResult Execute(
        PackageOperationKind operation,
        const std::string& packageId,
        const std::string& activityName = {});

    std::shared_ptr<DeviceSession> m_session;
    std::stop_token m_cancellation;
};

} // namespace UmaAssistant::GameTasks
