using System.Globalization;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Values read from one training card.  A nullable value means that the
/// corresponding glyph was not reliably observed; it is deliberately not
/// treated as zero by the scorer.
/// </summary>
public sealed record UraTrainingCandidate(
    string TrainingType,
    int? SpeedGain,
    int? StaminaGain,
    int? PowerGain,
    int? GutsGain,
    int? WitGain,
    int? SkillPointGain,
    int? FailureRatePercent,
    int? TrainingLevel = null,
    int? UnbondedSupportCount = null,
    int? HintCount = null,
    double CoreConfidence = 0,
    double FailureRateConfidence = 0,
    double AuxiliaryConfidence = 0)
{
    public bool HasCompleteCoreValues =>
        SpeedGain is >= 0
        && StaminaGain is >= 0
        && PowerGain is >= 0
        && GutsGain is >= 0
        && WitGain is >= 0
        && SkillPointGain is >= 0;

    public bool HasReliableCoreValues =>
        HasCompleteCoreValues && CoreConfidence >= UraSmartTrainingScorer.MinimumCoreConfidence;

    public bool HasReliableFailureRate =>
        FailureRatePercent is >= 0 and <= 100
        && FailureRateConfidence >= UraSmartTrainingScorer.MinimumFailureRateConfidence;

    public bool IsSafe =>
        HasReliableCoreValues
        && HasReliableFailureRate
        && FailureRatePercent!.Value <= UraSmartTrainingScorer.MaximumFailureRatePercent;

    public int AttributeTotal =>
        (SpeedGain ?? 0) + (StaminaGain ?? 0) + (PowerGain ?? 0)
        + (GutsGain ?? 0) + (WitGain ?? 0);

    public static UraTrainingCandidate Reliable(
        string trainingType,
        int speed,
        int stamina,
        int power,
        int guts,
        int wit,
        int skillPoints,
        int failureRate,
        double confidence = 1) =>
        new(trainingType, speed, stamina, power, guts, wit, skillPoints,
            failureRate, CoreConfidence: confidence, FailureRateConfidence: confidence);
}

public enum UraTrainingDistance
{
    Unknown,
    Sprint,
    Mile,
    Middle,
    Extended,
}

public sealed record UraTrainingScoreBreakdown(
    UraTrainingDistance Distance,
    double SpeedContribution,
    double StaminaContribution,
    double PowerContribution,
    double GutsContribution,
    double WitContribution,
    double SkillPointContribution,
    double SuccessProbability,
    double BaseScore,
    double BondBonus,
    double HintBonus,
    double LowEnergyWitBonus,
    double TotalScore,
    string? ExclusionReason = null)
{
    public static UraTrainingScoreBreakdown Excluded(
        UraTrainingCandidate candidate,
        string reason) =>
        new(UraTrainingDistance.Unknown, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, reason);
}

public sealed record UraSmartTrainingDecision(
    UraTrainingCandidate? Candidate,
    UraTrainingScoreBreakdown? Score,
    IReadOnlyList<(UraTrainingCandidate Candidate, UraTrainingScoreBreakdown Score)> ScoredCandidates,
    string Reason)
{
    public bool IsSafe => Candidate is not null && Score?.ExclusionReason is null;
}

/// <summary>
/// Scores only complete, confidence-qualified observations.  This is kept
/// independent from ADB/OCR so the same policy can be tested from captured
/// evidence without claiming that OCR is more reliable than it is.
/// </summary>
public static class UraSmartTrainingScorer
{
    public const int MaximumFailureRatePercent = 5;
    public const double MinimumCoreConfidence = 0.80;
    public const double MinimumFailureRateConfidence = 0.80;

    private static readonly Dictionary<UraTrainingDistance, double[]> DistanceWeights =
        new Dictionary<UraTrainingDistance, double[]>
        {
            [UraTrainingDistance.Sprint] = [1.4, 0.5, 1.0, 0.3, 0.8],
            [UraTrainingDistance.Mile] = [1.4, 0.8, 1.0, 0.3, 0.8],
            [UraTrainingDistance.Middle] = [1.4, 1.2, 1.0, 0.3, 0.8],
            [UraTrainingDistance.Extended] = [1.4, 1.6, 1.0, 0.3, 0.8],
            [UraTrainingDistance.Unknown] = [1.4, 1.2, 1.0, 0.3, 0.8],
        };

