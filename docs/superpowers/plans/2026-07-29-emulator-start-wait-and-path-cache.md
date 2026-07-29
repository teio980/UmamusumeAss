# Emulator Start Wait and Path Cache Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `subagent-driven-development` (recommended) or `executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let users configure how many seconds the app waits after starting an emulator, and ensure the configured emulator startup path is saved when auto-start is requested so it remains after restarting the app.

**Architecture:** Extend the existing `ConnectionSettings` JSON model and `SettingsViewModel` draft pattern. Use an injectable delay abstraction so the ViewModel can await the configured interval without blocking WPF or making tests sleep. The startup branch persists only the emulator executable path before launching, preserving unrelated unsaved draft edits and keeping the current no-retry/no-auto-connect behavior.

**Tech Stack:** .NET 10, C# 14, WPF, Stylet IoC/MVVM, HandyControls XAML, xUnit.

## Global Constraints

- Keep all connection changes in the existing Connection panel; do not add a new page or dependency.
- `AutoStartEmulatorWaitSeconds` is an integer, defaults to `5`, and clamps negative values to `0`.
- Wait only after `IEmulatorLauncher.Start()` reports `Started == true`.
- Do not add emulator rediscovery, automatic connection, or retry after the wait.
- Persist `EmulatorExecutablePath` when the auto-start branch runs; do not persist unrelated unsaved fields at that point.
- Do not use type-error suppressions or commit changes.

---

## File Map

| File | Responsibility |
|---|---|
| `src/UmamusumeWpfGui/Models/ConnectionSettings.cs` | Persisted wait-duration setting and validation. |
| `src/UmamusumeWpfGui/Helper/IAsyncDelay.cs` | Production delay contract and `Task.Delay` implementation. |
| `src/UmamusumeWpfGui/Bootstrapper.cs` | IoC binding for the delay service. |
| `src/UmamusumeWpfGui/ViewModels/SettingsViewModel.cs` | Editable wait draft, launch-time path cache, and non-blocking wait. |
| `src/UmamusumeWpfGui/Views/SettingsView.xaml` | Numeric wait-duration control in the auto-start section. |
| `src/UmamusumeWpfGui/Resources/Strings.en-US.xaml` | English label for the new setting. |
| `src/UmamusumeWpfGui/Resources/Strings.zh-CN.xaml` | Simplified Chinese label for the new setting. |
| `tests/UmamusumeWpfGui.Tests/Models/ConnectionSettingsTests.cs` | Model defaults, clamping, and JSON round-trip coverage. |
| `tests/UmamusumeWpfGui.Tests/ViewModels/SettingsViewModelTests.cs` | Draft, path-cache, and launch-delay behavior with fakes. |
| `tests/UmamusumeWpfGui.Tests/Views/SettingsViewContractTests.cs` | Connection-panel resource and binding contract coverage. |

### Task 1: Model the persisted wait duration

**Files:**
- Modify: `tests/UmamusumeWpfGui.Tests/Models/ConnectionSettingsTests.cs`
- Modify: `src/UmamusumeWpfGui/Models/ConnectionSettings.cs`

**Produces:** `ConnectionSettings.AutoStartEmulatorWaitSeconds`, a JSON-serializable, non-negative `int` defaulting to `5`.

- [ ] **Step 1: Write failing model tests**

Add a default/clamp test next to `Defaults_AutoStartEmulatorIsFalse`:

```csharp
[Fact]
public void AutoStartEmulatorWaitSeconds_DefaultsToFive_AndClampsNegativeValues()
{
    var settings = new ConnectionSettings();
    Assert.Equal(5, settings.AutoStartEmulatorWaitSeconds);

    settings.AutoStartEmulatorWaitSeconds = -1;
    Assert.Equal(0, settings.AutoStartEmulatorWaitSeconds);
}
```

Extend `JsonRoundtrip_AllPropertiesRoundtrip` with:

```csharp
AutoStartEmulatorWaitSeconds = 12,
```

and:

