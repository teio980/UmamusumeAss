using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace UmamusumeWpfGui.Tests.Packaging;

/// <summary>
/// Validates that tools/package.ps1 produces a portable ZIP with the
/// correct archive layout.  The test first asserts the script exists,
/// then runs it in a temporary output root and inspects every entry.
/// </summary>
public sealed class PackageLayoutTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _solutionRoot;
    private bool _disposed;

    public PackageLayoutTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "UmaAssPkgTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        // Walk up from the test assembly to find the solution root
        // (the directory containing CMakeLists.txt or UmamusumeAss.sln).
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

    // ================================================================
    // Helpers
    // ================================================================

    private static string ResolveSolutionRoot()
    {
        // Use CMakePresets.json as the root marker — it exists only at the
        // project root, not in tests/ or other subdirectories.
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
        // Windows: powershell.exe is always at a known location.
        // Fall back to PATH search for pwsh.exe (PowerShell Core).
        var ps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(ps))
            return ps;

        // Try pwsh.exe from PATH
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

        // Drain both streams concurrently to prevent pipe deadlock:
        // if one pipe buffer fills while we read the other sequentially,
        // the child process blocks and neither stream makes progress.
        var stdoutTask = Task.Run(() => process.StandardOutput.ReadToEnd());
        var stderrTask = Task.Run(() => process.StandardError.ReadToEnd());

        var timeoutMs = (int)TimeSpan.FromMinutes(10).TotalMilliseconds;
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }

            // Still wait for the readers so we can report partial output.
            Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(30));

            throw new TimeoutException(
                $"Process timed out after 10 minutes: {executable} {arguments}\n" +
                $"Partial STDOUT:\n{stdoutTask.Result}\n" +
                $"Partial STDERR:\n{stderrTask.Result}");
        }

        // Process exited normally — await the readers to finish.
        Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(30));

        return (process.ExitCode, stdoutTask.Result ?? "", stderrTask.Result ?? "");
    }

    // ================================================================
    // Required archive entries
    // ================================================================

    private static readonly string[] RequiredEntries =
    [
        "UmamusumeAss.exe",
        "UmamusumeCore.dll",
        "Umamusume.CoreBridge.dll",
        "resource/connection.json",
    ];

    // Self-contained runtime evidence — at least one of these must be present.
    private static readonly string[] RuntimeEvidence =
    [
        "hostfxr.dll",
        "System.Private.CoreLib.dll",
    ];

    // Paths that MUST NOT appear in the archive root (conflicting layout).
    private static readonly string[] ForbiddenRootEntries =
    [
        "lib/UmamusumeCore.lib",
    ];

    // VC++ redistributable DLL names.  With /MT static linking none of
    // these should appear in the portable ZIP — the archive must be
    // deployable to a clean Windows machine without a VC++ redistributable.
    // Note: vcruntime140_cor3.dll is a .NET-specific hosting DLL, not an
    // MSVC redistributable DLL, so it is intentionally excluded here.
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

    // ================================================================
    // Tests
    // ================================================================

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
        // Arrange
        var scriptPath = Path.Combine(_solutionRoot, "tools", "package.ps1");
        Assert.True(File.Exists(scriptPath),
            $"package.ps1 not found at expected path: {scriptPath}");

        var outputDir = Path.Combine(_tempRoot, "output");
        Directory.CreateDirectory(outputDir);

        var powershell = FindPowerShell();

        // Build the PowerShell command that invokes package.ps1 with the
        // test output directory.  The script is expected to accept -OutputDirectory.
        var psArgs = $"-NoProfile -ExecutionPolicy Bypass -Command \"& '{scriptPath}' -OutputDirectory '{outputDir}' -ErrorAction Stop\"";

        // Act
        var (exitCode, stdOut, stdErr) = RunProcess(powershell, psArgs, _solutionRoot);

        // Assert — script must succeed
        Assert.True(exitCode == 0,
            $"package.ps1 exited with code {exitCode}.\nSTDOUT:\n{stdOut}\nSTDERR:\n{stdErr}");

        // Find the produced ZIP
        var zipFiles = Directory.GetFiles(outputDir, "UmamusumeAss-win-x64.zip", SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(zipFiles);
        Assert.Single(zipFiles);
        var zipPath = zipFiles[0];

        // Inspect archive entries
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries;

        // Every required entry must be present
        foreach (var required in RequiredEntries)
        {
            var match = entries.FirstOrDefault(e =>
                string.Equals(e.FullName.Replace('\\', '/'), required, StringComparison.Ordinal));
            Assert.True(match is not null,
                $"Required entry '{required}' not found in archive. " +
                $"Entries: [{string.Join(", ", entries.Select(e => e.FullName))}]");

            // Non-directory entries must have non-zero length
            if (!required.EndsWith('/'))
            {
                Assert.True(match!.Length > 0,
                    $"Required entry '{required}' has zero length.");
            }
        }

        // At least one runtime evidence file must be present
        var hasRuntime = RuntimeEvidence.Any(evidence =>
            entries.Any(e =>
                string.Equals(e.FullName.Replace('\\', '/'), evidence, StringComparison.Ordinal)));
        Assert.True(hasRuntime,
            $"No self-contained runtime evidence found. " +
            $"Expected at least one of: [{string.Join(", ", RuntimeEvidence)}]. " +
            $"Entries: [{string.Join(", ", entries.Select(e => e.FullName))}]");

        // Forbidden root entries must not appear
        foreach (var forbidden in ForbiddenRootEntries)
        {
            var found = entries.Any(e =>
                string.Equals(e.FullName.Replace('\\', '/'), forbidden, StringComparison.Ordinal));
            Assert.False(found,
                $"Forbidden entry '{forbidden}' found in archive.");
        }

        // No VC++ redistributable DLLs — /MT static linking must be effective
        var vcRedistEntries = entries
            .Where(e => VcRedistDllNames.Contains(
                e.Name, StringComparer.OrdinalIgnoreCase))
            .Select(e => e.FullName)
            .ToList();
        Assert.False(vcRedistEntries.Count != 0,
            $"VC++ redistributable DLLs found in archive — /MT static linking is not effective: " +
            string.Join(", ", vcRedistEntries));

        // UmamusumeCore.dll must be at the root, not in a subdirectory
        var coreDll = entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/'), "UmamusumeCore.dll", StringComparison.Ordinal));
        Assert.True(coreDll is not null, "UmamusumeCore.dll must be at archive root.");
        Assert.True(coreDll!.Length > 0, "UmamusumeCore.dll must not be empty.");

        // resource/connection.json must be exactly one directory beneath root
        var resourceEntry = entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/'), "resource/connection.json", StringComparison.Ordinal));
        Assert.True(resourceEntry is not null, "resource/connection.json must be present.");
        Assert.True(resourceEntry!.Length > 0, "resource/connection.json must not be empty.");
    }
}