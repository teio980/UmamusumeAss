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
