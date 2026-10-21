using Umamusume.AppCore.Platform;
using Umamusume.AppCore.Services;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Update;

namespace Umamusume.Avalonia.Services;

public sealed class MacResourceUpdateCoordinator
{
    private readonly MacResourceStore _store;
    private readonly string _userDataRoot;

    public MacResourceUpdateCoordinator(MacResourceStore store, string? userDataRoot = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _userDataRoot = Path.GetFullPath(userDataRoot ?? store.UserDataRoot);
    }

    public async Task<string> ApplyAsync(
        StagedUpdate staged,
        IUmaAutomationService automation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(automation);
        if (staged.Scope != UpdateScope.Resource) throw new ArgumentException("A resource update is required.", nameof(staged));
        return await automation.RunMaintenanceWhenIdleAsync(async token =>
        {
            if (!automation.IsInitialized) await automation.InitializeAsync(token).ConfigureAwait(false);
            ResourceWorkspace oldWorkspace = automation.ActiveResourceWorkspace
                ?? throw new InvalidOperationException("The active resource workspace is not initialized.");
            byte[]? oldPointer = await _store.CaptureActivePointerAsync(token).ConfigureAwait(false);
            string newSource = await _store.InstallRevisionAsync(staged, token).ConfigureAwait(false);
            var candidate = await ResourceWorkspace.PrepareAsync(
                newSource, _userDataRoot, token).ConfigureAwait(false);
            try
            {
                await automation.CoreService.ReloadResourceAsync(candidate.ActiveBaseDirectory, token).ConfigureAwait(false);
                await automation.CoreService.GetCatalogAsync("all", token).ConfigureAwait(false);
                await _store.CommitActiveAsync(staged.Manifest.Version, token).ConfigureAwait(false);
                automation.ActivateResourceWorkspace(candidate);
                return staged.Manifest.Version;
            }
            catch (Exception applyError)
            {
                Exception? recoveryError = null;
                try
                {
                    await RestoreNativeWorkspaceAsync(automation, oldWorkspace).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    recoveryError = exception;
                }
                try { await _store.RestoreActivePointerAsync(oldPointer, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception) { recoveryError ??= exception; }
                if (recoveryError is not null)
                {
                    automation.RequireRestartAfterMaintenanceFailure(recoveryError.Message);
                    throw new AggregateException("Resource application and recovery both failed. Restart the application.", applyError, recoveryError);
                }
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RestoreNativeWorkspaceAsync(IUmaAutomationService automation, ResourceWorkspace oldWorkspace)
    {
        try
        {
            await automation.CoreService.ReloadResourceAsync(oldWorkspace.ActiveBaseDirectory, CancellationToken.None).ConfigureAwait(false);
            await automation.CoreService.GetCatalogAsync("all", CancellationToken.None).ConfigureAwait(false);
            automation.ActivateResourceWorkspace(oldWorkspace);
        }
        catch (Exception exception)
        {
            automation.RequireRestartAfterMaintenanceFailure(exception.Message);
            throw;
        }
    }
}
