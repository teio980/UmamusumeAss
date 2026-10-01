# Date Changed

`sample.png` is the original 900 × 1600 ADB capture. The three templates are
literal rectangular crops, with the original backgrounds and colors preserved:

| File | Rectangle (x, y, width, height) |
| --- | --- |
| title.png | 330, 492, 240, 43 |
| body.png | 347, 751, 207, 41 |
| ok.png | 271, 993, 358, 102 |

Every template pixel is opaque. Do not remove the white dialog background or
the green button background when replacing these assets.

`DateChangedDialogGuard` requires matching title, body, and full colored OK
button in two successive frames. Frames are normalized to the reference size
for recognition; the button coordinates are scaled back to the device size.

The owning task handles this as an interruption: verify OK disappears, launch
the configured package, run the existing StartGame pipeline, require verified
Home, and rebuild navigation. Career resumes its existing session and learned
skill cache; independent training keeps its checkpoint. Confirmed purchases and
race counts survive navigation restarts. An uncertain submission stops recovery
instead of submitting again.

Regression coverage is in `DateChangedDialogRecoveryTests`, `CareerGoalResumeTests`,
and `IndependentTrainingBehaviorTests`. It includes this capture at three
resolutions, competing green dialogs, unchanged crop pixels, repeated startup
modals, cancellation, bounded dismissal, and resumed task progress.