    public static UraTrainingDistance ParseDistance(string? distanceBand, int? distance = null)
    {
        var text = distanceBand?.Trim().ToLowerInvariant() ?? string.Empty;
        if (text.Contains("short") || text.Contains("sprint") || text.Contains('短'))
            return UraTrainingDistance.Sprint;
        if (text.Contains("mile") || text.Contains("英里") || text.Contains("マイル"))
            return UraTrainingDistance.Mile;
        if (text.Contains("long") || text.Contains('长') || text.Contains('長'))
            return UraTrainingDistance.Extended;
        if (text.Contains("middle") || text.Contains("medium") || text.Contains('中'))
            return UraTrainingDistance.Middle;
        if (distance is >= 1000)
            return distance <= 1400 ? UraTrainingDistance.Sprint
                : distance <= 1800 ? UraTrainingDistance.Mile
                : distance <= 2400 ? UraTrainingDistance.Middle
                : UraTrainingDistance.Extended;
        return UraTrainingDistance.Unknown;
    }

    public static UraTrainingScoreBreakdown Score(
        UraTrainingCandidate candidate,
        UraTrainingDistance distance,
        string? careerStage,
        int? energy)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!candidate.HasReliableCoreValues)
            return UraTrainingScoreBreakdown.Excluded(candidate, "core gains are unknown or below confidence threshold");
        if (!candidate.HasReliableFailureRate)
            return UraTrainingScoreBreakdown.Excluded(candidate, "failure rate is unknown or below confidence threshold");
        if (candidate.FailureRatePercent!.Value > MaximumFailureRatePercent)
            return UraTrainingScoreBreakdown.Excluded(candidate, $"failure rate {candidate.FailureRatePercent}% exceeds {MaximumFailureRatePercent}%");

        var weights = DistanceWeights.TryGetValue(distance, out var selected)
            ? selected
            : DistanceWeights[UraTrainingDistance.Unknown];
        var speed = candidate.SpeedGain!.Value * weights[0];
        var stamina = candidate.StaminaGain!.Value * weights[1];
        var power = candidate.PowerGain!.Value * weights[2];
        var guts = candidate.GutsGain!.Value * weights[3];
        var wit = candidate.WitGain!.Value * weights[4];
        var skill = candidate.SkillPointGain!.Value * 0.5;
        var success = 1 - candidate.FailureRatePercent.Value / 100d;
        var baseScore = (speed + stamina + power + guts + wit + skill) * success;
        if (baseScore <= 0)
            return UraTrainingScoreBreakdown.Excluded(candidate, "expected gains are zero");

        var bond = 0d;
        if (candidate.UnbondedSupportCount is >= 0
            && candidate.AuxiliaryConfidence >= MinimumCoreConfidence)
        {
            var perSupport = careerStage?.Trim().ToLowerInvariant() switch
            {
                "junior" => 12,
                "classic" => 6,
                _ => 0,
            };
            bond = candidate.UnbondedSupportCount.Value * perSupport;
        }

        var hint = candidate.HintCount is >= 0
            && candidate.AuxiliaryConfidence >= MinimumCoreConfidence
            ? candidate.HintCount.Value * 4
            : 0;
        var lowEnergyWit = energy is < 50
            && candidate.TrainingType.Equals("wit", StringComparison.OrdinalIgnoreCase)
            ? 15
            : 0;
        return new(distance, speed, stamina, power, guts, wit, skill, success,
            baseScore, bond, hint, lowEnergyWit,
            baseScore + bond + hint + lowEnergyWit);
    }

    public static UraSmartTrainingDecision Choose(
        IEnumerable<UraTrainingCandidate> candidates,
        UraTrainingDistance distance,
        string? careerStage,
        int? energy)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var scored = candidates
            .Where(candidate => UraTrainingTypeCatalog.TryNormalize(candidate.TrainingType, out _))
            .Select(candidate => (Candidate: candidate, Score: Score(candidate, distance, careerStage, energy)))
            .ToArray();
        var safe = scored
            .Where(item => item.Score.ExclusionReason is null)
            .OrderByDescending(item => item.Score.TotalScore)
            .ThenBy(item => item.Candidate.FailureRatePercent)
            .ThenBy(item => TieBreakRank(item.Candidate.TrainingType))
            .ToArray();
        if (safe.Length == 0)
            return new(null, null, scored,
                "No training candidate had reliable positive gains and failure rate at or below 5%.");

        var winner = safe[0];
        return new(winner.Candidate, winner.Score, scored,
            "Selected the highest-scoring safe training candidate.");
    }

    private static int TieBreakRank(string trainingType) => trainingType.Trim().ToLowerInvariant() switch
    {
        "speed" => 0,
        "stamina" => 1,
        "power" => 2,
        "wit" => 3,
        "guts" => 4,
        _ => 99,
    };
}

