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

Every template pixel is opaque. Do not remove the white dialog background or
the green button background when replacing these assets. `err.png` is a tight
crop of the Connection Error lettering with its original green patterned
background retained. `titlescreen.png` is cropped to the Title Screen lettering
with the original button background retained; the text match locates a tap
inside the button.

`DateChangedDialogGuard` accepts either the daily-reset title/body/full colored
OK button or the Connection Error title in two successive frames. Frames are
normalized to the reference size for recognition; the matching button
coordinates are scaled back to the device size. For Connection Error it taps
Title Screen, then runs the existing StartGame pipeline.

The owning task handles either dialog as an interruption: close it, launch the
configured package, run the existing StartGame pipeline, require verified Home,
and rebuild navigation. Career resumes its existing session and learned skill
cache; independent training keeps its checkpoint. Confirmed purchases and race
counts survive navigation restarts. An uncertain submission stops recovery
instead of submitting again.

Regression coverage is in `DateChangedDialogRecoveryTests`, `CareerGoalResumeTests`,
and `IndependentTrainingBehaviorTests`. It includes this capture at three
resolutions, competing green dialogs, unchanged crop pixels, repeated startup
modals, cancellation, bounded dismissal, and resumed task progress.
