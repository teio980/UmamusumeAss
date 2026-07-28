# Compact WPF Controls Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Replace default-looking shared WPF controls with a compact, original, light desktop-tool system without changing application behavior.

**Architecture:** Keep all controls resource-dictionary based. Add control templates only where WPF defaults prevent the declared visual behavior; preserve every existing style key and binding so consuming views do not change.

**Tech Stack:** .NET 10 WPF, XAML ResourceDictionaries, xUnit static XAML contracts.

## Global Constraints

- Use only tokens from `DESIGN.md`; light theme only; no MAA source, visual asset, markup, or color value.
- Preserve bindings, commands, localization, converter behavior, and existing public style keys.
- Do not add dependencies, dark mode, gradients, or a business-logic change.
- Do not commit unless explicitly requested.

### Task 1: Compact buttons and inputs

**Files:**
- Modify: `src/UmamusumeWpfGui/Res/Styles/Button.xaml`
- Modify: `src/UmamusumeWpfGui/Res/Styles/TextBox.xaml`
- Modify: `src/UmamusumeWpfGui/Res/Styles/ComboBox.xaml`
- Modify: `tests/UmamusumeWpfGui.Tests/Theme/ThemeResourceTests.cs`

- [ ] Add failing resource contracts for 34px primary buttons, 32px text/combo inputs, token-based borders, and focus/hover triggers.
- [ ] Run: `dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --filter "FullyQualifiedName~ThemeResourceTests"`; expect failure because compact template values are absent.
- [ ] Add compact XAML styles and ControlTemplates using `AccentPrimaryBrush`, `AccentHoverBrush`, `SurfacePanelBrush`, `SurfaceRaisedBrush`, and `BorderDefaultBrush`.
- [ ] Re-run the focused tests; expect pass.

### Task 2: Compact checkboxes, lists, and scrollbars

**Files:**
- Modify: `src/UmamusumeWpfGui/Res/Styles/CheckBox.xaml`
- Modify: `src/UmamusumeWpfGui/Res/Styles/ScrollBar.xaml`
- Modify: `src/UmamusumeWpfGui/Res/Style.xaml`
- Modify: `tests/UmamusumeWpfGui.Tests/Theme/ThemeResourceTests.cs`

- [ ] Add failing resource contracts for a 16px checkbox indicator, checked accent state, 8px scrollbar width, list-row hover state, and bottom dividers.
- [ ] Run the focused theme tests; expect failure because templates are absent.
- [ ] Implement the templates with vector tick geometry and token-driven triggers.
- [ ] Re-run the focused tests; expect pass.

### Task 3: Apply and verify shared control system

**Files:**
- Modify only view files that need a nonbehavioral style-key adjustment after Tasks 1–2.
- Test: `tests/UmamusumeWpfGui.Tests/`

- [ ] Run the full suite: `dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --no-restore`.
- [ ] Stop the running application and run `dotnet build src/UmamusumeWpfGui/UmamusumeWpfGui.csproj -c Release --no-restore`.
- [ ] Launch the Release app and verify hover, focus, checked, disabled, selected list, open dropdown, and scrollbar states manually.

## Self-review

- Shared controls are the only target; Settings and connection logic stay unchanged.
- The plan has no placeholder requirements.
- Every visual value maps to the existing design system tokens.
