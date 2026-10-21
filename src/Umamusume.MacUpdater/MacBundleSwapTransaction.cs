using System.Security.Cryptography;

internal sealed record MacUpdateJournal(
    string OperationId,
    string Nonce,
    string Stage,
    string AppPath,
    string CandidatePath,
    string BackupPath,
    string TargetVersion,
    string OldVersion,
    string ManifestSha256,
    string PayloadTreeSha256,
    int ParentPid);

internal sealed record MacHealthResult(string OperationId, string Nonce, bool Healthy, string? Error);

internal interface IMacLaunchedApplication : IAsyncDisposable
{
    Task<MacHealthResult?> WaitForHealthAsync(TimeSpan timeout, CancellationToken cancellationToken);
    Task StopAsync();
}

internal interface IMacBundleSwapHost
{
    string ReadBundleVersion(string appPath);
    bool TrySwap(string first, string second, out int error);
    Task VerifyCodesignAsync(string appPath);
    Task SaveJournalAsync(string path, MacUpdateJournal journal);
    Task<IMacLaunchedApplication> LaunchUpdatedAppAsync(string appPath, string operationRoot, string operationId, string nonce);
    Task RestartPreviousAppAsync(string appPath);
    Task PreservePreviousBundleAsync(string candidatePath, string backupPath);
    Task WriteResultAsync(string operationRoot, MacHealthResult result);
}

/// <summary>Failure-atomic app replacement logic, with process and filesystem effects supplied by the helper host.</summary>
internal sealed class MacBundleSwapTransaction(IMacBundleSwapHost host)
{
    public async Task<int> InstallAsync(
        string journalPath,
        string operationRoot,
        MacUpdateJournal journal,
        TimeSpan healthTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        IMacLaunchedApplication? launched = null;
        bool healthyCommitted = false;
        try
        {
            journal = journal with { Stage = "swapping" };
            await host.SaveJournalAsync(journalPath, journal).ConfigureAwait(false);
            if (!host.TrySwap(journal.CandidatePath, journal.AppPath, out int error))
                throw new IOException($"Atomic application replacement failed (renameatx_np error {error}).");

            journal = journal with { Stage = "swapped" };
            await host.SaveJournalAsync(journalPath, journal).ConfigureAwait(false);
            await host.VerifyCodesignAsync(journal.AppPath).ConfigureAwait(false);
            launched = await host.LaunchUpdatedAppAsync(journal.AppPath, operationRoot, journal.OperationId, journal.Nonce)
                .ConfigureAwait(false);
            journal = journal with { Stage = "health-check" };
            await host.SaveJournalAsync(journalPath, journal).ConfigureAwait(false);

            MacHealthResult? health = await launched.WaitForHealthAsync(healthTimeout, cancellationToken).ConfigureAwait(false);
            if (health is null)
                throw new TimeoutException($"The updated application did not complete its health check within {healthTimeout.TotalSeconds:0} seconds.");
            if (!health.Healthy || !health.OperationId.Equals(journal.OperationId, StringComparison.Ordinal)
                || !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(health.Nonce),
                    System.Text.Encoding.UTF8.GetBytes(journal.Nonce)))
                throw new InvalidOperationException(health.Error ?? "The updated application failed its health check.");

            journal = journal with { Stage = "healthy" };
            await host.SaveJournalAsync(journalPath, journal).ConfigureAwait(false);
            healthyCommitted = true;
        }
        catch (Exception installError)
        {
            if (healthyCommitted)
            {
                try { await host.WriteResultAsync(operationRoot,
                    new MacHealthResult(journal.OperationId, journal.Nonce, true, null)).ConfigureAwait(false); }
                catch (Exception) { }
                return 0;
            }

            try { if (launched is not null) await launched.StopAsync().ConfigureAwait(false); }
            catch (Exception) { }
            try
            {
                string appPath = Path.GetFullPath(journal.AppPath);
                string candidatePath = Path.GetFullPath(journal.CandidatePath);
                bool targetAtApp = Directory.Exists(appPath)
                    && host.ReadBundleVersion(appPath).Equals(journal.TargetVersion, StringComparison.Ordinal);
                bool oldAtCandidate = Directory.Exists(candidatePath)
                    && host.ReadBundleVersion(candidatePath).Equals(journal.OldVersion, StringComparison.Ordinal);
                bool oldAtApp = Directory.Exists(appPath)
                    && host.ReadBundleVersion(appPath).Equals(journal.OldVersion, StringComparison.Ordinal);

                if (targetAtApp && oldAtCandidate)
                {
                    await host.VerifyCodesignAsync(candidatePath).ConfigureAwait(false);
                    if (!host.TrySwap(candidatePath, appPath, out int rollbackError))
                        throw new IOException($"Rollback swap failed (renameatx_np error {rollbackError}).");
                }
                else if (!oldAtApp)
                {
                    throw new IOException("The old application bundle could not be verified after update failure.");
                }

                try { await host.SaveJournalAsync(journalPath, journal with { Stage = "rolled-back" }).ConfigureAwait(false); }
                catch (Exception) { }
                await host.RestartPreviousAppAsync(appPath).ConfigureAwait(false);
                try
                {
                    await host.WriteResultAsync(operationRoot,
                        new MacHealthResult(journal.OperationId, journal.Nonce, false, installError.Message)).ConfigureAwait(false);
                }
                catch (Exception) { }
                return 2;
            }
            catch (Exception rollbackError)
            {
                throw new IOException("The update failed and the helper could not restore the previous app bundle.",
                    new AggregateException(installError, rollbackError));
            }
        }
        finally
        {
            if (launched is not null) await launched.DisposeAsync().ConfigureAwait(false);
        }