```csharp
Assert.Equal(s.AutoStartEmulatorWaitSeconds, deserialized.AutoStartEmulatorWaitSeconds);
```

- [ ] **Step 2: Run the focused test and confirm it fails to compile**

Run:

```powershell
dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --filter "FullyQualifiedName~ConnectionSettingsTests"
```

Expected: failure because `AutoStartEmulatorWaitSeconds` does not yet exist.

- [ ] **Step 3: Implement the persisted property**

After `EmulatorExecutablePath` in `ConnectionSettings.cs`, add:

```csharp
private int _autoStartEmulatorWaitSeconds = 5;

/// <summary>Seconds to wait after successfully starting an emulator.</summary>
public int AutoStartEmulatorWaitSeconds
{
    get => _autoStartEmulatorWaitSeconds;
    set => _autoStartEmulatorWaitSeconds = Math.Max(0, value);
}
```

- [ ] **Step 4: Re-run the focused model tests**

Run the command from Step 2. Expected: PASS.

### Task 2: Add a testable non-blocking delay seam

**Files:**
- Create: `src/UmamusumeWpfGui/Helper/IAsyncDelay.cs`
- Modify: `src/UmamusumeWpfGui/Bootstrapper.cs`

**Produces:** `IAsyncDelay.DelayAsync(TimeSpan)` with a production implementation backed by `Task.Delay`.

- [ ] **Step 1: Create the delay contract and implementation**

Create `IAsyncDelay.cs`:

```csharp
namespace UmamusumeWpfGui.Helper;

public interface IAsyncDelay
{
    Task DelayAsync(TimeSpan duration);
}

public sealed class AsyncDelay : IAsyncDelay
{
    public Task DelayAsync(TimeSpan duration) => Task.Delay(duration);
}
```

- [ ] **Step 2: Register the service**

In `Bootstrapper.ConfigureIoC`, after the `IEmulatorLauncher` binding, add:

```csharp
builder.Bind<IAsyncDelay>()
    .To<AsyncDelay>();
```

- [ ] **Step 3: Build the GUI project**

Run:

```powershell
dotnet build src/UmamusumeWpfGui/UmamusumeWpfGui.csproj
```

Expected: build succeeds; no ViewModel constructor has changed yet.

### Task 3: Wire the ViewModel draft, launch cache, and delay behavior

**Files:**
- Modify: `tests/UmamusumeWpfGui.Tests/ViewModels/SettingsViewModelTests.cs`
- Modify: `src/UmamusumeWpfGui/ViewModels/SettingsViewModel.cs`

**Consumes:** `ConnectionSettings.AutoStartEmulatorWaitSeconds`, `IAsyncDelay.DelayAsync(TimeSpan)`.

**Produces:** `SettingsViewModel.DraftAutoStartEmulatorWaitSeconds` and launch-time persistence of `DraftEmulatorExecutablePath`.

- [ ] **Step 1: Add failing ViewModel tests and fakes**

Add a `FakeAsyncDelay : IAsyncDelay` that stores durations and completes immediately:

```csharp
private sealed class FakeAsyncDelay : IAsyncDelay
{
    public List<TimeSpan> Durations { get; } = [];

    public Task DelayAsync(TimeSpan duration)
    {
        Durations.Add(duration);
        return Task.CompletedTask;
    }
}
```

Extend the test fixture to pass it to `SettingsViewModel` and expose it as `f.Delay`. Add tests that:

