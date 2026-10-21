#pragma once

#include "CareerEntryFlow.hpp"

namespace UmaAssistant::GameTasks
{

/// Applies the Independent Training setup at Final Confirmation, starts the
/// mode once, and reports success only after its distinct Home marker returns.
class NativeIndependentTrainingFlow final
{
public:
    /// Resolves whether the shared Home-to-confirmation entry flow is needed.
    /// A verified post-start checkpoint must go straight to the home probe so
    /// recovery from Home can never submit a second Career Start action.
    [[nodiscard]] static bool NeedsEntry(
        Career::CareerRuntimeConfig const& config,
        TaskEngine::ITaskExecutionContext& context,
        bool& needsEntry,
        std::string& error,
        bool resumeAfterRecovery = false);

    static void RegisterActions(
        Career::CareerRuntimeConfig const& config,
        GameData::CareerSelectionCatalog const& catalogs,
        TaskEngine::ActionRegistry& registry);

    [[nodiscard]] static CareerEntryResult Run(
        Career::CareerRuntimeConfig const& config,
        Career::CareerVisualResources& resources,
        Career::ICareerObserver& observer,
        TaskEngine::ITaskExecutionContext& context,
        bool resumeAfterRecovery = false,
        std::optional<CareerDomain::CareerObservation> confirmedEntry = {});
};

} // namespace UmaAssistant::GameTasks