        // A durable healthy journal commits the new app. Backup/result cleanup is best effort.
        try { await host.PreservePreviousBundleAsync(journal.CandidatePath, journal.BackupPath).ConfigureAwait(false); }
        catch (Exception) { }
        try
        {
            await host.WriteResultAsync(operationRoot,
                new MacHealthResult(journal.OperationId, journal.Nonce, true, null)).ConfigureAwait(false);
        }
        catch (Exception) { }
        return 0;
    }

    public async Task RecoverInterruptedSwapAsync(string journalPath, string operationRoot, MacUpdateJournal journal)
    {
        if (journal.Stage is not ("swapping" or "swapped" or "health-check"))
            throw new InvalidDataException("The update journal is not in a recoverable state.");
        string appPath = Path.GetFullPath(journal.AppPath);
        string candidatePath = Path.GetFullPath(journal.CandidatePath);
        bool targetAtApp = Directory.Exists(appPath)
            && host.ReadBundleVersion(appPath).Equals(journal.TargetVersion, StringComparison.Ordinal);
        bool oldAtCandidate = Directory.Exists(candidatePath)
            && host.ReadBundleVersion(candidatePath).Equals(journal.OldVersion, StringComparison.Ordinal);
        bool oldAtApp = Directory.Exists(appPath)
            && host.ReadBundleVersion(appPath).Equals(journal.OldVersion, StringComparison.Ordinal);
        bool targetAtCandidate = Directory.Exists(candidatePath)
            && host.ReadBundleVersion(candidatePath).Equals(journal.TargetVersion, StringComparison.Ordinal);

        if (targetAtApp && oldAtCandidate)
        {
            await host.VerifyCodesignAsync(candidatePath).ConfigureAwait(false);
            if (!host.TrySwap(candidatePath, appPath, out int error))
                throw new IOException($"Could not roll back the interrupted app swap (renameatx_np error {error}).");
        }
        else if (!(oldAtApp && targetAtCandidate))
        {
            throw new IOException("Recovery found an unexpected bundle layout and did not change it.");
        }

        try { await host.SaveJournalAsync(journalPath, journal with { Stage = "rolled-back" }).ConfigureAwait(false); }
        catch (Exception) { }
        await host.RestartPreviousAppAsync(appPath).ConfigureAwait(false);
        try
        {
            await host.WriteResultAsync(operationRoot,
                new MacHealthResult(journal.OperationId, journal.Nonce, false,
                    "The interrupted update was rolled back and the previous application was restored.")).ConfigureAwait(false);
        }
        catch (Exception) { }
    }
}
