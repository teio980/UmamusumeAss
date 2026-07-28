# UmamusumeAss Design System

## 1. Atmosphere & Identity

UmamusumeAss is a calm, capable Windows control desk: it should make a user feel oriented before they act and informed while the assistant runs. Its signature is a pale operational canvas with a quiet violet route marker: navigation stays visually stable, live state is legible at a glance, and event history remains nearby without dominating the task.

## 2. Color

### Palette

| Role | Token | Value | Usage |
| --- | --- | --- | --- |
| Surface/canvas | `SurfaceCanvasBrush` | `#F6F5F8` | Window background |
| Surface/sidebar | `SurfaceSidebarBrush` | `#EEEAF3` | Persistent navigation rail |
| Surface/panel | `SurfacePanelBrush` | `#FFFFFF` | Primary work panels and forms |
| Surface/raised | `SurfaceRaisedBrush` | `#FBFAFD` | Nested areas and hover fills |
| Text/primary | `TextPrimaryBrush` | `#24212A` | Headings and main text |
| Text/secondary | `TextSecondaryBrush` | `#6D6875` | Hints and supporting copy |
| Text/disabled | `TextDisabledBrush` | `#9A94A3` | Disabled content |
| Border/default | `BorderDefaultBrush` | `#DCD7E2` | Panel and input outlines |
| Border/subtle | `BorderSubtleBrush` | `#ECE8F0` | Internal separators |
| Accent/primary | `AccentPrimaryBrush` | `#7653A6` | Active navigation, primary actions, focus |
| Accent/hover | `AccentHoverBrush` | `#65458F` | Hover and pressed feedback |
| Status/success | `StatusSuccessBrush` | `#317B62` | Connected and successful events |
| Status/warning | `StatusWarningBrush` | `#A86F23` | Caution and in-progress events |
| Status/error | `StatusErrorBrush` | `#B84B58` | Failed connections and error events |
| Status/info | `StatusInfoBrush` | `#4D739B` | Informational events |

### Rules

- The application is light-only. Do not add a dark theme, a theme toggle, or a background-image layer.
- Violet identifies action and location, not decoration. Status colors communicate state only.
- New colors require a semantic token in this file before use in XAML.

## 3. Typography

### Scale

| Level | Size | Weight | Line height | Usage |
| --- | --- | --- | --- | --- |
| Page title | 24px | SemiBold | 1.25 | Page heading |
| Section title | 18px | SemiBold | 1.35 | Panel title |
| Body | 14px | Regular | 1.5 | Default content |
| Body/emphasis | 14px | Medium | 1.5 | Values and selected rows |
| Caption | 12px | Medium | 1.4 | Labels, timestamps, metadata |

### Font Stack

- Primary: `Segoe UI Variable`, `Segoe UI`, sans-serif.
- Mono: `Cascadia Mono`, `Consolas`, monospace for logs and connection identifiers.

### Rules

- Use sentence-case labels and direct operational copy.
- Keep body text at 14px or larger. Use the mono stack only for timestamps, endpoints, and technical identifiers.

## 4. Spacing & Layout

### Base Unit

All spacing uses a 4px base unit: `Space1=4`, `Space2=8`, `Space3=12`, `Space4=16`, `Space5=20`, `Space6=24`, `Space8=32`.

### Desktop Shell

- Minimum window: 960×680; content grows from that baseline.
- Root navigation rail: 184px; main content padding: 24px; standard panel gap: 16px.
- Overview uses a two-column content grid: status summary and recent activity.
- Settings uses a 184px category list plus a flexible settings detail pane.
- Logs use a single wide, filterable event pane.
- A future control center may use three panes: 220px action list, 280px detail, and flexible live log. It is not part of the current implementation because no task-control API exists yet.

### Rules

- Use `Grid` for multi-pane WPF layout, not positional margins.
- Preserve usable content at the minimum window size. Collapse secondary metadata before shrinking primary actions.

## 5. Components

### Navigation item

- **Structure:** icon path, localized label, selected indicator.
- **Variants:** default, hover, selected, disabled.
- **Spacing:** 12px horizontal padding, 10px vertical padding, 8px icon-to-label gap.
- **States:** selected uses the violet marker and raised surface; focus remains visible by keyboard.

### Operational panel

- **Structure:** title row, optional status marker, content body, optional footer actions.
- **Variants:** standard, status, form, log.
- **Spacing:** 20px padding with 16px internal groups.
- **States:** empty, loading, error, disabled; errors explain the next user action.

### Status chip

- **Structure:** colored dot, localized state label, optional technical detail.
- **Variants:** info, success, warning, error.
- **Spacing:** 8px inner gap; use it only for connection and log state.

### Log row

- **Structure:** mono timestamp, severity marker, concise message, optional detail.
- **Variants:** info, success, warning, error.
- **States:** normal, selected, empty; list remains keyboard-scrollable.

## 6. Motion & Interaction

| Type | Duration | Usage |
| --- | --- | --- |
| Micro | 120ms | Button and navigation press feedback |
| Standard | 200ms | Hover and content-region transition |

- Animate opacity and translation only; do not animate layout width, height, margins, or padding.
- Every actionable control has hover, pressed, disabled, and keyboard-focus states.
- Respect the operating system’s reduced-motion preference when technically available; static states remain fully understandable.

## 7. Depth & Surface

### Strategy

Use **borders-only with tonal separation**. Panels use a white surface, a subtle cool-violet border, and no decorative drop shadows. Raised state is conveyed by `SurfaceRaisedBrush` and a slightly stronger border, preserving a precise desktop-tool feel.
