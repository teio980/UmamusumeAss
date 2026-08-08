using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.ViewModels;

/// <summary>
/// Editable, string-backed form for every timing value in the ordinary
/// Hachimi schema. Keeping the values as strings lets the form preserve an
/// invalid value until the user presses Validate or Save.
/// </summary>
public sealed class HachimiPipelineTimingEditorItem : INotifyPropertyChanged
{
    private string _navigationMs = "1200";
    private string _mailboxLoadMs = "1800";
    private string _collectionSettleMs = "1200";
    private string _homeTimeoutMs = "5000";
    private string _homeRetryTimeoutMs = "2500";
    private string _homeVerifyTimeoutMs = "3000";
    private string _backAttempts = "3";
    private string _backSettleMs = "600";
    private string _pollIntervalMs = "300";
    private string _teamDownloadMs = "10000";
    private string _nextRaceLoadMs = "10000";
    private string _playbackLoadMs = "20000";
    private string _skipSettleMs = "2500";
    private string _raceTimeoutMs = "60000";
    private string _shopProbeMs = "1500";
    private string _betweenRacesMs = "1200";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string NavigationMs { get => _navigationMs; set => Set(ref _navigationMs, value); }
    public string MailboxLoadMs { get => _mailboxLoadMs; set => Set(ref _mailboxLoadMs, value); }
    public string CollectionSettleMs { get => _collectionSettleMs; set => Set(ref _collectionSettleMs, value); }
    public string HomeTimeoutMs { get => _homeTimeoutMs; set => Set(ref _homeTimeoutMs, value); }
    public string HomeRetryTimeoutMs { get => _homeRetryTimeoutMs; set => Set(ref _homeRetryTimeoutMs, value); }
    public string HomeVerifyTimeoutMs { get => _homeVerifyTimeoutMs; set => Set(ref _homeVerifyTimeoutMs, value); }
    public string BackAttempts { get => _backAttempts; set => Set(ref _backAttempts, value); }
    public string BackSettleMs { get => _backSettleMs; set => Set(ref _backSettleMs, value); }
    public string PollIntervalMs { get => _pollIntervalMs; set => Set(ref _pollIntervalMs, value); }
    public string TeamDownloadMs { get => _teamDownloadMs; set => Set(ref _teamDownloadMs, value); }
    public string NextRaceLoadMs { get => _nextRaceLoadMs; set => Set(ref _nextRaceLoadMs, value); }
    public string PlaybackLoadMs { get => _playbackLoadMs; set => Set(ref _playbackLoadMs, value); }
    public string SkipSettleMs { get => _skipSettleMs; set => Set(ref _skipSettleMs, value); }
    public string RaceTimeoutMs { get => _raceTimeoutMs; set => Set(ref _raceTimeoutMs, value); }
    public string ShopProbeMs { get => _shopProbeMs; set => Set(ref _shopProbeMs, value); }
    public string BetweenRacesMs { get => _betweenRacesMs; set => Set(ref _betweenRacesMs, value); }

