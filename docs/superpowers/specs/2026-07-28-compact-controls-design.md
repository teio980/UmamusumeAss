# Compact WPF Controls Design

## Goal

Replace the remaining default-looking WPF controls with an original compact desktop-tool system. The system takes generic inspiration from MAA's dense, stateful desktop-tool rhythm but does not copy its templates, colors, assets, labels, or code.

## Scope

- Update shared styles for Button, TextBox, ComboBox, CheckBox, ScrollBar, ListBox, ListBoxItem, and Border.
- Keep all existing bindings, commands, converters, localization, and connection logic unchanged.
- Apply the styles consistently to Root, Settings, Logs, and Selection Dialog through existing resource dictionaries.

## Control rules

### Buttons

- Standard height: 34px; compact actions: 28px.
- Primary action: solid `AccentPrimaryBrush`, white text, 4px radius.
- Secondary action: `SurfacePanelBrush`, `BorderDefaultBrush`, 4px radius.
- Hover: `AccentHoverBrush` for primary; `SurfaceRaisedBrush` for secondary.
- Disabled: muted foreground and reduced emphasis, with no layout shift.

### Inputs and ComboBoxes

- Standard height: 32px; 4px radius; 1px `BorderDefaultBrush` outline.
- Default white surface; hover uses `SurfaceRaisedBrush`; focus uses `AccentPrimaryBrush` outline.
- ComboBox popup follows the same panel/border tokens and has compact rows.

### CheckBoxes

- 16px square indicator with 1px border.
- Checked state is `AccentPrimaryBrush` with a white vector tick.
- Label spacing is 8px with no default system bevel or oversized chrome.

### Lists and scrollbars

- Navigation/list rows use a transparent surface and subtle bottom divider, with `SurfaceRaisedBrush` hover.
- Scrollbars are narrow (8px), neutral by default, and use the accent only while thumb is active.

## Constraints

- Light theme only; no gradients, dark-mode toggle, third-party UI library, remote asset, or icon import.
- All colors come from `DESIGN.md` tokens; all spacing is a multiple of 4px.
- No MAA assets, XAML, names, color values, or markup may be copied.

## Verification

- Add or update XAML contract tests for compact height, token-based borders, focus/hover triggers, checkbox checked state, list dividers, and narrow scrollbar width.
- Run focused theme/control tests, full `UmamusumeWpfGui.Tests`, and Release build after closing the launched application.
- Manually inspect the running WPF app with hover, focus, checked, disabled, open dropdown, selected list, and scroll states.
