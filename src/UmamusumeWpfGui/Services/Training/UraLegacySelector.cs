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
    private const int ReferenceWidth = 900;
    private const int ReferenceHeight = 1600;
    private const double TemplateThreshold = 0.78;

    private static readonly LegacyCell[] FirstRowCells =
    [
        new(195, 860, 160, 190),
        new(370, 860, 160, 190),
        new(545, 860, 160, 190),
        new(720, 860, 160, 190),
    ];

    private static readonly Dictionary<string, LegacyFilterOption> FilterOptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Speed"] = new("Speed", 90, 345),
            ["Stamina"] = new("Stamina", 370, 345),
            ["Power"] = new("Power", 652, 345),
            ["Guts"] = new("Guts", 90, 440),
            ["Wit"] = new("Wit", 370, 440),
            ["Turf"] = new("Turf", 90, 860),
            ["Dirt"] = new("Dirt", 370, 860),
            ["Sprint"] = new("Sprint", 652, 860),
            ["Mile"] = new("Mile", 90, 956),
            ["Medium"] = new("Medium", 370, 956),
            ["Long"] = new("Long", 652, 956),
            ["Front"] = new("Front", 90, 1050),
            ["Pace"] = new("Pace", 370, 1050),
            ["Late"] = new("Late", 652, 1050),
            ["End"] = new("End", 90, 1145),
        };

    private readonly IVisualPipelineRuntime _visualRuntime;

    public UraLegacySelector(IVisualPipelineRuntime visualRuntime)
    {
        ArgumentNullException.ThrowIfNull(visualRuntime);
        _visualRuntime = visualRuntime;
    }

    public async Task<UraLegacySelectionResult> SelectAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        CareerTrainingSettings settings,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(settings);

        if (string.Equals(settings.LegacySelectionMode, "manual", StringComparison.OrdinalIgnoreCase))
        {
            return await SelectManuallyAsync(
                    connection,
                    definition,
                    settings,
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

    private async Task<UraLegacySelectionResult> SelectAutomaticallyAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        bool includeGuests,
        CancellationToken cancellationToken)
    {
        if (!await TapTemplateAsync(
                connection,
                definition,
                "legacy_auto_select.png",
                [430, 1140, 450, 170],
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
                "legacy_auto_select_ok.png",
                [430, 930, 430, 220],
                "uraLegacyAutoSelectConfirm",
                cancellationToken)
            .ConfigureAwait(false))
        {
            return Failure("Could not confirm URA Auto-Select.");
        }

        if (!await TapTemplateAsync(
                connection,
                definition,
                "legacy_next_enabled.png",
                [250, 1250, 400, 200],
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
                "legacy_auto_select_include_guests.png",
                [270, 770, 180, 180],
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
                "legacy_auto_select_include_guests_on.png",
                [270, 770, 180, 180],
                "uraLegacyAutoSelectGuestsOn",
                2_500,
                cancellationToken)
            .ConfigureAwait(false) is { Found: true };
    }

    private async Task<UraLegacySelectionResult> SelectManuallyAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        CareerTrainingSettings settings,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        if (!await SelectLegacySlotAsync(
                connection,
                definition,
                slot: 1,
                useGuest: settings.UseLegacyGuest,
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
                "legacy_next_enabled.png",
                [250, 1250, 400, 200],
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
        CareerTrainingSettings settings,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var slotTemplate = slot == 1 ? "legacy1_slot.png" : "legacy2_slot.png";
        if (!await TapTemplateAsync(
                connection,
                definition,
                slotTemplate,
                slot == 1 ? [0, 760, 450, 450] : [430, 760, 450, 450],
                $"uraLegacy{slot}Open",
                cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }

        await _visualRuntime.DelayAsync(450, cancellationToken).ConfigureAwait(false);
        if (useGuest
            && !await TapTemplateAsync(
                    connection,
                    definition,
                    "legacy_guests_tab.png",
                    [400, 760, 480, 130],
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
                ReferenceWidth,
                ReferenceHeight,
                $"uraLegacy{slot}Pick",
                cancellationToken)
            .ConfigureAwait(false);

        return await TapTemplateAsync(
                connection,
                definition,
                "legacy_confirm_selection.png",
                [250, 1250, 400, 200],
                $"uraLegacy{slot}Confirm",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> ConfigureDisplayAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        CareerTrainingSettings settings,
        CancellationToken cancellationToken)
    {
        var off = await FindTemplateAsync(
                connection,
                definition,
                "legacy_view_sparks_off.png",
                [0, 1140, 300, 160],
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
                     "legacy_view_sparks_on.png",
                     [0, 1140, 300, 160],
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
                "legacy_display_button.png",
                [500, 1100, 350, 200],
                "uraLegacyDisplayOpen",
                cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }

        if (!await TapTemplateAsync(
                connection,
                definition,
                "legacy_display_sparks.png",
                [0, 350, 300, 200],
                "uraLegacySortSparks",
                cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }

        if (!await TapTemplateAsync(
                connection,
                definition,
                "legacy_display_filter_tab.png",
                [430, 100, 450, 150],
                "uraLegacyFilterTab",
                cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }

        if (!await TapTemplateAsync(
                connection,
                definition,
                "legacy_filter_reset.png",
                [550, 1200, 350, 220],
                "uraLegacyFilterReset",
                cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }

        foreach (var key in settings.LegacyAttributeSparks.Concat(settings.LegacyAptitudeSparks))
        {
            if (!FilterOptions.TryGetValue(key.Trim(), out var option))
                continue;

            await _visualRuntime.TapAsync(
                    connection,
                    option.X,
                    option.Y,
                    ReferenceWidth,
                    ReferenceHeight,
                    $"uraLegacyFilter{option.Name}",
                    cancellationToken)
                .ConfigureAwait(false);
            await _visualRuntime.DelayAsync(100, cancellationToken).ConfigureAwait(false);
        }

        return await TapTemplateAsync(
                connection,
                definition,
                "legacy_filter_ok.png",
                [430, 1350, 430, 200],
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
        var roi = new[] { 730, 1130, 170, 150 };
        var ascending = await FindTemplateAsync(
                connection,
                definition,
                "legacy_sort_asc.png",
                roi,
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
                "legacy_sort_desc.png",
                roi,
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
                "templates/legacy/legacy_trainee_badge.png",
                definition.BaseDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        if (traineeBadge is null)
            return FirstRowCells[0];

        foreach (var cell in FirstRowCells)
        {
            var badge = TemplateMatcher.Find(
                frame,
                traineeBadge,
                [cell.X, cell.Y, cell.Width, 75],
                0.78,
                ReferenceWidth,
                ReferenceHeight);
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
        string templateName,
        int[] roi,
        string actionName,
        CancellationToken cancellationToken)
    {
        var match = await FindTemplateAsync(
                connection,
                definition,
                templateName,
                roi,
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
        string templateName,
        int[] roi,
        string actionName,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        return await _visualRuntime.WaitForMatchAsync(
                connection,
                $"templates/legacy/{templateName}",
                roi,
                TemplateThreshold,
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

    private readonly record struct LegacyCell(int X, int Y, int Width, int Height);

    private sealed record LegacyFilterOption(string Name, int X, int Y);
}

public sealed record UraLegacySelectionResult(bool Succeeded, string Message);