/// <summary>
/// URA smart strategy.  The main page returns a harmless speed placeholder
/// so the existing picker entry can be opened; the dedicated picker flow
/// replaces it with the score selected after all five candidates are read.
/// </summary>
public sealed class UraSmartTrainingStrategy : UraDefaultStrategy
{
    public const string StrategyId = "smart-balanced-safe";

    public UraSmartTrainingStrategy()
        : base("speed", restThreshold: 50)
    {
    }

    public UraSmartTrainingDecision? LastDecision { get; private set; }

    /// <summary>
    /// Optional data-only race lookup supplied by the engine. It lets smart
    /// scoring use the selected trainee's global race distance while the
    /// normal scenario module keeps its existing OCR-derived objective flow.
    /// </summary>
    internal Func<UraCareerSessionState, UraRaceDefinition?>? DistanceRaceResolver { get; set; }

    public UraSmartTrainingDecision SelectCandidate(
        UraScenarioModule scenarioModule,
        UraCareerSessionState state,
        IEnumerable<UraTrainingCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(scenarioModule);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(candidates);
        var race = DistanceRaceResolver?.Invoke(state) ?? scenarioModule.CurrentRace(state);
        var distance = UraSmartTrainingScorer.ParseDistance(
            race?.Course.DistanceBand,
            race?.Course.Distance);
        var stage = ParseStage(state.TurnPositionLabel);
        var decision = UraSmartTrainingScorer.Choose(
            candidates, distance, stage, state.Energy.Value);
        LastDecision = decision;
        state.SmartTrainingCandidates = decision.ScoredCandidates.Select(item => item.Candidate).ToArray();
        if (decision.Candidate is null)
        {
            state.SmartTrainingFallbackPending = true;
            state.SmartTrainingPreviewReady = false;
            state.SmartTrainingChosenType = null;
            return decision;
        }

        state.SmartTrainingFallbackPending = false;
        state.SmartTrainingChosenType = decision.Candidate.TrainingType.Trim().ToLowerInvariant();
        state.SmartTrainingPreviewReady = true;
        return decision;
    }

