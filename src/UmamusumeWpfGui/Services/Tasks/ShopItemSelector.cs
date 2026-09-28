using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>Selects shop rows by their visible names, never by their list index.</summary>
internal sealed class ShopItemSelector
{
    private const int ReferenceWidth = 900;
    private const int ReferenceHeight = 1600;
    private const int CheckboxX = 765;
    private const int CheckboxBelowName = 42;
    private const int MaximumScrolls = 30;
    private static readonly int[] ListTextRoi = [175, 585, 520, 565];
    // On the live list, a 400 px finger swipe moves cards by over 500 px.
    // Shorter swipes keep each row fully visible on at least one OCR pass.
    private static readonly int[] ScrollDown = [450, 1050, 450, 850, 450];
    private static readonly int[] ScrollUp = [450, 650, 450, 1050, 450];
    private static readonly double[] CheckboxScales = [0.78, 0.85, 0.92, 1.0];

    private readonly IVisualPipelineRuntime _visual;

    public ShopItemSelector(IVisualPipelineRuntime visual) =>
        _visual = visual ?? throw new ArgumentNullException(nameof(visual));

    public async Task<ShopSelectionResult> SelectAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        ShopPurchaseOptions options,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        if (!options.HasIndividualSelections)
        {
            Log(logSink, "No individual shop items are enabled in Settings.");
            return new ShopSelectionResult(false, 0, 0);
        }

