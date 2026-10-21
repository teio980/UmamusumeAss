# Date Changed

`sample.png` is the original 900 × 1600 ADB capture. The three templates are
literal rectangular crops, with the original backgrounds and colors preserved:

| File | Rectangle (x, y, width, height) |
| --- | --- |
| title.png | 330, 492, 240, 43 |
| body.png | 347, 751, 207, 41 |
| ok.png | 271, 993, 358, 102 |
| err.png | 320, 500, 261, 26 (Connection Error title from the live ADB capture) |
| titlescreen.png | 160, 1028, 183, 26 (Title Screen lettering from the same capture) |
| session_error_title.png | 345, 492, 210, 43 (Session Error title) |
| session_error_body.png | 160, 750, 580, 52 (Returning to Title screen due to inactivity.) |
| session_error_title_screen.png | 347, 1020, 207, 46 (centered Title Screen lettering) |

The Session Error crops come from the unmodified ADB capture
`testdata/hachimi/ura/captures/session_error_inactivity_mumu_20261014.png` at the
repository root. They retain the original opaque background and colors.

Every template pixel is opaque. Do not remove the white dialog background or
the green button background when replacing these assets. `err.png` is a tight
crop of the Connection Error lettering with its original green patterned
background retained. `titlescreen.png` is cropped to the Title Screen lettering
with the original button background retained; the text match locates a tap
inside the button.

`DetectGameRecoveryDialog` accepts the daily-reset title/body/full colored
OK button or the Connection Error title/left Title Screen button in two successive
frames. Session Error accepts any one of its title, inactivity body, or centered
Title Screen button in a single frame and immediately dismisses the dialog.
Frames are normalized to the reference size for recognition; the matching button
coordinates are scaled back to the device size. When only the Session Error title
or body matches, its observed position determines the centered button location.
For Connection Error it taps
Title Screen, then runs the existing StartGame pipeline. Session Error uses the
same bounded Home recovery and Career reconciliation path; it is not treated as
an unknown training outcome or permission to repeat the interrupted training tap.

The native detector searches the tight text crops at every pixel, retaining
main's explicit one-pixel search step and its original thresholds. Only the
recovery view is normalized; the selected input frame and its provenance remain
in device geometry. Long task delays inspect a separate frame once per second;
Date Changed and Connection Error require two-frame confirmation. Session Error
requests a current input frame immediately without the second-frame gate.
Inspection frames are released immediately and
cannot authorize input or replace a pending action's selected frame.

The owning task handles each dialog as an interruption: close it, launch the
configured package, run the existing StartGame pipeline, require verified Home,
and rebuild navigation. Career resumes its existing session and learned skill
cache; independent training keeps its checkpoint. Confirmed purchases and race
counts survive navigation restarts. An uncertain submission stops recovery
instead of submitting again.

Native pixel coverage is in `GameRecoveryDialogTests`: this actual Date Changed
capture at three resolutions, competing green dialogs, a missing OK button, and
a Connection Error fixture made from the original title/button crops at their
captured coordinates. The real inactivity Session Error capture is also tested
at three resolutions, including each marker alone and rejection with all markers removed.
`GameTaskTests` feeds both real reset and session-error pixels through the
guard, including input/long-delay interruptions and uncertain-purchase recovery.
`NativeTaskContextTests` uses FakeAdb to verify that inspection preserves ordinary
and retained input evidence and that a released inspection frame cannot tap.
The historical managed coverage was `DateChangedDialogRecoveryTests`,
`CareerGoalResumeTests`, and `IndependentTrainingBehaviorTests` on main.
