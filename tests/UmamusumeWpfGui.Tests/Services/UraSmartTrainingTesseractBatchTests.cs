using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraSmartTrainingTesseractBatchTests
{
    [Fact]
    public void Missing_pages_and_reordered_words_never_shift_other_fields()
    {
        const string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n"
            + "5\t3\t1\t1\t1\t1\t0\t0\t40\t20\t90\t0%\n"
            + "5\t1\t1\t1\t1\t1\t0\t0\t40\t20\t90\t+18\n"
            + "5\t5\t1\t1\t1\t1\t0\t0\t40\t20\t90\t99\n";
        var pages = UraSmartTrainingTesseractBatch.ParsePages(tsv, 3);
        Assert.Equal("+18", pages[1]);
        Assert.False(pages.ContainsKey(2));
        Assert.Equal("0%", pages[3]);
        Assert.Equal(2, pages.Count);
    }

    [Fact]
    public async Task An_expired_frame_budget_does_not_start_a_process_or_guess_zero()
    {
        using var expired = new CancellationTokenSource();
        expired.Cancel();
        var calls = 0;
        var readings = await UraSmartTrainingTesseractBatch.ReadAsync(
            [new("speed", new GrayImage(1, 1, [0]))], expired.Token,
            CancellationToken.None, () => calls++);
        Assert.Null(readings["speed"].Value);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UraSmartTrainingTesseractBatch.ReadAsync([], CancellationToken.None, canceled.Token));
    }

    [Fact]
    public async Task Interrupted_process_keeps_unknown_and_next_frame_can_read_again()
    {
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            CareerTestResourceResolver.FindWorkspaceRoot(),
            "smart_runtime_20261006/budget-speed-1.png"))!;
        var crop = CareerNumericOcrReader.Crop(frame, [25, 965, 152, 65], 900, 1600)!;
        var prepared = UraTrainingNumberImagePreprocessor.Prepare(crop, true,
            (r, g, b) => r >= 240 && g is >= 95 and <= 225 && b <= 95 && r >= g + 25)!;
        KeyValuePair<string, GrayImage>[] fields = [new("speed", prepared)];
        using var expired = new CancellationTokenSource();
        var interrupted = await UraSmartTrainingTesseractBatch.ReadAsync(fields, expired.Token,
            CancellationToken.None, expired.Cancel);
        Assert.Null(interrupted["speed"].Value);
        var next = await UraSmartTrainingTesseractBatch.ReadAsync(fields,
            CancellationToken.None, CancellationToken.None);
        Assert.Equal(18, next["speed"].Value);
    }
}
