# WPF Layout Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the two-tab WPF shell with an original light-only navigation rail, add a truthful read-only Overview, and restyle Logs and Settings using `DESIGN.md` while preserving every existing connection behavior.

**Architecture:** `RootViewModel` owns cached Overview, Log, and Settings child view models, exposes a selected navigation index, and swaps one `ActiveContent` reference in the root `ContentControl`. `LogViewModel` becomes a singleton because it subscribes to core events; navigation never recreates or resubscribes it. The new Overview reads only `IConnectionStateService` and `IUmaService` data already present in the app.

**Tech Stack:** .NET WPF, Stylet, StyletIoC, HandyControls, XAML ResourceDictionaries, xUnit.

## Global Constraints

- Follow the root `DESIGN.md`; every new brush, font size, spacing value, and reused component must use its declared tokens.
- Use a fixed light theme only. Do not add dark mode, theme settings, background images, external assets, or new dependencies.
- Do not copy MaaAssistantArknights code, XAML, assets, strings, brush values, navigation names, icons, or templates. Only its generic desktop information-architecture principle is in scope.
- Preserve all existing connection, device selection, localization, JSON persistence, log collection, and Settings commands.
- Do not introduce a task queue, fake dashboard data, or a disabled control-center shell. Native task-control APIs do not exist.
- Every new string goes in both `Resources/Strings.en-US.xaml` and `Resources/Strings.zh-CN.xaml`.
- Use test-first steps. Do not suppress warnings or type errors. Do not commit unless the user explicitly asks.

---

## File structure

| File | Responsibility |
| --- | --- |
| `src/UmamusumeWpfGui/Models/RootNavigationItem.cs` | Immutable navigation metadata used by the root rail. |
| `src/UmamusumeWpfGui/ViewModels/OverviewViewModel.cs` | Read-only projection of existing connection and core data. |
| `src/UmamusumeWpfGui/ViewModels/RootViewModel.cs` | Cached child VMs and root navigation state. |
| `src/UmamusumeWpfGui/Views/OverviewView.xaml` | Overview layout with connected and no-connection states. |
| `src/UmamusumeWpfGui/Views/RootView.xaml` | Two-column window shell and primary navigation rail. |
| `src/UmamusumeWpfGui/Res/Themes/Light.xaml` | `DESIGN.md` brush-token declarations. |
| `src/UmamusumeWpfGui/Res/Icons.xaml` | Original simple geometry resources for the three root sections. |
| `src/UmamusumeWpfGui/Res/Theme.xaml` | Merge entry for `Icons.xaml`. |
| `src/UmamusumeWpfGui/Views/LogView.xaml` | Token-driven, status-marker log presentation. |
| `src/UmamusumeWpfGui/Views/SettingsView.xaml` | Existing behavior with token-driven visual polish. |
| `tests/UmamusumeWpfGui.Tests/...` | New unit and XAML contract coverage; existing contract updates. |

### Task 1: Establish localization and the light design tokens

**Files:**
- Modify: `src/UmamusumeWpfGui/Resources/Strings.en-US.xaml`
- Modify: `src/UmamusumeWpfGui/Resources/Strings.zh-CN.xaml`
- Modify: `src/UmamusumeWpfGui/Res/Themes/Light.xaml`
- Modify: `tests/UmamusumeWpfGui.Tests/Theme/ThemeResourceTests.cs`

**Interfaces:**
- Produces DynamicResource keys `NavOverview`, `OverviewTitle`, `OverviewConnectionStatus`, `OverviewDevice`, `OverviewCoreVersion`, `OverviewNoConnection`, and `OverviewOpenSettings` in both locales.
- Produces the `DESIGN.md` brush keys, including `SurfaceCanvasBrush`, `SurfaceSidebarBrush`, `SurfacePanelBrush`, `SurfaceRaisedBrush`, `TextPrimaryBrush`, `TextSecondaryBrush`, `TextDisabledBrush`, `BorderDefaultBrush`, `BorderSubtleBrush`, `AccentPrimaryBrush`, `AccentHoverBrush`, `StatusSuccessBrush`, `StatusWarningBrush`, `StatusErrorBrush`, and `StatusInfoBrush`.

- [ ] **Step 1: Write failing resource-contract tests**

Add tests that parse both locale dictionaries, assert all seven Overview string keys exist, and parse `Light.xaml` to assert every design brush key from `DESIGN.md` exists. Assert that the previous `PrimaryBrush`, `GoldBrush`, and `WindowBackgroundGradient` keys are absent.

- [ ] **Step 2: Run the focused tests and verify red**

Run: `dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --filter "FullyQualifiedName~ThemeResourceTests"`

Expected: FAIL because the Overview keys and design-system brush keys do not exist yet.

- [ ] **Step 3: Add the localized strings and replace the palette**