    public override UraActionIntent ChooseTurnAction(
        UraScenarioModule scenarioModule,
        UraCareerSessionState state)
    {
        ArgumentNullException.ThrowIfNull(scenarioModule);
        ArgumentNullException.ThrowIfNull(state);
        if (state.SmartTrainingFallbackPending)
        {
            state.SmartTrainingFallbackPending = false;
            state.SmartTrainingCandidates = [];
            state.SmartTrainingChosenType = null;
            return new(
                UraPlannedAction.Rest,
                null,
                "No safe training candidate was readable; returning to Rest as the configured fallback.",
                false,
                [UraPlannedAction.Training]);
        }

        var intent = base.ChooseTurnAction(scenarioModule, state);
        // Low energy goes straight to Rest even when the old mood policy
        // suggests Recreation. Required races keep their existing priority.
        if (intent.Action == UraPlannedAction.Recreation
            && state.Energy.Value is int energy && energy < RestThreshold)
        {
            return intent with
            {
                Action = UraPlannedAction.Rest,
                TargetId = null,
                Reason = $"Observed energy {energy}% is below {RestThreshold}%; choose Rest before Recreation.",
                FallbackActions = [UraPlannedAction.Training],
            };
        }
        if (intent.Action != UraPlannedAction.Training)
            return intent;
        if (state.Energy.Value is not int
            || state.Energy.Source == UraStateSource.Unknown
            || state.Energy.Confidence < 0.70)
        {
            return intent with
            {
                Action = UraPlannedAction.Rest,
                TargetId = null,
                Reason = intent.Reason
                    + " Energy was not read with sufficient confidence; using the cautious Rest fallback.",
                FallbackActions = [UraPlannedAction.Training],
            };
        }
        if (state.Energy.Value is < 80 && IsLateJune(state))
        {
            return intent with
            {
                Action = UraPlannedAction.Rest,
                TargetId = null,
                Reason = intent.Reason
                    + " Classic/Senior late June energy is below 80%; preparing for Summer Camp.",
                FallbackActions = [UraPlannedAction.Training],
            };
        }
        var target = state.SmartTrainingPreviewReady
            && UraTrainingTypeCatalog.TryNormalize(state.SmartTrainingChosenType, out var chosen)
            ? chosen
            : "speed";
        return intent with
        {
            TargetId = target,
            Reason = intent.Reason + (state.SmartTrainingPreviewReady
                ? $" Preview selected {target}."
                : " Smart preview will scan all five training cards before confirmation."),
        };
    }

    public override void ConfirmTraining(
        CareerSessionState<UraCareerSessionState> session,
        string trainingType)
    {
        ArgumentNullException.ThrowIfNull(session);
        LastDecision = null;
        session.Scenario.SmartTrainingCandidates = [];
        session.Scenario.SmartTrainingChosenType = null;
        session.Scenario.SmartTrainingPreviewReady = false;
        session.Scenario.SmartTrainingFallbackPending = false;
    }

    internal static string? ParseStage(string? turnPosition)
    {
        var text = turnPosition?.Trim().ToLowerInvariant() ?? string.Empty;
        if (text.Contains("junior"))
            return "junior";
        if (text.Contains("classic"))
            return "classic";
        if (text.Contains("senior"))
            return "senior";
        return null;
    }

    private static bool IsLateJune(UraCareerSessionState state)
    {
        var text = state.TurnPositionLabel?.Trim().ToLowerInvariant() ?? string.Empty;
        var knownYear = text.Contains("classic") || text.Contains("senior");
        var june = text.Contains("june") || text.Contains("jun") || text.Contains("六月");
        var late = text.Contains("late") || text.Contains("下半") || text.Contains("后半");
        return knownYear && june && late;
    }
}

/// <summary>
/// Resolves only the next race's distance for smart scoring. It does not
/// mutate scenario state or replace the runtime's objective reconstruction.
/// </summary>
internal sealed class UraSmartTrainingDistanceResolver
{
    private readonly UmaTraineeRecord _trainee;
    private readonly Dictionary<string, UmaCareerRaceRecord> _races;

    public UraSmartTrainingDistanceResolver(
        UmaTraineeRecord trainee,
        IEnumerable<UmaCareerRaceRecord> races)
    {
        _trainee = trainee ?? throw new ArgumentNullException(nameof(trainee));
        _races = races.ToDictionary(
            item => item.RaceId.ToString(CultureInfo.InvariantCulture),
            StringComparer.OrdinalIgnoreCase);
    }

    public UraRaceDefinition? Resolve(UraCareerSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var raceId = state.CurrentRaceId;
        if (raceId is null || !_races.TryGetValue(raceId, out var race))
        {
            raceId = _trainee.CareerObjectives
                .Where(item => item.Turn >= state.TurnIndex)
                .OrderBy(item => item.Turn)
                .ThenBy(item => item.Order)
                .SelectMany(item => item.RaceIds)
                .FirstOrDefault(item => _races.ContainsKey(item));
            if (raceId is null || !_races.TryGetValue(raceId, out race))
                return null;
        }

        return new UraRaceDefinition
        {
            RaceId = raceId,
            Name = race.NameEn,
            Grade = race.Grade,
            Course = new UraRaceCourse
            {
                Surface = race.Surface,
                Distance = race.Distance,
                DistanceBand = race.DistanceBand,
            },
            RewardFans = race.FansGained,
        };
    }
}
