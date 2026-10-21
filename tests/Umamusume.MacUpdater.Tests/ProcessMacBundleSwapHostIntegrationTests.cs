using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace Umamusume.MacUpdater.Tests;

public sealed class ProcessMacBundleSwapHostIntegrationTests
{
    [Theory]
    [InlineData("healthy", 0, "2.0.0")]
    [InlineData("negative", 2, "1.0.0")]
    [InlineData("timeout", 2, "1.0.0")]
    public async Task RealProcessHostSwapsConfirmsHealthAndRollsBackFailures(string mode, int expectedExitCode, string expectedVersion)
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var fixture = new RealAppFixture(mode);
        using var appLock = new FileStream(Path.Combine(fixture.Root, "app-instance.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var host = new global::Program.ProcessMacBundleSwapHost(appLock);
        var transaction = new MacBundleSwapTransaction(host);
        int result = await transaction.InstallAsync(fixture.JournalPath, fixture.OperationRoot,
            fixture.Journal, mode == "timeout" ? TimeSpan.FromMilliseconds(600) : TimeSpan.FromSeconds(5));

        Assert.Equal(expectedExitCode, result);
        Assert.Equal(expectedVersion, host.ReadBundleVersion(fixture.AppPath));
        if (mode == "healthy")
        {
            Assert.Equal("1.0.0", host.ReadBundleVersion(fixture.BackupPath));
            Assert.False(File.Exists(fixture.RestartMarkerPath));
            Assert.True(await ReadHealthyResultAsync(fixture.OperationRoot));
        }
        else
        {
            Assert.Equal("2.0.0", host.ReadBundleVersion(fixture.CandidatePath));
            await WaitForFileAsync(fixture.RestartMarkerPath, TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(fixture.RestartMarkerPath));
            Assert.False(await ReadHealthyResultAsync(fixture.OperationRoot));
        }
    }

    private static async Task<bool> ReadHealthyResultAsync(string operationRoot)
    {
        string path = Path.Combine(operationRoot, "health.json");
        if (!File.Exists(path)) path = Path.Combine(operationRoot, "result.json");
        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path));
        return document.RootElement.GetProperty("Healthy").GetBoolean();
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!File.Exists(path)) await Task.Delay(25, cancellation.Token);
    }

    private sealed class RealAppFixture : IDisposable
    {
        private const string ProbeSource = """
            #include <stdio.h>
            #include <string.h>
            #include <unistd.h>
            #include <limits.h>

            static void executable_directory(const char *path, char *out, size_t capacity) {
                const char *slash = strrchr(path, '/');
                size_t length = slash ? (size_t)(slash - path) : 0;
                if (length >= capacity) length = capacity - 1;
                memcpy(out, path, length);
                out[length] = '\0';
            }

            int main(int argc, char **argv) {
                char directory[PATH_MAX];
                executable_directory(argv[0], directory, sizeof(directory));
                if (argc == 5 && strcmp(argv[1], "--update-health") == 0) {
                    char flag[PATH_MAX], health[PATH_MAX];
                    snprintf(flag, sizeof(flag), "%s/health-timeout", directory);
                    if (access(flag, F_OK) == 0) return 0;
                    snprintf(flag, sizeof(flag), "%s/health-negative", directory);
                    int healthy = access(flag, F_OK) != 0;
                    snprintf(health, sizeof(health), "%s/health.json", argv[2]);
                    FILE *result = fopen(health, "w");
                    if (!result) return 3;
                    fprintf(result, "{\"OperationId\":\"%s\",\"Nonce\":\"%s\",\"Healthy\":%s,\"Error\":\"probe\"}",
                        argv[3], argv[4], healthy ? "true" : "false");
                    fclose(result);
                    return 0;
                }
                char marker[PATH_MAX];
                snprintf(marker, sizeof(marker), "%s/restart.marker", directory);
                FILE *restarted = fopen(marker, "w");
                if (!restarted) return 4;
                fputs("previous app restarted", restarted);
                fclose(restarted);
                return 0;
            }
            """;

        private readonly string _sourcePath;
        private readonly string _lockPath;

        public RealAppFixture(string mode)
        {
            Root = Path.Combine(Path.GetTempPath(), "uma mac host integration " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            OperationRoot = Path.Combine(Root, "operation");
            Directory.CreateDirectory(OperationRoot);
            AppPath = Path.Combine(Root, "UmamusumeAss.app");
            CandidatePath = Path.Combine(Root, "UmamusumeAss.app.update-new", "UmamusumeAss.app");
            BackupPath = Path.Combine(Root, "UmamusumeAss.app.previous");
            _sourcePath = Path.Combine(Root, "health-probe.c");
            _lockPath = Path.Combine(Root, "app-instance.lock");
            File.WriteAllText(_sourcePath, ProbeSource, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            string binary = Path.Combine(Root, "health-probe");
            RunTool("/usr/bin/clang", "-O2", "-mmacosx-version-min=14.0", "-o", binary, _sourcePath);
            CreateApp(AppPath, "1.0.0", binary, null);
            string? failureMode = mode == "healthy" ? null : mode;
            CreateApp(CandidatePath, "2.0.0", binary, failureMode);
            Sign(AppPath);
            Sign(CandidatePath);
            JournalPath = Path.Combine(OperationRoot, "journal.json");
            Journal = new MacUpdateJournal("0123456789abcdef0123456789abcdef", new string('A', 64), "prepared",
                AppPath, CandidatePath, BackupPath, "2.0.0", "1.0.0", new string('B', 64), new string('C', 64), 1234);
        }

        public string Root { get; }
        public string OperationRoot { get; }
        public string AppPath { get; }
        public string CandidatePath { get; }
        public string BackupPath { get; }
        public string JournalPath { get; }
        public string RestartMarkerPath => Path.Combine(AppPath, "Contents", "MacOS", "restart.marker");
        public MacUpdateJournal Journal { get; }

        private static void CreateApp(string bundlePath, string version, string binary, string? failureMode)
        {
            string contents = Path.Combine(bundlePath, "Contents");
            string macOS = Path.Combine(contents, "MacOS");
            Directory.CreateDirectory(macOS);
            File.Copy(binary, Path.Combine(macOS, "Umamusume.Avalonia"));
            if (OperatingSystem.IsMacOS())
                File.SetUnixFileMode(Path.Combine(macOS, "Umamusume.Avalonia"),
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var plist = new XDocument(new XElement("plist", new XAttribute("version", "1.0"),
                new XElement("dict",
                    new XElement("key", "CFBundleIdentifier"), new XElement("string", "com.uma.test"),
                    new XElement("key", "CFBundleShortVersionString"), new XElement("string", version))));
            plist.Save(Path.Combine(contents, "Info.plist"));
            if (failureMode is not null) File.WriteAllText(Path.Combine(macOS, "health-" + failureMode), "1");
        }

        private static void Sign(string bundlePath) => RunTool("/usr/bin/codesign", "--force", "--deep", "-s", "-", bundlePath);

        private static void RunTool(string executable, params string[] arguments)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException($"Could not start {executable}.");
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(executable)} failed: {error}");
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