Add the following values, using XAML resources rather than hardcoded view text:

| Key | en-US | zh-CN |
| --- | --- | --- |
| `NavOverview` | `Overview` | `概览` |
| `OverviewTitle` | `Overview` | `概览` |
| `OverviewConnectionStatus` | `Connection status` | `连接状态` |
| `OverviewDevice` | `Device` | `设备` |
| `OverviewCoreVersion` | `Core version` | `核心版本` |
| `OverviewNoConnection` | `No device connected. Configure a connection in Settings.` | `未连接设备。请在设置中配置连接。` |
| `OverviewOpenSettings` | `Open settings` | `打开设置` |

Replace the old pink/gold brushes in `Light.xaml` with exactly the brush keys and colors from `DESIGN.md`. Do not retain compatibility aliases: all dependent XAML will be migrated in later tasks.

- [ ] **Step 4: Run the focused tests and verify green**

Run: `dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --filter "FullyQualifiedName~ThemeResourceTests"`

Expected: PASS; both locale dictionaries and the light palette satisfy their contracts.

### Task 2: Add the read-only Overview ViewModel

**Files:**
- Create: `src/UmamusumeWpfGui/ViewModels/OverviewViewModel.cs`
- Create: `tests/UmamusumeWpfGui.Tests/ViewModels/OverviewViewModelTests.cs`

**Interfaces:**
- Consumes: `IConnectionStateService.State`, `IConnectionStateService.LastVerifiedConnection`, `IConnectionStateService.StateChanged`, `IUmaService.CoreVersion`.
- Produces: `ConnectionState State`, `LastVerifiedConnection? LastVerifiedConnection`, `string CoreVersion`, `bool HasVerifiedConnection`, and `event PropertyChangedEventHandler? PropertyChanged`.

- [ ] **Step 1: Write failing ViewModel tests**

Using fakes shaped like those in `SettingsViewModelTests.cs`, add tests named:

```csharp
[Fact] public void Constructor_UsesCurrentConnectionState();
[Fact] public void Constructor_UsesCurrentLastVerifiedConnection();
[Fact] public void StateChanged_RaisesStateAndConnectionProperties();
[Fact] public void CoreVersion_UsesUmaServiceValue();
[Fact] public void HasVerifiedConnection_IsFalseWhenSnapshotIsNull();
[Fact] public void HasVerifiedConnection_IsTrueWhenSnapshotExists();
[Fact] public void Dispose_UnsubscribesFromStateChanged();
[Fact] public void Dispose_IsIdempotent();
```

- [ ] **Step 2: Run the new test file and verify red**

Run: `dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --filter "FullyQualifiedName~OverviewViewModelTests"`

Expected: FAIL because `OverviewViewModel` does not exist.

- [ ] **Step 3: Implement the minimal read-only projection**

Implement `OverviewViewModel` as `IDisposable` and `INotifyPropertyChanged`. Subscribe once to `StateChanged`; on notification raise property changes for `State`, `LastVerifiedConnection`, and `HasVerifiedConnection`. Expose `CoreVersion` directly from the injected `IUmaService`. Do not add Connect, Cancel, task, or command methods.

- [ ] **Step 4: Run the new test file and verify green**

Run: `dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --filter "FullyQualifiedName~OverviewViewModelTests"`

Expected: PASS with all eight behavior tests green.

### Task 3: Build the Overview view and its XAML contract tests

**Files:**
- Create: `src/UmamusumeWpfGui/Views/OverviewView.xaml`
- Create: `src/UmamusumeWpfGui/Views/OverviewView.xaml.cs`
- Create: `tests/UmamusumeWpfGui.Tests/Views/OverviewViewContractTests.cs`

**Interfaces:**
- Consumes: `OverviewViewModel.State`, `LastVerifiedConnection`, `CoreVersion`, and `HasVerifiedConnection`.
- Produces: a scrollable root page with a status panel, a device/core panel, and no connection commands.

- [ ] **Step 1: Write failing XAML contracts**

Create contracts asserting that `OverviewView.xaml` exists, has `UserControl` root, contains a `ScrollViewer`, binds `CoreVersion`, binds `LastVerifiedConnection`, uses `OverviewTitle` and `OverviewConnectionStatus` through `DynamicResource`, and contains no `ConnectCommand`, `CancelCommand`, or `TaskQueue` text.

- [ ] **Step 2: Run the contract tests and verify red**

Run: `dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --filter "FullyQualifiedName~OverviewViewContractTests"`

Expected: FAIL because the XAML file does not exist.

- [ ] **Step 3: Implement the Overview layout**

