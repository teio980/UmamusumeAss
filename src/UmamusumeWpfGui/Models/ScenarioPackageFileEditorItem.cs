using System.ComponentModel;
using System.IO;

namespace UmamusumeWpfGui.Models;

/// <summary>
/// One JSON document inside a scenario package. The package editor keeps the
/// original text so fields added by a future scenario version are not lost.
/// </summary>
public sealed class ScenarioPackageFileEditorItem : INotifyPropertyChanged
{
    private string _jsonText;

    public ScenarioPackageFileEditorItem(
        string relativePath,
        string fullPath,
        string jsonText)
    {
        RelativePath = relativePath;
        FullPath = fullPath;
        _jsonText = jsonText;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string RelativePath { get; }

    public string FullPath { get; }

    public string FileName => Path.GetFileName(RelativePath);

    public bool IsExecution => RelativePath.Equals(
        "screens/execution.json",
        StringComparison.OrdinalIgnoreCase);

    public bool IsRawJsonEditable => !IsExecution;

    public string JsonText
    {
        get => _jsonText;
        set
        {
            if (string.Equals(_jsonText, value, StringComparison.Ordinal))
                return;

            _jsonText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(JsonText)));
        }
    }

    public string DisplayName => RelativePath;
}
