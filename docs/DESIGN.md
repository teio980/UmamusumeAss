# UmamusumeAss Design System

## 1. Atmosphere & Identity

A desktop toolshed for Umamusume: Pretty Derby — professional, focused, and visually warm. The signature is a pink-and-gold accent palette over a soft gradient background, evoking the game's visual identity without mimicking it. Surfaces are clean and card-based, with HandyControls providing consistent Win32-native feel. The application is dense with information when needed, spacious when not.

## 2. Color

### Palette

| Token | Value | Usage |
|-------|-------|-------|
| `PrimaryBrush` | `#E91E8C` | Buttons, active tab, selected nav item |
| `PrimaryLightBrush` | `#FF6B9D` | Hover states |
| `PrimaryLighterBrush` | `#FFB3D0` | Secondary backgrounds, dividers |
| `PrimaryLightestBrush` | `#FFE4EC` | Gradient start, selected nav background |
| `GoldBrush` | `#FFB300` | Accent, badges |
| `WindowBackgroundBrush` | `#FFF0F5` | Window background (mid-gradient) |
| `CardBackgroundBrush` | `#FFFFFF` | Card/panel backgrounds |
| `TextPrimaryBrush` | `#2D2D2D` | Body text |
| `TextSecondaryBrush` | `#888888` | Secondary text |
| `TextOnPrimaryBrush` | `#FFFFFF` | Text on primary buttons |
| `DividerBrush` | `#FFE4EC` | Dividers |
| `SuccessBrush` | `#4CAF50` | Connection success indicator |
| `ErrorBrush` | `#F44336` | Connection failure indicator |

### Window Background Gradient

Linear gradient top-to-bottom: `#FFE4EC` (top) → `#FFF0F5` (mid) → `#FFFFFF` (bottom).

### Rules

- `PrimaryBrush` is used **only** for interactive elements and active indicators — never decoration.
- No color is introduced outside this table. Extend the table first.
- Status colors (`SuccessBrush`, `ErrorBrush`) are reserved for connection/diagnostics feedback only.

## 3. Typography

### Scale

| Level | Size | Weight | Color | Usage |
|-------|------|--------|-------|-------|
| Heading | 16 px | Bold (700) | `TextPrimaryBrush` | Section titles, card headings |
| Body | 14 px | Normal (400) | `TextPrimaryBrush` | Default text, labels |
| Body Secondary | 14 px | Normal (400) | `TextSecondaryBrush` | Captions, hints, secondary info |
| Button Primary | 18 px | Semi-bold (600) | `TextOnPrimaryBrush` | Connect action button |
| Button Secondary | 14 px | Normal (400) | `TextPrimaryBrush` | Cancel, Browse, Save |
| Nav Item | 14 px | Normal (400) | `TextPrimaryBrush` / `TextSecondaryBrush` | Settings left-nav items |

### Font Stack

- Primary: `"Microsoft YaHei UI", "Segoe UI", -apple-system, sans-serif`
- Mono: `"Cascadia Code", "Consolas", "Courier New", monospace`

### Rules

- Body text never below 14 px.
- All user-visible labels use WPF `DynamicResource` keys, never hardcoded strings.

## 4. Spacing & Layout

### Base Unit

All spacing derives from a base of **4 px**.

| Token | Value | Usage |
|-------|-------|-------|
| `Space-1` | 4 px | Tight: icon-to-label, inner element gaps |
| `Space-2` | 8 px | Compact: list items, inline groups |
| `Space-3` | 12 px | Default: form field padding |
| `Space-4` | 16 px | Standard: card padding, input height context |
| `Space-5` | 20 px | Comfortable: section inner spacing, button padding |
| `Space-6` | 24 px | Generous: right content panel padding |
| `Space-8` | 32 px | Separated: between card groups |
| `Space-10` | 40 px | Sections within a page |
| `Space-12` | 48 px | Major section breaks, tab bar height |
| `Space-16` | 64 px | Page-level vertical rhythm |

### Layout Constants

| Element | Value |
|---------|-------|
| Settings left navigation width | 160 px |
| Tab bar height | 48 px |
| Right content panel padding | 24 px |
| Left nav item padding | 16 px top/bottom, 10 px left/right |

## 5. Components

### Borders & Radius

| Token | Value | Usage |
|-------|-------|-------|
| Input border radius | 4 px | TextBox, ComboBox |
| Button border radius | 6 px | Primary, secondary buttons |
| Card border radius | 8 px | Device info, status cards |
| Card border | 1 px solid `DividerBrush` | Card outline |
| Input border | 2 px solid `PrimaryLighterBrush` | Default input state |
| Input focus border | 2 px solid `PrimaryBrush` | Focused input state |

### Buttons

- **Primary (Connect)**: Pink gradient (`PrimaryLightestBrush` to `PrimaryBrush`), white text (`TextOnPrimaryBrush`), 6 px radius, 200×60 px.
- **Secondary (Cancel/Browse)**: White background (`CardBackgroundBrush`), 1 px `PrimaryBrush` border, `TextPrimaryBrush` text, 6 px radius.
- **Disabled**: Grayed out, no pointer events.

### Inputs

- **TextBox / ComboBox**: White background, 2 px `PrimaryLighterBrush` border, 4 px radius. Focus border turns `PrimaryBrush`.
- **ComboBox**: Editable (`IsEditable=True`) for serial address input.
- **CheckBox**: HandyControls `ToggleButtonSwitch`, pink fill when checked.

### Cards

- White background (`CardBackgroundBrush`), 1 px `DividerBrush` border, 8 px radius, subtle shadow.

### Tab Bar

- 48 px height, bottom-aligned with window top.
- Active tab: `PrimaryBrush` text + 2 px `PrimaryBrush` top bar.
- Inactive tab: `TextSecondaryBrush` text.

### Settings Navigation

- 160 px fixed-width left panel.
- Each item: 16 px top/bottom + 10 px left/right padding, 14 px font.
- Selected: `PrimaryBrush` text, 2 px left `PrimaryBrush` indicator, `PrimaryLightestBrush` background.
- Unselected: `TextSecondaryBrush` text.
- Bound to `SettingsViewModel.MenuItems` via `ItemsControl`.

## 6. Theming Strategy

### HandyControls Override-Only Rule

All styling is done through HandyControls property overrides and resource dictionary merging. **No custom `ControlTemplate` definitions** are permitted. The complete visual system is expressed through:

1. **Brush resources** defined in `Res/Themes/Light.xaml`.
2. **HandyControls property overrides** in `Res/Style.xaml` — setting `Background`, `Foreground`, `BorderBrush`, `BorderThickness`, `CornerRadius`, and `FontSize` via implicit styles.
3. **Gradient resources** defined alongside brushes.

This ensures HandyControls version upgrades never break custom template rewrites.

### Resource Merging

```
App.xaml
  └─ Res/Theme.xaml
       ├─ Res/Themes/Light.xaml   (brush and gradient resources)
       └─ Res/Style.xaml          (HandyControls property overrides)
```

### Dark Mode

Not implemented in S1. Future dark mode adds `Res/Themes/Dark.xaml` with inverted palette values and swaps via `Application.Current.Resources.MergedDictionaries`.

## 7. Motion

### Transitions

- Tab switch: instant (no animation in S1).
- Settings panel switch: instant (no animation in S1).
- Button state changes: HandyControls default press animation only.

### Rules

- No decorative animations in S1.
- All motion is GPU-composited (HandyControls default).
