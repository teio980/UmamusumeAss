#pragma once

#include "ObservedValue.hpp"
#include <algorithm>
#include <cmath>
#include <string>
#include <vector>
#include <utility>

namespace UmaAssistant::CareerDomain
{

enum class ActionIntentKind
{
    DoNothing,
    Rest,
    Training,
    Infirmary,
    Recreation,
    Race,
    Skill,
    SelectEventOption,
    Custom
};

struct ActionIntent
{
    ActionIntentKind kind{ActionIntentKind::DoNothing};
    std::string trainingType; // "speed", "stamina", "power", "guts", "wit"
    int optionIndex{1};       // 1-based for event choices
    std::string raceTarget;
    std::string tactic;
    std::vector<int> skillIds;
    std::string customActionName;
    std::string reason;
    ObservationSource source{ObservationSource::Derived};
    double confidence{1.0};

    static double NormalizeConfidence(double value) noexcept
    {
        return std::isfinite(value) ? std::clamp(value, 0.0, 1.0) : 0.0;
    }

    static ActionIntent MakeRest(std::string reason = "Energy low", double confidence = 1.0, ObservationSource source = ObservationSource::Derived)
    {
        ActionIntent intent;
        intent.kind = ActionIntentKind::Rest;
        intent.reason = std::move(reason);
        intent.confidence = NormalizeConfidence(confidence);
        intent.source = source;
        return intent;
    }

    static ActionIntent MakeTraining(std::string type, std::string reason = "Strategy selected", double confidence = 1.0, ObservationSource source = ObservationSource::Derived)
    {
        ActionIntent intent;
        intent.kind = ActionIntentKind::Training;
        intent.trainingType = std::move(type);
        intent.reason = std::move(reason);
        intent.confidence = NormalizeConfidence(confidence);
        intent.source = source;
        return intent;
    }

    static ActionIntent MakeInfirmary(std::string reason = "Cure illness", double confidence = 1.0, ObservationSource source = ObservationSource::Derived)
    {
        ActionIntent intent;
        intent.kind = ActionIntentKind::Infirmary;
        intent.reason = std::move(reason);
        intent.confidence = NormalizeConfidence(confidence);
        intent.source = source;
        return intent;
    }

    static ActionIntent MakeRecreation(std::string reason = "Boost mood", double confidence = 1.0, ObservationSource source = ObservationSource::Derived)
    {
        ActionIntent intent;
        intent.kind = ActionIntentKind::Recreation;
        intent.reason = std::move(reason);
        intent.confidence = NormalizeConfidence(confidence);
        intent.source = source;
        return intent;
    }

    static ActionIntent MakeEventChoice(int option, std::string reason = "Default event choice", double confidence = 1.0, ObservationSource source = ObservationSource::Derived)
    {
        ActionIntent intent;
        intent.kind = ActionIntentKind::SelectEventOption;
        intent.optionIndex = option;
        intent.reason = std::move(reason);
        intent.confidence = NormalizeConfidence(confidence);
        intent.source = source;
        return intent;
    }

    static ActionIntent MakeRace(std::string target = "", std::string tactic = "", std::string reason = "Scheduled race", double confidence = 1.0, ObservationSource source = ObservationSource::Derived)
    {
        ActionIntent intent;
        intent.kind = ActionIntentKind::Race;
        intent.raceTarget = std::move(target);
        intent.tactic = std::move(tactic);
        intent.reason = std::move(reason);
        intent.confidence = NormalizeConfidence(confidence);
        intent.source = source;
        return intent;
    }

    static ActionIntent MakeAdvance(std::string reason = "Advance screen", double confidence = 1.0, ObservationSource source = ObservationSource::Derived)
    {
        ActionIntent intent;
        intent.kind = ActionIntentKind::Custom;
        intent.customActionName = "Advance";
        intent.reason = std::move(reason);
        intent.confidence = NormalizeConfidence(confidence);
        intent.source = source;
        return intent;
    }
};

} // namespace UmaAssistant::CareerDomain
