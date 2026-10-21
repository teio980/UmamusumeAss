using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Platform;
using Umamusume.AppCore.Services;
using Umamusume.Avalonia.Services;

namespace Umamusume.Avalonia.ViewModels;

public sealed partial class LogsViewModel : ViewModelBase
{
    private readonly ILogService _logService;
    private readonly ILocalizationService _localizationService;
    private readonly List<LogEntry> _allLogs = new();

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string _selectedLevel = "All";

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _filteredCount;

    [ObservableProperty]
    private bool _isNotificationOpen;

    [ObservableProperty]
    private string _notificationTitle = string.Empty;

    [ObservableProperty]
    private string _notificationMessage = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _notificationSeverity = InfoBarSeverity.Informational;

    private bool IsEnglish => UiText.IsEnglish(_localizationService);
    private string T(string chinese, string english) => UiText.Get(_localizationService, chinese, english);

    public IReadOnlyList<string> AvailableLevels => IsEnglish
        ? ["All", "Information (Normal)", "Success", "Warning", "Error", "System"]
        : ["全部", "信息 (Normal)", "成功 (Success)", "警告 (Warning)", "错误 (Error)", "系统 (System)"];

    public ObservableCollection<LogEntry> FilteredLogs { get; } = new();

    public LogsViewModel(ILogService logService, ILocalizationService localizationService)
    {
        _logService = logService;
        _localizationService = localizationService;
        _selectedLevel = T("全部", "All");
        _localizationService.LanguageChanged += OnLanguageChanged;
        _allLogs.AddRange(logService.Entries.Reverse());
        TotalCount = _allLogs.Count;
        ApplyFilter();

        _logService.LogReceived += entry =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _allLogs.Insert(0, entry);
                while (_allLogs.Count > 1500)
                {
                    _allLogs.RemoveAt(_allLogs.Count - 1);
                }
                TotalCount = _allLogs.Count;

                if (MatchesFilter(entry))
                {
                    FilteredLogs.Insert(0, entry);
                    while (FilteredLogs.Count > 1000)
                    {
                        FilteredLogs.RemoveAt(FilteredLogs.Count - 1);
                    }
                    FilteredCount = FilteredLogs.Count;
                }
            });
        };
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilter();
    partial void OnSelectedLevelChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredLogs.Clear();
        foreach (var entry in _allLogs)
        {
            if (MatchesFilter(entry))
            {
                FilteredLogs.Add(entry);
            }
        }
        FilteredCount = FilteredLogs.Count;
    }

    private bool MatchesFilter(LogEntry entry)
    {
        // Level check
        if (SelectedLevel is not ("全部" or "All"))
        {
            bool matchLevel = SelectedLevel switch
            {
                "信息 (Normal)" or "Information (Normal)" => entry.Kind == LogEntryKind.Normal,
                "成功 (Success)" or "Success" => entry.Kind == LogEntryKind.Success,
                "警告 (Warning)" or "Warning" => entry.Kind == LogEntryKind.Warning,
                "错误 (Error)" or "Error" => entry.Kind == LogEntryKind.Error,
                "系统 (System)" or "System" => entry.Kind == LogEntryKind.System,
                _ => true
            };
            if (!matchLevel) return false;
        }

        // Query check
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            return entry.Message.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                   (entry.Category != null && entry.Category.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase));
        }

        return true;
    }

    [RelayCommand]
    private void ClearLogs()
    {
        _logService.Clear();
        _allLogs.Clear();
        FilteredLogs.Clear();
        TotalCount = 0;
        FilteredCount = 0;
        ShowNotification(T("日志已清空", "Logs cleared"), T("全部内存日志记录已清除。", "All in-memory log entries have been cleared."), InfoBarSeverity.Informational);
    }

    [RelayCommand]
    private async Task CopyLogsAsync()
    {
        var logs = FilteredLogs.Reverse().ToArray();
        if (logs.Length == 0)
        {
            ShowNotification(T("没有可复制的日志", "No logs to copy"), T("当前筛选条件下没有日志记录。", "There are no log entries for the current filters."), InfoBarSeverity.Informational);
            return;
        }

        try
        {
            var text = string.Join(Environment.NewLine, logs.Select(FormatLogEntry));
            if (!await DesktopActions.SetClipboardTextAsync(text))
            {
                ShowNotification(T("复制失败", "Copy failed"), T("无法访问系统剪贴板。", "Could not access the system clipboard."), InfoBarSeverity.Error);
                return;
            }

            ShowNotification(T("日志已复制", "Logs copied"), IsEnglish
                ? $"Copied {logs.Length} filtered log entr{(logs.Length == 1 ? "y" : "ies")}."
                : $"已复制 {logs.Length} 条当前筛选的日志。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowNotification(T("复制失败", "Copy failed"), ex.Message, InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task ExportLogsAsync()
    {
        try
        {
            string? filePath = await DesktopActions.SaveFileAsync(T("导出当前筛选的日志", "Export filtered logs"), $"Umamusume_Log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            if (filePath == null) return;

            var sb = new StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Umamusume Assistant Log Export - {DateTime.Now}");
            sb.AppendLine(new string('-', 60));
            foreach (var log in FilteredLogs.Reverse().ToArray())
            {
                sb.AppendLine(FormatLogEntry(log));
            }

            await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8);
            ShowNotification(T("日志导出成功", "Logs exported"), $"{T("已保存至", "Saved to")}: {filePath}", InfoBarSeverity.Success);
            _logService.Log($"{T("日志导出成功", "Logs exported")}: {filePath}", LogEntryKind.Success);
        }
        catch (Exception ex)
        {
            ShowNotification(T("导出失败", "Export failed"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private static string FormatLogEntry(LogEntry log) =>
        FormattableString.Invariant($"[{log.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{log.Kind,-7}] [{log.Category}] {log.Message}");

    [RelayCommand]
    private void DismissNotification()
    {
        IsNotificationOpen = false;
    }

    private void ShowNotification(string title, string message, InfoBarSeverity severity)
    {
        NotificationTitle = title;
        NotificationMessage = message;
        NotificationSeverity = severity;
        IsNotificationOpen = true;
    }

    private void OnLanguageChanged()
    {
        bool wasAll = SelectedLevel is "全部" or "All";
        SelectedLevel = SelectedLevel switch
        {
            "信息 (Normal)" or "Information (Normal)" => T("信息 (Normal)", "Information (Normal)"),
            "成功 (Success)" or "Success" => T("成功 (Success)", "Success"),
            "警告 (Warning)" or "Warning" => T("警告 (Warning)", "Warning"),
            "错误 (Error)" or "Error" => T("错误 (Error)", "Error"),
            "系统 (System)" or "System" => T("系统 (System)", "System"),
            _ => T("全部", "All")
        };
        if (wasAll) SelectedLevel = T("全部", "All");
        OnPropertyChanged(nameof(AvailableLevels));
        IsNotificationOpen = false;
    }
}
