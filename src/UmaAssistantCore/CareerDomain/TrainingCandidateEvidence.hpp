#pragma once

#include "ObservedValue.hpp"

#include <array>
#include <optional>
#include <string>

namespace UmaAssistant::CareerDomain
{

// Value-only observations collected from a training preview. Scenario modules
// may interpret these shared fields differently, but unknowns and provenance
// remain intact across the strategy boundary.
struct TrainingCandidateEvidence
{
    std::string trainingType;
    std::array<ObservedValue<int>, 5> statGains{
        ObservedValue<int>::Unknown(), ObservedValue<int>::Unknown(),
        ObservedValue<int>::Unknown(), ObservedValue<int>::Unknown(),
        ObservedValue<int>::Unknown()};
    ObservedValue<int> skillPointGain{ObservedValue<int>::Unknown()};
    ObservedValue<int> failureRatePercent{ObservedValue<int>::Unknown()};
    ObservedValue<int> trainingLevel{ObservedValue<int>::Unknown()};
    ObservedValue<int> unbondedSupportCount{ObservedValue<int>::Unknown()};
    ObservedValue<int> hintCount{ObservedValue<int>::Unknown()};
};

} // namespace UmaAssistant::CareerDomain
