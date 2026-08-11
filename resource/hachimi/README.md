# Hachimi Start Game pipeline

The Start Game task launches the configured Android package, then follows the
editable task graph in `start_game.json` until the real game home screen is
detected. The current Umamusume flow handles the startup title, the first-run
data confirmation, promotional skip button, Notices close button, and the
final Home tab.

The captured templates in `templates/` were taken from the configured MuMu
device at 900x1600 portrait resolution. Tasks use small visual templates in a
restricted ROI; the matched rectangle is clicked and no page-sized screenshot
is used for Team Race interaction.

Supported actions include `Screenshot`, `Wait`, `TapToStart`, `ClickSelf`,
`ClickRect`, `Swipe`, `Back`, `Input`, and `KeyEvent`.

## Team Race flow

`team_race.json` contains the MAA-style button templates, per-button ROIs,
thresholds, and timing used by the implemented `AdbTeamRacePipeline`. The
executor enters Race > Team Trials > Team Race, selects an opponent, starts
each race, optionally skips the playback intro, waits for the final score
marker, and repeats the same routine for the configured count. The UI accepts
1–5 races. Team Race templates belong under `templates/team_race/`.
The optional random-shop branch is enabled only when a client-specific shop
template and close-button template are configured.

## Daily Race flow

`daily_race.json` follows the captured game path Race > Daily Program > Daily
Races. The task settings choose either the Monies event (Moonlight Sho) or the
Support Points event (Jupiter Cup) and clamp the requested count to 1–6. The
shared graph selects the configured difficulty card (Very Hard, Hard, Normal,
or Easy), disables Multi-Race for a one-race request and enables it for a
multi-race request, confirms the configured runner, normalizes the ticket count
with the minus/plus controls when needed, leaves items unselected, accepts the
portrait playback prompt when it appears, waits for the result, and handles the
optional Daily Sale dialog before returning to the Daily Race page.
Monies and Support Points share one difficulty-row template; the settings-driven
ROI selects the requested row.

The ticket dialog currently uses placeholder templates under
`templates/daily_race/`: `multi_race_ticket_dialog.png`,
`multi_race_ticket_minus.png`, `multi_race_ticket_plus.png`, and
`multi_race_ticket_confirm.png`. Capture these from the target device before
running the updated flow.

Daily Race templates belong under `templates/daily_race/`; they were captured
from the configured 900x1600 MuMu device and cropped to stable cards/buttons.

## Pipeline schema

`mail_collection.json` and `team_race.json` use the shared ordinary-pipeline
schema: the root uses `tasks`, visual thresholds use MAA's `templThreshold`,
and both definitions use schema version `1`. Their visual matching, tapping,
waiting, screenshot, and delay operations are executed by the shared
`AdbVisualPipelineRuntime`.

`start_game.json` intentionally remains on its compatibility schema. Its
`StartupMonitor`, same-frame candidate priority, `triggerTask`, and
`triggerChain` behavior are specific to game startup and are not routed
through the ordinary task runner.

## Ordinary JSON structure

The ordinary pipeline files (`mail_collection.json` and `team_race.json`)
share this top-level shape:

```json
{
  "name": "mail-collection",
  "schemaVersion": 1,
  "description": "...",
  "referenceWidth": 900,
  "referenceHeight": 1600,
  "templates": {},
  "tasks": {},
  "timing": {}
}
```

`name`, `description`, and `schemaVersion` identify the definition. The
reference dimensions describe the coordinate system used by `roi` and
`specificRect`; the current client is 900x1600 portrait. `templates` contains
optional flow-level overrides, such as Team Race's result or random-shop
template.

Each entry in `tasks` is a named visual task. A typical task is:

```json
{
  "algorithm": "MatchTemplate",
  "action": "ClickSelf",
  "template": "templates/team_race/race_tab.png",
  "templThreshold": 0.88,
  "roi": [560, 1480, 280, 120],
  "timeoutMs": 12000,
  "pollIntervalMs": 300
}
```

- `template` is relative to the directory containing the JSON file.
- `templThreshold` is the MAA-style template-match threshold.
- `roi` is `[x, y, width, height]`; limiting it reduces false matches.
- `timeoutMs` is the maximum time to wait for this task.
- `pollIntervalMs` controls the interval between screenshots; the pipeline
  timing value is used when this field is omitted or zero.
- `algorithm` and `action` document the MAA-style intent. The current ordinary
  executor implements template matching followed by tapping. `RunPipeline`
  is the reusable nested-flow action: its `pipeline` path is relative to the
  current definition, its optional `entry` names the child entry task, and
  the child returns to the parent's `next` transition after it completes.
  New action types must be added to the shared runtime before they can be used.

For example, a task can probe the shared shop flow without copying its steps:

```json
{
  "action": "RunPipeline",
  "pipeline": "shop.json",
  "entry": "shopProbe",
  "next": ["continueParentFlow"],
  "onErrorNext": ["continueParentFlow"]
}
```

The shop pipeline reuses the Hachimi page's global shop purchase options and
returns success when the shop is absent, so the parent task can continue
normally. Shop configuration is not a task-queue item.

`timing` contains business-flow waits rather than visual-match settings. Mail
uses values such as `mailboxLoadMs` and `collectionSettleMs`; Team Race uses
values such as `playbackLoadMs`, `raceTimeoutMs`, and `betweenRacesMs`. Both
files use the same timing object shape, but only the values relevant to that
flow need to be present.

The loader is strict for ordinary pipelines: the property names must use the
documented spelling, `schemaVersion` must be `1`, and unknown properties are
rejected. This prevents a misspelled field from silently falling back to a
default value.

## Unified development flow

When adding or changing an ordinary task:

1. Put the required screenshot template under the corresponding
   `templates/<pipeline>/` directory.
2. Add or update the named entry under `tasks` with its template, ROI,
   threshold, timeout, and polling interval.
3. Add flow-specific waits under `timing` only when the state transition needs
   a delay; visual waiting belongs to the task's timeout and polling fields.
4. Keep the business sequence in the pipeline class. It retrieves a task by
   name and delegates screenshot, matching, tapping, waiting, delay, and debug
   screenshot operations to `AdbVisualPipelineRuntime`.
5. If a low-level visual operation is useful to multiple pipelines, extend
   `IVisualPipelineRuntime` and its implementation instead of copying the
   operation into another pipeline.
6. Run the JSON definition tests, build, and a real emulator dry run. Confirm
   the match score, ROI, click coordinate, and transition log before enabling
   the task by default.

StartGame follows a separate development flow: its monitor graph and
`AdbStartGamePipeline` must be changed together only when startup behavior is
intentionally changed. Do not move `StartupMonitor`, `monitorTasks`,
`triggerTask`, `triggerChain`, or `onErrorNext` into the ordinary schema;
their same-frame priority and trigger semantics are part of the startup
contract.
