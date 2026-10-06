using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.Services.Training;

internal sealed record UraSmartTrainingPendingConfirmation(
    int TraineeId,
    string TrainingType,
    int TurnIndex,
    DateTimeOffset RecordedAt);

/// <summary>
/// Keeps the one dangerous confirmation guard across Stop/Restart. Candidate
/// observations are intentionally excluded; a later run must observe the
/// result or pause on the training picker.
/// </summary>
internal static class UraSmartTrainingConfirmationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static async Task<UraSmartTrainingPendingConfirmation?> LoadAsync(
        LastVerifiedConnection connection,
        int traineeId,
        CancellationToken cancellationToken)
    {
        var path = GetPath(connection, traineeId);
        if (!File.Exists(path))
            return null;
        try
        {
            await using var stream = File.OpenRead(path);
            var confirmation = await JsonSerializer.DeserializeAsync<UraSmartTrainingPendingConfirmation>(
                    stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return confirmation ?? throw new InvalidDataException(
                "The smart training confirmation guard contains JSON null; automation must remain paused.");
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new InvalidDataException(
                "The smart training confirmation guard exists but could not be read; automation must remain paused.",
                exception);
        }
    }

    public static async Task SaveAsync(
        LastVerifiedConnection connection,
        UraSmartTrainingPendingConfirmation confirmation,
        CancellationToken cancellationToken)
    {
        var path = GetPath(connection, confirmation.TraineeId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, confirmation, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            // Replacing the completed file keeps a cancellation or process
            // stop from exposing a truncated guard to the next run.
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // The completed target remains the authoritative guard.
            }
        }
    }

    public static Task ClearAsync(
        LastVerifiedConnection connection,
        int traineeId)
    {
        var path = GetPath(connection, traineeId);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // A stale guard is safer than pretending it was cleared.
        }

        return Task.CompletedTask;
    }

    private static string GetPath(LastVerifiedConnection connection, int traineeId)
    {
        var identity = $"{connection.AndroidId}|{connection.Serial}|{traineeId}";
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20].ToLowerInvariant();
        return Path.Combine(
            HachimiResourcePaths.GetDebugDirectory("career"),
            $"smart-training-pending-{digest}.json");
    }
}
