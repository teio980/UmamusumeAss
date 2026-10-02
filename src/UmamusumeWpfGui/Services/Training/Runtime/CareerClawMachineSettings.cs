namespace UmamusumeWpfGui.Services.Training;

/// <summary>Detection tolerances and bounded waits, authored in the screen profile.</summary>
public sealed class CareerClawMachineSettings
{
    public double MarkerSearchScale { get; set; } = 0.25;
    public double MarkerCandidateThreshold { get; set; } = 0.80;
    public string CreditLabelTemplate { get; set; } = "templates/career/turn/claw_credit_label.png";
    public double CreditLabelThreshold { get; set; } = 0.94;
    public double OcrScale { get; set; } = 2;
    public double CreditOcrPaddingLabelRatio { get; set; } = 0.25;
    public double ControlBorderPaddingRatio { get; set; } = 0.10;
    public int ReadyTimeoutMs { get; set; } = 12_000;
    public int NextAttemptTimeoutMs { get; set; } = 45_000;
    public int ResultTimeoutMs { get; set; } = 25_000;
    public int ExitTimeoutMs { get; set; } = 20_000;
    public int PollIntervalMs { get; set; } = 200;
    public int HoldDurationMs { get; set; } = 1_000;
    public int StableSamples { get; set; } = 2;
    public double StableToleranceControlRatio { get; set; } = 0.08;
    public double ControlPinkPixelRatio { get; set; } = 0.08;

    public ClawPixelRule WhiteControlColor { get; set; } = new()
    {
        MinimumRgb = [235, 220, 235], MaximumRgb = [255, 255, 255],
    };
    public ClawPixelRule PinkControlColor { get; set; } = new()
    {
        MinimumRgb = [210, 0, 140], MaximumRgb = [255, 229, 255],
        RedMinusGreen = [20, 255], GreenMinusBlue = [-255, -15],
    };
    public ClawPixelRule ResultButtonColor { get; set; } = new()
    {
        MinimumRgb = [60, 150, 0], MaximumRgb = [190, 255, 120],
    };
    public ClawPixelRule OcrTextColor { get; set; } = new()
    {
        MinimumRgb = [235, 235, 235], MaximumRgb = [255, 255, 255],
    };
    public ClawBlobRule ControlShape { get; set; } = new()
    {
        MinimumAreaRatio = 0.01, MaximumAreaRatio = 0.15,
        MinimumWidthRatio = 0.18, MaximumWidthRatio = 0.50,
        MinimumHeightRatio = 0.10, MaximumHeightRatio = 0.30,
        MinimumAspectRatio = 0.65, MaximumAspectRatio = 1.5,
    };
    public ClawBlobRule ResultButtonShape { get; set; } = new()
    {
        MinimumAreaRatio = 0.005, MaximumAreaRatio = 0.10,
        MinimumWidthRatio = 0.15, MaximumWidthRatio = 0.65,
        MinimumHeightRatio = 0.025, MaximumHeightRatio = 0.15,
        MinimumAspectRatio = 2, MaximumAspectRatio = 8,
    };
}

public sealed class ClawPixelRule
{
    public int[] MinimumRgb { get; set; } = [0, 0, 0];
    public int[] MaximumRgb { get; set; } = [255, 255, 255];
    public int[]? RedMinusGreen { get; set; }
    public int[]? GreenMinusBlue { get; set; }

    internal bool Accepts(byte red, byte green, byte blue) =>
        Between(red, MinimumRgb[0], MaximumRgb[0])
        && Between(green, MinimumRgb[1], MaximumRgb[1])
        && Between(blue, MinimumRgb[2], MaximumRgb[2])
        && (RedMinusGreen is not [var rgMin, var rgMax]
            || Between(red - green, rgMin, rgMax))
        && (GreenMinusBlue is not [var gbMin, var gbMax]
            || Between(green - blue, gbMin, gbMax));

    private static bool Between(double value, double minimum, double maximum) =>
        value >= minimum && value <= maximum;
}

public sealed class ClawBlobRule
{
    public double MinimumAreaRatio { get; set; }
    public double MaximumAreaRatio { get; set; } = 1;
    public double MinimumWidthRatio { get; set; }
    public double MaximumWidthRatio { get; set; } = 1;
    public double MinimumHeightRatio { get; set; }
    public double MaximumHeightRatio { get; set; } = 1;
    public double MinimumAspectRatio { get; set; }
    public double MaximumAspectRatio { get; set; } = double.MaxValue;
}
