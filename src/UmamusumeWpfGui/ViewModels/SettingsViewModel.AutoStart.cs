using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.ViewModels;

public sealed partial class SettingsViewModel
{
    private static readonly TimeSpan AutoStartDiscoveryPollInterval =
        TimeSpan.FromMilliseconds(250);

    private int _draftAutoStartEmulatorWaitSeconds;

    public int DraftAutoStartEmulatorWaitSeconds
    {
        get => _draftAutoStartEmulatorWaitSeconds;
        set
        {
            var clamped = Math.Clamp(
                value,
                0,
                ConnectionSettings.MaxAutoStartEmulatorWaitSeconds);
            if (_draftAutoStartEmulatorWaitSeconds == clamped)
                return;

            _draftAutoStartEmulatorWaitSeconds = clamped;
            OnPropertyChanged();
        }
    }

    private void PersistEmulatorExecutablePath()
    {
        _draft.EmulatorExecutablePath = DraftEmulatorExecutablePath;
        _settingsService.Save(_draft);
    }

    private async Task<bool> HandleAutoStartLaunchAsync(CancellationToken cancellationToken)
    {
        PersistEmulatorExecutablePath();
        var launch = _emulatorLauncher.Start(DraftEmulatorExecutablePath);
        SetConnectionDiagnostic(launch.Message);

        if (!launch.Started)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Re-scans for an emulator after a launch request. Emulator processes do
    /// not become discoverable at one deterministic point, so the configured
    /// wait is treated as a bounded readiness window and sampled at a small
    /// interval. A zero wait still performs one immediate scan.
    /// </summary>
    private async Task<DiscoveryResult> RefreshAfterAutoStartAsync(
        CancellationToken cancellationToken)
    {
        var pollCount = (int)Math.Clamp(
            (long)DraftAutoStartEmulatorWaitSeconds * 1000
                / (long)AutoStartDiscoveryPollInterval.TotalMilliseconds + 1,
            1,
            int.MaxValue);
        DiscoveryResult result = new([], []);

        for (var poll = 0; poll < pollCount; poll++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = _winAdapter.RefreshEmulatorsInfo();
            if (result.Candidates.Any(candidate => candidate.AdbPath is not null)
                || poll == pollCount - 1)
            {
                return result;
            }

            await _asyncDelay.DelayAsync(
                AutoStartDiscoveryPollInterval,
                cancellationToken).ConfigureAwait(true);
        }

        return result;
    }
}
