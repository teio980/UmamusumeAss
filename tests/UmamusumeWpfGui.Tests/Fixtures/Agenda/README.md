# Agenda race picker regression capture

`junior-early-oct-race-cards.png` is the unscaled (20, 260, 300, 950)
crop from a 900×1600 ADB capture of the Global Junior Year Early October
race picker. The Saudi Arabia Royal Cup is the first visible card.
Only the race-card column is retained; tests reconstruct its original coordinates.

With the original 3057.png template and scale list, coarse-only matching
returned 0.513 despite the card being visible. Pixel-precise local scale
refinement resolves the narrow peak above the unchanged 0.62 threshold.
