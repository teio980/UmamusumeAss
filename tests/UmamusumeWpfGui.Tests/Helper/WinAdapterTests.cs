using System;
using System.Linq;
using UmamusumeWpfGui.Helper;
using Xunit;

namespace UmamusumeWpfGui.Tests.Helper;

public sealed class WinAdapterTests
{
    // ================================================================
    // RefreshEmulatorsInfo — process table matching
    // ================================================================

    [Fact]
    public void RefreshEmulatorsInfo_NoProcesses_ReturnsEmptyCandidates()
    {
        var adapter = CreateAdapter(processes: []);
        var result = adapter.RefreshEmulatorsInfo();
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void RefreshEmulatorsInfo_HDPlayerWithAdb_FindsBlueStacksCandidate()
    {
        var adapter = CreateAdapter(processes:
        [
            new("HD-Player", @"C:\Program Files\BlueStacks_nxt\HD-Player.exe")
        ]);
        var result = adapter.RefreshEmulatorsInfo();

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("BlueStacks", candidate.EmulatorName);
        Assert.NotNull(candidate.AdbPath);
        Assert.EndsWith("HD-Adb.exe", candidate.AdbPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BlueStacks_nxt", candidate.AdbPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefreshEmulatorsInfo_HDPlayerWithoutAdbFile_ReportsNullAdbPath()
    {
        var adapter = CreateAdapter(processes:
        [
            new("HD-Player", @"C:\BlueStacks\HD-Player.exe")
        ],
        existingFiles: []); // no HD-Adb.exe anywhere

        var result = adapter.RefreshEmulatorsInfo();
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("BlueStacks", candidate.EmulatorName);
        Assert.Null(candidate.AdbPath);
    }

    [Fact]
    public void RefreshEmulatorsInfo_HDPlayerWithEngineFallback_UsesAltPath()
    {
        // Primary HD-Adb.exe missing, fallback Engine\\ProgramFiles\\HD-Adb.exe exists
        var adapter = CreateAdapter(processes:
        [
            new("HD-Player", @"C:\BlueStacks\HD-Player.exe")
        ],
        existingFiles:
        [
            @"C:\BlueStacks\Engine\ProgramFiles\HD-Adb.exe"
        ]);

        var result = adapter.RefreshEmulatorsInfo();
        var candidate = Assert.Single(result.Candidates);
        Assert.NotNull(candidate.AdbPath);
        Assert.EndsWith(@"Engine\ProgramFiles\HD-Adb.exe", candidate.AdbPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefreshEmulatorsInfo_DnplayerWithAdb_FindsLDPlayerCandidate()
    {
        var adapter = CreateAdapter(processes:
        [
            new("dnplayer", @"D:\LDPlayer\dnplayer.exe")
        ]);
        var result = adapter.RefreshEmulatorsInfo();

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("LDPlayer", candidate.EmulatorName);
        Assert.NotNull(candidate.AdbPath);
        Assert.EndsWith("adb.exe", candidate.AdbPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LDPlayer", candidate.AdbPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefreshEmulatorsInfo_NoxWithNoxAdb_FindsNoxCandidate()
    {
        var adapter = CreateAdapter(processes:
        [
            new("Nox", @"E:\Nox\bin\Nox.exe")
        ]);
        var result = adapter.RefreshEmulatorsInfo();

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Nox", candidate.EmulatorName);
        Assert.NotNull(candidate.AdbPath);
        Assert.EndsWith("nox_adb.exe", candidate.AdbPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nox", candidate.AdbPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefreshEmulatorsInfo_NoxWithoutNoxAdb_ReportsNullAdbPath()
    {
        var adapter = CreateAdapter(processes:
        [
            new("Nox", @"E:\Nox\bin\Nox.exe")
        ],
        existingFiles: []);

        var result = adapter.RefreshEmulatorsInfo();
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Nox", candidate.EmulatorName);
        Assert.Null(candidate.AdbPath);
    }

    [Fact]
    public void RefreshEmulatorsInfo_MuMuPlayerWithNxMainAdb_FindsMuMuCandidate()
    {
        var adapter = CreateAdapter(processes:
        [
            new("MuMuPlayer", @"F:\MuMu\emulator\nemushell\MuMuPlayer.exe")
        ]);
        // Relative: ..\..\..\nx_main\adb.exe from emulator\nemushell -> MuMu\nx_main\adb.exe
        var result = adapter.RefreshEmulatorsInfo();

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("MuMuEmulator12", candidate.EmulatorName);
        Assert.NotNull(candidate.AdbPath);
        Assert.Contains("nx_main", candidate.AdbPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefreshEmulatorsInfo_MuMuNxDevice_FindsMuMuCandidate()
    {
        var adapter = CreateAdapter(processes:
        [
            new("MuMuNxDevice", @"F:\MuMu\emulator\nemushell\MuMuNxDevice.exe")
        ]);
        var result = adapter.RefreshEmulatorsInfo();

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("MuMuEmulator12", candidate.EmulatorName);
        Assert.NotNull(candidate.AdbPath);
    }

    [Fact]
    public void RefreshEmulatorsInfo_MEmuWithAdb_FindsXYAZCandidate()
    {
        var adapter = CreateAdapter(processes:
        [
            new("MEmu", @"G:\MEmu\MEmu.exe")
        ]);
        var result = adapter.RefreshEmulatorsInfo();

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("XYAZ", candidate.EmulatorName);
        Assert.NotNull(candidate.AdbPath);
        Assert.EndsWith("adb.exe", candidate.AdbPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefreshEmulatorsInfo_MultipleKnownEmulators_ReturnsAllCandidates()
    {
        var adapter = CreateAdapter(processes:
        [
            new("HD-Player", @"C:\BlueStacks\HD-Player.exe"),
            new("dnplayer", @"D:\LDPlayer\dnplayer.exe"),
            new("Nox", @"E:\Nox\bin\Nox.exe"),
        ]);
        var result = adapter.RefreshEmulatorsInfo();

        Assert.Equal(3, result.Candidates.Count);
        Assert.Contains(result.Candidates, c => c.EmulatorName == "BlueStacks");
        Assert.Contains(result.Candidates, c => c.EmulatorName == "LDPlayer");
        Assert.Contains(result.Candidates, c => c.EmulatorName == "Nox");
    }

    [Fact]
    public void RefreshEmulatorsInfo_DuplicateResolvedPath_Deduplicates()
    {
        // Two HD-Player processes pointing to the same BlueStacks install
        var adapter = CreateAdapter(processes:
        [
            new("HD-Player", @"C:\BlueStacks\HD-Player.exe"),
            new("HD-Player", @"C:\BlueStacks\HD-Player.exe"),
        ]);
        var result = adapter.RefreshEmulatorsInfo();

        Assert.Single(result.Candidates);
        Assert.Equal("BlueStacks", result.Candidates[0].EmulatorName);
    }

    [Fact]
    public void RefreshEmulatorsInfo_UnknownProcess_ReportsDiagnostic()
    {
        var adapter = CreateAdapter(processes:
        [
            new("SomeRandomProcess", @"C:\Random\app.exe")
        ]);
        var result = adapter.RefreshEmulatorsInfo();

        Assert.Empty(result.Candidates);
        Assert.Contains(result.Diagnostics, d =>
            d.Message.Contains("SomeRandomProcess", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RefreshEmulatorsInfo_InaccessibleProcess_ReportsDiagnostic()
    {
        var adapter = CreateAdapter(processes:
        [
            new("HD-Player", null) // inaccessible process
        ]);
        var result = adapter.RefreshEmulatorsInfo();

        // Process with null main-module path should still be recognized
        // but may have null AdbPath since we can't derive the directory
        Assert.Empty(result.Candidates);
        Assert.NotEmpty(result.Diagnostics);
    }

    // ================================================================
    // GetAdbDevices — exact `adb devices` parsing
    // ================================================================

    [Fact]
    public void GetAdbDevices_EmptyOutput_ReturnsEmptyRecords()
    {
        var adapter = CreateAdapterWithAdbRunner(outputs:
        [
            ("", "", 0, false, null)
        ]);
        var result = adapter.GetAdbDevices(@"C:\adb\adb.exe");

        Assert.Empty(result.Records);
    }

    [Fact]
    public void GetAdbDevices_HeaderOnly_ReturnsEmptyRecords()
    {
        var adapter = CreateAdapterWithAdbRunner(outputs:
        [
            ("List of devices attached\n", "", 0, false, null)
        ]);
        var result = adapter.GetAdbDevices(@"C:\adb\adb.exe");

        Assert.Empty(result.Records);
    }

    [Fact]
    public void GetAdbDevices_SingleDevice_ParsesSerialAndState()
    {
        var adapter = CreateAdapterWithAdbRunner(outputs:
        [
            ("List of devices attached\nemulator-5554\tdevice\n", "", 0, false, null)
        ]);
        var result = adapter.GetAdbDevices(@"C:\adb\adb.exe");

        var record = Assert.Single(result.Records);
        Assert.Equal("emulator-5554", record.Serial);
        Assert.Equal("device", record.State);
    }

    [Fact]
    public void GetAdbDevices_OfflineState_PreservesOffline()
    {
        var adapter = CreateAdapterWithAdbRunner(outputs:
        [
            ("List of devices attached\n127.0.0.1:5555\toffline\n", "", 0, false, null)
        ]);
        var result = adapter.GetAdbDevices(@"C:\adb\adb.exe");

        var record = Assert.Single(result.Records);
        Assert.Equal("127.0.0.1:5555", record.Serial);
        Assert.Equal("offline", record.State);
    }

    [Fact]
    public void GetAdbDevices_UnauthorizedState_PreservesUnauthorized()
    {
        var adapter = CreateAdapterWithAdbRunner(outputs:
        [
            ("List of devices attached\n192.168.1.100:5555\tunauthorized\n", "", 0, false, null)
        ]);
        var result = adapter.GetAdbDevices(@"C:\adb\adb.exe");

        var record = Assert.Single(result.Records);
        Assert.Equal("192.168.1.100:5555", record.Serial);
        Assert.Equal("unauthorized", record.State);
    }

    [Fact]
    public void GetAdbDevices_MultipleDevices_ParsesAllRecords()
    {
        var adapter = CreateAdapterWithAdbRunner(outputs:
        [
            ("List of devices attached\nemulator-5554\tdevice\nemulator-5556\tdevice\n127.0.0.1:5555\tdevice\n",
             "", 0, false, null)
        ]);
        var result = adapter.GetAdbDevices(@"C:\adb\adb.exe");

        Assert.Equal(3, result.Records.Count);
        Assert.Contains(result.Records, r => r.Serial == "emulator-5554" && r.State == "device");
        Assert.Contains(result.Records, r => r.Serial == "emulator-5556" && r.State == "device");
        Assert.Contains(result.Records, r => r.Serial == "127.0.0.1:5555" && r.State == "device");
    }

    [Fact]
    public void GetAdbDevices_MixedStates_ParsesEachExactly()
    {
        var adapter = CreateAdapterWithAdbRunner(outputs:
        [
            ("List of devices attached\ns1\tdevice\ns2\toffline\ns3\tunauthorized\ns4\tdevice\n",
             "", 0, false, null)
        ]);
        var result = adapter.GetAdbDevices(@"C:\adb\adb.exe");

        Assert.Equal(4, result.Records.Count);
        Assert.Equal("device", result.Records[0].State);
        Assert.Equal("offline", result.Records[1].State);
        Assert.Equal("unauthorized", result.Records[2].State);
        Assert.Equal("device", result.Records[3].State);
    }

    [Fact]
    public void GetAdbDevices_MalformedLine_ReportsDiagnostic()
    {
        var adapter = CreateAdapterWithAdbRunner(outputs:
        [
            ("List of devices attached\nemulator-5554\tdevice\nthis_line_has_no_tab\n", "", 0, false, null)
        ]);
        var result = adapter.GetAdbDevices(@"C:\adb\adb.exe");

        // Valid record is still parsed
        Assert.Single(result.Records);
        Assert.Equal("emulator-5554", result.Records[0].Serial);

        // Malformed line produces diagnostic
        Assert.Contains(result.Diagnostics, d =>
            d.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase) ||
            d.Message.Contains("unexpected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetAdbDevices_NonZeroExitCode_ReportsDiagnostic()
    {
        var adapter = CreateAdapterWithAdbRunner(outputs:
        [
            ("error: no devices/emulators found", "adb: failed", 1, false, null)
        ]);
        var result = adapter.GetAdbDevices(@"C:\adb\adb.exe");

        Assert.Empty(result.Records);
        Assert.Contains(result.Diagnostics, d =>
            d.Message.Contains("exit code", StringComparison.OrdinalIgnoreCase) ||
            d.Message.Contains("non-zero", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetAdbDevices_StderrOutput_ReportsDiagnostic()
    {
        var adapter = CreateAdapterWithAdbRunner(outputs:
        [
            ("List of devices attached\n", "adb server outdated", 0, false, null)
        ]);
        var result = adapter.GetAdbDevices(@"C:\adb\adb.exe");

        Assert.Empty(result.Records);
        Assert.Contains(result.Diagnostics, d =>
            d.Message.Contains("stderr", StringComparison.OrdinalIgnoreCase) ||
            d.Message.Contains("adb server", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetAdbDevices_TimedOut_ReportsDiagnostic()
    {
        var adapter = CreateAdapterWithAdbRunner(outputs:
        [
            ("", "", 0, true, null)
        ]);
        var result = adapter.GetAdbDevices(@"C:\adb\adb.exe");

        Assert.Empty(result.Records);
        Assert.Contains(result.Diagnostics, d =>
            d.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            d.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetAdbDevices_Exception_ReportsDiagnostic()
    {
        var adapter = CreateAdapterWithAdbRunner(outputs:
        [
            ("", "", 0, false, new InvalidOperationException("ADB crashed"))
        ]);
        var result = adapter.GetAdbDevices(@"C:\adb\adb.exe");

        Assert.Empty(result.Records);
        Assert.Contains(result.Diagnostics, d =>
            d.Message.Contains("ADB crashed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetAdbDevices_BlankLines_AreSkipped()
    {
        var adapter = CreateAdapterWithAdbRunner(outputs:
        [
            ("List of devices attached\n\ndevice1\tdevice\n\n\ndevice2\tdevice\n", "", 0, false, null)
        ]);
        var result = adapter.GetAdbDevices(@"C:\adb\adb.exe");

        Assert.Equal(2, result.Records.Count);
    }

    // ================================================================
    // Factory helpers
    // ================================================================

    /// <summary>
    /// Creates a WinAdapter with a fake process enumerator and a real
    /// WinAdapter-internal file-existence check that consults the given set.
    /// </summary>
    private static WinAdapter CreateAdapter(
        ProcessEntry[] processes,
        string[]? existingFiles = null)
    {
        var fakeProcEnum = new FakeProcessEnumerator(processes);
        var fakeAdbRunner = new FakeAdbRunner([]);
        return new WinAdapter(
            fakeProcEnum,
            fakeAdbRunner,
            new FakeFileSystem(existingFiles));
    }

    /// <summary>
    /// Creates a WinAdapter with a fake ADB runner that returns the given outputs.
    /// </summary>
    private static WinAdapter CreateAdapterWithAdbRunner(
        params (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error)[] outputs)
    {
        var fakeProcEnum = new FakeProcessEnumerator([]);
        var fakeAdbRunner = new FakeAdbRunner(outputs);
        return new WinAdapter(
            fakeProcEnum,
            fakeAdbRunner,
            new FakeFileSystem(null));
    }

    // ================================================================
    // Test fakes
    // ================================================================

    private sealed class FakeProcessEnumerator : IProcessEnumerator
    {
        private readonly ProcessEntry[] _entries;

        public FakeProcessEnumerator(ProcessEntry[] entries) => _entries = entries;

        public ProcessEntry[] GetProcesses() => _entries;
    }

    private sealed class FakeAdbRunner : IAdbRunner
    {
        private readonly (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error)[] _outputs;
        private int _callIndex;

        public FakeAdbRunner(
            (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error)[] outputs)
        {
            _outputs = outputs;
        }

        public (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error) RunDevices(string adbPath)
        {
            var result = _outputs[_callIndex];
            _callIndex++;
            return result;
        }
    }

    private sealed class FakeFileSystem : IFileSystem
    {
        private readonly string[]? _existingFiles;

        public FakeFileSystem(string[]? existingFiles) => _existingFiles = existingFiles;

        public bool FileExists(string path)
        {
            if (_existingFiles == null)
                return true; // default: everything exists
            return _existingFiles.Contains(path, StringComparer.OrdinalIgnoreCase);
        }
    }
}
