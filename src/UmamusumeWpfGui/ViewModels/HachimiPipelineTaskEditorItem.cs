using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.ViewModels;

/// <summary>
/// String-backed editor surface for one ordinary Hachimi task. Keeping the
/// inputs as strings lets the WPF form show invalid values without losing the
/// user's text; conversion happens only when validating or saving.
/// </summary>
public sealed class HachimiPipelineTaskEditorItem : INotifyPropertyChanged
{
    private string _algorithm = "MatchTemplate";
    private string _action = "ClickSelf";
    private string _pipeline = string.Empty;
    private string _entry = string.Empty;
    private string _swipeText = string.Empty;
    private string _template = string.Empty;
    private string _templateThresholdText = "0.86";
    private string _roiText = string.Empty;
    private string _specificRectText = string.Empty;
    private string _preDelayText = "0";
    private string _postDelayText = "0";
    private string _waitMillisecondsText = "0";
    private string _timeoutMillisecondsText = "10000";
    private string _pollIntervalMillisecondsText = "0";
    private string _nextText = string.Empty;
    private string _onErrorNextText = string.Empty;
    private string _exceededNextText = string.Empty;
    private string _subText = string.Empty;
    private string _monitorTasksText = string.Empty;
    private string _successTask = string.Empty;
    private string _maxTimesText = "0";
    private bool _required = true;
    private bool _success;
    private string _countAs = string.Empty;

    private HachimiPipelineTaskEditorItem(string name)
    {
        Name = name;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public string Algorithm { get => _algorithm; set => Set(ref _algorithm, value); }

    public string Action { get => _action; set => Set(ref _action, value); }

    public string Pipeline { get => _pipeline; set => Set(ref _pipeline, value); }

    public string Entry { get => _entry; set => Set(ref _entry, value); }

    public string SwipeText { get => _swipeText; set => Set(ref _swipeText, value); }

    public string Template { get => _template; set => Set(ref _template, value); }

    public string TemplateThresholdText
    {
        get => _templateThresholdText;
        set => Set(ref _templateThresholdText, value);
    }

    public string RoiText { get => _roiText; set => Set(ref _roiText, value); }

    public string SpecificRectText
    {
        get => _specificRectText;
        set => Set(ref _specificRectText, value);
    }

    public string PreDelayText { get => _preDelayText; set => Set(ref _preDelayText, value); }

    public string PostDelayText { get => _postDelayText; set => Set(ref _postDelayText, value); }

    public string WaitMillisecondsText
    {
        get => _waitMillisecondsText;
        set => Set(ref _waitMillisecondsText, value);
    }

    public string TimeoutMillisecondsText
    {
        get => _timeoutMillisecondsText;
        set => Set(ref _timeoutMillisecondsText, value);
    }

    public string PollIntervalMillisecondsText
    {
        get => _pollIntervalMillisecondsText;
        set => Set(ref _pollIntervalMillisecondsText, value);
    }

    public string NextText { get => _nextText; set => Set(ref _nextText, value); }

    public string OnErrorNextText
    {
        get => _onErrorNextText;
        set => Set(ref _onErrorNextText, value);
    }

    public string ExceededNextText
    {
        get => _exceededNextText;
        set => Set(ref _exceededNextText, value);
    }

    public string SubText { get => _subText; set => Set(ref _subText, value); }

    public string MonitorTasksText
    {
        get => _monitorTasksText;
        set => Set(ref _monitorTasksText, value);
    }

    public string SuccessTask { get => _successTask; set => Set(ref _successTask, value); }

    public string MaxTimesText { get => _maxTimesText; set => Set(ref _maxTimesText, value); }

    public bool Required { get => _required; set => Set(ref _required, value); }

    public bool Success { get => _success; set => Set(ref _success, value); }

    public string CountAs { get => _countAs; set => Set(ref _countAs, value); }

    public static HachimiPipelineTaskEditorItem FromTask(
        string name,
        HachimiPipelineTask task)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(task);

        return new HachimiPipelineTaskEditorItem(name)
        {
            Algorithm = task.Algorithm,
            Action = task.Action,
            Pipeline = task.Pipeline ?? string.Empty,
            Entry = task.Entry ?? string.Empty,
            SwipeText = FormatArray(task.Swipe),
            Template = task.Template ?? string.Empty,
            TemplateThresholdText = task.TemplateThreshold.ToString("0.###", CultureInfo.InvariantCulture),
            RoiText = FormatArray(task.Roi),
            SpecificRectText = FormatArray(task.SpecificRect),
            PreDelayText = task.PreDelay.ToString(CultureInfo.InvariantCulture),
            PostDelayText = task.PostDelay.ToString(CultureInfo.InvariantCulture),
            WaitMillisecondsText = task.WaitMilliseconds.ToString(CultureInfo.InvariantCulture),
            TimeoutMillisecondsText = task.TimeoutMilliseconds.ToString(CultureInfo.InvariantCulture),
            PollIntervalMillisecondsText = task.PollIntervalMilliseconds.ToString(CultureInfo.InvariantCulture),
            NextText = FormatList(task.Next),
            OnErrorNextText = FormatList(task.OnErrorNext),
            ExceededNextText = FormatList(task.ExceededNext),
            SubText = FormatList(task.Sub),
            MonitorTasksText = FormatList(task.MonitorTasks),
            SuccessTask = task.SuccessTask ?? string.Empty,
            MaxTimesText = task.MaxTimes.ToString(CultureInfo.InvariantCulture),
            Required = task.Required,
            Success = task.Success,
            CountAs = task.CountAs ?? string.Empty,
        };
    }

