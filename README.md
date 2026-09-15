# Umamusume Assistant (UmamusumeAss)

> A local Windows automation assistant for *Umamusume: Pretty Derby* running on Android emulators.

UmamusumeAss connects to an Android emulator through ADB, reads the game screen, and carries out configurable visual workflows. It is made for trainers who want to reduce repetitive operations such as launching the game, collecting rewards, running races, and completing Career training.

The bundled resources currently focus on the Global version of *Umamusume: Pretty Derby*.

## Installation

Program releases provide both a portable ZIP and a Windows installer. Use the
`*-setup.exe` installer if you want a Start menu entry and an uninstall entry
under Windows **Installed apps**. The installer is per-user and does not require
administrator privileges. The ZIP remains available for portable use.

For local packaging, install Inno Setup 6 and run
`tools/package.ps1 -BuildInstaller`.

## Features

### Emulator connection

- Connect to an emulator through ADB serial or TCP address
- Automatically detect available ADB devices and common emulator installations
- Optionally start the emulator when it is not running
- Wait for Android to finish booting before starting game operations
- Verify the device Android ID, Android version, and screen resolution
- Monitor connection health and report disconnections

### Task queue

The Hachimi workspace provides a persistent task queue. Tasks can be added, copied, deleted, reordered, started, and stopped.

| Task | Function |
| --- | --- |
| Start Game | Launch the configured Android game and wait for the game process or home screen |
| Career Training | Run modular Career flows, including URA Finale Normal Career and Independent Training |
| Daily Race | Select Monies or Support Points, choose a difficulty, set the race count, and select a trainee |
| Team Race | Run the Team Race workflow for 1–5 races |
| Mail Collection | Open the in-game mailbox and collect available presents |
| Mission Collection | Collect available rewards from the Daily, Main, Titles, and Special mission tabs |
| Friends & Shop | Check the shop and purchase configured items |

### Career training

Career training can be configured with:

- Trainee selection and Career mode
- Support deck presets or selected support cards
- Career strategy and legacy options
- Independent Training focus, lineup, races, and skills
- Resume or restart behavior for interrupted sessions

The included URA scenario package contains the scenario rules, objectives, race data, screen definitions, localization, and recognition resources needed by the Career workflows.

### Visual automation

- Screenshot-based game interaction through ADB
- Grayscale template matching with configurable thresholds and regions of interest
- Windows offline OCR for reading game text and locating interactive elements
- Reference-coordinate scaling for different screen resolutions
- Configurable taps, swipes, delays, timeouts, branches, retry paths, and failure recovery
- JSON-based Hachimi workflows that can be adjusted without changing application code

### Developer tools

- Capture emulator screenshots for inspection
- Crop and save trainee recognition reference images
- Create and edit ordinary Hachimi JSON workflows
- Define templates, ROIs, actions, timing, transitions, and monitoring tasks
- Validate workflows, preview their state transitions, and simulate them offline
- Run an edited workflow directly on a connected emulator before saving it

### User interface

- Overview page for connection and device information
- Hachimi task workspace with task settings and execution logs
- Dedicated log page for tracing every workflow step
- Settings for ADB, emulator startup, connection profiles, and language
- English (`en-US`) and Simplified Chinese (`zh-CN`) localization
- Local settings and task queue persistence

## Supported scope

The current recognition and data resources are designed primarily for the Global client. Compatibility may vary across game versions, emulators, resolutions, and UI changes; updated templates or workflows may be required after a game update.

## Disclaimer

UmamusumeAss is an unofficial personal/community project and is not affiliated with or endorsed by Cygames, the game publisher, or any other rights holder. The game name, characters, images, and other assets belong to their respective owners.

Please ensure that your use complies with the game's terms of service, emulator rules, and applicable laws. Automation may result in account restrictions, data issues, or other risks. Use it at your own risk.
