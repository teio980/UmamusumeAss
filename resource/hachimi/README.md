# Hachimi Start Game pipeline

The Start Game task launches the configured Android package, then follows the
editable task graph in `start_game.json` until the real game home screen is
detected. The current Umamusume flow handles the startup title, the first-run
data confirmation, promotional skip button, Notices close button, and the
final Home tab.

The captured templates in `templates/` were taken from the configured MuMu
device at 900x1600 portrait resolution. Each task declares that reference
size, and coordinates are scaled to the connected device.

Supported actions include `Screenshot`, `Wait`, `TapToStart`, `ClickSelf`,
`ClickRect`, `Swipe`, `Back`, `Input`, and `KeyEvent`.

## Team Race scaffold

`team_race.json` is a separate MAA-inspired state-machine skeleton for the
Team Race / Team Arena task. It includes the navigation states, retry paths,
ticket check, and terminal states, but deliberately does not include client
specific OCR execution or newly captured templates. Startup templates are
under `templates/start_game/`; Team Race templates belong under
`templates/team_race/`. Replace the `TeamRacePipelinePlaceholder` binding in
the WPF bootstrapper with the implementation of `ITeamRacePipeline`.
