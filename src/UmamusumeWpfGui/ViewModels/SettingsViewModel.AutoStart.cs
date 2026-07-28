using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.ViewModels;

public sealed partial class SettingsViewModel
{
    private int _draftAutoStartEmulatorWaitSeconds;

    public int DraftAutoStartEmulatorWaitSeconds
    {
        get => _draftAutoStartEmulatorWaitSeconds;
        set
        {
            var clamped = Math.Max(0, value);
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

    private async Task HandleAutoStartLaunchAsync(CancellationToken cancellationToken)
    {
        PersistEmulatorExecutablePath();
        var launch = _emulatorLauncher.Start(DraftEmulatorExecutablePath);
        SetConnectionDiagnostic(launch.Message);

        if (launch.Started && DraftAutoStartEmulatorWaitSeconds > 0)
        {
            await _asyncDelay.DelayAsync(
                TimeSpan.FromSeconds(DraftAutoStartEmulatorWaitSeconds), cancellationToken);
        }

        _connectionState.SetState(ConnectionState.Disconnected);
    }
}
