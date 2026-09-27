using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Keeps confirmed Normal Career skills across stopped and resumed task runs.
/// A new career clears this file; reaching Home after settlement removes it.
/// </summary>
internal sealed class NormalCareerSkillCache
{
    private const int CurrentVersion = 1;
    private readonly string _path;
    private readonly string _deviceKey;
    private readonly int _traineeId;

    internal NormalCareerSkillCache(
        LastVerifiedConnection connection,
        int traineeId,
        string? rootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(traineeId);

        _traineeId = traineeId;
        _deviceKey = !string.IsNullOrWhiteSpace(connection.AndroidId)
            ? connection.AndroidId
            : connection.Serial;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_deviceKey)));
        var directory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UmamusumeAss", "checkpoints");
        _path = Path.Combine(directory, $"normal-skills-{traineeId}-{hash[..16]}.json");
    }

    internal string CachePath => _path;

    internal async Task<IReadOnlyList<int>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
            return [];

        try
        {
            var json = await File.ReadAllTextAsync(_path, cancellationToken)
                .ConfigureAwait(false);
            var snapshot = JsonSerializer.Deserialize<Snapshot>(json);
            if (snapshot is null || snapshot.Version != CurrentVersion
                || snapshot.TraineeId != _traineeId || snapshot.DeviceKey != _deviceKey)
                return [];

            return (snapshot.SkillIds ?? []).Where(id => id > 0).Distinct().ToArray();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal async Task SaveAsync(IReadOnlyCollection<int> skillIds)
    {
        ArgumentNullException.ThrowIfNull(skillIds);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var snapshot = new Snapshot(CurrentVersion, _traineeId, _deviceKey,
                skillIds.Where(id => id > 0).Distinct().ToArray());
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(snapshot))
                .ConfigureAwait(false);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    internal Task ClearAsync()
    {
        if (File.Exists(_path))
            File.Delete(_path);
        return Task.CompletedTask;
    }

    private sealed record Snapshot(int Version, int TraineeId, string DeviceKey, int[] SkillIds);
}
