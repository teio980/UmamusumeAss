using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace UmamusumeWpfGui.Tests.Packaging;






public sealed class PackageLayoutTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _solutionRoot;
    private bool _disposed;

    public PackageLayoutTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "UmaAssPkgTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);



        _solutionRoot = ResolveSolutionRoot();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                "[PackageLayoutTests] Cleanup failed for {0}: {1}",
                _tempRoot, ex);
        }
    }





    private static string ResolveSolutionRoot()
    {


        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "CMakePresets.json")))
            {
                return dir;
            }
            var parent = Directory.GetParent(dir);
            if (parent is null)
                break;
            dir = parent.FullName;
        }

        throw new InvalidOperationException(
            $"Could not locate solution root from {AppContext.BaseDirectory}");
    }

    private static string FindPowerShell()
    {


        var ps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(ps))
            return ps;


        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var p in paths)
        {
            var candidate = Path.Combine(p.Trim(), "pwsh.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "Could not locate PowerShell (powershell.exe or pwsh.exe).");
    }

    private static (int ExitCode, string StdOut, string StdErr) RunProcess(
        string executable, string arguments, string workingDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            }
        };

        process.Start();




        var stdoutTask = Task.Run(() => process.StandardOutput.ReadToEnd());
        var stderrTask = Task.Run(() => process.StandardError.ReadToEnd());

        var timeoutMs = (int)TimeSpan.FromMinutes(10).TotalMilliseconds;
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) {   }


            Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(30));

            throw new TimeoutException(
                $"Process timed out after 10 minutes: {executable} {arguments}\n" +
                $"Partial STDOUT:\n{stdoutTask.Result}\n" +
                $"Partial STDERR:\n{stderrTask.Result}");
        }


        Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(30));

        return (process.ExitCode, stdoutTask.Result ?? "", stderrTask.Result ?? "");
    }





    private static readonly string[] RequiredEntries =
    [
        "UmamusumeAss.exe",
        "UmamusumeCore.dll",
        "Umamusume.CoreBridge.dll",
        "resource/hachimi/ura/manifest.json",
        "resource/hachimi/ura/screens/captures/debut_race_result_wait.png",
    ];


    private static readonly string[] RuntimeEvidence =
    [
        "hostfxr.dll",
        "System.Private.CoreLib.dll",
    ];


    private static readonly string[] ForbiddenRootEntries =
    [
        "lib/UmamusumeCore.lib",
    ];






    private static readonly string[] VcRedistDllNames =
    [
        "vcruntime140.dll",
        "vcruntime140_1.dll",
        "vcruntime140d.dll",
        "msvcp140.dll",
        "msvcp140_1.dll",
        "msvcp140_2.dll",
        "msvcp140d.dll",
        "concrt140.dll",
        "concrt140d.dll",
    ];





    [Fact]
    public void PackageScript_Exists()
    {
        var scriptPath = Path.Combine(_solutionRoot, "tools", "package.ps1");
        Assert.True(File.Exists(scriptPath),
            $"package.ps1 not found at expected path: {scriptPath}");
    }

    [Fact]
    public void PackageScript_ProducesValidZipWithCorrectLayout()
    {

        var scriptPath = Path.Combine(_solutionRoot, "tools", "package.ps1");
        Assert.True(File.Exists(scriptPath),
            $"package.ps1 not found at expected path: {scriptPath}");

        var outputDir = Path.Combine(_tempRoot, "output");
        Directory.CreateDirectory(outputDir);

        var powershell = FindPowerShell();



        var psArgs = $"-NoProfile -ExecutionPolicy Bypass -Command \"& '{scriptPath}' -OutputDirectory '{outputDir}' -ErrorAction Stop\"";


        var (exitCode, stdOut, stdErr) = RunProcess(powershell, psArgs, _solutionRoot);


        Assert.True(exitCode == 0,
            $"package.ps1 exited with code {exitCode}.\nSTDOUT:\n{stdOut}\nSTDERR:\n{stdErr}");


        var zipFiles = Directory.GetFiles(outputDir, "UmamusumeAss-win-x64.zip", SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(zipFiles);
        Assert.Single(zipFiles);
        var zipPath = zipFiles[0];


        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries;


        foreach (var required in RequiredEntries)
        {
            var match = entries.FirstOrDefault(e =>
                string.Equals(e.FullName.Replace('\\', '/'), required, StringComparison.Ordinal));
            Assert.True(match is not null,
                $"Required entry '{required}' not found in archive. " +
                $"Entries: [{string.Join(", ", entries.Select(e => e.FullName))}]");


            if (!required.EndsWith('/'))
            {
                Assert.True(match!.Length > 0,
                    $"Required entry '{required}' has zero length.");
            }
        }


        var hasRuntime = RuntimeEvidence.Any(evidence =>
            entries.Any(e =>
                string.Equals(e.FullName.Replace('\\', '/'), evidence, StringComparison.Ordinal)));
        Assert.True(hasRuntime,
            $"No self-contained runtime evidence found. " +
            $"Expected at least one of: [{string.Join(", ", RuntimeEvidence)}]. " +
            $"Entries: [{string.Join(", ", entries.Select(e => e.FullName))}]");


        foreach (var forbidden in ForbiddenRootEntries)
        {
            var found = entries.Any(e =>
                string.Equals(e.FullName.Replace('\\', '/'), forbidden, StringComparison.Ordinal));
            Assert.False(found,
                $"Forbidden entry '{forbidden}' found in archive.");
        }


        var vcRedistEntries = entries
            .Where(e => VcRedistDllNames.Contains(
                e.Name, StringComparer.OrdinalIgnoreCase))
            .Select(e => e.FullName)
            .ToList();
        Assert.False(vcRedistEntries.Count != 0,
            $"VC++ redistributable DLLs found in archive — /MT static linking is not effective: " +
            string.Join(", ", vcRedistEntries));


        var coreDll = entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/'), "UmamusumeCore.dll", StringComparison.Ordinal));
        Assert.True(coreDll is not null, "UmamusumeCore.dll must be at archive root.");
        Assert.True(coreDll!.Length > 0, "UmamusumeCore.dll must not be empty.");
    }
}
