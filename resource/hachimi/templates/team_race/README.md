# Team Race templates

The executor follows the MAA pattern: each interaction has a small button
template and an ROI. It searches the current screenshot in that ROI, then
clicks the matched rectangle (`ClickSelf`). The page background, team cards,
and race animation are not used as full-page templates.

- `buttons/` — one small reference crop per clickable control
- `race_result.png` — small Final Score state marker used to detect completion

The same button set is reused for every configured race. There are no separate
race-2/race-3 screenshots.
