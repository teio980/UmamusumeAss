using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Selects the two URA legacy parents from the live Legacy Select page.
/// The controls are intentionally backed by URA-local ADB captures instead
/// of the Daily Race runner assets: this page has different tabs, sort
/// choices, and a trainee marker.
/// </summary>
public sealed class UraLegacySelector
{
    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly HachimiJsonPipelineRunner _jsonRunner;
    private readonly AsyncLocal<CareerVisualResourcePackage?> _activeResources = new();

    public UraLegacySelector(
        IVisualPipelineRuntime visualRuntime,
        HachimiJsonPipelineRunner jsonRunner)
    {
        ArgumentNullException.ThrowIfNull(visualRuntime);
        ArgumentNullException.ThrowIfNull(jsonRunner);
        _visualRuntime = visualRuntime;
        _jsonRunner = jsonRunner;
    }

    public async Task<UraLegacySelectionResult> SelectAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        ICareerEntrySelectionSettings settings,
        IGrassTaskLogSink? logSink,
        CareerVisualResourcePackage? visualResources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(settings);
        _activeResources.Value = visualResources
            ?? throw new InvalidDataException("Career Legacy selection requires its visual resource package.");

        var slotStates = await DetectLegacySlotsAsync(
                connection,
                definition,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);

        if (settings.UseCachedLegacy
            && slotStates.Legacy1 == LegacySlotState.Cached
            && slotStates.Legacy2 == LegacySlotState.Cached)
        {
            logSink?.Add(
                "Career Training",
                "Detected cached Legacy 1 and Legacy 2 records; keeping both cached selections.");

            if (!await TapTemplateAsync(
                    connection,
                    definition,
                    "career.entry.legacy.legacy_next_enabled",
                    "career.entry.legacy.next",
                "uraLegacyNextCached",
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return Failure("Could not confirm the cached URA Legacy 1 and Legacy 2 selections.");
            }

            return new UraLegacySelectionResult(
                true,
                "Kept the cached URA Legacy 1 and Legacy 2 selections.");
        }

        if (string.Equals(settings.LegacySelectionMode, "manual", StringComparison.OrdinalIgnoreCase))
        {
            return await SelectManuallyAsync(
                    connection,
                    definition,
                    settings,
                    slotStates,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await SelectAutomaticallyAsync(
                connection,
                definition,
                settings.UseLegacyGuest,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(LegacySlotState Legacy1, LegacySlotState Legacy2)> DetectLegacySlotsAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var legacy1 = await DetectLegacySlotAsync(
                connection,
                definition,
                slot: 1,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        var legacy2 = await DetectLegacySlotAsync(
                connection,
                definition,
                slot: 2,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        return (legacy1, legacy2);
    }

    private async Task<LegacySlotState> DetectLegacySlotAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        int slot,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var assetId = slot == 1
            ? "career.entry.legacy.legacy1_cached_record"
            : "career.entry.legacy.legacy2_cached_record";
        var regionId = slot == 1
            ? "career.entry.legacy.cached1"
            : "career.entry.legacy.cached2";
        var match = await FindTemplateAsync(
                connection,
                definition,
                assetId,
                regionId,
                $"uraLegacy{slot}CachedRecord",
                1_500,
                cancellationToken)
            .ConfigureAwait(false);
        if (match is { Found: true })
        {
            logSink?.Add(
                "Career Training",
                $"Detected cached Legacy {slot} record from its Change button.");
            return LegacySlotState.Cached;
        }

        return LegacySlotState.Unknown;
    }

    private async Task<UraLegacySelectionResult> SelectAutomaticallyAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        bool includeGuests,
        CancellationToken cancellationToken)
    {
        if (!await TapTemplateAsync(
                connection,
                definition,
                "career.entry.legacy.legacy_auto_select",
                "career.entry.legacy.auto_select",
                "uraLegacyAutoSelect",
                cancellationToken)
            .ConfigureAwait(false))
        {
            return Failure("Could not open URA Auto-Select.");
        }

        if (includeGuests
            && !await EnsureAutoSelectGuestsAsync(
                    connection,
                    definition,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return Failure("Could not enable guests for URA Auto-Select.");
        }

        if (!await TapTemplateAsync(
                connection,
                definition,
                "career.entry.legacy.legacy_auto_select_ok",
                "career.entry.legacy.auto_select.confirm",
                "uraLegacyAutoSelectConfirm",
                cancellationToken)
            .ConfigureAwait(false))
        {
            return Failure("Could not confirm URA Auto-Select.");
        }

        if (!await TapTemplateAsync(
                connection,
                definition,
                "career.entry.legacy.legacy_next_enabled",
                "career.entry.legacy.next",
                "uraLegacyNext",
                cancellationToken)
            .ConfigureAwait(false))
        {
            return Failure("Could not confirm the automatically selected URA legacies.");
        }

        return new UraLegacySelectionResult(true, "Automatically selected and confirmed both URA legacies.");
    }

    private async Task<bool> EnsureAutoSelectGuestsAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        CancellationToken cancellationToken)
    {
        var off = await FindTemplateAsync(
                connection,
                definition,
                "career.entry.legacy.legacy_auto_select_include_guests",
                "career.entry.legacy.guest.checkbox",
                "uraLegacyAutoSelectGuestsOff",
                2_500,
                cancellationToken)
            .ConfigureAwait(false);
        if (off is { Found: true })
        {
            await _visualRuntime.TapMatchAsync(
                    connection,
                    off,
                    "uraLegacyAutoSelectGuests",
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        return await FindTemplateAsync(
                connection,
                definition,
                "career.entry.legacy.legacy_auto_select_include_guests_on",
                "career.entry.legacy.guest.checkbox",
                "uraLegacyAutoSelectGuestsOn",
                2_500,
                cancellationToken)
            .ConfigureAwait(false) is { Found: true };
    }

    private async Task<UraLegacySelectionResult> SelectManuallyAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        ICareerEntrySelectionSettings settings,
        (LegacySlotState Legacy1, LegacySlotState Legacy2) slotStates,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        if (!await SelectLegacySlotAsync(
                connection,
                definition,
                slot: 1,
                useGuest: settings.UseLegacyGuest,
                slotState: slotStates.Legacy1,
                settings,
                logSink,
                cancellationToken)
            .ConfigureAwait(false))
        {
            return Failure("Could not select and confirm URA Legacy 1.");
        }

        if (!await SelectLegacySlotAsync(
                connection,
                definition,
                slot: 2,
                useGuest: false,
                slotState: slotStates.Legacy2,
                settings,
                logSink,
                cancellationToken)
            .ConfigureAwait(false))
        {
            return Failure("Could not select and confirm URA Legacy 2.");
        }

        if (!await TapTemplateAsync(
                connection,
                definition,
                "career.entry.legacy.legacy_next_enabled",
                "career.entry.legacy.next",
                "uraLegacyNext",
                cancellationToken)
            .ConfigureAwait(false))
        {
            return Failure("Could not confirm both manually selected URA legacies.");
        }

        return new UraLegacySelectionResult(true, "Selected and confirmed URA Legacy 1 and Legacy 2.");
    }

    private async Task<bool> SelectLegacySlotAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        int slot,
        bool useGuest,
        LegacySlotState slotState,
        ICareerEntrySelectionSettings settings,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        if (settings.UseCachedLegacy && slotState == LegacySlotState.Cached)
        {
            logSink?.Add(
                "Career Training",
                $"Keeping cached Legacy {slot} record; skipping Legacy {slot} replacement.");
            return true;
        }

        if (!settings.UseCachedLegacy && slotState == LegacySlotState.Cached)
        {
            logSink?.Add(
                "Career Training",
                $"Cached Legacy {slot} record detected, but cache use is disabled; reselecting it.");

            if (!await RunJsonActionAsync(
                    connection,
                    definition,
                    slot == 1
                        ? "legacy_select_legacy1_clear_cached"
                        : "legacy_select_legacy2_clear_cached",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return false;
            }

        }

        if (!await RunJsonActionAsync(
                connection,
                definition,
                slot == 1
                    ? "legacy_select_legacy1_open"
                    : "legacy_select_legacy2_open",
                logSink,
                cancellationToken)
            .ConfigureAwait(false))
        {
            logSink?.Add(
                "Career Training",
                $"Could not find the Legacy {slot} slot template; no tap was sent.",
                LogEntryKind.Failure);
            return false;
        }

        if (useGuest
            && !await TapTemplateAsync(
                    connection,
                    definition,
                    "career.entry.legacy.legacy_guests_tab",
                    "career.entry.legacy.guests.tab",
                    "uraLegacyGuests",
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        if (!await ConfigureDisplayAsync(
                connection,
                definition,
                settings,
                cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }

        var cell = await FindFirstSelectableCellAsync(
                connection,
                definition,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (cell is null)
            return false;

        await _visualRuntime.TapAsync(
                connection,
                cell.Value.X + cell.Value.Width / 2,
                cell.Value.Y + cell.Value.Height / 2,
                definition.ReferenceWidth,
                definition.ReferenceHeight,
                $"uraLegacy{slot}Pick",
                cancellationToken)
            .ConfigureAwait(false);

        return await TapTemplateAsync(
                connection,
                definition,
                "career.entry.legacy.legacy_confirm_selection",
                "career.entry.legacy.confirm_selection",
                $"uraLegacy{slot}Confirm",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> RunJsonActionAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        string taskName,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var result = await _jsonRunner.RunAsync(
                connection,
                definition,
                taskName,
                logSink: logSink,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded;
    }

    private async Task<bool> ConfigureDisplayAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        ICareerEntrySelectionSettings settings,
        CancellationToken cancellationToken)
    {
        var off = await FindTemplateAsync(
                connection,
                definition,
                "career.entry.legacy.legacy_view_sparks_off",
                "career.entry.legacy.view_sparks",
                "uraLegacyViewSparksOff",
                2_500,
                cancellationToken)
            .ConfigureAwait(false);
        if (off is { Found: true })
        {
            await _visualRuntime.TapMatchAsync(
                    connection,
                    off,
                    "uraLegacyViewSparksOn",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (await FindTemplateAsync(
                     connection,
                     definition,
                     "career.entry.legacy.legacy_view_sparks_on",
                     "career.entry.legacy.view_sparks",
                     "uraLegacyViewSparksOnCheck",
                     2_500,
                     cancellationToken)
                 .ConfigureAwait(false) is not { Found: true })
        {
            return false;
        }

        if (!await TapTemplateAsync(
                connection,
                definition,
                "career.entry.legacy.legacy_display_button",
                "career.entry.legacy.display.open",
                "uraLegacyDisplayOpen",
                cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }

        if (!await TapTemplateAsync(
                connection,
                definition,
                "career.entry.legacy.legacy_display_sparks",
                "career.entry.legacy.display.sparks",
                "uraLegacySortSparks",
                cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }

        if (!await TapTemplateAsync(
                connection,
                definition,
                "career.entry.legacy.legacy_display_filter_tab",
                "career.entry.legacy.display.filter_tab",
                "uraLegacyFilterTab",
                cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }

        if (!await TapTemplateAsync(
                connection,
                definition,
                "career.entry.legacy.legacy_filter_reset",
                "career.entry.legacy.filter.reset",
                "uraLegacyFilterReset",
                cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }

        foreach (var key in settings.LegacyAttributeSparks.Concat(settings.LegacyAptitudeSparks))
        {
            var normalized = NormalizeFilterKey(key);
            if (normalized is null)
                continue;

            await TapRegionCenterAsync(
                    connection,
                    definition,
                    $"career.entry.legacy.filter.{normalized}",
                    $"uraLegacyFilter{normalized}",
                    cancellationToken)
                .ConfigureAwait(false);
            await _visualRuntime.DelayAsync(100, cancellationToken).ConfigureAwait(false);
        }

        return await TapTemplateAsync(
                connection,
                definition,
                "career.entry.legacy.legacy_filter_ok",
                "career.entry.legacy.filter.ok",
                "uraLegacyFilterOk",
                cancellationToken)
            .ConfigureAwait(false)
            && await EnsureDescendingAsync(
                connection,
                definition,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> EnsureDescendingAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        CancellationToken cancellationToken)
    {
        var ascending = await FindTemplateAsync(
                connection,
                definition,
                "career.entry.legacy.legacy_sort_asc",
                "career.entry.legacy.sort",
                "uraLegacySortAscending",
                2_500,
                cancellationToken)
            .ConfigureAwait(false);
        if (ascending is { Found: true })
        {
            await _visualRuntime.TapMatchAsync(
                    connection,
                    ascending,
                    "uraLegacySortDescending",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await FindTemplateAsync(
                connection,
                definition,
                "career.entry.legacy.legacy_sort_desc",
                "career.entry.legacy.sort",
                "uraLegacySortDescendingCheck",
                2_500,
                cancellationToken)
            .ConfigureAwait(false) is { Found: true };
    }

    private async Task<LegacyCell?> FindFirstSelectableCellAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var frame = await _visualRuntime.CaptureGrayAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (frame is null)
            return null;

        var traineeBadge = await _visualRuntime.LoadTemplateAsync(
                ResolveAsset("career.entry.legacy.legacy_trainee_badge"),
                definition.BaseDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        if (traineeBadge is null)
        {
            var cells = GetLegacyCells();
            return cells.Count > 0 ? cells[0] : null;
        }

        foreach (var cell in GetLegacyCells())
        {
            var region = RequireRegion(cell.RegionId);
            var badgeRoi = region.FocusedRoi is { Length: >= 4 } focusedRoi
                ? focusedRoi
                : GetRequiredMetadataRoi(region, "badgeRoi");
            var badge = TemplateMatcher.Find(
                frame,
                traineeBadge,
                badgeRoi,
                ResolveAssetThreshold("career.entry.legacy.legacy_trainee_badge"),
                definition.ReferenceWidth,
                definition.ReferenceHeight);
            if (badge.Found)
            {
                logSink?.Add(
                    "Career Training",
                    $"Skipped a first-row card marked Trainee at ({cell.X},{cell.Y}).");
                continue;
            }

            return cell;
        }

        return null;
    }

    private async Task<bool> TapTemplateAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        string assetId,
        string regionId,
        string actionName,
        CancellationToken cancellationToken)
    {
        var match = await FindTemplateAsync(
                connection,
                definition,
                assetId,
                regionId,
                actionName,
                8_000,
                cancellationToken)
            .ConfigureAwait(false);
        if (match is not { Found: true })
            return false;

        await _visualRuntime.TapMatchAsync(
                connection,
                match,
                actionName,
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private async Task<TemplateMatchResult?> FindTemplateAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        string assetId,
        string regionId,
        string actionName,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        return await _visualRuntime.WaitForMatchAsync(
                connection,
                ResolveAsset(assetId),
                GetRequiredRegionRoi(regionId),
                ResolveAssetThreshold(assetId),
                definition.ReferenceWidth,
                definition.ReferenceHeight,
                timeoutMilliseconds,
                250,
                actionName,
                definition.BaseDirectory,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static UraLegacySelectionResult Failure(string message) =>
        new(false, message);

    private CareerVisualResourcePackage Resources =>
        _activeResources.Value
            ?? throw new InvalidDataException("Career Legacy visual resources are not active.");

    private string ResolveAsset(string assetId) => Resources.ResolveVisualResource(assetId);

    private double ResolveAssetThreshold(string assetId)
    {
        if (Resources.TryGetAsset(assetId, out var asset) && asset?.Threshold is { } threshold)
            return threshold;
        throw new InvalidDataException($"Career Legacy asset '{assetId}' has no threshold.");
    }

    private int[] GetRequiredRegionRoi(string regionId) =>
        RequireRegion(regionId).Roi is { Length: >= 4 } roi
            ? roi
            : throw new InvalidDataException($"Career Legacy region '{regionId}' has no ROI.");

    private CareerVisualRegionDefinition RequireRegion(string regionId) =>
        Resources.TryGetRegion(regionId, out var region) && region is not null
            ? region
            : throw new InvalidDataException($"Career Legacy visual region '{regionId}' is missing.");

    private async Task TapRegionCenterAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        string regionId,
        string actionName,
        CancellationToken cancellationToken)
    {
        var roi = GetRequiredRegionRoi(regionId);
        var x = (int)Math.Round((roi[0] + roi[2] / 2d)
            * Math.Max(1, connection.Width) / Math.Max(1, definition.ReferenceWidth));
        var y = (int)Math.Round((roi[1] + roi[3] / 2d)
            * Math.Max(1, connection.Height) / Math.Max(1, definition.ReferenceHeight));
        await _visualRuntime.TapMatchAsync(
                connection,
                new TemplateMatchResult(true, 1d, x, y, 1, 1),
                actionName,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private List<LegacyCell> GetLegacyCells()
    {
        var cells = new List<LegacyCell>(4);
        for (var index = 1; index <= 4; index++)
        {
            var regionId = $"career.entry.legacy.cell.{index}";
            var roi = GetRequiredRegionRoi(regionId);
            cells.Add(new LegacyCell(roi[0], roi[1], roi[2], roi[3], regionId));
        }
        return cells;
    }

    private static int[] GetRequiredMetadataRoi(
        CareerVisualRegionDefinition region,
        string metadataName)
    {
        if (region.Metadata?.TryGetValue(metadataName, out var value) != true
            || value.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Career Legacy region '{region.Id}' is missing '{metadataName}' metadata.");
        }
        var roi = value.EnumerateArray().Select(item => item.GetInt32()).ToArray();
        return roi.Length >= 4
            ? roi
            : throw new InvalidDataException(
                $"Career Legacy region '{region.Id}' has invalid '{metadataName}' metadata.");
    }

    private static string? NormalizeFilterKey(string key) => key.Trim().ToLowerInvariant() switch
    {
        "wisdom" => "wit",
        "speed" or "stamina" or "power" or "guts" or "wit" or "turf" or "dirt"
            or "sprint" or "mile" or "medium" or "long" or "front" or "pace" or "late" or "end"
            => key.Trim().ToLowerInvariant(),
        _ => null,
    };

    private enum LegacySlotState
    {
        Unknown,
        Cached,
    }

    private readonly record struct LegacyCell(int X, int Y, int Width, int Height, string RegionId);
}

public sealed record UraLegacySelectionResult(bool Succeeded, string Message);
