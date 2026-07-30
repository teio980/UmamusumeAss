using System.ComponentModel;
using System.Runtime.CompilerServices;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.ViewModels.Tasks;

/// <summary>
/// Settings owned by the Start game task module.
/// </summary>
public sealed class StartGameTaskSettingsViewModel : INotifyPropertyChanged
{
    public const string DefaultPackageId = "com.cygames.umamusume";

    private readonly ISettingsService _settingsService;
    private string _packageId;
    private string _activityName;
    private string _status = string.Empty;

    public StartGameTaskSettingsViewModel(ISettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        _settingsService = settingsService;
        var settings = settingsService.Load();
        _packageId = settings.TargetPackageIds.FirstOrDefault(
            package => !string.IsNullOrWhiteSpace(package))
            ?? DefaultPackageId;
        _activityName = settings.TargetActivityName.Trim();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string PackageId
    {
        get => _packageId;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (_packageId == normalized)
                return;
            _packageId = normalized;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Optional fully-qualified Activity or Activity class name. Blank uses
    /// the package's launcher Activity.
    /// </summary>
    public string ActivityName
    {
        get => _activityName;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (_activityName == normalized)
                return;
            _activityName = normalized;
            OnPropertyChanged();
        }
    }

    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value)
                return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public void Persist()
    {
        if (string.IsNullOrWhiteSpace(PackageId))
            return;

        var settings = _settingsService.Load();
        settings.TargetPackageIds.RemoveAll(package =>
            package.Equals(PackageId, StringComparison.OrdinalIgnoreCase));
        settings.TargetPackageIds.Insert(0, PackageId);
        if (settings.TargetPackageIds.Count > 5)
            settings.TargetPackageIds.RemoveRange(5, settings.TargetPackageIds.Count - 5);
        settings.TargetActivityName = ActivityName;
        _settingsService.Save(settings);
    }

    internal void SetStatus(string status) => Status = status;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
