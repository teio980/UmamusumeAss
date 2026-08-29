using System.IO;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class IndependentSkillSelectionFlowContractTests
{
    [Fact]
    public async Task Each_independent_skill_reopens_picker_and_confirms_before_next_iteration()
    {
        var root = FindSolutionRoot();
        var pipelineSource = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "UmamusumeWpfGui",
            "Services",
            "Training",
            "AdbCareerTrainingPipeline.cs"));

        var skillsSectionStart = pipelineSource.IndexOf(
            "if (!state.IndependentSkillsConfigured)",
            StringComparison.Ordinal);
        Assert.True(skillsSectionStart >= 0);

        var loopStart = pipelineSource.IndexOf(
            "foreach (var skillId in settings.IndependentSkillIds ?? [])",
            skillsSectionStart,
            StringComparison.Ordinal);
        Assert.True(loopStart >= 0);

        var loopBodyStart = pipelineSource.IndexOf('{', loopStart);
        var loopBodyEnd = FindMatchingBrace(pipelineSource, loopBodyStart);
        Assert.True(loopBodyStart >= 0);
        Assert.True(loopBodyEnd > loopBodyStart);

        var loopBody = pipelineSource[loopBodyStart..(loopBodyEnd + 1)];
        var openIndex = loopBody.IndexOf(
            "\"independent.skills.open\"",
            StringComparison.Ordinal);
        var searchIndex = loopBody.IndexOf(
            "IndependentTrainingCatalog.SkillSearchResetSemanticAction()",
            openIndex,
            StringComparison.Ordinal);
        var selectIndex = loopBody.IndexOf(
            "IndependentTrainingCatalog.SkillSearchCheckboxSemanticAction()",
            searchIndex,
            StringComparison.Ordinal);
        var confirmIndex = loopBody.IndexOf(
            "\"independent.skills.save\"",
            selectIndex,
            StringComparison.Ordinal);

        Assert.True(openIndex >= 0);
        Assert.True(searchIndex > openIndex);
        Assert.True(selectIndex > searchIndex);
        Assert.True(confirmIndex > selectIndex);
        Assert.Equal(1, CountOccurrences(loopBody, "\"independent.skills.open\""));
        Assert.Equal(1, CountOccurrences(loopBody, "\"independent.skills.save\""));

        // A picker open/confirm outside the loop would batch multiple checked
        // skills into one dialog, causing the game to keep only the last one.
        Assert.DoesNotContain(
            "\"independent.skills.open\"",
            pipelineSource[..loopStart]);
        Assert.DoesNotContain(
            "\"independent.skills.save\"",
            pipelineSource[(loopBodyEnd + 1)..]);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static int FindMatchingBrace(string source, int openingBrace)
    {
        if (openingBrace < 0)
            return -1;

        var depth = 0;
        for (var index = openingBrace; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return index;
                    break;
            }
        }

        return -1;
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CMakePresets.json")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
