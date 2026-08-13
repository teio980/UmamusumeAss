using System.IO;

namespace UmamusumeWpfGui.Services.Training;

public sealed class UraCheckpointStore
{
    private readonly string _path;

    public UraCheckpointStore(int traineeId)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UmamusumeAss",
            "checkpoints");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, $"ura-{traineeId}.json");
    }

    public async Task<UraCareerSessionState?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
            return UraCareerSessionState.Deserialize(json);
        }
        catch (Exception) when (File.Exists(_path))
        {
            return null;
        }
    }

    public async Task SaveAsync(
        UraCareerSessionState state,
        CancellationToken cancellationToken)
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

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_path))
            File.Delete(_path);
        return Task.CompletedTask;
    }
}