        var checkbox = await _visual.LoadTemplateAsync(
                "templates/shop/item_checkbox.png",
                definition.BaseDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        var disabledConfirm = await _visual.LoadTemplateAsync(
                "templates/shop/confirm_disabled.png",
                definition.BaseDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        if (checkbox is null || disabledConfirm is null)
        {
            Log(logSink, "Shop selection references are unavailable; skipping purchase.");
            return new ShopSelectionResult(false, 0, 1);
        }

        // A previous manual selection must not make an unconfigured item part
        // of the purchase. Reset is a fixed footer control, unlike list rows.
        await _visual.TapAsync(
                connection, 770, 1350, ReferenceWidth, ReferenceHeight,
                "shopSelectItems.reset", cancellationToken)
            .ConfigureAwait(false);
        await _visual.DelayAsync(250, cancellationToken).ConfigureAwait(false);

        var frame = await _visual.CaptureGrayAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (frame is null)
        {
            Log(logSink, "Could not capture the shop list.");
            return new ShopSelectionResult(false, 0, 1);
        }
        var resetConfirmed = TemplateMatcher.Find(
            frame, disabledConfirm, [250, 1250, 430, 240], 0.80,
            ReferenceWidth, ReferenceHeight).Found;
        if (!resetConfirmed)
        {
            await _visual.DelayAsync(350, cancellationToken).ConfigureAwait(false);
            frame = await _visual.CaptureGrayAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            resetConfirmed = frame is not null && TemplateMatcher.Find(
                frame, disabledConfirm, [250, 1250, 430, 240], 0.80,
                ReferenceWidth, ReferenceHeight).Found;
            if (frame is null || !resetConfirmed)
            {
                Log(logSink, "Reset did not clear previous shop selections; skipping purchase.");
                return new ShopSelectionResult(false, 0, 1);
            }
        }

        // The flow can be entered while the list is already scrolled. Start
        // from the first card, then visit each page with overlap.
        var reachedTop = false;
        for (var rewind = 0; rewind < MaximumScrolls; rewind++)
        {
            await _visual.SwipeAsync(
                    connection, ScrollUp, ReferenceWidth, ReferenceHeight,
                    "shopSelectItems.rewind", cancellationToken)
                .ConfigureAwait(false);
            await _visual.DelayAsync(350, cancellationToken).ConfigureAwait(false);
            var next = await _visual.CaptureGrayAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            if (next is null)
                break;
            var unchanged = IsSameList(frame, next);
            frame = next;
            if (unchanged)
            {
                reachedTop = true;
                break;
            }
        }
        if (!reachedTop)
        {
            Log(logSink, "Could not establish the top of the shop list; skipping purchase.");
            return new ShopSelectionResult(false, 0, 1);
        }

        var selected = 0;
        var hasSelection = false;
        var skipped = 0;
        var observed = new HashSet<ShopItemCategory>();
        var reachedBottom = false;
        for (var page = 0; page <= MaximumScrolls; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recognized = await _visual.DetectTextAsync(
                    frame, ListTextRoi, ReferenceWidth, ReferenceHeight,
                    "en-US", "shop.items", cancellationToken)
                .ConfigureAwait(false);
            if (recognized is null)
            {
                skipped++;
                Log(logSink, $"Page {page + 1}: OCR unavailable; skipped visible rows.");
            }
            else
            {
                foreach (var detection in recognized.Detections.OrderBy(item => item.Bounds.Y))
                {
                    var category = Classify(detection.Text);
                    if (category is null || !options.IsSelected(category.Value))
                        continue;

                    var expected = ExpectedCheckbox(detection.Bounds, frame.Width, frame.Height);
                    if (expected is null)
                        continue; // The card is clipped at the top or bottom.
                    observed.Add(category.Value);

                    var (x, y) = expected.Value;
                    var selectedBefore = await IsCheckedAsync(
                            connection, x, y, "shopSelectItems.precheck", cancellationToken)
                        .ConfigureAwait(false);
                    if (selectedBefore)
                    {
                        hasSelection = true;
                        continue; // An overlapping page must not toggle it off.
                    }

                    var roi = new[] { x - 55, y - 55, 110, 110 };
                    var match = TemplateMatcher.FindScaled(
                        frame, checkbox, roi, 0.76,
                        ReferenceWidth, ReferenceHeight, CheckboxScales);
                    if (!match.Found
                        || Math.Abs(ToReference(match.CenterX, frame.Width, ReferenceWidth) - x) > 20
                        || Math.Abs(ToReference(match.CenterY, frame.Height, ReferenceHeight) - y) > 24)
                    {
                        skipped++;
                        Log(logSink, $"Skipped '{detection.Text}': adjacent checkbox was not uniquely located.");
                        continue;
                    }

                    await _visual.TapMatchAsync(
                            connection, match, "shopSelectItems", cancellationToken)
                        .ConfigureAwait(false);
                    var checkedAfter = await IsCheckedAsync(
                            connection, x, y, "shopSelectItems.verify", cancellationToken,
                            timeoutMilliseconds: 1200)
                        .ConfigureAwait(false);
                    if (checkedAfter)
                    {
                        selected++;
                        hasSelection = true;
                        Log(logSink, $"Selected '{detection.Text}' at ({match.CenterX},{match.CenterY}).");
                    }
                    else
                    {
                        skipped++;
                        var afterTap = await _visual.CaptureGrayAsync(connection, cancellationToken)
                            .ConfigureAwait(false);
                        var stillUnchecked = afterTap is not null
                            && TemplateMatcher.FindScaled(
                                afterTap, checkbox, roi, 0.76,
                                ReferenceWidth, ReferenceHeight, CheckboxScales).Found;
                        if (!stillUnchecked)
                        {
                            // The tap may have selected the item even though
                            // the color probe missed it. Do not submit an
                            // uncertain purchase alongside verified rows.
                            await _visual.TapAsync(
                                    connection, 770, 1350, ReferenceWidth, ReferenceHeight,
                                    "shopSelectItems.resetUncertain", cancellationToken)
                                .ConfigureAwait(false);
                            Log(logSink, $"Purchase canceled: checkbox state for '{detection.Text}' is uncertain.");
                            return new ShopSelectionResult(false, selected, skipped);
                        }
                        Log(logSink, $"Skipped '{detection.Text}': checkbox remained unchecked after tap.");
                    }
                }
            }

            if (page == MaximumScrolls)
                break;
            await _visual.SwipeAsync(
                    connection, ScrollDown, ReferenceWidth, ReferenceHeight,
                    "shopSelectItems.scroll", cancellationToken)
                .ConfigureAwait(false);
            await _visual.DelayAsync(350, cancellationToken).ConfigureAwait(false);
            var scrolled = await _visual.CaptureGrayAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            if (scrolled is null)
                break;
            var atBottom = IsSameList(frame, scrolled);
            frame = scrolled;
            if (atBottom)
            {
                reachedBottom = true;
                break;
            }
        }

        if (!reachedBottom)
        {
            await _visual.TapAsync(
                    connection, 770, 1350, ReferenceWidth, ReferenceHeight,
                    "shopSelectItems.resetIncomplete", cancellationToken)
                .ConfigureAwait(false);
            Log(logSink, "Shop scan did not reach the bottom; selection was reset and purchase skipped.");
            return new ShopSelectionResult(false, selected, skipped + 1);
        }

        foreach (var category in Enum.GetValues<ShopItemCategory>())
        {
            if (!options.IsSelected(category) || observed.Contains(category))
                continue;
            skipped++;
            Log(logSink, $"No OCR match for configured {category} in the scanned shop list; skipped.");
        }

        Log(logSink, $"Shop OCR finished: {selected} verified row(s), {skipped} skipped row(s).");
        return new ShopSelectionResult(hasSelection, selected, skipped);
    }

    internal static ShopItemCategory? Classify(string text)
    {
        var normalized = new string(text
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        if (normalized.Contains("starpiece", StringComparison.Ordinal)
            || normalized.Contains("startpiece", StringComparison.Ordinal))
            return ShopItemCategory.StarPieces;
        if (normalized.Contains("alarmclock", StringComparison.Ordinal))
            return ShopItemCategory.AlarmClock;
        if (normalized.Contains("pleasingparfait", StringComparison.Ordinal))
            return ShopItemCategory.PleasingParfait;
        if (normalized.Contains("racingshoes", StringComparison.Ordinal))
            return ShopItemCategory.Shoes;
        if (normalized.Contains("supportpoints", StringComparison.Ordinal))
            return ShopItemCategory.SupportPoints;
        if (normalized.Contains("winnerssash", StringComparison.Ordinal)
            || normalized.Contains("winningflag", StringComparison.Ordinal))
            return ShopItemCategory.Flags;
        return null;
    }

    internal static (int X, int Y)? ExpectedCheckbox(
        ScreenTextRect text,
        int actualWidth,
        int actualHeight)
    {
        var nameY = ToReference(text.CenterY, actualHeight, ReferenceHeight);
        var nameX = ToReference(text.X, actualWidth, ReferenceWidth);
        if (nameX < 165 || nameX > 670 || nameY < 592 || nameY > 1060)
            return null;
        return (CheckboxX, nameY + CheckboxBelowName);
    }

    private async Task<bool> IsCheckedAsync(
        LastVerifiedConnection connection,
        int x,
        int y,
        string taskName,
        CancellationToken cancellationToken,
        int timeoutMilliseconds = 0)
    {
        var probe = await _visual.ProbeHsvAsync(
                connection, x, y, null, 22,
                ReferenceWidth, ReferenceHeight,
                70, 160, 0.38, 1, 0.3, 1, 0.10,
                timeoutMilliseconds, 120, taskName, cancellationToken)
            .ConfigureAwait(false);
        return probe?.Matched == true;
    }

    private static bool IsSameList(GrayImage before, GrayImage after)
    {
        if (before.Width != after.Width || before.Height != after.Height)
            return false;
        long difference = 0;
        var samples = 0;
        for (var referenceY = 610; referenceY <= 1100; referenceY += 12)
        {
            var y = ToActual(referenceY, before.Height, ReferenceHeight);
            for (var referenceX = 190; referenceX <= 650; referenceX += 12)
            {
                var x = ToActual(referenceX, before.Width, ReferenceWidth);
                var index = y * before.Width + x;
                difference += Math.Abs(before.Pixels[index] - after.Pixels[index]);
                samples++;
            }
        }
        long scrollbarDifference = 0;
        var scrollbarSamples = 0;
        var scrollbarX = ToActual(876, before.Width, ReferenceWidth);
        for (var referenceY = 610; referenceY <= 1130; referenceY += 6)
        {
            var y = ToActual(referenceY, before.Height, ReferenceHeight);
            var index = y * before.Width + scrollbarX;
            scrollbarDifference += Math.Abs(before.Pixels[index] - after.Pixels[index]);
            scrollbarSamples++;
        }
        return samples > 0 && scrollbarSamples > 0
            && difference / (double)samples < 3.0
            && scrollbarDifference / (double)scrollbarSamples < 5.0;
    }

    private static int ToReference(int value, int actualSize, int referenceSize) =>
        (int)Math.Round(value * (double)referenceSize / Math.Max(1, actualSize));

    private static int ToActual(int value, int actualSize, int referenceSize) =>
        Math.Clamp((int)Math.Round(value * (double)actualSize / referenceSize), 0, actualSize - 1);

    private static void Log(IGrassTaskLogSink? sink, string message) =>
        sink?.Add("shopSelectItems", message, LogEntryKind.Info);
}

internal sealed record ShopSelectionResult(bool HasSelection, int VerifiedRows, int SkippedRows);