    public static HachimiPipelineTaskEditorItem Create(string name) =>
        new(name);

    public HachimiPipelineTaskEditorItem Clone() => new(Name)
    {
        Algorithm = Algorithm,
        Action = Action,
        Pipeline = Pipeline,
        Entry = Entry,
        SwipeText = SwipeText,
        Template = Template,
        TemplateThresholdText = TemplateThresholdText,
        RoiText = RoiText,
        SpecificRectText = SpecificRectText,
        PreDelayText = PreDelayText,
        PostDelayText = PostDelayText,
        WaitMillisecondsText = WaitMillisecondsText,
        TimeoutMillisecondsText = TimeoutMillisecondsText,
        PollIntervalMillisecondsText = PollIntervalMillisecondsText,
        NextText = NextText,
        OnErrorNextText = OnErrorNextText,
        ExceededNextText = ExceededNextText,
        SubText = SubText,
        MonitorTasksText = MonitorTasksText,
        SuccessTask = SuccessTask,
        MaxTimesText = MaxTimesText,
        Required = Required,
        Success = Success,
        CountAs = CountAs,
    };

    public HachimiPipelineTask ToTask()
    {
        var task = new HachimiPipelineTask();
        ApplyTo(task);
        return task;
    }

    public void ApplyTo(HachimiPipelineTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        task.Algorithm = RequiredText(Algorithm, nameof(Algorithm));
        task.Action = RequiredText(Action, nameof(Action));
        task.Pipeline = OptionalText(Pipeline);
        task.Entry = OptionalText(Entry);
        task.Swipe = ParseArray(SwipeText, nameof(SwipeText), expectedLength: 5);
        task.Template = OptionalText(Template);
        task.TemplateThreshold = ParseDouble(
            TemplateThresholdText,
            nameof(TemplateThresholdText),
            defaultValue: 0.86);
        task.Roi = ParseArray(RoiText, nameof(RoiText), expectedLength: 4);
        task.SpecificRect = ParseArray(SpecificRectText, nameof(SpecificRectText), expectedLength: 4);
        task.PreDelay = ParseInt(PreDelayText, nameof(PreDelayText));
        task.PostDelay = ParseInt(PostDelayText, nameof(PostDelayText));
        task.WaitMilliseconds = ParseInt(WaitMillisecondsText, nameof(WaitMillisecondsText));
        task.TimeoutMilliseconds = ParseInt(
            TimeoutMillisecondsText,
            nameof(TimeoutMillisecondsText),
            defaultValue: 10_000);
        task.PollIntervalMilliseconds = ParseInt(
            PollIntervalMillisecondsText,
            nameof(PollIntervalMillisecondsText));
        task.Next = ParseList(NextText);
        task.OnErrorNext = ParseList(OnErrorNextText);
        task.ExceededNext = ParseList(ExceededNextText);
        task.Sub = ParseList(SubText);
        task.MonitorTasks = ParseList(MonitorTasksText);
        task.SuccessTask = OptionalText(SuccessTask);
        task.MaxTimes = ParseInt(MaxTimesText, nameof(MaxTimesText));
        task.Required = Required;
        task.Success = Success;
        task.CountAs = OptionalText(CountAs);
    }

    public string? GetFirstValidationError()
    {
        try
        {
            _ = ToTask();
            return null;
        }
        catch (FormatException exception)
        {
            return exception.Message;
        }
    }

    private static string RequiredText(string? value, string fieldName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new FormatException($"{fieldName} cannot be empty.")
            : value.Trim();

    private static string? OptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int ParseInt(
        string? value,
        string fieldName,
        int defaultValue = 0)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            && result >= 0)
        {
            return result;
        }

        throw new FormatException($"{fieldName} must be a non-negative integer.");
    }

    private static double ParseDouble(
        string? value,
        string fieldName,
        double defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        if (double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            && result is >= 0 and <= 1)
        {
            return result;
        }

        throw new FormatException($"{fieldName} must be a number between 0 and 1.");
    }

    private static int[]? ParseArray(
        string? value,
        string fieldName,
        int expectedLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var parts = value
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal)
            .Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != expectedLength)
        {
            throw new FormatException(
                $"{fieldName} must contain exactly {expectedLength} integers.");
        }

        var values = new int[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var valuePart)
                || valuePart < 0)
            {
                throw new FormatException($"{fieldName} contains an invalid integer.");
            }

            values[index] = valuePart;
        }

        return values;
    }

    private static List<string> ParseList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value
                .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .ToList();

    private static string FormatArray(int[]? values) =>
        values is null ? string.Empty : string.Join(", ", values);

    private static string FormatList(IEnumerable<string> values) =>
        string.Join(Environment.NewLine, values);

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
