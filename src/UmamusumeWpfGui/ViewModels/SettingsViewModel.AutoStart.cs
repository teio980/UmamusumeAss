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






    private async Task<string?> WaitForAutoStartAdbDeviceAsync(
        string adbPath,
        CancellationToken cancellationToken)
    {
        var pollCount = (int)Math.Clamp(
            (long)DraftAutoStartEmulatorWaitSeconds * 1000
                / (long)AutoStartDiscoveryPollInterval.TotalMilliseconds + 1,
            1,
            int.MaxValue);

        SetConnectionDiagnostic("Waiting for emulator ADB device...");

        for (var poll = 0; poll < pollCount; poll++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var devices = await _winAdapter.GetAdbDevicesAsync(
                adbPath,
                cancellationToken).ConfigureAwait(true);
            var readyDevice = devices.Records.FirstOrDefault(
                device => string.Equals(
                    device.State,
                    "device",
                    StringComparison.OrdinalIgnoreCase));
            if (readyDevice is not null)
                return readyDevice.Serial;

            if (poll < pollCount - 1)
            {
                await _asyncDelay.DelayAsync(
                    AutoStartDiscoveryPollInterval,
                    cancellationToken).ConfigureAwait(true);
            }
        }

        return null;
    }
}
