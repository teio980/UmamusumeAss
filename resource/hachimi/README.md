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
  executor implements template matching followed by tapping; new action types
  must be added to the shared runtime before they can be used.

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
