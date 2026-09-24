using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

internal static class CareerRestConfirmationGate
{
    public static bool HasOkDisappeared(
        GrayImage frame,
        GrayImage okTemplate,
        HachimiPipelineTask confirmTask,
        int referenceWidth,
        int referenceHeight) =>
        !TemplateMatcher.Find(
            frame,
            okTemplate,
            confirmTask.Roi,
            confirmTask.TemplateThreshold,
            referenceWidth,
            referenceHeight).Found;
}
