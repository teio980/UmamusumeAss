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
    private readonly List<LogEntry> _allLogs = new();

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string _selectedLevel = "全部";

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

    public IReadOnlyList<string> AvailableLevels { get; } = new[]
    {
        "全部", "信息 (Normal)", "成功 (Success)", "警告 (Warning)", "错误 (Error)", "系统 (System)"
    };

    public ObservableCollection<LogEntry> FilteredLogs { get; } = new();

    public LogsViewModel(ILogService logService)
    {
        _logService = logService;
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
        if (SelectedLevel != "全部")
        {
            bool matchLevel = SelectedLevel switch
            {
                "信息 (Normal)" => entry.Kind == LogEntryKind.Normal,
                "成功 (Success)" => entry.Kind == LogEntryKind.Success,
                "警告 (Warning)" => entry.Kind == LogEntryKind.Warning,
                "错误 (Error)" => entry.Kind == LogEntryKind.Error,
                "系统 (System)" => entry.Kind == LogEntryKind.System,
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
        ShowNotification("日志已清空", "全部内存日志记录已清除。", InfoBarSeverity.Informational);
    }

    [RelayCommand]
    private async Task CopyLogsAsync()
    {
        var logs = FilteredLogs.Reverse().ToArray();
        if (logs.Length == 0)
        {
            ShowNotification("没有可复制的日志", "当前筛选条件下没有日志记录。", InfoBarSeverity.Informational);
            return;
        }

        try
        {
            var text = string.Join(Environment.NewLine, logs.Select(FormatLogEntry));
            if (!await DesktopActions.SetClipboardTextAsync(text))
            {
                ShowNotification("复制失败", "无法访问系统剪贴板。", InfoBarSeverity.Error);
                return;
            }

            ShowNotification("日志已复制", $"已复制 {logs.Length} 条当前筛选的日志。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowNotification("复制失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task ExportLogsAsync()
    {
        try
        {
            string? filePath = await DesktopActions.SaveFileAsync("导出当前筛选的日志", $"Umamusume_Log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            if (filePath == null) return;

            var sb = new StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Umamusume Assistant Log Export - {DateTime.Now}");
            sb.AppendLine(new string('-', 60));
            foreach (var log in FilteredLogs.Reverse().ToArray())
            {
                sb.AppendLine(FormatLogEntry(log));
            }

            await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8);
            ShowNotification("日志导出成功", $"已保存至: {filePath}", InfoBarSeverity.Success);
            _logService.Log($"日志导出成功: {filePath}", LogEntryKind.Success);
        }
        catch (Exception ex)
        {
            ShowNotification("导出失败", ex.Message, InfoBarSeverity.Error);
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
}