Use `SurfaceCanvasBrush` for the page and two `SurfacePanelBrush` panels in a `Grid` with 16px spacing. The status panel uses the enum state and the existing connection state resources; the device panel shows `LastVerifiedConnection` fields only when `HasVerifiedConnection` is true. When false, render `OverviewNoConnection`. Use `NullToVisibilityConverter` or a boolean-trigger pattern already present in the project. Use no fabricated operational metric and no actionable connection button.

- [ ] **Step 4: Run the contract tests and verify green**

Run: `dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --filter "FullyQualifiedName~OverviewViewContractTests"`

Expected: PASS.

### Task 4: Refactor composition and root navigation state

**Files:**
- Create: `src/UmamusumeWpfGui/Models/RootNavigationItem.cs`
- Modify: `src/UmamusumeWpfGui/ViewModels/RootViewModel.cs`
- Modify: `src/UmamusumeWpfGui/Bootstrapper.cs`
- Create: `tests/UmamusumeWpfGui.Tests/ViewModels/RootViewModelTests.cs`
- Modify: `tests/UmamusumeWpfGui.Tests/BootstrapperRegistrationTests.cs`

**Interfaces:**
- `RootNavigationItem(string labelKey, int index)` holds only stable metadata.
- `RootViewModel` produces `IReadOnlyList<RootNavigationItem> NavigationItems`, `int SelectedNavigationIndex`, and `object ActiveContent`.
- `SelectedNavigationIndex` maps 0 to Overview, 1 to Logs, and 2 to Settings.

- [ ] **Step 1: Write failing root-navigation and registration tests**

Add tests that assert RootViewModel starts on Overview, changes ActiveContent for each valid index, rejects invalid indexes without changing the active child, and disposes each owned child once. Update Bootstrapper tests to assert `LogViewModel` resolves as the same instance twice and `OverviewViewModel` resolves successfully.

- [ ] **Step 2: Run the focused tests and verify red**

Run: `dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --filter "FullyQualifiedName~RootViewModelTests|FullyQualifiedName~BootstrapperRegistrationTests"`

Expected: FAIL because the navigation API and Overview registration do not exist.

- [ ] **Step 3: Implement cached composition**

Register `LogViewModel` with `InSingletonScope()` and register `OverviewViewModel` normally in `Bootstrapper`. Inject Overview, Log, and Settings into `RootViewModel`, retain them as child properties, expose exactly three `RootNavigationItem` entries using `NavOverview`, `TabLog`, and `TabSettings`, and update `ActiveContent` plus `PropertyChanged` when the selected index changes. Keep `LogViewModel.Dispose()` only in `RootViewModel.Dispose()` at application shutdown.

- [ ] **Step 4: Run the focused tests and verify green**

Run: `dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --filter "FullyQualifiedName~RootViewModelTests|FullyQualifiedName~BootstrapperRegistrationTests"`

Expected: PASS. The LogViewModel is long-lived, root navigation is deterministic, and child disposal is idempotent.

### Task 5: Replace the root tabs with an original navigation shell

**Files:**
- Create: `src/UmamusumeWpfGui/Res/Icons.xaml`
- Modify: `src/UmamusumeWpfGui/Res/Theme.xaml`
- Modify: `src/UmamusumeWpfGui/Views/RootView.xaml`
- Modify: `src/UmamusumeWpfGui/Views/RootView.xaml.cs`
- Modify: `tests/UmamusumeWpfGui.Tests/Views/RootViewContractTests.cs`

**Interfaces:**
- Consumes: `NavigationItems`, `SelectedNavigationIndex`, and `ActiveContent` from `RootViewModel`.
- Produces: a 184px root navigation rail and an `s:View.Model` content host.

- [ ] **Step 1: Rewrite failing root XAML contracts**

Replace tab-specific assertions with tests asserting: an 184px left `ColumnDefinition`, a navigation `ItemsControl` bound to `NavigationItems`, a `ContentControl` bound to `ActiveContent` through `s:View.Model`, `SurfaceCanvasBrush` window background, `SurfaceSidebarBrush` rail background, exactly three label keys, and no `TabControl` or `TabItem` elements.

- [ ] **Step 2: Run the contract tests and verify red**

Run: `dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --filter "FullyQualifiedName~RootViewContractTests"`

Expected: FAIL because the existing root window still uses a TabControl.

- [ ] **Step 3: Implement the root shell**

Create three original vector `Geometry` resources in `Icons.xaml` for overview, logs, and settings, merge that dictionary from `Theme.xaml`, and use a two-column `Grid` in `RootView.xaml`. Bind selection to `SelectedNavigationIndex` using a WPF selector-compatible pattern, rather than manual mouse-only code. Each navigation item includes geometry, localized text, selected fill, hover, and keyboard focus state. Host `ActiveContent` with Stylet `s:View.Model` in the flexible column.

- [ ] **Step 4: Run root contracts and verify green**

Run: `dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --filter "FullyQualifiedName~RootViewContractTests"`