```csharp
[Fact]
public void Constructor_LoadsAutoStartWaitSeconds()
{
    var f = CreateFixture(new ConnectionSettings { AutoStartEmulatorWaitSeconds = 9 });
    var vm = f.CreateViewModel();
    Assert.Equal(9, vm.DraftAutoStartEmulatorWaitSeconds);
}

[Fact]
public async Task Connect_AutoStart_PersistsPathAndWaitsConfiguredDuration()
{
    var f = CreateFixture(new ConnectionSettings
    {
        AutoDetectConnection = true,
        AutoStartEmulator = true,
        EmulatorExecutablePath = @"C:\MuMu\MuMuNxDevice.exe",
        AutoStartEmulatorWaitSeconds = 7,
    });
    var vm = f.CreateViewModel();
    f.WinAdapter.NextDiscoveryResult = new DiscoveryResult([], []);

    await vm.ConnectAsync();

    Assert.Equal(@"C:\MuMu\MuMuNxDevice.exe", f.EmulatorLauncher.StartedPath);
    Assert.Equal([TimeSpan.FromSeconds(7)], f.Delay.Durations);
    Assert.Equal(@"C:\MuMu\MuMuNxDevice.exe", f.Settings.Load().EmulatorExecutablePath);
    Assert.Equal(0, f.UmaService.ConnectCallCount);
}
```

Add a second test with a launcher fake that returns `new EmulatorLaunchResult(false, "failed")`; assert `f.Delay.Durations` is empty.

- [ ] **Step 2: Run the focused ViewModel tests and confirm they fail to compile**

Run:

```powershell
dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelTests"
```

Expected: failure because the constructor lacks `IAsyncDelay` and the draft property does not exist.

- [ ] **Step 3: Add the new dependency and draft property**

In `SettingsViewModel`, add an `IAsyncDelay` constructor parameter and readonly field. Load the new draft after `_draftEmulatorExecutablePath`:

```csharp
_draftAutoStartEmulatorWaitSeconds = _draft.AutoStartEmulatorWaitSeconds;
```

Add the backing field and public property:

```csharp
private int _draftAutoStartEmulatorWaitSeconds;

public int DraftAutoStartEmulatorWaitSeconds
{
    get => _draftAutoStartEmulatorWaitSeconds;
    set
    {
        var clamped = Math.Max(0, value);
        if (_draftAutoStartEmulatorWaitSeconds == clamped)
            return;
        _draftAutoStartEmulatorWaitSeconds = clamped;
        OnPropertyChanged();
    }
}
```

In `SaveSettings`, copy the draft value:

```csharp
_draft.AutoStartEmulatorWaitSeconds = DraftAutoStartEmulatorWaitSeconds;
```

- [ ] **Step 4: Cache the emulator path and wait after a successful launch**

Add a private helper that writes only the startup path:

```csharp
private void PersistEmulatorExecutablePath()
{
    _draft.EmulatorExecutablePath = DraftEmulatorExecutablePath;
    _settingsService.Save(_draft);
}
```

In the `DraftAutoStartEmulator` branch of `RunDiscoveryAsync`, replace the current launch block with:

```csharp
PersistEmulatorExecutablePath();
var launch = _emulatorLauncher.Start(DraftEmulatorExecutablePath);
SetConnectionDiagnostic(launch.Message);
if (launch.Started && DraftAutoStartEmulatorWaitSeconds > 0)
{
    await _asyncDelay.DelayAsync(
        TimeSpan.FromSeconds(DraftAutoStartEmulatorWaitSeconds));
}
_connectionState.SetState(ConnectionState.Disconnected);
return;
```

- [ ] **Step 5: Re-run focused ViewModel tests**

Run the command from Step 2. Expected: PASS with no real-time wait.

### Task 4: Add the Connection-panel setting and localization

**Files:**
- Modify: `tests/UmamusumeWpfGui.Tests/Views/SettingsViewContractTests.cs`
- Modify: `src/UmamusumeWpfGui/Views/SettingsView.xaml`
- Modify: `src/UmamusumeWpfGui/Resources/Strings.en-US.xaml`
- Modify: `src/UmamusumeWpfGui/Resources/Strings.zh-CN.xaml`

**Consumes:** `SettingsViewModel.DraftAutoStartEmulatorWaitSeconds`.

**Produces:** A localized non-negative numeric setting in the existing auto-start section.

- [ ] **Step 1: Add failing XAML contract assertions**

In `ConnectionPanel_HasRequiredElements`, add:

