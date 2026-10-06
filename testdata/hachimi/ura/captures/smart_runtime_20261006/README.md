# Smart training slow-run evidence

Source: automatically saved runtime evidence `smart-training-20261006-052206576-9f446364`, supplied with the 05:21–05:27 log on 2026-10-06.

The original 900 × 1600 pixel geometry, Training header and lower training UI are preserved. Rows 45–944 containing character art and goal details are whitened to reduce fixture size; no matcher or numeric-reader ROI intersects those rows.

First speed preview: +19 speed, +9 power, +6 skill points. Stamina: +13 stamina, +7 guts, +4 skill points. Power: +10 stamina, +13 power, +5 skill points. Guts: +12 speed, +11 power, +15 guts, +7 skill points. Wit: +4 speed, +17 wit, +8 skill points. All show 0% failure.

Stamina and guts sample 2 contain sparkle occlusion. The original all-five matcher reports speed on those frames: they are deliberately tested as unverified for their requested type. Wit sample 1 produces an offset match during sparkles; the percent remains readable but its match is outside the configured raised-click ROI, so it must not use the fast confirmation path.

Additional evidence from the 06:25–06:30 run: `wit-speed-five.png` comes from `smart-training-20261006-062828137-df296e2b/wit/sample-1.png` and shows +5 speed, +16 wit, +8 skill points, 0% failure. On this exact prepared +5 crop, Tesseract PSM 7 returned +35 while PSM 8 and 13 returned +5. The glyph-count check rejects the extra digit before accepting a fallback value or caching it.

`adb-guts-fifteen.png` was captured read-only through ADB on 2026-10-06 at 14:31 local time. The current selected Guts preview shows +13 speed, +8 power, +15 guts, +6 skill points, 0% failure. It is a later turn than the supplied log's two 141 reads; those Guts frames were not saved by the previous unreadable-only diagnostics. No historical numeric value is inferred from this later frame.

`budget-speed-1.png`, `budget-speed-2.png`, `budget-stamina-1.png` and `budget-stamina-2.png` preserve numeric pixels from `smart-training-20261006-065710373-9a380846`. Speed shows +18 speed, +9 power, +5 skill points and 0% failure; stamina shows +14 stamina, +7 guts, +4 skill points and 0% failure. The speed fallback consumed the entire shared 2.5-second budget on its first field, leaving other fields and the second frame unread. These fixtures exercise the smart-only multipage Tesseract path, with page numbers preserving field identity and an independent retry budget per verified frame.
