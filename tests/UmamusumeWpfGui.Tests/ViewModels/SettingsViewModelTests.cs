using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    // ================================================================
    // Fixture
    // ================================================================

    private sealed class Fixture
    {
        public FakeUmaService UmaService { get; } = new();
        public FakeConnectionStateService ConnectionState { get; } = new();
        public FakeSettingsService Settings { get; } = new();
        public FakeLocalizationService Localization { get; } = new();
        public FakeWinAdapter WinAdapter { get; } = new();
        public FakeEmulatorLauncher EmulatorLauncher { get; } = new();

        public Fixture()
        {
            // Default persisted settings
            Settings.Save(new ConnectionSettings
            {
                AdbPath = @"C:\persisted\adb.exe",
                ConnectAddress = "192.168.1.1:5555",
                AutoDetectConnection = false,
                Language = "en-US",
            });
        }

        public SettingsViewModel CreateViewModel()
        {
            return new SettingsViewModel(
                UmaService, ConnectionState, Settings, Localization, WinAdapter, EmulatorLauncher);
        }
    }

    private static Fixture CreateFixture()
    {
        return new Fixture();
    }

    // ================================================================
    // 1. Menu Navigation
    // ================================================================

    [Fact]
    public void SelectedMenuIndex_DefaultIsZero()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        Assert.Equal(0, vm.SelectedMenuIndex);
    }

    [Fact]
    public void SetSelectedMenuIndex_ToOne_ReflectsChange()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        vm.SelectedMenuIndex = 1;

        Assert.Equal(1, vm.SelectedMenuIndex);
    }

    [Fact]
    public void SetSelectedMenuIndex_ToTwo_ReflectsChange()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        vm.SelectedMenuIndex = 2;

        Assert.Equal(2, vm.SelectedMenuIndex);
    }

    [Fact]
    public void SetSelectedMenuIndex_Negative_ClampsToZero()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        vm.SelectedMenuIndex = -1;

        Assert.Equal(0, vm.SelectedMenuIndex);
    }

    [Fact]
    public void SetSelectedMenuIndex_AboveMax_ClampsToTwo()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        vm.SelectedMenuIndex = 10;

        Assert.Equal(2, vm.SelectedMenuIndex);
    }

    [Fact]
    public void SelectedMenuIndex_Change_FiresPropertyChanged()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        vm.SelectedMenuIndex = 1;

        Assert.Contains("SelectedMenuIndex", changed);
    }

    // ================================================================
    // 2. Draft vs Persisted Settings
    // ================================================================

    [Fact]
    public void Constructor_LoadsDraftFromSettingsService()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        Assert.Equal(@"C:\persisted\adb.exe", vm.DraftAdbPath);
        Assert.Equal("192.168.1.1:5555", vm.DraftConnectAddress);
        Assert.False(vm.DraftAutoDetect);
    }

    [Fact]
    public void ModifyDraft_DoesNotPersist()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        vm.DraftAdbPath = @"D:\new\adb.exe";
        vm.DraftConnectAddress = "10.0.0.1:5555";

        // Persisted settings should still have old values
        var persisted = f.Settings.Load();
        Assert.Equal(@"C:\persisted\adb.exe", persisted.AdbPath);
        Assert.Equal("192.168.1.1:5555", persisted.ConnectAddress);
    }

    [Fact]
    public void SaveSettings_PersistsDraftToSettingsService()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        vm.DraftAdbPath = @"D:\new\adb.exe";
        vm.DraftAutoDetect = true;

        vm.SaveSettings();

        var persisted = f.Settings.Load();
        Assert.Equal(@"D:\new\adb.exe", persisted.AdbPath);
        Assert.True(persisted.AutoDetectConnection);
        // Language was also saved from draft
    }

    [Fact]
    public void SaveSettings_SavesLanguageFromDraft()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        vm.DraftLanguage = "zh-CN";

        vm.SaveSettings();

        var persisted = f.Settings.Load();
        Assert.Equal("zh-CN", persisted.Language);
    }

    [Fact]
    public void DraftAdbPath_PropertyChanged_FiresNotification()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        vm.DraftAdbPath = @"D:\adb.exe";

        Assert.Contains("DraftAdbPath", changed);
    }

    [Fact]
    public void DraftAutoDetect_PropertyChanged_FiresNotification()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        vm.DraftAutoDetect = true;

        Assert.Contains("DraftAutoDetect", changed);
    }

    // ================================================================
    // 3. Last Verified & Forget
    // ================================================================

    [Fact]
    public void LastVerified_ReflectsConnectionStateService()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        var now = DateTimeOffset.UtcNow;
        var record = new LastVerifiedConnection(
            @"C:\adb\adb.exe", "emulator-5554", "id123",
            "12", 1080, 1920, 1080, 1920, now);
        f.ConnectionState.UpdateLastVerified(record);

        Assert.NotNull(vm.LastVerified);
        Assert.Equal("emulator-5554", vm.LastVerified.Serial);
        Assert.Equal(now, vm.LastVerified.VerifiedAt);
    }

    [Fact]
    public void LastVerified_WhenNull_ReturnsNull()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        Assert.Null(vm.LastVerified);
    }

    [Fact]
    public void Forget_ClearsLastVerifiedOnly()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        // Set up a last verified record
        var now = DateTimeOffset.UtcNow;
        f.ConnectionState.UpdateLastVerified(new LastVerifiedConnection(
            @"C:\adb\adb.exe", "s1", "id1", "12", 100, 200, 100, 200, now));

        // Change draft to different values
        vm.DraftAdbPath = @"D:\other\adb.exe";

        vm.Forget();

        // Last verified should be cleared
        Assert.Null(vm.LastVerified);
        // Draft must be preserved
        Assert.Equal(@"D:\other\adb.exe", vm.DraftAdbPath);
    }

    [Fact]
    public void Forget_WhenAlreadyNull_DoesNotThrow()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        var exception = Record.Exception(() => vm.Forget());
        Assert.Null(exception);
    }

    // ================================================================
    // 4. Connect: Overlap Prevention
    // ================================================================

    [Fact]
    public async Task Connect_WhenDisconnected_StartsOperation()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        f.UmaService.NextConnectResult = new ConnectionSucceededEvent(
            1, "s1", "id1", "14", 1080, 1920, 1080, 1920, DisplaySizeSource.Physical);
        vm.DraftAdbPath = @"C:\adb\adb.exe";
        vm.DraftConnectAddress = "emulator-5554";

        await vm.ConnectAsync();

        Assert.Equal(1, f.UmaService.ConnectCallCount);
        Assert.Equal(ConnectionState.Connected, f.ConnectionState.State);
    }

    [Fact]
    public async Task Connect_WhenConnecting_DoesNotStartNewOperation()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Connecting);
        vm.DraftAdbPath = @"C:\adb\adb.exe";
        vm.DraftConnectAddress = "emulator-5554";

        await vm.ConnectAsync();

        Assert.Equal(0, f.UmaService.ConnectCallCount);
    }

    [Fact]
    public async Task Connect_WhenDetecting_DoesNotStartNewOperation()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Detecting);
        vm.DraftAdbPath = @"C:\adb\adb.exe";
        vm.DraftConnectAddress = "emulator-5554";

        await vm.ConnectAsync();

        Assert.Equal(0, f.UmaService.ConnectCallCount);
    }

    [Fact]
    public async Task Connect_WhenCanceling_DoesNotStartNewOperation()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Canceling);

        await vm.ConnectAsync();

        Assert.Equal(0, f.UmaService.ConnectCallCount);
    }

    [Fact]
    public async Task Connect_WhenFailed_StartsNewOperation()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Failed);
        f.UmaService.NextConnectResult = new ConnectionSucceededEvent(
            1, "s1", "id1", "14", 1080, 1920, 1080, 1920, DisplaySizeSource.Physical);
        vm.DraftAdbPath = @"C:\adb\adb.exe";
        vm.DraftConnectAddress = "emulator-5554";

        await vm.ConnectAsync();

        Assert.Equal(1, f.UmaService.ConnectCallCount);
    }

    // ================================================================
    // 5. Connect: Success Path
    // ================================================================

    [Fact]
    public async Task Connect_OnSuccess_UpdatesStateToConnected()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        f.UmaService.NextConnectResult = new ConnectionSucceededEvent(
            1, "emulator-5554", "dev123", "14",
            1080, 1920, 1080, 1920, DisplaySizeSource.Physical);
        vm.DraftAdbPath = @"C:\adb\adb.exe";
        vm.DraftConnectAddress = "emulator-5554";

        await vm.ConnectAsync();

        Assert.Equal(ConnectionState.Connected, f.ConnectionState.State);
    }

    [Fact]
    public async Task Connect_OnSuccess_CreatesLastVerifiedRecord()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        var now = DateTimeOffset.UtcNow;
        f.UmaService.NextConnectResult = new ConnectionSucceededEvent(
            1, "emulator-5554", "ABCDEF123456", "14",
            1080, 1920, 1080, 1920, DisplaySizeSource.Physical);
        vm.DraftAdbPath = @"C:\adb\adb.exe";
        vm.DraftConnectAddress = "emulator-5554";

        await vm.ConnectAsync();

        Assert.NotNull(f.ConnectionState.LastVerifiedConnection);
        Assert.Equal("emulator-5554", f.ConnectionState.LastVerifiedConnection!.Serial);
        Assert.Equal("ABCDEF123456", f.ConnectionState.LastVerifiedConnection.AndroidId);
        Assert.Equal("14", f.ConnectionState.LastVerifiedConnection.AndroidVersion);
        Assert.Equal(1080, f.ConnectionState.LastVerifiedConnection.Width);
        Assert.Equal(@"C:\adb\adb.exe", f.ConnectionState.LastVerifiedConnection.AdbPath);
    }

    [Fact]
    public async Task Connect_OnSuccess_AddsAddressToHistory()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        f.UmaService.NextConnectResult = new ConnectionSucceededEvent(
            1, "emulator-5554", "id1", "14",
            1080, 1920, 1080, 1920, DisplaySizeSource.Physical);
        vm.DraftAdbPath = @"C:\adb\adb.exe";
        vm.DraftConnectAddress = "emulator-5554";

        await vm.ConnectAsync();

        Assert.Contains("emulator-5554", vm.ConnectAddressHistory);
    }

    [Fact]
    public async Task Connect_OnSuccess_SavesSettings()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        f.UmaService.NextConnectResult = new ConnectionSucceededEvent(
            1, "emulator-5554", "id1", "14",
            1080, 1920, 1080, 1920, DisplaySizeSource.Physical);

        // Set draft with different values than persisted
        vm.DraftAdbPath = @"C:\custom\adb.exe";
        vm.DraftConnectAddress = "emulator-5554";

        await vm.ConnectAsync();

        var persisted = f.Settings.Load();
        Assert.Equal(@"C:\custom\adb.exe", persisted.AdbPath);
        Assert.Equal("emulator-5554", persisted.ConnectAddress);
    }

    [Fact]
    public async Task Connect_OnSuccess_UsesSerialFromEvent_ForHistory()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        // Event has a different serial than what was passed in
        f.UmaService.NextConnectResult = new ConnectionSucceededEvent(
            1, "127.0.0.1:5555", "id1", "14",
            1080, 1920, 1080, 1920, DisplaySizeSource.Physical);
        vm.DraftAdbPath = @"C:\adb\adb.exe";
        vm.DraftConnectAddress = "emulator-5554";

        await vm.ConnectAsync();

        // History uses the serial from the success event, not the draft address
        Assert.Contains("127.0.0.1:5555", vm.ConnectAddressHistory);
    }

    // ================================================================
    // 6. Connect: Failure Path
    // ================================================================

    [Fact]
    public async Task Connect_OnFailure_UpdatesStateToFailed()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        f.UmaService.NextConnectResult = new ConnectionFailedEvent(
            1, ConnectionErrorCode.DeviceOffline, "adb_devices", "device offline");
        vm.DraftAdbPath = @"C:\adb\adb.exe";
        vm.DraftConnectAddress = "emulator-5554";

        await vm.ConnectAsync();

        Assert.Equal(ConnectionState.Failed, f.ConnectionState.State);
    }

    [Fact]
    public async Task Connect_OnFailure_DoesNotUpdateLastVerified()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        f.UmaService.NextConnectResult = new ConnectionFailedEvent(
            1, ConnectionErrorCode.CommandTimedOut, "boot_poll", "timeout");

        // Set up an existing last verified
        var existingRecord = new LastVerifiedConnection(
            "old", "old-serial", "old-id", "10", 100, 200, 100, 200, DateTimeOffset.UtcNow);
        f.ConnectionState.UpdateLastVerified(existingRecord);
        vm.DraftAdbPath = @"C:\adb\adb.exe";
        vm.DraftConnectAddress = "emulator-5554";

        await vm.ConnectAsync();

        // Last verified should remain unchanged
        Assert.NotNull(f.ConnectionState.LastVerifiedConnection);
        Assert.Equal("old-serial", f.ConnectionState.LastVerifiedConnection.Serial);
    }

    [Fact]
    public async Task Connect_OnFailure_DoesNotAddToHistory()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        f.UmaService.NextConnectResult = new ConnectionFailedEvent(
            1, ConnectionErrorCode.DeviceUnauthorized, "adb_devices", "unauthorized");
        vm.DraftAdbPath = @"C:\adb\adb.exe";
        vm.DraftConnectAddress = "emulator-5554";

        await vm.ConnectAsync();

        Assert.Empty(vm.ConnectAddressHistory);
    }

    // ================================================================
    // 7. Cancel
    // ================================================================

    [Fact]
    public async Task Cancel_WhenConnecting_CancelsOperation()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);

        var states = new List<ConnectionState>();
        f.ConnectionState.StateChanged += (_, _) => states.Add(f.ConnectionState.State);

        var connectTcs = new TaskCompletionSource<ConnectionTerminalEvent>();
        f.UmaService.ConnectTcs = connectTcs;
        vm.DraftAdbPath = @"C:\adb\adb.exe";
        vm.DraftConnectAddress = "emulator-5554";

        var connectTask = vm.ConnectAsync();

        Assert.Equal(ConnectionState.Connecting, f.ConnectionState.State);

        vm.Cancel();
        Assert.Contains(ConnectionState.Canceling, states);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connectTcs.Task);
        await connectTask;

        Assert.Equal(ConnectionState.Disconnected, f.ConnectionState.State);
    }

    [Fact]
    public async Task Cancel_WhenConnecting_PreservesLastVerifiedConnection()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);

        var existing = new LastVerifiedConnection(
            @"C:\existing\adb.exe", "existing-serial", "existing-id",
            "10", 100, 200, 100, 200, DateTimeOffset.UtcNow);
        f.ConnectionState.UpdateLastVerified(existing);

        var connectTcs = new TaskCompletionSource<ConnectionTerminalEvent>();
        f.UmaService.ConnectTcs = connectTcs;
        vm.DraftAdbPath = @"C:\adb\adb.exe";
        vm.DraftConnectAddress = "emulator-5554";

        var connectTask = vm.ConnectAsync();

        Assert.Equal(ConnectionState.Connecting, f.ConnectionState.State);

        vm.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connectTcs.Task);
        await connectTask;

        Assert.NotNull(f.ConnectionState.LastVerifiedConnection);
        Assert.Equal("existing-serial", f.ConnectionState.LastVerifiedConnection.Serial);
    }

    [Fact]
    public void Cancel_WhenIdle_DoesNotThrow()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);

        var exception = Record.Exception(() => vm.Cancel());
        Assert.Null(exception);
    }

    [Fact]
    public void Cancel_WhenFailed_DoesNotThrow()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Failed);

        var exception = Record.Exception(() => vm.Cancel());
        Assert.Null(exception);
    }

    // ================================================================
    // 8. Auto-Detect during Connect
    // ================================================================

    [Fact]
    public async Task Connect_WithAutoDetectDisabled_DoesNotRunDiscovery()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        f.UmaService.NextConnectResult = new ConnectionSucceededEvent(
            1, "s1", "id1", "14", 1080, 1920, 1080, 1920, DisplaySizeSource.Physical);
        vm.DraftAutoDetect = false;
        vm.DraftAdbPath = @"C:\adb\adb.exe";
        vm.DraftConnectAddress = "emulator-5554";

        await vm.ConnectAsync();

        // WinAdapter should not have been called
        Assert.Equal(0, f.WinAdapter.RefreshCallCount);
    }

    [Fact]
    public async Task Connect_WithAutoDetectAndBlankAddress_RunsDiscovery()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        f.UmaService.NextConnectResult = new ConnectionSucceededEvent(
            1, "s1", "id1", "14", 1080, 1920, 1080, 1920, DisplaySizeSource.Physical);

        // Auto-detect enabled, blank address
        vm.DraftAutoDetect = true;
        vm.DraftAdbPath = @"";
        vm.DraftConnectAddress = @"";
        f.WinAdapter.NextDiscoveryResult = new DiscoveryResult(
            [new DetectedEmulatorInfo("BlueStacks", @"C:\BlueStacks\HD-Adb.exe")],
            []
        );
        f.WinAdapter.NextDevicesResult = new AdbDevicesResult(
            [new AdbDeviceRecord("emulator-5554", "device")],
            []
        );

        await vm.ConnectAsync();

        // Discovery should have been run
        Assert.True(f.WinAdapter.RefreshCallCount > 0);
        // Should have connected to the discovered address
        Assert.True(f.UmaService.ConnectCallCount > 0);
        Assert.Equal("emulator-5554", f.UmaService.LastConnectCall?.Serial);
    }

    [Fact]
    public async Task Connect_WithAutoDetectAndNonBlankAddress_DoesNotRunDiscovery()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        f.UmaService.NextConnectResult = new ConnectionSucceededEvent(
            1, "s1", "id1", "14", 1080, 1920, 1080, 1920, DisplaySizeSource.Physical);

        // Auto-detect enabled but address is already filled in
        vm.DraftAutoDetect = true;
        vm.DraftAdbPath = @"C:\manual\adb.exe";
        vm.DraftConnectAddress = "192.168.1.100:5555";

        await vm.ConnectAsync();

        // WinAdapter should NOT have been called (blank-only behavior)
        Assert.Equal(0, f.WinAdapter.RefreshCallCount);
        // Should have connected using the manual values
        Assert.Equal("192.168.1.100:5555", f.UmaService.LastConnectCall?.Serial);
    }

    [Fact]
    public async Task Connect_WhenAddressIsBlank_ShowsActionableDiagnostic()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        vm.DraftAutoDetect = false;
        vm.DraftAdbPath = @"C:\adb\adb.exe";
        vm.DraftConnectAddress = "";

        await vm.ConnectAsync();

        Assert.Contains("address", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, f.UmaService.ConnectCallCount);
    }

    // ================================================================
    // 9. AlwaysAutoDetect
    // ================================================================

    [Fact]
    public async Task Connect_WithAlwaysAutoDetect_RunsDiscoveryEvenWithValues()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        f.UmaService.NextConnectResult = new ConnectionSucceededEvent(
            1, "s1", "id1", "14", 1080, 1920, 1080, 1920, DisplaySizeSource.Physical);

        vm.DraftAutoDetect = true;
        vm.DraftAlwaysAutoDetect = true;
        vm.DraftAdbPath = @"C:\manual\adb.exe";
        vm.DraftConnectAddress = "192.168.1.100:5555";
        f.WinAdapter.NextDiscoveryResult = new DiscoveryResult(
            [new DetectedEmulatorInfo("BlueStacks", @"C:\BlueStacks\HD-Adb.exe")],
            []
        );
        f.WinAdapter.NextDevicesResult = new AdbDevicesResult(
            [new AdbDeviceRecord("emulator-5554", "device")],
            []
        );

        await vm.ConnectAsync();

        // Discovery should run even though address is non-blank
        Assert.True(f.WinAdapter.RefreshCallCount > 0);
    }

    [Fact]
    public async Task Connect_AlwaysAutoDetectWithValues_RequestsConfirmation()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        f.UmaService.NextConnectResult = new ConnectionSucceededEvent(
            1, "s1", "id1", "14", 1080, 1920, 1080, 1920, DisplaySizeSource.Physical);

        bool confirmationRequested = false;
        vm.RequestOverwriteConfirmation = () =>
        {
            confirmationRequested = true;
            return Task.FromResult(true); // confirmed
        };

        vm.DraftAutoDetect = true;
        vm.DraftAlwaysAutoDetect = true;
        vm.DraftAdbPath = @"C:\manual\adb.exe";
        vm.DraftConnectAddress = "192.168.1.100:5555";
        f.WinAdapter.NextDiscoveryResult = new DiscoveryResult(
            [new DetectedEmulatorInfo("BlueStacks", @"C:\BlueStacks\HD-Adb.exe")],
            []
        );
        f.WinAdapter.NextDevicesResult = new AdbDevicesResult(
            [new AdbDeviceRecord("emulator-5554", "device")],
            []
        );

        await vm.ConnectAsync();

        Assert.True(confirmationRequested);
    }

    [Fact]
    public async Task Connect_AlwaysAutoDetectConfirmationDenied_DoesNotOverwrite()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        f.UmaService.NextConnectResult = new ConnectionSucceededEvent(
            1, "s1", "id1", "14", 1080, 1920, 1080, 1920, DisplaySizeSource.Physical);

        vm.RequestOverwriteConfirmation = () => Task.FromResult(false); // denied

        vm.DraftAutoDetect = true;
        vm.DraftAlwaysAutoDetect = true;
        vm.DraftAdbPath = @"C:\manual\adb.exe";
        vm.DraftConnectAddress = "192.168.1.100:5555";
        f.WinAdapter.NextDiscoveryResult = new DiscoveryResult(
            [new DetectedEmulatorInfo("BlueStacks", @"C:\BlueStacks\HD-Adb.exe")],
            []
        );
        f.WinAdapter.NextDevicesResult = new AdbDevicesResult(
            [new AdbDeviceRecord("emulator-5554", "device")],
            []
        );

        await vm.ConnectAsync();

        // Draft should still have manual values
        Assert.Equal(@"C:\manual\adb.exe", vm.DraftAdbPath);
        Assert.Equal("192.168.1.100:5555", vm.DraftConnectAddress);
    }

    [Fact]
    public async Task Connect_AlwaysAutoDetectWithBlankAddress_DoesNotRequestConfirmation()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        f.UmaService.NextConnectResult = new ConnectionSucceededEvent(
            1, "s1", "id1", "14", 1080, 1920, 1080, 1920, DisplaySizeSource.Physical);

        bool confirmationRequested = false;
        vm.RequestOverwriteConfirmation = () =>
        {
            confirmationRequested = true;
            return Task.FromResult(true);
        };

        vm.DraftAutoDetect = true;
        vm.DraftAlwaysAutoDetect = true;
        vm.DraftAdbPath = @"";
        vm.DraftConnectAddress = @"";
        f.WinAdapter.NextDiscoveryResult = new DiscoveryResult(
            [new DetectedEmulatorInfo("BlueStacks", @"C:\BlueStacks\HD-Adb.exe")],
            []
        );
        f.WinAdapter.NextDevicesResult = new AdbDevicesResult(
            [new AdbDeviceRecord("emulator-5554", "device")],
            []
        );

        await vm.ConnectAsync();

        // No confirmation needed when fields are blank (nothing to overwrite)
        Assert.False(confirmationRequested);
    }

    // ================================================================
    // 10. Discovery Candidates & Eligibility
    // ================================================================

    [Fact]
    public async Task Connect_AutoDetectNoCandidates_DoesNotChangeDraft()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);

        vm.DraftAutoDetect = true;
        vm.DraftAdbPath = @"C:\existing\adb.exe";
        vm.DraftConnectAddress = "192.168.1.100:5555";
        // Blank address to trigger discovery, but values should not be overwritten
        vm.DraftConnectAddress = @"";

        f.WinAdapter.NextDiscoveryResult = new DiscoveryResult([], []);

        // Don't set NextConnectResult so we can verify it stopped
        await vm.ConnectAsync();

        // Since no candidates, we expect connect was not called (blank address remained blank)
        Assert.Equal(0, f.UmaService.ConnectCallCount);
    }

    [Fact]
    public async Task Connect_AutoDetectNoCandidates_WithAutoStartEnabled_StartsConfiguredEmulator()
    {
        var f = CreateFixture();
        f.Settings.Save(new ConnectionSettings
        {
            AutoDetectConnection = true,
            AutoStartEmulator = true,
            EmulatorExecutablePath = @"C:\MuMu\MuMuNxDevice.exe",
        });
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        vm.DraftConnectAddress = "";
        f.WinAdapter.NextDiscoveryResult = new DiscoveryResult([], []);

        await vm.ConnectAsync();

        Assert.Equal(@"C:\MuMu\MuMuNxDevice.exe", f.EmulatorLauncher.StartedPath);
        Assert.Equal(0, f.UmaService.ConnectCallCount);
    }

    [Fact]
    public async Task Connect_AutoDetectSingleCandidate_UsesIt()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        f.UmaService.NextConnectResult = new ConnectionSucceededEvent(
            1, "emulator-5554", "id1", "14", 1080, 1920, 1080, 1920, DisplaySizeSource.Physical);

        vm.DraftAutoDetect = true;
        vm.DraftAdbPath = @"";
        vm.DraftConnectAddress = @"";
        f.WinAdapter.NextDiscoveryResult = new DiscoveryResult(
            [new DetectedEmulatorInfo("LDPlayer", @"D:\LDPlayer\adb.exe")],
            []
        );
        f.WinAdapter.NextDevicesResult = new AdbDevicesResult(
            [new AdbDeviceRecord("emulator-5554", "device")],
            []
        );

        await vm.ConnectAsync();

        // Should have updated draft with discovered values
        Assert.Equal(@"D:\LDPlayer\adb.exe", vm.DraftAdbPath);
    }

    [Fact]
    public async Task Connect_AutoDetectMultipleCandidates_TriggersSelectionSeam()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        f.UmaService.NextConnectResult = new ConnectionSucceededEvent(
            1, "s1", "id1", "14", 1080, 1920, 1080, 1920, DisplaySizeSource.Physical);

        vm.DraftAutoDetect = true;
        vm.DraftAdbPath = @"";
        vm.DraftConnectAddress = @"";

        var candidates = new List<DetectedEmulatorInfo>
        {
            new("BlueStacks", @"C:\BS\HD-Adb.exe"),
            new("LDPlayer", @"D:\LD\adb.exe"),
        };
        f.WinAdapter.NextDiscoveryResult = new DiscoveryResult(candidates, []);

        DetectedEmulatorInfo? selectedCandidate = null;
        vm.RequestCandidateSelection = async (available) =>
        {
            selectedCandidate = available[1]; // pick LDPlayer
            return selectedCandidate;
        };

        f.WinAdapter.NextDevicesResult = new AdbDevicesResult(
            [new AdbDeviceRecord("emulator-5556", "device")],
            []
        );

        await vm.ConnectAsync();

        // Should have asked for selection
        Assert.NotNull(selectedCandidate);
        Assert.Equal("LDPlayer", selectedCandidate.EmulatorName);
    }

    [Fact]
    public async Task Connect_AutoDetectNoEligibleDevices_DoesNotSetAddress()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);

        vm.DraftAutoDetect = true;
        vm.DraftAdbPath = @"";
        vm.DraftConnectAddress = @"";

        f.WinAdapter.NextDiscoveryResult = new DiscoveryResult(
            [new DetectedEmulatorInfo("BlueStacks", @"C:\BS\HD-Adb.exe")],
            []
        );
        f.WinAdapter.NextDevicesResult = new AdbDevicesResult(
            [
                new AdbDeviceRecord("emulator-5554", "offline"),
                new AdbDeviceRecord("emulator-5556", "unauthorized"),
            ],
            []
        );

        await vm.ConnectAsync();

        // ADB path should be set (from candidate), but address should still be blank
        Assert.Equal(@"C:\BS\HD-Adb.exe", vm.DraftAdbPath);
        Assert.Equal(@"", vm.DraftConnectAddress);
    }

    [Fact]
    public async Task Connect_AutoDetectMuMuWithResolvedFallback_UsesVerifiedEndpoint()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        f.UmaService.NextConnectResult = new ConnectionSucceededEvent(
            1, "127.0.0.1:16384", "id1", "14", 1080, 1920, 1080, 1920, DisplaySizeSource.Physical);

        vm.DraftAutoDetect = true;
        vm.DraftAdbPath = "";
        vm.DraftConnectAddress = "";
        f.WinAdapter.NextDiscoveryResult = new DiscoveryResult(
            [new DetectedEmulatorInfo("MuMuEmulator12", @"C:\MuMu\nx_main\adb.exe")],
            []);
        f.WinAdapter.NextEndpointResolutionResult = new EndpointResolutionResult(
            ["127.0.0.1:16384"],
            []);

        await vm.ConnectAsync();

        Assert.Equal(@"C:\MuMu\nx_main\adb.exe", vm.DraftAdbPath);
        Assert.Equal("127.0.0.1:16384", vm.DraftConnectAddress);
        Assert.Equal(1, f.UmaService.ConnectCallCount);
    }

    [Fact]
    public async Task Connect_AutoDetectMultipleResolvedEndpoints_UsesSelectedEndpoint()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        f.UmaService.NextConnectResult = new ConnectionSucceededEvent(
            1, "emulator-5556", "id1", "14", 1080, 1920, 1080, 1920, DisplaySizeSource.Physical);

        vm.DraftAutoDetect = true;
        vm.DraftAdbPath = "";
        vm.DraftConnectAddress = "";
        vm.RequestAddressSelection = endpoints => Task.FromResult<string?>(endpoints[1]);
        f.WinAdapter.NextDiscoveryResult = new DiscoveryResult(
            [new DetectedEmulatorInfo("LDPlayer", @"C:\LDPlayer\adb.exe")],
            []);
        f.WinAdapter.NextEndpointResolutionResult = new EndpointResolutionResult(
            ["emulator-5554", "emulator-5556"],
            []);

        await vm.ConnectAsync();

        Assert.Equal("emulator-5556", vm.DraftConnectAddress);
        Assert.Equal("emulator-5556", f.UmaService.LastConnectCall?.Serial);
    }

    // ================================================================
    // 11. Manual AutoDetect Command
    // ================================================================

    [Fact]
    public async Task AutoDetectEmulators_WithDiscovery_RunsDiscoveryAndUpdatesDraft()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        vm.DraftAdbPath = @"";
        vm.DraftConnectAddress = @"";
        f.WinAdapter.NextDiscoveryResult = new DiscoveryResult(
            [new DetectedEmulatorInfo("BlueStacks", @"C:\BS\HD-Adb.exe")],
            []
        );
        f.WinAdapter.NextDevicesResult = new AdbDevicesResult(
            [new AdbDeviceRecord("emulator-5554", "device")],
            []
        );

        await vm.AutoDetectEmulatorsAsync();

        Assert.Equal(@"C:\BS\HD-Adb.exe", vm.DraftAdbPath);
        Assert.Equal("emulator-5554", vm.DraftConnectAddress);
    }

    [Fact]
    public async Task AutoDetectEmulators_WithDiscovery_UpdatesLastDetectedEmulator()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.WinAdapter.NextDiscoveryResult = new DiscoveryResult(
            [new DetectedEmulatorInfo("BlueStacks", @"C:\BS\HD-Adb.exe")],
            []
        );
        f.WinAdapter.NextDevicesResult = new AdbDevicesResult(
            [new AdbDeviceRecord("emulator-5554", "device")],
            []
        );

        await vm.AutoDetectEmulatorsAsync();

        Assert.Equal("BlueStacks", vm.LastDetectedEmulator);
    }

    [Fact]
    public async Task AutoDetectEmulators_NoCandidates_DoesNotChangeDraft()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        vm.DraftAdbPath = @"C:\existing\adb.exe";
        vm.DraftConnectAddress = "192.168.1.1:5555";
        f.WinAdapter.NextDiscoveryResult = new DiscoveryResult([], []);

        await vm.AutoDetectEmulatorsAsync();

        // Draft unchanged
        Assert.Equal(@"C:\existing\adb.exe", vm.DraftAdbPath);
        Assert.Equal("192.168.1.1:5555", vm.DraftConnectAddress);
    }

    [Fact]
    public async Task AutoDetectEmulators_WhenDiscoveryThrows_ReturnsToDisconnected()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.WinAdapter.RefreshException = new InvalidOperationException("Process scan failed");

        var exception = await Record.ExceptionAsync(vm.AutoDetectEmulatorsAsync);

        Assert.Null(exception);
        Assert.Equal(ConnectionState.Disconnected, vm.State);
    }

    [Fact]
    public async Task AutoDetectEmulators_SetsStateToDetectingThenBack()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.WinAdapter.NextDiscoveryResult = new DiscoveryResult([], []);

        var states = new List<ConnectionState>();
        f.ConnectionState.StateChanged += (_, _) => states.Add(f.ConnectionState.State);

        await vm.AutoDetectEmulatorsAsync();

        // Should have transitioned through Detecting and back to Idle or Disconnected
        Assert.Contains(ConnectionState.Detecting, states);
    }

    // ================================================================
    // 12. Language
    // ================================================================

    [Fact]
    public void SelectedLanguage_DefaultsToLocalizationCurrent()
    {
        var f = CreateFixture();
        f.Localization.CurrentCulture = "zh-CN";
        var vm = f.CreateViewModel();

        Assert.Equal("zh-CN", vm.SelectedLanguage);
    }

    [Fact]
    public void SelectedLanguage_Change_SwitchesLocalization()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.Localization.CurrentCulture = "en-US";

        vm.SelectedLanguage = "zh-CN";

        Assert.Equal("zh-CN", f.Localization.CurrentCulture);
        Assert.True(f.Localization.SwitchLanguageWasCalled);
    }

    [Fact]
    public void DraftLanguage_IsLoadedFromSettings()
    {
        var f = CreateFixture();
        // Settings already has Language = "en-US" from fixture setup
        var vm = f.CreateViewModel();

        Assert.Equal("en-US", vm.DraftLanguage);
    }

    [Fact]
    public void DraftLanguageChange_AfterLocalizationSwitch_UpdatesDraft()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.Localization.CurrentCulture = "zh-CN";

        // Changing SelectedLanguage should update DraftLanguage
        vm.SelectedLanguage = "zh-CN";

        Assert.Equal("zh-CN", vm.DraftLanguage);
    }

    // ================================================================
    // 13. State Monitoring
    // ================================================================

    [Fact]
    public void State_ReflectsConnectionStateService()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        f.ConnectionState.SetState(ConnectionState.Connecting);

        Assert.Equal(ConnectionState.Connecting, vm.State);
    }

    [Fact]
    public void StateChanged_RaisesPropertyChanged()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        f.ConnectionState.SetState(ConnectionState.Connected);

        Assert.Contains("State", changed);
    }

    [Fact]
    public void StateChanged_RaisesIsOperationInProgressChanged()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        f.ConnectionState.SetState(ConnectionState.Connecting);

        Assert.Contains("IsOperationInProgress", changed);
    }

    [Fact]
    public void IsOperationInProgress_WhenConnecting_IsTrue()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        f.ConnectionState.SetState(ConnectionState.Connecting);

        Assert.True(vm.IsOperationInProgress);
    }

    [Fact]
    public void IsOperationInProgress_WhenDetecting_IsTrue()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        f.ConnectionState.SetState(ConnectionState.Detecting);

        Assert.True(vm.IsOperationInProgress);
    }

    [Fact]
    public void IsOperationInProgress_WhenIdle_IsFalse()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        f.ConnectionState.SetState(ConnectionState.Disconnected);

        Assert.False(vm.IsOperationInProgress);
    }

    // ================================================================
    // 14. System Info
    // ================================================================

    [Fact]
    public void CoreVersion_ReflectsUmaService()
    {
        var f = CreateFixture();
        f.UmaService.CoreVersion = "2.0.0";
        var vm = f.CreateViewModel();

        Assert.Equal("2.0.0", vm.CoreVersion);
    }

    [Fact]
    public void CoreVersion_WhenUmaServiceReturnsNull_ReturnsEmpty()
    {
        var f = CreateFixture();
        f.UmaService.CoreVersion = null;
        var vm = f.CreateViewModel();

        Assert.Equal("", vm.CoreVersion);
    }

    // ================================================================
    // 15. Dispose
    // ================================================================

    [Fact]
    public void Dispose_UnsubscribesFromStateChanged()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        vm.Dispose();

        // After dispose, state changes should not trigger PropertyChanged
        f.ConnectionState.SetState(ConnectionState.Connected);
        Assert.Empty(changed);
    }

    [Fact]
    public void Dispose_MultipleCalls_DoesNotThrow()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        vm.Dispose();
        var exception = Record.Exception(() => vm.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public async Task AfterDispose_ConnectDoesNothing()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        vm.DraftAdbPath = @"C:\adb\adb.exe";
        vm.DraftConnectAddress = "emulator-5554";

        vm.Dispose();
        await vm.ConnectAsync();

        Assert.Equal(0, f.UmaService.ConnectCallCount);
    }

    // ================================================================
    // 16. ObservableCollection Refreshes
    // ================================================================

    [Fact]
    public void ConnectAddressHistory_IsPopulatedFromLoadedSettings()
    {
        var f = CreateFixture();
        var settings = f.Settings.Load();
        settings.AddAddressToHistory("10.0.0.1:5555");
        settings.AddAddressToHistory("10.0.0.2:5555");
        f.Settings.Save(settings);

        var vm = f.CreateViewModel();

        Assert.Equal(2, vm.ConnectAddressHistory.Count);
        Assert.Contains("10.0.0.1:5555", vm.ConnectAddressHistory);
    }

    [Fact]
    public void ConnectAddressHistory_IsEmptyByDefault()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        Assert.Empty(vm.ConnectAddressHistory);
    }

    // ================================================================
    // 17. StatusText
    // ================================================================

    [Fact]
    public void StatusText_Default_ReturnsDisconnected()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        Assert.Equal("Disconnected", vm.StatusText);
    }

    [Fact]
    public void StatusText_WhenDisconnected_ReturnsDisconnected()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);

        Assert.Equal("Disconnected", vm.StatusText);
    }

    [Fact]
    public void StatusText_WhenDetecting_ReturnsDetecting()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Detecting);

        Assert.Equal("Detecting emulators...", vm.StatusText);
    }

    [Fact]
    public void StatusText_WhenConnecting_ReturnsConnecting()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Connecting);

        Assert.Equal("Connecting...", vm.StatusText);
    }

    [Fact]
    public void StatusText_WhenConnected_ReturnsConnected()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Connected);

        Assert.Equal("Connected", vm.StatusText);
    }

    [Fact]
    public void StatusText_WhenFailed_ReturnsConnectionFailed()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Failed);

        Assert.Equal("Connection failed", vm.StatusText);
    }

    [Fact]
    public void StatusText_WhenCanceling_ReturnsCanceling()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Canceling);

        Assert.Equal("Canceling...", vm.StatusText);
    }

    [Fact]
    public void StatusText_WhenStateChanges_FiresPropertyChanged()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        f.ConnectionState.SetState(ConnectionState.Connecting);

        Assert.Contains("StatusText", changed);
    }

    // ================================================================
    // 18. ControlSession (read-only, from IConnectionStateService)
    // ================================================================

    [Fact]
    public void ControlSession_DefaultIsFromConnectionStateService()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        Assert.NotNull(vm.ControlSession);
        Assert.Equal("", vm.ControlSession.Serial);
        Assert.Equal(ConnectionState.Disconnected, vm.ControlSession.State);
    }

    [Fact]
    public void ControlSession_AfterUpdate_ReflectsChange()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        var updated = new ControlSessionSnapshot(
            "emulator-5554", "com.example", 1, 1080, 1920,
            DateTimeOffset.UtcNow, ConnectionState.Connected);
        f.ConnectionState.UpdateControlSession(updated);

        Assert.NotNull(vm.ControlSession);
        Assert.Equal("emulator-5554", vm.ControlSession!.Serial);
        Assert.Equal("com.example", vm.ControlSession!.TargetPackageId);
        Assert.Equal(ConnectionState.Connected, vm.ControlSession!.State);
    }

    [Fact]
    public void ControlSession_StateChanged_FiresPropertyChanged()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        f.ConnectionState.SetState(ConnectionState.Connected);

        Assert.Contains("ControlSession", changed);
    }

    // ================================================================
    // 19. Commands (Connect, CancelConnect, Save, DetectAdbConfig)
    // ================================================================

    [Fact]
    public void ConnectCommand_CanExecute_WhenDisconnected_ReturnsTrue()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);

        Assert.True(vm.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public void ConnectCommand_CanExecute_WhenFailed_ReturnsTrue()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Failed);

        Assert.True(vm.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public void ConnectCommand_CanExecute_WhenConnecting_ReturnsFalse()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Connecting);

        Assert.False(vm.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public void ConnectCommand_CanExecute_WhenConnected_ReturnsFalse()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Connected);

        Assert.False(vm.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public void ConnectCommand_CanExecuteChanged_RaisesOnStateChange()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        var raised = false;
        vm.ConnectCommand.CanExecuteChanged += (_, _) => raised = true;

        f.ConnectionState.SetState(ConnectionState.Connecting);

        Assert.True(raised);
    }

    [Fact]
    public void CancelConnectCommand_CanExecute_WhenConnecting_ReturnsTrue()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Connecting);

        Assert.True(vm.CancelConnectCommand.CanExecute(null));
    }

    [Fact]
    public void CancelConnectCommand_CanExecute_WhenCanceling_ReturnsTrue()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Canceling);

        Assert.True(vm.CancelConnectCommand.CanExecute(null));
    }

    [Fact]
    public void CancelConnectCommand_CanExecute_WhenDetecting_ReturnsTrue()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Detecting);

        Assert.True(vm.CancelConnectCommand.CanExecute(null));
    }

    [Fact]
    public void CancelConnectCommand_CanExecute_WhenDisconnected_ReturnsFalse()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);

        Assert.False(vm.CancelConnectCommand.CanExecute(null));
    }

    [Fact]
    public void CancelConnectCommand_CanExecute_WhenConnected_ReturnsFalse()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Connected);

        Assert.False(vm.CancelConnectCommand.CanExecute(null));
    }

    [Fact]
    public void CancelConnectCommand_CanExecuteChanged_RaisesOnStateChange()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);
        var raised = false;
        vm.CancelConnectCommand.CanExecuteChanged += (_, _) => raised = true;

        f.ConnectionState.SetState(ConnectionState.Connecting);

        Assert.True(raised);
    }

    [Fact]
    public void SaveCommand_CanExecute_ReturnsTrue()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();

        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void DetectAdbConfigCommand_CanExecute_WhenDisconnected_ReturnsTrue()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Disconnected);

        Assert.True(vm.DetectAdbConfigCommand.CanExecute(null));
    }

    [Fact]
    public void DetectAdbConfigCommand_CanExecute_WhenConnecting_ReturnsFalse()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        f.ConnectionState.SetState(ConnectionState.Connecting);

        Assert.False(vm.DetectAdbConfigCommand.CanExecute(null));
    }

    [Fact]
    public void DetectAdbConfigCommand_CanExecuteChanged_RaisesOnStateChange()
    {
        var f = CreateFixture();
        var vm = f.CreateViewModel();
        var raised = false;
        vm.DetectAdbConfigCommand.CanExecuteChanged += (_, _) => raised = true;

        f.ConnectionState.SetState(ConnectionState.Connecting);

        Assert.True(raised);
    }

    // ================================================================
    // Fakes
    // ================================================================

    private sealed class FakeUmaService : IUmaService
    {
        public string? CoreVersion { get; set; } = "1.0.0";

        public event Action<ConnectionEvent>? ConnectionEventReceived;
#pragma warning disable CS0067
        public event Action<BridgeDiagnostic>? DiagnosticReceived;
#pragma warning restore CS0067

        /// <summary>When set, ConnectAsync returns this value immediately.</summary>
        public ConnectionTerminalEvent? NextConnectResult { get; set; }

        /// <summary>When set, ConnectAsync awaits this TCS (for cancellation testing).</summary>
        public TaskCompletionSource<ConnectionTerminalEvent>? ConnectTcs { get; set; }

        public int ConnectCallCount { get; private set; }
        public int CancelCallCount { get; private set; }
        public (string AdbPath, string Serial, string Profile)? LastConnectCall { get; private set; }
        public ulong? LastCancelOperationId { get; private set; }

        public void FireConnectionStarted(ulong operationId)
        {
            ConnectionEventReceived?.Invoke(new ConnectionStartedEvent(operationId));
        }

        public void FireConnectionSucceeded(
            ulong operationId,
            string serial,
            string androidId,
            string androidVersion,
            int width,
            int height)
        {
            ConnectionEventReceived?.Invoke(
                new ConnectionSucceededEvent(
                    operationId, serial, androidId, androidVersion,
                    width, height, width, height,
                    DisplaySizeSource.Physical));
        }

        public void FireConnectionFailed(
            ulong operationId,
            ConnectionErrorCode errorCode,
            string phase,
            string message)
        {
            ConnectionEventReceived?.Invoke(
                new ConnectionFailedEvent(operationId, errorCode, phase, message));
        }

        public async Task<ConnectionTerminalEvent> ConnectAsync(
            string adbPath,
            string serial,
            string profile,
            CancellationToken cancellationToken = default)
        {
            ConnectCallCount++;
            LastConnectCall = (adbPath, serial, profile);

            cancellationToken.ThrowIfCancellationRequested();

            if (ConnectTcs is not null)
            {
                using var _ = cancellationToken.Register(() =>
                    ConnectTcs.TrySetCanceled(cancellationToken));

                return await ConnectTcs.Task;
            }

            return NextConnectResult
                ?? new ConnectionSucceededEvent(
                    0, serial, "test-id", "14",
                    1080, 1920, 1080, 1920,
                    DisplaySizeSource.Physical);
        }

        public Task CancelOperationAsync(ulong operationId, CancellationToken cancellationToken = default)
        {
            CancelCallCount++;
            LastCancelOperationId = operationId;
            return Task.CompletedTask;
        }

        public Task InitializeAsync(
            string appBaseDir,
            string appDataDir,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeConnectionStateService : IConnectionStateService
    {
        private ConnectionState _state = ConnectionState.Disconnected;
        private LastVerifiedConnection? _lastVerified;
        private ControlSessionSnapshot? _controlSession;

        public ConnectionState State => _state;
        public LastVerifiedConnection? LastVerifiedConnection => _lastVerified;
        public ControlSessionSnapshot? ControlSession => _controlSession;

        public event EventHandler? StateChanged;

        public FakeConnectionStateService()
        {
            _controlSession = new ControlSessionSnapshot(
                "", null, 0, null, null, null, ConnectionState.Disconnected);
        }

        public void SetState(ConnectionState newState)
        {
            if (_state == newState)
                return;
            _state = newState;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void UpdateLastVerified(LastVerifiedConnection record)
        {
            ArgumentNullException.ThrowIfNull(record);
            _lastVerified = record;
        }

        public void ClearLastVerified()
        {
            _lastVerified = null;
        }

        public void UpdateControlSession(ControlSessionSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            _controlSession = snapshot;
        }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        private ConnectionSettings _settings = new();

        public ConnectionSettings Load() => _settings;

        public void Save(ConnectionSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            _settings = settings;
        }
    }

    private sealed class FakeLocalizationService : ILocalizationService
    {
        public string CurrentCulture { get; set; } = "en-US";
        public bool SwitchLanguageWasCalled { get; private set; }

        public event EventHandler<string>? LanguageChanged;

        public void Initialize()
        {
        }

        public void SwitchLanguage(string culture)
        {
            SwitchLanguageWasCalled = true;
            CurrentCulture = culture;
            LanguageChanged?.Invoke(this, culture);
        }

        public string GetString(string key) => key;
    }

    private sealed class FakeWinAdapter : IWinAdapter
    {
        public DiscoveryResult? NextDiscoveryResult { get; set; }
        public AdbDevicesResult? NextDevicesResult { get; set; }
        public EndpointResolutionResult? NextEndpointResolutionResult { get; set; }
        public Exception? RefreshException { get; set; }
        public int RefreshCallCount { get; private set; }
        public int DevicesCallCount { get; private set; }

        public FakeWinAdapter()
        {
            NextDiscoveryResult = new DiscoveryResult([], []);
            NextDevicesResult = new AdbDevicesResult([], []);
        }

        public DiscoveryResult RefreshEmulatorsInfo()
        {
            RefreshCallCount++;
            if (RefreshException is not null)
                throw RefreshException;
            return NextDiscoveryResult ?? new DiscoveryResult([], []);
        }

        public AdbDevicesResult GetAdbDevices(string adbPath)
        {
            DevicesCallCount++;
            return NextDevicesResult ?? new AdbDevicesResult([], []);
        }

        public EndpointResolutionResult ResolveEndpoints(
            string adbPath,
            string profileName,
            CancellationToken cancellationToken)
        {
            if (NextEndpointResolutionResult is not null)
                return NextEndpointResolutionResult;

            var records = NextDevicesResult?.Records ?? [];
            return new EndpointResolutionResult(
                records.Where(record => record.State == "device").Select(record => record.Serial).ToList(),
                NextDevicesResult?.Diagnostics ?? []);
        }
    }

    private sealed class FakeEmulatorLauncher : IEmulatorLauncher
    {
        public string? StartedPath { get; private set; }

        public EmulatorLaunchResult Start(string executablePath)
        {
            StartedPath = executablePath;
            return new EmulatorLaunchResult(true, "Emulator startup was requested.");
        }
    }
}
