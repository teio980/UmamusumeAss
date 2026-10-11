namespace UmamusumeWpfGui.Models;

/// <summary>Editable relative weights; native Core owns training selection and its cursor.</summary>
public sealed record NormalTrainingRatio(int Speed, int Stamina, int Power, int Guts, int Wit)
{
    public static NormalTrainingRatio Default { get; } = new(2, 1, 0, 0, 0);
}