    public static HachimiPipelineTimingEditorItem FromTiming(HachimiPipelineTiming timing) =>
        new()
        {
            NavigationMs = timing.NavigationMilliseconds.ToString(CultureInfo.InvariantCulture),
            MailboxLoadMs = timing.MailboxLoadMilliseconds.ToString(CultureInfo.InvariantCulture),
            CollectionSettleMs = timing.CollectionSettleMilliseconds.ToString(CultureInfo.InvariantCulture),
            HomeTimeoutMs = timing.HomeTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture),
            HomeRetryTimeoutMs = timing.HomeRetryTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture),
            HomeVerifyTimeoutMs = timing.HomeVerifyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture),
            BackAttempts = timing.BackAttempts.ToString(CultureInfo.InvariantCulture),
            BackSettleMs = timing.BackSettleMilliseconds.ToString(CultureInfo.InvariantCulture),
            PollIntervalMs = timing.PollIntervalMilliseconds.ToString(CultureInfo.InvariantCulture),
            TeamDownloadMs = timing.TeamDownloadMilliseconds.ToString(CultureInfo.InvariantCulture),
            NextRaceLoadMs = timing.NextRaceLoadMilliseconds.ToString(CultureInfo.InvariantCulture),
            PlaybackLoadMs = timing.PlaybackLoadMilliseconds.ToString(CultureInfo.InvariantCulture),
            SkipSettleMs = timing.SkipSettleMilliseconds.ToString(CultureInfo.InvariantCulture),
            RaceTimeoutMs = timing.RaceTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture),
            ShopProbeMs = timing.ShopProbeMilliseconds.ToString(CultureInfo.InvariantCulture),
            BetweenRacesMs = timing.BetweenRacesMilliseconds.ToString(CultureInfo.InvariantCulture),
        };

    public static HachimiPipelineTimingEditorItem CreateDefault() =>
        FromTiming(new HachimiPipelineTiming());

    public HachimiPipelineTimingEditorItem Clone() => new()
    {
        NavigationMs = NavigationMs,
        MailboxLoadMs = MailboxLoadMs,
        CollectionSettleMs = CollectionSettleMs,
        HomeTimeoutMs = HomeTimeoutMs,
        HomeRetryTimeoutMs = HomeRetryTimeoutMs,
        HomeVerifyTimeoutMs = HomeVerifyTimeoutMs,
        BackAttempts = BackAttempts,
        BackSettleMs = BackSettleMs,
        PollIntervalMs = PollIntervalMs,
        TeamDownloadMs = TeamDownloadMs,
        NextRaceLoadMs = NextRaceLoadMs,
        PlaybackLoadMs = PlaybackLoadMs,
        SkipSettleMs = SkipSettleMs,
        RaceTimeoutMs = RaceTimeoutMs,
        ShopProbeMs = ShopProbeMs,
        BetweenRacesMs = BetweenRacesMs,
    };

    public HachimiPipelineTiming ToTiming() => new()
    {
        NavigationMilliseconds = Parse(NavigationMs, nameof(NavigationMs)),
        MailboxLoadMilliseconds = Parse(MailboxLoadMs, nameof(MailboxLoadMs)),
        CollectionSettleMilliseconds = Parse(CollectionSettleMs, nameof(CollectionSettleMs)),
        HomeTimeoutMilliseconds = Parse(HomeTimeoutMs, nameof(HomeTimeoutMs)),
        HomeRetryTimeoutMilliseconds = Parse(HomeRetryTimeoutMs, nameof(HomeRetryTimeoutMs)),
        HomeVerifyTimeoutMilliseconds = Parse(HomeVerifyTimeoutMs, nameof(HomeVerifyTimeoutMs)),
        BackAttempts = Parse(BackAttempts, nameof(BackAttempts)),
        BackSettleMilliseconds = Parse(BackSettleMs, nameof(BackSettleMs)),
        PollIntervalMilliseconds = Parse(PollIntervalMs, nameof(PollIntervalMs)),
        TeamDownloadMilliseconds = Parse(TeamDownloadMs, nameof(TeamDownloadMs)),
        NextRaceLoadMilliseconds = Parse(NextRaceLoadMs, nameof(NextRaceLoadMs)),
        PlaybackLoadMilliseconds = Parse(PlaybackLoadMs, nameof(PlaybackLoadMs)),
        SkipSettleMilliseconds = Parse(SkipSettleMs, nameof(SkipSettleMs)),
        RaceTimeoutMilliseconds = Parse(RaceTimeoutMs, nameof(RaceTimeoutMs)),
        ShopProbeMilliseconds = Parse(ShopProbeMs, nameof(ShopProbeMs)),
        BetweenRacesMilliseconds = Parse(BetweenRacesMs, nameof(BetweenRacesMs)),
    };

    private static int Parse(string value, string fieldName)
    {
        if (int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            && result >= 0)
        {
            return result;
        }

        throw new FormatException($"{fieldName} must be a non-negative integer.");
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
