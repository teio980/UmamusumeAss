using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Monotonic top-level progress for an Independent Training run.
/// </summary>
public enum IndependentTrainingStage
{
    EntryConfiguration = 0,
    IndependentConfiguration = 1,
    Start = 2,
    Completed = 3,
}

/// <summary>
/// Monotonic progress inside the Independent configuration page.
/// </summary>
public enum IndependentTrainingConfigurationStep
{
    Mode = 0,
    LineupExpanded = 1,
    Focus = 2,
    Agenda = 3,
    Skills = 4,
    LineupPrepared = 5,
    LineupVerified = 6,
    Strategy = 7,
}

/// <summary>
/// Independent-only checkpoint state. It intentionally contains no URA
/// turn-engine values (turn, energy, objective, pending race or finale).
/// </summary>
public sealed class IndependentTrainingSessionState
{
    public IndependentTrainingStage Stage { get; set; } =
        IndependentTrainingStage.EntryConfiguration;

    public IndependentTrainingConfigurationStep ConfigurationStep { get; set; } =
        IndependentTrainingConfigurationStep.Mode;

    public string LastScreenId { get; set; } = "unknown";
    public int RetryCount { get; set; }
    public int ActionsCompleted { get; set; }

    /// <summary>
    /// This is deliberately runtime-only. A persisted collapsed flag is not
    /// trusted after a restart; the Lineup page is collapsed and verified in
    /// the live UI again before Strategy or Start is allowed.
    /// </summary>
    [JsonIgnore]
    public bool LineupCollapseVerifiedThisRun { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this);

    public static IndependentTrainingSessionState Deserialize(string json) =>
        JsonSerializer.Deserialize<IndependentTrainingSessionState>(json)
        ?? throw new InvalidDataException("Independent checkpoint is empty.");

    public void NormalizeForResume()
    {
        if (!Enum.IsDefined(Stage))
            Stage = IndependentTrainingStage.EntryConfiguration;
        if (!Enum.IsDefined(ConfigurationStep))
            ConfigurationStep = IndependentTrainingConfigurationStep.Mode;

        Stage = Stage switch
        {
            IndependentTrainingStage.EntryConfiguration => IndependentTrainingStage.EntryConfiguration,
            IndependentTrainingStage.IndependentConfiguration => IndependentTrainingStage.IndependentConfiguration,
            IndependentTrainingStage.Start => IndependentTrainingStage.Start,
            IndependentTrainingStage.Completed => IndependentTrainingStage.Completed,
            _ => IndependentTrainingStage.EntryConfiguration,
        };
        ConfigurationStep = (IndependentTrainingConfigurationStep)Math.Clamp(
            (int)ConfigurationStep,
            (int)IndependentTrainingConfigurationStep.Mode,
            (int)IndependentTrainingConfigurationStep.Strategy);
        RetryCount = Math.Max(0, RetryCount);
        ActionsCompleted = Math.Max(0, ActionsCompleted);

        // A checkpoint that got as far as Lineup must always perform a fresh
        // closed-right verification on the next process/run boundary.
        if (Stage == IndependentTrainingStage.IndependentConfiguration
            && ConfigurationStep >= IndependentTrainingConfigurationStep.LineupPrepared)
        {
            ConfigurationStep = IndependentTrainingConfigurationStep.LineupPrepared;
        }

        // Stage and substep are written together, but a torn or hand-edited
        // checkpoint must never allow a later stage to skip an untrusted
        // suffix. Rewind to the last coherent boundary instead.
        if (Stage == IndependentTrainingStage.EntryConfiguration)
        {
            ConfigurationStep = IndependentTrainingConfigurationStep.Mode;
        }
        else if (Stage == IndependentTrainingStage.IndependentConfiguration
            && ConfigurationStep == IndependentTrainingConfigurationStep.Strategy)
        {
            Stage = IndependentTrainingStage.Start;
        }
        else if ((Stage is IndependentTrainingStage.Start or IndependentTrainingStage.Completed)
            && ConfigurationStep < IndependentTrainingConfigurationStep.Strategy)
        {
            Stage = IndependentTrainingStage.IndependentConfiguration;
        }

        LineupCollapseVerifiedThisRun = false;
    }
}

/// <summary>
/// The old URA checkpoint is intentionally read through this DTO, so
/// Independent remains independent of the URA session engine while still
/// offering best-effort migration.
/// </summary>
internal sealed class IndependentLegacyCheckpointDto
{
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
            var dto = JsonSerializer.Deserialize<IndependentLegacyCheckpointDto>(json);
            if (dto is null)
                return null;

            // A later true flag cannot make an earlier false flag true. This
            // turns contradictory old checkpoints into the last trusted
            // monotonic step instead of replaying an unsafe suffix.
            var state = new IndependentTrainingSessionState();
            if (!dto.IndependentModeSelected)
                return state;

            state.Stage = IndependentTrainingStage.IndependentConfiguration;
            state.ConfigurationStep = IndependentTrainingConfigurationStep.LineupExpanded;
            if (!dto.IndependentLineupConfigured)
                return state;
            state.ConfigurationStep = IndependentTrainingConfigurationStep.Focus;
            if (!dto.IndependentTrainingFocusConfigured)
                return state;
            state.ConfigurationStep = IndependentTrainingConfigurationStep.Agenda;
            if (!dto.IndependentAgendaConfigured)
                return state;
            state.ConfigurationStep = IndependentTrainingConfigurationStep.Skills;
            if (!dto.IndependentSkillsConfigured)
                return state;
            state.ConfigurationStep = IndependentTrainingConfigurationStep.LineupPrepared;
            if (!dto.IndependentLineupCollapsed)
                return state;
            state.ConfigurationStep = IndependentTrainingConfigurationStep.LineupVerified;
            if (!dto.IndependentStrategyConfigured)
                return state;
            state.ConfigurationStep = IndependentTrainingConfigurationStep.Strategy;
            if (!dto.IndependentSetupCompleted)
            {
                state.Stage = IndependentTrainingStage.Start;
                return state;
            }

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
            var state = IndependentTrainingSessionState.Deserialize(json);
            state.NormalizeForResume();
            return state;
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
