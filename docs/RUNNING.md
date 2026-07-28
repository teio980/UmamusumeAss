# Running UmamusumeAss

> **Platform:** Windows 10 or Windows 11, x64.  
> **Release type:** Portable self-contained ZIP. No installer, no system .NET runtime required.  
> **Version:** Phase 6 (S1 connection verifier).

---

## Table of Contents

- [What's in the Box](#whats-in-the-box)
- [Requirements](#requirements)
- [Quick Start](#quick-start)
  - [1. Extract](#1-extract)
  - [2. Launch](#2-launch)
  - [3. Configure ADB](#3-configure-adb)
  - [4. Connect](#4-connect)
- [ADB Setup](#adb-setup)
  - [Manual ADB](#manual-adb)
  - [Auto Detect](#auto-detect)
  - [Supported Emulators](#supported-emulators)
- [Understanding the Connection Panel](#understanding-the-connection-panel)
  - [Last Verified](#last-verified)
  - [Connection State](#connection-state)
  - [What S1 Does and Does Not Do](#what-s1-does-and-does-not-do)
- [Troubleshooting](#troubleshooting)
  - [Error Code Reference](#error-code-reference)
  - [Common Issues](#common-issues)
- [Release Gates (Maintainers)](#release-gates-maintainers)
  - [Portable ZIP Verification](#portable-zip-verification)
  - [Real-Emulator Smoke Test](#real-emulator-smoke-test)

---

## What's in the Box

Extract `UmamusumeAss-win-x64.zip` to any folder. The contents are:

```
UmamusumeAss.exe              -- GUI application (WPF / .NET 10 self-contained)
UmamusumeCore.dll             -- Core connection engine (C++20, /MT static runtime)
Umamusume.CoreBridge.dll      -- P/Invoke bridge between GUI and Core DLL
*.dll (100+ files)            -- .NET runtime assemblies (self-contained)
resource/
└── connection.json           -- ADB command profile (do not modify)
```

Key details:

- **Self-contained .NET** -- The ZIP bundles the entire .NET 10 runtime. You do not need to install a .NET SDK or runtime on the target machine.
- **Static C++ runtime** -- `UmamusumeCore.dll` is compiled with `/MT` (static MSVC runtime). There is no dependency on `vcruntime140.dll` or any VC++ redistributable package. The packaging script explicitly rejects archives that contain those DLLs.
- **Single folder** -- All files sit flat in one directory (except `resource/`). Do not move files between folders.
- **No installer, no registry** -- Delete the folder to uninstall.

---

## Requirements

| Requirement | Details |
|---|---|
| OS | Windows 10 (x64, 22H2+) or Windows 11 (x64, 23H2+) |
| RAM | 512 MB minimum, 2 GB recommended |
| Disk | ~200 MB for the extracted application |
| ADB | `adb.exe` from Android SDK Platform Tools, or an emulator-bundled ADB |
| Emulator | One of: BlueStacks, LDPlayer, MuMu 12, Nox, or MEmu (XYAZ) |

The application connects to an Android emulator over ADB. It does not run on Linux or macOS.

---

## Quick Start

### 1. Extract

Right-click `UmamusumeAss-win-x64.zip` and choose **Extract All...**, or use PowerShell:

```powershell
Expand-Archive -LiteralPath UmamusumeAss-win-x64.zip -DestinationPath C:\Tools\UmamusumeAss
```

Use a short path with no spaces if possible. Long paths can cause rare ADB argument-parsing issues.

### 2. Launch

Double-click `UmamusumeAss.exe`.

The first thing you will see is a pink-themed window with two tabs: **Log** and **Settings**. Go to the **Settings** tab. The Connection panel is the default view.

If the application fails to start, see [Troubleshooting](#troubleshooting).

### 3. Configure ADB

You need to tell the application where your ADB executable is and which emulator to talk to.

**ADB Path** -- Full path to `adb.exe`. Examples:

- `C:\Android\platform-tools\adb.exe`
- `C:\Program Files\BlueStacks_nxt\HD-Adb.exe`
- `C:\LDPlayer\ldplayer9\adb.exe`

**Serial / Address** -- The device identifier that `adb devices` shows. Examples:

- `127.0.0.1:5555` (MuMu 12, Nox, MEmu -- TCP address)
- `emulator-5554` (Android AVD)
- `0123456789abcdef` (USB-connected device)

See [Auto Detect](#auto-detect) to fill these fields automatically.

### 4. Connect

Click the pink **Connect** button.

The application will:

1. Run `adb devices` to find the target
2. If the target is a TCP address not yet listed, run `adb connect <address>`
3. Wait for Android to finish booting (`sys.boot_completed = 1`)
4. Read the device's Android ID, Android version, and screen resolution
5. Show the results in the **Device Information** card

On success, the Device Information card displays:

- **Serial** -- the connected device address
- **Android ID** -- the device's unique Android identifier (hex string)
- **Android Ver** -- Android version number (e.g., 9, 12, 14)
- **Resolution** -- effective display size (e.g., 1920 x 1080)

Connection progress is shown in the **Log** tab in real time.

---

## ADB Setup

### Manual ADB

If you already have ADB installed (from Android Studio, SDK Platform Tools, or an emulator):

1. Click **Browse** next to the ADB Path field and select your `adb.exe`
2. Type the device serial or TCP address into the Serial field
3. Click **Connect**

The Serial combo box remembers up to 5 previously used addresses. Select one from the dropdown instead of typing it again.

### Auto Detect

Click **Auto Detect** to let the application find your emulator automatically.

How it works:

1. The application scans running processes for known emulator executables
2. It derives the ADB path from the emulator's installation directory
3. It runs `adb devices` and lists every device in `device` state
4. If it finds exactly one candidate, it fills both fields
5. If it finds multiple candidates, it shows a selection dialog

**Auto-detect policy:**

- By default, auto-detect runs only when either the ADB path or serial field is empty. It fills blank fields only.
- Check **Always auto-detect before connect** to force discovery on every connect attempt. When checked, auto-detect runs even if both fields are filled, and asks for confirmation before overwriting them.
- If auto-detect fails but both fields contain valid manual values, Connect proceeds with the manual values.

### Supported Emulators

The application recognizes these emulators by process name:

| Emulator | Process Name | ADB Path (relative to process dir) |
|---|---|---|
| BlueStacks 5 | `HD-Player` | `HD-Adb.exe` or `Engine\ProgramFiles\HD-Adb.exe` |
| LDPlayer 9 | `dnplayer` | `adb.exe` |
| Nox | `Nox` | `nox_adb.exe` |
| MuMu 12 | `MuMuPlayer` / `MuMuNxDevice` | `..\..\..\nx_main\adb.exe` or others |
| MEmu / XYAZ | `MEmu` | `adb.exe` |

Auto-detect is process-based: the emulator must be running for discovery to find it. If the emulator is installed but not running, enter the ADB path and serial manually.

---

## Understanding the Connection Panel

### Last Verified

The **Device Information** card is labelled **Last verified**. This is intentional.

- It shows the details from the *last successful connection*, not a live heartbeat.
- The card is a historical snapshot. It does not update when the device state changes.
- The **Connect** button and the **Serial** combobox operate on a separate editable draft. Changing the draft does not affect the Last Verified card.
- Click **Forget** to clear the Last Verified card. This does not disconnect the device (there is no persistent connection in S1).

### Connection State

The status text below the Connect button shows the current state:

| State | Meaning |
|---|---|
| Ready | Idle, waiting for user action |
| Detecting | Auto-detect is scanning for emulators and ADB devices |
| Connecting | A connection attempt is in progress (Connect button is disabled) |
| Connected | The last handshake succeeded (Last Verified card is populated) |
| Disconnected | S2 session only -- the previously connected device is no longer reachable |
| Failed | The last connection attempt ended with an error |
| Canceling | A cancellation was requested; waiting for the ADB process to stop |

**S1 does not implement automatic reconnect.** If a device disappears, you must click Connect again manually.

**S1 does not stop or restart the ADB server.** The app may start an ADB server indirectly through `adb devices` (normal ADB client behavior), but it will never run `adb kill-server` unless the user explicitly enables cleanup (not implemented in S1).

### What S1 Does and Does Not Do

**S1 is a connection verifier.** It proves that the application can:

- Find and run your ADB executable
- Discover your emulator over ADB
- Wait for Android to boot
- Read device identity and display information

**S1 does NOT:**

- Capture screenshots (S2)
- Send tap or swipe input (S2)
- Verify that Umamusume: Pretty Derby is installed (S2)
- Perform any game automation (S3)
- Automatically reconnect if the device disconnects
- Run `adb kill-server`
- Discover emulators through the Windows registry

The application becomes a usable assistant only after S2 capabilities are shipped (package verification, live screenshot, manual input).

---

## Troubleshooting

### Error Code Reference

If a connection fails, the Log tab shows an error code and a descriptive message. Here is what each code means and what to do about it.

| Code | Name | What Happened | What To Do |
|---|---|---|---|
| 1 | `AdbExecutableNotFound` | The ADB path does not point to an existing `.exe` file. | Check the ADB Path field. Use **Browse** to select `adb.exe`. |
| 2 | `ProcessStartFailed` | Windows could not start the ADB process. | Check that the ADB path is valid and the file is not blocked by antivirus. |
| 3 | `CommandTimedOut` | An ADB command did not finish within its time limit. | The device may be too slow or unresponsive. Try again, or check the emulator's state. |
| 4 | `DeviceUnauthorized` | The device rejected the ADB RSA key. | On the emulator, check for an "Allow USB debugging?" dialog and accept it. Restart ADB (`adb kill-server; adb start-server`) and try again. |
| 5 | `DeviceOffline` | The device is in `offline` state. | The emulator may still be booting. Wait and try again. |
| 6 | `DeviceUnavailable` | The device is not reachable. `adb connect` failed, or the serial was not found and is not a connectable TCP endpoint. | Verify the serial is correct. For TCP addresses (`host:port`), ensure the emulator's ADB port is open. For USB serials, check the cable and driver. |
| 7 | `CommandFailed` | An ADB command returned a non-zero exit code. | Check the Log tab for the ADB output. The device may be in an unexpected state. |
| 8 | `InvalidDeviceResponse` | ADB returned data that could not be parsed (empty, malformed, or out of range). | The device may be in an unusual state. Try restarting the emulator. |
| 9 | `Canceled` | The connection was canceled by the user. | This is normal. Click Connect again when ready. |
| 10 | `DeviceNotReady` | `adb connect` succeeded, but the device did not become `device` in time. | The emulator may still be initializing. Wait and try again. |
| 11 | `InvalidArgument` | The ADB path, serial, or profile is empty, malformed, or contains unsafe characters. | Check that all fields are filled correctly. Use only printable ASCII characters. |
| 12 | `Busy` | A connection or operation is already in progress. | Wait for the current operation to finish or cancel it. |
| 13 | `BootNotCompleted` | Android did not report `sys.boot_completed = 1` within 60 seconds. | The emulator may be stuck booting. Restart the emulator. |
| 15 | `DeviceDisconnected` | (S2 only) A connected device disappeared or stopped responding. | Click Connect to re-establish the session. |

### Common Issues

**"Application does not start"**

- Make sure all files from the ZIP are in the same folder. `UmamusumeAss.exe` needs `UmamusumeCore.dll` and the .NET runtime DLLs next to it.
- If you see a Windows Defender SmartScreen warning, click **More info** then **Run anyway**. This is a portable unsigned executable.
- If the application crashes immediately, check Windows Event Viewer for .NET runtime error details. The `/MT` build eliminates VC++ redistributable issues, but antivirus software may quarantine .NET JIT-compiled code.

**"adb.exe not found" even though it exists**

- Some emulators install ADB in a protected folder. Run the application as Administrator, or copy `adb.exe` to a user-writable folder.
- Auto-detect may fail if the emulator process is running elevated and the application is not. Use manual entry in this case.

**"Auto Detect finds nothing"**

- The emulator must be running. Auto-detect scans running processes, not installed software.
- Make sure the emulator's process name matches the supported list above. LDPlayer uses `dnplayer.exe`, BlueStacks uses `HD-Player.exe`, etc.
- If the emulator is running but still not detected, enter the ADB path and serial manually.

**"Connection succeeds but the resolution looks wrong"**

- The application reads `wm size` from the Android shell. This reports the effective display size, which may differ from the physical size when the emulator uses override scaling. Both values are shown in the connection result.
- If the resolution is 0x0 or clearly wrong, the emulator's display system may not be ready. Reconnect after the emulator fully boots.

**"I see 'Device Unauthorized' every time"**

- This means the emulator rejected the ADB RSA key fingerprint. On most emulators, you need to enable ADB root access or USB debugging in the emulator settings (not the Android settings).
- For BlueStacks: enable ADB in Settings > Advanced > Android Debug Bridge.
- For LDPlayer: enable ADB in Settings > Other Settings > ADB.
- After changing the setting, restart both the emulator and the application.

**"Can I use this with a physical Android phone?"**

- The ADB protocol works with any Android device, but S1's auto-detect is tuned for emulators. You can enter the ADB path and USB serial manually. S2 package verification and input assume the device is running Umamusume: Pretty Derby at a known resolution.

**"Does this work on Windows on ARM (e.g., Surface Pro X)?"**

- No. The .NET self-contained build targets `win-x64`. x64 emulation on ARM may work but is not tested or supported.

---

## Release Gates (Maintainers)

Two manual release gates must pass before shipping a Phase 6 release. These are not CI checks. They must be performed on a clean real Windows machine with a supported emulator.

### Portable ZIP Verification

**Gate 10/11/19 from the implementation spec.** Verify that the built ZIP works on a machine that has never had a .NET runtime or VC++ redistributable installed.

Procedure:

1. On a Windows 10 or 11 VM or clean machine (no Visual Studio, no .NET SDK, no VC++ redist),
2. Download or copy `UmamusumeAss-win-x64.zip`
3. Extract to a folder
4. Double-click `UmamusumeAss.exe`
5. Verify the GUI window appears with the pink theme and two tabs (Log, Settings)

If the application launches without error, the `dotnet publish --self-contained` and `/MT` static linking are working correctly.

This gate is mandatory evidence for every release. Document the VM image, date, and result in the release notes. It cannot be automated in GitHub-hosted CI because those runners have .NET SDK pre-installed.

### Real-Emulator Smoke Test

**Gate 10 from the implementation spec.** Run the smoke-test CLI against a real running emulator.

The smoke tool is `uma_connect_smoke.exe` (built from `tools/connect_smoke.cpp`). It is a standalone C++ executable that loads `resource/connection.json` and performs the full S1 handshake.

Usage:

```
uma_connect_smoke.exe <adb_path> <serial>
```

Example:

```
uma_connect_smoke.exe "C:\Program Files\BlueStacks_nxt\HD-Adb.exe" 127.0.0.1:5555
```

Expected output on success (exit code 0):

```
serial:           127.0.0.1:5555
android_id:       0123456789abcdef
android_version:  14
effective:        1920x1080
physical:         1920x1080
```

Expected output on failure (exit code 1, stderr):

```
ERROR [<phase>] (code <N>): <message>
```

Supported emulators for the smoke test (at least one must pass):

- BlueStacks 5 (ADB: `HD-Adb.exe`, serial: `127.0.0.1:5555` or emulator-assigned)
- LDPlayer 9 (ADB: `adb.exe`, serial: `127.0.0.1:5555` or `emulator-5554`)
- MuMu 12 (ADB: `adb_server.exe` or `adb.exe`, serial: `127.0.0.1:5555`)

Prerequisites:

- The emulator must be running and fully booted
- ADB debugging must be enabled in the emulator settings
- No other application should be using the ADB server for the same serial

Document the emulator model, ADB version, and output in the release notes.
