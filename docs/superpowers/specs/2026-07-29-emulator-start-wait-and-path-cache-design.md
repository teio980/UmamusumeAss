# Emulator Startup Wait and Path Cache Design

## Goal

Improve the Connection settings experience for emulator auto-start without changing
the existing connection or retry behavior.

## Scope

1. Add a persisted, user-configurable wait duration for emulator auto-start.
2. Persist and restore the emulator executable path using the same settings JSON
   lifecycle as `AdbPath`.

## Settings Model

Add `AutoStartEmulatorWaitSeconds` to `ConnectionSettings`.

- Type: non-negative integer.
- Default: `5` seconds.
- It is serialized to `%APPDATA%/UmamusumeAss/connection_settings.json` with the
  rest of `ConnectionSettings`.

`EmulatorExecutablePath` remains the stored emulator startup path. Its selected or
entered value is copied into the persisted settings immediately, matching the
durability expectation of `AdbPath`, so it is restored after the application restarts.

## Connection Settings UI

In the existing Connection panel:

- Keep the Auto Start Emulator toggle and emulator executable path field.
- Add a numeric wait-seconds control adjacent to these controls.
- Bind it to a ViewModel draft property and prevent values below zero.
- Provide localized English and Simplified Chinese labels/help text.

## Runtime Behavior

When emulator auto-start is enabled and discovery finds no running emulator:

1. Start the configured emulator executable.
2. Wait for `AutoStartEmulatorWaitSeconds`.
3. Preserve the current end-of-flow behavior after the wait; this change does not
   introduce automatic discovery retry or automatic connection.

## Persistence Behavior

- Loading settings restores both `AdbPath` and `EmulatorExecutablePath` into their
  respective editable Connection settings fields.
- Editing or selecting an emulator executable path persists it through the existing
  settings service, rather than requiring a subsequent successful connection.
- Existing malformed or missing settings-file recovery behavior remains unchanged.

## Validation

- Model tests cover default, non-negative behavior, and JSON round-trip for the wait
  duration and emulator path.
- ViewModel tests cover restoring and persisting the executable path, saving the wait
  duration, and applying the configured wait after launch.
- XAML contract tests cover the wait control and its ViewModel binding.
- Localization resources contain the new labels in both supported languages.
