using System.IO;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class NormalCareerSkillCacheTests
{
    [Fact]
    public async Task Confirmed_skills_survive_a_new_run_then_clear_after_career_completion()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"normal-skill-cache-{Guid.NewGuid():N}");
        var connection = new LastVerifiedConnection("adb", "serial", "android", "version",
            900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);
        try
        {
            var firstRun = new NormalCareerSkillCache(connection, 100602, directory);
            await firstRun.SaveAsync([101, 202, 101]);

            var resumedRun = new NormalCareerSkillCache(connection, 100602, directory);
            Assert.Equal([101, 202], await resumedRun.LoadAsync());
            Assert.Empty(await new NormalCareerSkillCache(connection, 100603, directory)
                .LoadAsync());

            await resumedRun.ClearAsync();
            Assert.False(File.Exists(resumedRun.CachePath));
            Assert.Empty(await new NormalCareerSkillCache(connection, 100602, directory)
                .LoadAsync());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
