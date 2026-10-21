using Xunit;

namespace Umamusume.MacUpdater.Tests;

public sealed class MacBundleSwapTransactionTests
{
    [Fact]
    public async Task HealthyInstallCommitsBeforeBestEffortBackupCleanup()
    {
        using var fixture = new TransactionFixture();
        fixture.Host.PreserveFailure = new IOException("cleanup failed");

        int result = await fixture.Transaction.InstallAsync(fixture.JournalPath, fixture.OperationRoot,
            fixture.Journal, TimeSpan.FromSeconds(1));

        Assert.Equal(0, result);
        Assert.Equal("2.0.0", fixture.Host.VersionAt(fixture.AppPath));
        Assert.Equal("healthy", fixture.Host.SavedStages.Last());
        Assert.Equal(1, fixture.Host.LaunchCount);
        Assert.Equal(0, fixture.Host.RestartCount);
        Assert.False(fixture.Host.LaunchedApp!.WasStopped);
    }

    [Theory]
    [InlineData("launch")]
    [InlineData("negative-health")]
    [InlineData("timeout")]
    public async Task LaunchOrHealthFailureRestoresAndRestartsPreviousBundle(string failure)
    {
        using var fixture = new TransactionFixture();
        if (failure == "launch") fixture.Host.LaunchFailure = new InvalidOperationException("launch failed");
        if (failure == "negative-health")
            fixture.Host.Health = new MacHealthResult(fixture.Journal.OperationId, fixture.Journal.Nonce, false, "catalog failed");
        if (failure == "timeout") fixture.Host.Health = null;

        int result = await fixture.Transaction.InstallAsync(fixture.JournalPath, fixture.OperationRoot,
            fixture.Journal, TimeSpan.FromMilliseconds(10));

        Assert.Equal(2, result);
        Assert.Equal("1.0.0", fixture.Host.VersionAt(fixture.AppPath));
        Assert.Equal("rolled-back", fixture.Host.SavedStages.Last());
        Assert.Equal(1, fixture.Host.RestartCount);
        Assert.False(fixture.Host.Results.Last().Healthy);
        if (failure == "launch") Assert.Null(fixture.Host.LaunchedApp);
        else Assert.True(fixture.Host.LaunchedApp!.WasStopped);
    }

    [Fact]
    public async Task JournalFailureImmediatelyAfterAtomicSwapIsDetectedAndRolledBack()
    {
        using var fixture = new TransactionFixture();
        fixture.Host.FailJournalStage = "swapped";

        int result = await fixture.Transaction.InstallAsync(fixture.JournalPath, fixture.OperationRoot,
            fixture.Journal, TimeSpan.FromSeconds(1));

        Assert.Equal(2, result);
        Assert.Equal(2, fixture.Host.SwapCount);
        Assert.Equal("1.0.0", fixture.Host.VersionAt(fixture.AppPath));
        Assert.Equal(1, fixture.Host.RestartCount);
    }