Expected: PASS. There are no root tabs and all three pages have a stable navigation entry.

### Task 6: Apply the design system to Logs and Settings without changing behavior

**Files:**
- Modify: `src/UmamusumeWpfGui/Res/Style.xaml`
- Modify: `src/UmamusumeWpfGui/Res/Styles/Button.xaml`
- Modify: `src/UmamusumeWpfGui/Res/Styles/TextBox.xaml`
- Modify: `src/UmamusumeWpfGui/Res/Styles/ComboBox.xaml`
- Modify: `src/UmamusumeWpfGui/Res/Styles/CheckBox.xaml`
- Modify: `src/UmamusumeWpfGui/Res/Styles/ScrollBar.xaml`
- Modify: `src/UmamusumeWpfGui/Converters/LogEntryKindToColorConverter.cs`
- Modify: `src/UmamusumeWpfGui/Views/LogView.xaml`
- Modify: `src/UmamusumeWpfGui/Views/SettingsView.xaml`
- Modify: `tests/UmamusumeWpfGui.Tests/Converters/LogEntryKindToColorConverterTests.cs`
- Modify: `tests/UmamusumeWpfGui.Tests/Views/SettingsViewContractTests.cs`

**Interfaces:**
- Consumes: the brush tokens from Task 1 and existing Log/Settings bindings and commands.
- Produces: light-only panel surfaces, semantic log markers, focusable inputs, and the existing Settings groups without new business behavior.

- [ ] **Step 1: Write failing visual-contract and converter tests**

Update the converter tests to assert `Info` maps to `StatusInfoBrush`, `Success` maps to `StatusSuccessBrush`, and `Failure` maps to `StatusErrorBrush`. Update Settings contracts to expect `SurfaceCanvasBrush`, `SurfaceSidebarBrush`, `SurfacePanelBrush`, `BorderDefaultBrush`, and `AccentPrimaryBrush`, while retaining the existing connection, language, and system binding assertions.

- [ ] **Step 2: Run focused tests and verify red**

Run: `dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --filter "FullyQualifiedName~LogEntryKindToColorConverterTests|FullyQualifiedName~SettingsViewContractTests"`

Expected: FAIL because the old pink brush names and converter behavior are still present.

- [ ] **Step 3: Implement visual-only XAML changes**

Replace every old brush reference in the styles and target views with `DESIGN.md` resources. Preserve all existing commands, converters, collection bindings, and settings-panel visibility behavior. In `LogView.xaml`, retain the three columns and auto-scroll code-behind, but render a small semantic severity marker and mono timestamp instead of tinting the full row. Give the selected item `SurfaceRaisedBrush` and a discreet `AccentPrimaryBrush` indication. In Settings, keep its current internal three-section navigation, but use the root design tokens for surfaces, input focus, disabled explanation text, and primary/secondary actions.

- [ ] **Step 4: Run focused tests and verify green**

Run: `dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --filter "FullyQualifiedName~LogEntryKindToColorConverterTests|FullyQualifiedName~SettingsViewContractTests"`

Expected: PASS. Existing Settings commands remain covered and log color mapping uses the new semantic tokens.

### Task 7: Run the full verification and WPF visual QA

**Files:**
- Modify only if verification exposes an issue caused by Tasks 1–6.

- [ ] **Step 1: Run the complete automated suite**

Run: `dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj`

Expected: PASS with no new failures.

- [ ] **Step 2: Build the production application**

Run: `dotnet build src/UmamusumeWpfGui/UmamusumeWpfGui.csproj -c Release --no-restore`

Expected: exit code 0 and no warnings introduced by the redesign.

- [ ] **Step 3: Run WPF visual checks**

Launch the application at its 960×680 minimum and a wider desktop size. Verify Overview with and without a verified device, Logs with empty and colored event rows, Settings input focus, navigation hover/focus/selected states, and both English and Simplified Chinese resources. Record screenshots and fix only regressions caused by this redesign.

- [ ] **Step 4: Check design-system compliance**

Search the changed WPF views/styles for old palette keys and raw color literals. Confirm all new colors map to `DESIGN.md`, all new spacing follows the 4px scale, and no dark-mode or MAA resource reference was introduced.

## Self-review

- **Spec coverage:** Tasks 1–6 implement the light-only token system, root navigation, truthful Overview, elevated Logs and Settings, localization, and existing-behavior preservation. Task 7 verifies the result. Deferred control-center, settings search, tray, and dark mode remain intentionally absent.
- **Placeholder scan:** This plan contains no unspecified implementation placeholder. Future control work is explicitly excluded, not left incomplete.
- **Type consistency:** `RootNavigationItem`, `OverviewViewModel`, `SelectedNavigationIndex`, and `ActiveContent` are the names used consistently by subsequent tasks.