```csharp
Assert.True(content.Contains("AutoStartEmulatorWaitSecondsLabel"),
    "Connection panel should label the emulator startup wait setting");
Assert.True(content.Contains("DraftAutoStartEmulatorWaitSeconds"),
    "Connection panel should bind the emulator startup wait setting");
```

Add a test that reads each resource file and asserts the key occurs once:

```csharp
[Fact]
public void ConnectionResources_ContainAutoStartWaitLabel()
{
    Assert.Contains("AutoStartEmulatorWaitSecondsLabel",
        File.ReadAllText(Path.Combine(ProjectDir, "src", "UmamusumeWpfGui",
            "Resources", "Strings.en-US.xaml")));
    Assert.Contains("AutoStartEmulatorWaitSecondsLabel",
        File.ReadAllText(Path.Combine(ProjectDir, "src", "UmamusumeWpfGui",
            "Resources", "Strings.zh-CN.xaml")));
}
```

- [ ] **Step 2: Run the XAML contract tests and confirm they fail**

Run:

```powershell
dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --filter "FullyQualifiedName~SettingsViewContractTests"
```

Expected: failure because the new resource key and binding are absent.

- [ ] **Step 3: Add the localized resource strings**

After `EmulatorExecutablePathHint` in each resource file, add:

```xml
<!-- Strings.en-US.xaml -->
<sys:String x:Key="AutoStartEmulatorWaitSecondsLabel">Wait after startup (seconds)</sys:String>
```

```xml
<!-- Strings.zh-CN.xaml -->
<sys:String x:Key="AutoStartEmulatorWaitSecondsLabel">启动后等待（秒）</sys:String>
```

- [ ] **Step 4: Add the numeric control to the existing auto-start Grid**

Change the Grid at `SettingsView.xaml` around lines 236–251 to use three `Auto` rows. Retain the existing checkbox and path TextBox. Add this third-row control:

```xml
<StackPanel Grid.Row="2" Orientation="Horizontal" Margin="0,6,0,0">
  <TextBlock Text="{DynamicResource AutoStartEmulatorWaitSecondsLabel}"
             VerticalAlignment="Center"
             Foreground="{DynamicResource TextSecondaryBrush}" />
  <hc:NumericUpDown Value="{Binding DraftAutoStartEmulatorWaitSeconds}"
                    Minimum="0"
                    Maximum="120"
                    Width="80"
                    Height="30"
                    Margin="8,0,0,0"
                    VerticalAlignment="Center" />
</StackPanel>
```

- [ ] **Step 5: Re-run the XAML contract tests**

Run the command from Step 2. Expected: PASS.

### Task 5: Verify the complete feature

**Files:** All files changed by Tasks 1–4.

- [ ] **Step 1: Build the solution**

Run:

```powershell
dotnet build src/UmamusumeWpfGui/UmamusumeWpfGui.csproj
```

Expected: exit code `0` with no new warnings or errors.

- [ ] **Step 2: Run the WPF GUI test project**

Run:

```powershell
dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj
```

Expected: all tests pass.

- [ ] **Step 3: Manual behavior check**

Launch the app, enter an emulator executable path and a wait value, enable auto-start, then trigger Connect with no emulator running. Verify the path remains after restarting the app and that the app waits the configured duration before returning to the disconnected state. Verify it does not retry discovery or initiate a connection automatically.

## Plan Self-Review

- **Spec coverage:** Task 1 provides the persisted defaulted setting; Task 3 performs the configured non-blocking wait and launch-time path cache; Task 4 supplies localized Connection UI; Task 5 verifies no retry/connection behavior was added.
- **Ambiguity resolved:** Caching occurs when auto-start is requested, which covers the path that is actually used even when the connection never succeeds. Existing explicit Save behavior remains unchanged.
- **Testability:** `IAsyncDelay` records the configured wait in tests, avoiding real multi-second sleeps.
- **Type consistency:** All planned public names are `AutoStartEmulatorWaitSeconds`, `DraftAutoStartEmulatorWaitSeconds`, and `IAsyncDelay.DelayAsync(TimeSpan)`.