    [Fact]
    public async Task StartupRecoveryReconcilesSwapCompletedBeforeJournalAdvanced()
    {
        using var fixture = new TransactionFixture();
        fixture.Host.SetVersion(fixture.AppPath, "2.0.0");
        fixture.Host.SetVersion(fixture.CandidatePath, "1.0.0");
        await fixture.Transaction.RecoverInterruptedSwapAsync(fixture.JournalPath, fixture.OperationRoot,
            fixture.Journal with { Stage = "swapping" });

        Assert.Equal("1.0.0", fixture.Host.VersionAt(fixture.AppPath));
        Assert.Equal("2.0.0", fixture.Host.VersionAt(fixture.CandidatePath));
        Assert.Equal("rolled-back", fixture.Host.SavedStages.Last());
        Assert.Equal(1, fixture.Host.RestartCount);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Transaction.RecoverInterruptedSwapAsync(
            fixture.JournalPath, fixture.OperationRoot, fixture.Journal with { Stage = "rolled-back" }));
    }

    private sealed class TransactionFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "uma-mac-swap-test-" + Guid.NewGuid().ToString("N"));

        public TransactionFixture()
        {
            Directory.CreateDirectory(_root);
            OperationRoot = Path.Combine(_root, "operation");
            Directory.CreateDirectory(OperationRoot);
            AppPath = Path.Combine(_root, "UmamusumeAss.app");
            CandidatePath = Path.Combine(_root, "UmamusumeAss.app.update-new", "UmamusumeAss.app");
            Directory.CreateDirectory(AppPath);
            Directory.CreateDirectory(CandidatePath);
            JournalPath = Path.Combine(OperationRoot, "journal.json");
            Journal = new MacUpdateJournal("0123456789abcdef0123456789abcdef", new string('A', 64), "prepared",
                AppPath, CandidatePath, Path.Combine(_root, "UmamusumeAss.app.previous"), "2.0.0", "1.0.0",
                new string('B', 64), new string('C', 64), 1234);
            Host = new FakeHost(AppPath, CandidatePath);
            Transaction = new MacBundleSwapTransaction(Host);
        }

        public string OperationRoot { get; }
        public string AppPath { get; }
        public string CandidatePath { get; }
        public string JournalPath { get; }
        public MacUpdateJournal Journal { get; }
        public FakeHost Host { get; }
        public MacBundleSwapTransaction Transaction { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeHost(string appPath, string candidatePath) : IMacBundleSwapHost
    {
        private readonly Dictionary<string, string> _versions = new(StringComparer.Ordinal)
        {
            [appPath] = "1.0.0",
            [candidatePath] = "2.0.0",
        };

        public List<string> SavedStages { get; } = [];
        public List<MacHealthResult> Results { get; } = [];
        public int SwapCount { get; private set; }
        public int LaunchCount { get; private set; }
        public int RestartCount { get; private set; }
        public bool PreserveCalled { get; private set; }
        public Exception? LaunchFailure { get; set; }
        public Exception? PreserveFailure { get; set; }
        public string? FailJournalStage { get; set; }
        public MacHealthResult? Health { get; set; } = new("0123456789abcdef0123456789abcdef", new string('A', 64), true, null);
        public FakeLaunchedApp? LaunchedApp { get; private set; }

        public string VersionAt(string path) => _versions[path];
        public void SetVersion(string path, string version) => _versions[path] = version;
        public string ReadBundleVersion(string path) => _versions[path];
        public bool TrySwap(string first, string second, out int error)
        {
            error = 0;
            (_versions[first], _versions[second]) = (_versions[second], _versions[first]);
            SwapCount++;
            return true;
        }
        public Task VerifyCodesignAsync(string appPath) => Task.CompletedTask;
        public Task SaveJournalAsync(string path, MacUpdateJournal journal)
        {
            if (journal.Stage == FailJournalStage) throw new IOException("injected journal failure");
            SavedStages.Add(journal.Stage);
            return Task.CompletedTask;
        }
        public Task<IMacLaunchedApplication> LaunchUpdatedAppAsync(string appPath, string operationRoot, string operationId, string nonce)
        {
            LaunchCount++;
            if (LaunchFailure is not null) throw LaunchFailure;
            LaunchedApp = new FakeLaunchedApp(Health);
            return Task.FromResult<IMacLaunchedApplication>(LaunchedApp);
        }
        public Task RestartPreviousAppAsync(string path)
        {
            RestartCount++;
            return Task.CompletedTask;
        }
        public Task PreservePreviousBundleAsync(string candidate, string backup)
        {
            PreserveCalled = true;
            if (PreserveFailure is not null) throw PreserveFailure;
            _versions[backup] = _versions[candidate];
            _versions.Remove(candidate);
            return Task.CompletedTask;
        }
        public Task WriteResultAsync(string operationRoot, MacHealthResult result)
        {
            Results.Add(result);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLaunchedApp(MacHealthResult? result) : IMacLaunchedApplication
    {
        public bool WasStopped { get; private set; }
        public Task<MacHealthResult?> WaitForHealthAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(result);
        public Task StopAsync()
        {
            WasStopped = true;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
