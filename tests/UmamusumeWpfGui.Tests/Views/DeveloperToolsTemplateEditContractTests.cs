using System.IO;

namespace UmamusumeWpfGui.Tests.Views;

public sealed class DeveloperToolsTemplateEditContractTests
{
    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void Edited_pipeline_template_preserves_search_roi()
    {
        var viewModelPath = Path.Combine(
            RepositoryRoot,
            "src",
            "UmamusumeWpfGui",
            "ViewModels",
            "DeveloperToolsViewModel.cs");
        var content = File.ReadAllText(viewModelPath);
        var start = content.IndexOf(
            "private void SaveEditedPipelineTemplate()",
            StringComparison.Ordinal);
        var end = content.IndexOf(
            "    public void ValidatePipeline()",
            start,
            StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);
        var saveMethod = content[start..end];

        Assert.DoesNotContain("MapTemplateCropToRoi", saveMethod);
        Assert.DoesNotContain("SelectedPipelineTask.RoiText =", saveMethod);
        Assert.Contains("preserved search ROI", saveMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void Edited_template_button_does_not_promise_automatic_roi_changes()
    {
        var english = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "UmamusumeWpfGui",
            "Resources",
            "Strings.en-US.xaml"));
        var chinese = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "UmamusumeWpfGui",
            "Resources",
            "Strings.zh-CN.xaml"));

        Assert.Contains("preserve search ROI", english, StringComparison.Ordinal);
        Assert.DoesNotContain("auto ROI", english, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("保留搜索ROI", chinese, StringComparison.Ordinal);
        Assert.DoesNotContain("自动更新ROI", chinese, StringComparison.Ordinal);
    }
}
