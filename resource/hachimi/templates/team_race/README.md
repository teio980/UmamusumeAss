# Team Race templates

`team_race.json` deliberately references the startup template at
`../start_game/game_home.png` plus the placeholder templates below. Capture
the Team Race templates from the target client and tune
the threshold/ROI in the JSON before enabling the executor:

- `arena_home.png` — Team Race landing page
- `race_result.png` — race result page

The OCR text and regions are also placeholders because the available client
language and game build determine the final wording and layout. Keep the
reference coordinate space at `900 x 1600` unless the executor explicitly
normalizes another resolution.
