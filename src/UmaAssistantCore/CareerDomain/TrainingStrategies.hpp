#pragma once

#include "ICareerStrategy.hpp"

#include <string>
#include <optional>
#include <vector>

namespace UmaAssistant::CareerDomain
{

class TrainingTypeCatalog
{
public:
    [[nodiscard]] static bool TryNormalize(const std::string& value, std::string& normalized);
    [[nodiscard]] static bool IsSupported(const std::string& value);
};

struct TrainingRatio
{
    int speed{2};
    int stamina{1};
    int power{0};
    int guts{0};
    int wit{0};

    [[nodiscard]] bool IsValid() const noexcept;
    [[nodiscard]] TrainingRatio Reduce() const;
    [[nodiscard]] std::vector<std::string> BuildCycle() const;

private:
    static int Gcd(int a, int b) noexcept;
};

class DefaultTrainingStrategy : public ICareerStrategy
{
public:
    explicit DefaultTrainingStrategy(
        std::string trainingType = "speed",
        int restThreshold = 50);

    ActionIntent Choose(
        const CareerState& state,
        const IScenarioModule* scenario) override;

private:
    std::string m_trainingType;
    int m_restThreshold;
};

class RatioTrainingStrategy : public ICareerStrategy
{
public:
    explicit RatioTrainingStrategy(
        TrainingRatio ratio = TrainingRatio{2, 1, 0, 0, 0},
        int restThreshold = 50);

    ActionIntent Choose(
        const CareerState& state,
        const IScenarioModule* scenario) override;

    void ConfirmTraining(
        CareerState& state,
        const std::string& trainingType) override;

private:
    friend class WitRatioTrainingStrategy;
    void ConfirmCurrentStep();

    std::vector<std::string> m_cycle;
    size_t m_currentIndex{0};
    int m_restThreshold;
    std::optional<std::string> m_pendingTrainingType;
};

class WitRatioTrainingStrategy : public ICareerStrategy
{
public:
    explicit WitRatioTrainingStrategy(
        TrainingRatio ratio = TrainingRatio{2, 1, 0, 0, 1},
        int restThreshold = 50,
        int witEnergyThreshold = 35);

    ActionIntent Choose(
        const CareerState& state,
        const IScenarioModule* scenario) override;

    void ConfirmTraining(
        CareerState& state,
        const std::string& trainingType) override;

private:
    RatioTrainingStrategy m_innerRatio;
    int m_restThreshold;
    int m_witEnergyThreshold;
    bool m_witOverridePending{false};
};

} // namespace UmaAssistant::CareerDomain
