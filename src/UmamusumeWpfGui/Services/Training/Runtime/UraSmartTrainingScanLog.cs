using System.Windows;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using SharedScanLog = Umamusume.CoreBridge.Logging.UraSmartTrainingScanLog;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>Windows sink adapter for the shared smart training summary.</summary>
internal static class UraSmartTrainingScanLog
{
    public static void WriteJson(
        IHachimiTaskLogSink? sink,
        string jsonOrText,
        ILocalizationService? localizationService = null,
        Func<string, string?>? lookup = null)
    {
        if (sink is null) return;
        if (lookup is null && localizationService is not null)
            lookup = localizationService.GetString;
        if (lookup is null && Application.Current is { } app)
            lookup = key => app.Dispatcher.Invoke(() => app.TryFindResource(key) as string);
        SharedScanLog.WriteJson((step, message) => sink.Add(step, message, HachimiTaskLogEventKind.Detection),
            jsonOrText, lookup);
    }
}
