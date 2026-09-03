using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// The single ordered progress model for an Independent Training run.
/// Keeping entry, configuration, start, and post-start handling in one enum
/// makes a checkpoint self-describing and prevents contradictory flags.
/// </summary>
public enum IndependentTrainingStage
{
    EnterCareer,
    HandleExistingCareer,
    SelectScenario,
    SelectTrainee,
    SelectLegacy,
    SelectSupportDeck,
    OpenFinalConfirmation,
    SelectIndependentMode,
    ExpandLineup,
    ConfigureFocus,
    ConfigureAgenda,
    ConfigureSkills,
    CollapseLineup,
    ConfigureStrategy,
    StartTraining,
    HandlePostStartDialog,
    ReturnHome,
    Completed,
}

/// <summary>
/// Independent-only checkpoint state. It intentionally contains no URA
/// turn-engine values (turn, energy, objective, pending race or finale).
/// </summary>
public sealed class IndependentTrainingSessionState
{
    public const int CurrentCheckpointVersion = 1;

    private static readonly JsonSerializerOptions CheckpointJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };

    private static readonly JsonSerializerOptions LegacyJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public int Version { get; set; } = CurrentCheckpointVersion;

    public IndependentTrainingStage Stage { get; set; } =
        IndependentTrainingStage.EnterCareer;

    public int AgendaIndex { get; set; }
    public int SkillIndex { get; set; }
    public int? CurrentSkillId { get; set; }

    public string LastConfirmedScreen { get; set; } = "unknown";
    public int RetryCount { get; set; }
    public int ActionsCompleted { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this, CheckpointJsonOptions);

    public static IndependentTrainingSessionState Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);
        if (HasProperty(document.RootElement, "ConfigurationStep"))
        {
            return DeserializePreviousIndependentCheckpoint(document.RootElement);
        }

        var state = JsonSerializer.Deserialize<IndependentTrainingSessionState>(
            json,
            CheckpointJsonOptions)
            ?? throw new InvalidDataException("Independent checkpoint is empty.");
        state.NormalizeForResume();
        return state;
    }

    public void NormalizeForResume()
    {
        if (Version != CurrentCheckpointVersion)
            Version = CurrentCheckpointVersion;
        if (!Enum.IsDefined(Stage))
            Stage = IndependentTrainingStage.EnterCareer;

        AgendaIndex = Math.Max(0, AgendaIndex);
        SkillIndex = Math.Max(0, SkillIndex);
        RetryCount = Math.Max(0, RetryCount);
        ActionsCompleted = Math.Max(0, ActionsCompleted);
        LastConfirmedScreen = string.IsNullOrWhiteSpace(LastConfirmedScreen)
            ? "unknown"
            : LastConfirmedScreen.Trim();

        if (CurrentSkillId is <= 0)
            CurrentSkillId = null;

        // A cursor is meaningful only after its stage has been reached. Clear
        // stale values so a torn or hand-edited checkpoint cannot affect a
        // later stage.
        if ((int)Stage < (int)IndependentTrainingStage.ConfigureAgenda)
            AgendaIndex = 0;
        if (Stage != IndependentTrainingStage.ConfigureSkills)
            CurrentSkillId = null;
        if ((int)Stage < (int)IndependentTrainingStage.ConfigureSkills)
        {
            SkillIndex = 0;
            CurrentSkillId = null;
        }
    }

    private static bool HasProperty(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IndependentTrainingSessionState DeserializePreviousIndependentCheckpoint(
        JsonElement root)
    {
        var previous = root.Deserialize<PreviousIndependentCheckpointDto>(
            LegacyJsonOptions);
        if (previous is null)
            throw new InvalidDataException("Independent checkpoint is empty.");

        var state = new IndependentTrainingSessionState
        {
            Version = CurrentCheckpointVersion,
            LastConfirmedScreen = string.IsNullOrWhiteSpace(previous.LastScreenId)
                ? "unknown"
                : previous.LastScreenId,
            RetryCount = previous.RetryCount,
            ActionsCompleted = previous.ActionsCompleted,
            Stage = MapPreviousStage(previous),
        };
        state.NormalizeForResume();
        return state;
    }

    private static IndependentTrainingStage MapPreviousStage(
        PreviousIndependentCheckpointDto previous)
    {
        if (previous.Stage <= 0)
        {
            return MapEntryScreen(previous.LastScreenId);
        }

        if (previous.Stage == 1)
        {
            return previous.ConfigurationStep switch
            {
                <= 0 => IndependentTrainingStage.SelectIndependentMode,
                1 => IndependentTrainingStage.ExpandLineup,
                2 => IndependentTrainingStage.ConfigureFocus,
                3 => IndependentTrainingStage.ConfigureAgenda,
                4 => IndependentTrainingStage.ConfigureSkills,
                5 or 6 => IndependentTrainingStage.CollapseLineup,
                _ => IndependentTrainingStage.StartTraining,
            };
        }

        if (previous.Stage == 2)
            return IndependentTrainingStage.StartTraining;

        return previous.ConfigurationStep >= 7
            ? IndependentTrainingStage.Completed
            : IndependentTrainingStage.ConfigureStrategy;
    }

    private static IndependentTrainingStage MapEntryScreen(string? screenId) =>
        screenId?.Trim().ToLowerInvariant() switch
        {
            "career_continue" => IndependentTrainingStage.HandleExistingCareer,
            "scenario_select" => IndependentTrainingStage.SelectScenario,
            "trainee_select" => IndependentTrainingStage.SelectTrainee,
            "legacy_select" => IndependentTrainingStage.SelectLegacy,
            "support_select" or "support_autofill_confirmation" or "support_ready"
                or "support_start_transition" => IndependentTrainingStage.SelectSupportDeck,
            "career_final_confirmation" => IndependentTrainingStage.OpenFinalConfirmation,
            _ => IndependentTrainingStage.EnterCareer,
        };

    /// <summary>
    /// The previous refactor persisted a top-level stage plus a configuration
    /// sub-step. Read it once and immediately convert it to the single-stage
    /// model; no compatibility fields are kept on the live state.
    /// </summary>
    private sealed class PreviousIndependentCheckpointDto
    {
        public int Stage { get; set; }
        public int ConfigurationStep { get; set; }
        public string? LastScreenId { get; set; }
        public int RetryCount { get; set; }
        public int ActionsCompleted { get; set; }
    }
}

