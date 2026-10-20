using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Umamusume.AppCore.Models;

namespace Umamusume.Avalonia.Converters;

public sealed class LogKindToBrushConverter : IValueConverter
{
    public static readonly LogKindToBrushConverter Instance = new();

    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#107C41"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#D83B01"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#A80000"));
    private static readonly IBrush SystemBrush = new SolidColorBrush(Color.Parse("#0078D4"));
    private static readonly IBrush DefaultBrush = new SolidColorBrush(Color.Parse("#8A8886"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is LogEntryKind kind)
        {
            return kind switch
            {
                LogEntryKind.Success => SuccessBrush,
                LogEntryKind.Warning => WarningBrush,
                LogEntryKind.Error => ErrorBrush,
                LogEntryKind.System => SystemBrush,
                _ => DefaultBrush
            };
        }
        return DefaultBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class LogKindToBackgroundConverter : IValueConverter
{
    public static readonly LogKindToBackgroundConverter Instance = new();

    private static readonly IBrush SuccessBg = new SolidColorBrush(Color.FromArgb(40, 16, 124, 65));
    private static readonly IBrush WarningBg = new SolidColorBrush(Color.FromArgb(40, 216, 59, 1));
    private static readonly IBrush ErrorBg = new SolidColorBrush(Color.FromArgb(40, 168, 0, 0));
    private static readonly IBrush SystemBg = new SolidColorBrush(Color.FromArgb(40, 0, 120, 212));
    private static readonly IBrush DefaultBg = new SolidColorBrush(Color.FromArgb(25, 128, 128, 128));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is LogEntryKind kind)
        {
            return kind switch
            {
                LogEntryKind.Success => SuccessBg,
                LogEntryKind.Warning => WarningBg,
                LogEntryKind.Error => ErrorBg,
                LogEntryKind.System => SystemBg,
                _ => DefaultBg
            };
        }
        return DefaultBg;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ConnectionStateToBrushConverter : IValueConverter
{
    public static readonly ConnectionStateToBrushConverter Instance = new();

    private static readonly IBrush ConnectedBrush = new SolidColorBrush(Color.Parse("#107C41"));
    private static readonly IBrush ConnectingBrush = new SolidColorBrush(Color.Parse("#0078D4"));
    private static readonly IBrush DisconnectedBrush = new SolidColorBrush(Color.Parse("#8A8886"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isConnected && isConnected)
        {
            return ConnectedBrush;
        }
        return DisconnectedBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class TaskStatusToBrushConverter : IValueConverter
{
    public static readonly TaskStatusToBrushConverter Instance = new();

    private static readonly IBrush RunningBrush = new SolidColorBrush(Color.Parse("#0078D4"));
    private static readonly IBrush CompletedBrush = new SolidColorBrush(Color.Parse("#107C41"));
    private static readonly IBrush FailedBrush = new SolidColorBrush(Color.Parse("#A80000"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#D83B01"));
    private static readonly IBrush WaitingBrush = new SolidColorBrush(Color.Parse("#8A8886"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string status)
        {
            if (status.Contains("运行", StringComparison.OrdinalIgnoreCase) || status.Contains("Run", StringComparison.OrdinalIgnoreCase)) return RunningBrush;
            if (status.Contains("完成", StringComparison.OrdinalIgnoreCase) || status.Contains("Complete", StringComparison.OrdinalIgnoreCase)) return CompletedBrush;
            if (status.Contains("失败", StringComparison.OrdinalIgnoreCase) || status.Contains("Fail", StringComparison.OrdinalIgnoreCase)) return FailedBrush;
            if (status.Contains("取消", StringComparison.OrdinalIgnoreCase) || status.Contains("Cancel", StringComparison.OrdinalIgnoreCase)
                || status.Contains("跳过", StringComparison.OrdinalIgnoreCase) || status.Contains("Skip", StringComparison.OrdinalIgnoreCase)) return WarningBrush;
        }
        return WaitingBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NotNullToBoolConverter : IValueConverter
{
    public static readonly NotNullToBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is not null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class EqualityToBoolConverter : IValueConverter
{
    public static readonly EqualityToBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null && parameter is null) return true;
        if (value is null || parameter is null) return false;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
