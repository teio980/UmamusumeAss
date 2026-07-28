# WPF Layout Redesign Design

**Goal:** Rework the existing UmamusumeAss WPF interface into an original, light-only desktop control desk whose navigation, content density, and live-log placement take generic inspiration from MaaAssistantArknights without copying any of its code, assets, names, colors, templates, terminology, or page content.

## Scope and constraints

- Retain all existing connection, device selection, language, persistence, and log behavior.
- Preserve the Stylet MVVM architecture, HandyControls dependency, resource-dictionary localization, and xUnit contract-test style.
- Do not introduce task automation, fake metrics, a background image, a tray feature, a dark theme, a theme switcher, or a three-pane control center before the native control APIs exist.
- Create a root `DESIGN.md` as the token source for all subsequent XAML visual changes.
- Preserve both English and Simplified Chinese localized resource coverage.
- Do not copy MAA source code, XAML templates, visual assets, app strings, icons, color values, control names, top-level labels, or game-specific concepts.

## Evidence informing the design

- The current window is a 960×680 `TabControl` shell with only Log and Settings; settings contains a nested 160px side navigation with visibility-switched panels.
- The reference project demonstrates transferable layout principles: stable primary navigation, a compact settings category rail, a visible event stream, and disciplined content density.
- `SettingsViewModel` owns the live connection lifecycle; `LogViewModel` owns an event subscription and must retain its lifetime while views switch.
- Current native control APIs are not implemented. A task queue or command center would be a non-functional shell, so it is explicitly deferred.

## Approved visual direction

The redesign uses a fixed light palette rather than a dark theme. A quiet warm-gray canvas, a restrained violet accent, neutral panels, and semantic green/amber/red event states create a clear desktop-tool identity. The design is intentionally not game-themed and does not use anime art, MAA assets, or branded decoration.

See root `DESIGN.md` for the exact tokens, type scale, panel conventions, motion rules, and visual constraints.

## Information architecture

### Stage 1, implement now

1. **Overview**: a read-only landing page showing existing connection state, last verified device data when available, core version, and a compact recent-event summary. It has no duplicate Connect action.
2. **Logs**: the existing log list promoted to a first-class primary section, with an intentional empty state and status-aware visual hierarchy.
3. **Settings**: the existing connection, language, and system panels remain the authoritative place for configuration and connection actions. The internal menu stays until a later settings-information-architecture need exists.

The root window has a fixed left navigation rail and a flexible content pane. It replaces the top-level `TabControl`; the rail holds Overview, Logs, and Settings. This follows the reference project’s stable shell concept while using original labels, tokens, layout, and implementation.

### Deferred deliberately

- A task/control center with three panes (action list, configuration, live log) waits for usable control APIs.
- Searchable two-pane settings waits until the application has enough genuine categories to justify a settings-entry data model.
- Profile switching, tray integration, notification overlays, dynamic background art, and theme settings are out of scope.

## View and ViewModel design

### Root shell

- `RootView` becomes a two-column grid: 184px navigation rail plus flexible content.
- The root content is a `ContentControl` driven by an active, cached ViewModel reference. Stylet selects the matching View for each ViewModel.
- Navigation uses a small root-level model with localized label keys, icon geometry keys, and selection state. The selection state has keyboard-visible focus and a clear active marker.
- `RootViewModel` owns one long-lived instance each of Overview, Log, and Settings ViewModels. Switching navigation does not recreate `LogViewModel` or duplicate its core event subscription.

### Overview

- `OverviewViewModel` consumes only current, existing data: `IConnectionStateService`, `IUmaService.CoreVersion`, and any existing last-verified connection snapshot exposed through the settings flow.
- It presents a truthful disconnected state when no connection has been verified. It does not fabricate a device, task count, readiness score, or automation state.
- It contains a contextual secondary action that navigates to Settings when connection configuration is needed, rather than replicating connection commands.

### Logs

- Preserve the existing bounded event collection and core event mapping.
- Update only XAML layout and semantic presentation: mono timestamps, concise primary message, semantic severity treatment, selected-row state, and an intentional empty state.
- Keep automatic scroll behavior accessible and avoid suppressing user inspection while events arrive.

### Settings

- Keep connection ownership and all existing commands in `SettingsViewModel`.
- Update the surface system, spacing, labels, grouping, disabled explanation, and focused input states based on `DESIGN.md`.
- Retain the current three configuration groups and their localized strings. Do not create a search function before there is a settings-entry model and sufficient category count.

## Visual layout

- Window canvas: `SurfaceCanvasBrush`; navigation rail: `SurfaceSidebarBrush`; content panels: `SurfacePanelBrush`.
- Main content starts with a page title and short operational hint, followed by panels with 20px interior padding and 16px gaps.
- Overview status and device/core details use a two-column grid above a recent activity panel. At the minimum window width, supporting detail wraps below rather than compressing the status.
- Logs prioritize a wide reading area. Severity is conveyed by a small marker and text color, not by high-saturation full-row fills.
- Settings retains its compact categories and content panel, but replaces generic pink borders and heavyweight button treatment with the design-system tokens.

## States, accessibility, and localization

- Required states: no connection, detecting, connecting, connected, disconnected, failed, selection required, no log entries, and disabled capability.
- Every state provides concrete next-step text. A disabled control area must say why it is unavailable.
- Keyboard users can tab through root navigation, buttons, form fields, lists, and dialogs with visible focus treatment.
- All labels and new user-visible strings are resource keys in both `Strings.en-US.xaml` and `Strings.zh-CN.xaml`.
- Use simple vector geometry resources or existing framework mechanisms for icons. Do not use emoji or imported brand icons.

## Testing and verification plan

- Update root XAML contract tests from exactly two `TabItem` elements to three root navigation items plus a `ContentControl` host.
- Move root-navigation layout assertions out of Settings tests; preserve the current Settings panel behavior tests.
- Add unit tests for root navigation state and overview state mapping with no verified device.
- Preserve `LogViewModel` lifetime while switching root sections; add a regression test that navigation does not duplicate subscriptions or lose retained entries.
- Run the targeted WPF test project and the existing full test/build commands. Use WPF runtime inspection or screenshots for visual QA at the minimum window size and a wider desktop size, including hover, focus, empty, error, and disconnected states.

## Non-goals

- This redesign does not create the future task/control feature itself.
- It does not migrate to a different UI framework, add a web frontend, or replace Stylet.
- It does not add theme preferences, dark mode, online assets, or new external dependencies.

## Design review conclusion

The project can credibly ship a MAA-inspired structural redesign now if it limits itself to a root navigation shell, a truthful read-only Overview, stronger Logs, and polished Settings. The more ambitious three-pane control center is preserved as an explicit future extension rather than a disabled visual imitation.