/// <summary>
/// The old URA checkpoint is intentionally read through this DTO, so
/// Independent remains independent of the URA session engine while still
/// offering best-effort migration. These booleans are legacy input only;
/// they are never part of the active checkpoint state.
/// </summary>
internal sealed class IndependentLegacyCheckpointDto
{
    private static readonly string[] LegacyPropertyNames =
    {
        nameof(IndependentModeSelected),
        nameof(IndependentLineupConfigured),
        nameof(IndependentTrainingFocusConfigured),
        nameof(IndependentAgendaConfigured),
        nameof(IndependentSkillsConfigured),
        nameof(IndependentLineupCollapsed),
        nameof(IndependentStrategyConfigured),
        nameof(IndependentSetupCompleted),
    };

    public bool IndependentModeSelected { get; set; }
    public bool IndependentLineupConfigured { get; set; }
    public bool IndependentTrainingFocusConfigured { get; set; }
    public bool IndependentAgendaConfigured { get; set; }
    public bool IndependentSkillsConfigured { get; set; }
    public bool IndependentLineupCollapsed { get; set; }
    public bool IndependentStrategyConfigured { get; set; }
    public bool IndependentSetupCompleted { get; set; }

    public static IndependentTrainingSessionState? TryMigrate(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.EnumerateObject().Any(property =>
                    LegacyPropertyNames.Contains(
                        property.Name,
                        StringComparer.OrdinalIgnoreCase)))
            {
                return null;
            }

            var dto = JsonSerializer.Deserialize<IndependentLegacyCheckpointDto>(json);
            if (dto is null)
                return null;

            var state = new IndependentTrainingSessionState
            {
                // The old flags were only written on the final confirmation
                // flow, so the first missing flag maps to the corresponding
                // new stage rather than pretending entry was incomplete.
                Stage = IndependentTrainingStage.SelectIndependentMode,
            };
            if (!dto.IndependentModeSelected)
                return state;
            state.Stage = IndependentTrainingStage.ExpandLineup;
            if (!dto.IndependentLineupConfigured)
                return state;
            state.Stage = IndependentTrainingStage.ConfigureFocus;
            if (!dto.IndependentTrainingFocusConfigured)
                return state;
            state.Stage = IndependentTrainingStage.ConfigureAgenda;
            if (!dto.IndependentAgendaConfigured)
                return state;
            state.Stage = IndependentTrainingStage.ConfigureSkills;
            if (!dto.IndependentSkillsConfigured)
                return state;
            state.Stage = IndependentTrainingStage.CollapseLineup;
            if (!dto.IndependentLineupCollapsed)
                return state;
            state.Stage = IndependentTrainingStage.ConfigureStrategy;
            if (!dto.IndependentStrategyConfigured)
                return state;
            state.Stage = IndependentTrainingStage.StartTraining;
            if (!dto.IndependentSetupCompleted)
                return state;

            state.Stage = IndependentTrainingStage.Completed;
            return state;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}

public sealed class IndependentCheckpointStore
{
    private readonly string _path;
    private readonly string _legacyPath;

    public IndependentCheckpointStore(int traineeId, string? rootDirectory = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(traineeId);

        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UmamusumeAss",
            "checkpoints");
        Directory.CreateDirectory(RootDirectory);
        _path = Path.Combine(RootDirectory, $"independent-{traineeId}.json");
        _legacyPath = Path.Combine(RootDirectory, $"ura-{traineeId}.json");
    }

    public string RootDirectory { get; }
    public string CheckpointPath => _path;
    public string LegacyCheckpointPath => _legacyPath;

    public async Task<IndependentTrainingSessionState?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(_path, cancellationToken)
                .ConfigureAwait(false);
            return IndependentTrainingSessionState.Deserialize(json);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            && File.Exists(_path))
        {
            return null;
        }
    }

    public async Task<IndependentTrainingSessionState?> LoadLegacyAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_legacyPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(_legacyPath, cancellationToken)
                .ConfigureAwait(false);
            var state = IndependentLegacyCheckpointDto.TryMigrate(json);
            state?.NormalizeForResume();
            return state;
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            && File.Exists(_legacyPath))
        {
            return null;
        }
    }

    public async Task SaveAsync(
        IndependentTrainingSessionState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.NormalizeForResume();
        var temporaryPath = _path + ".tmp";
        await File.WriteAllTextAsync(
                temporaryPath,
                state.Serialize(),
                cancellationToken)
            .ConfigureAwait(false);
        File.Move(temporaryPath, _path, overwrite: true);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_path))
            File.Delete(_path);
        return Task.CompletedTask;
    }
}
