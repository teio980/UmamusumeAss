# Independent career offline catalogs

These catalogs are checked-in snapshots for the Global server. Runtime does not
depend on the web pages; the URLs below are provenance and refresh references.

## Races / agenda

- Guide: <https://uma.guide/agenda-planner/>
- Data bundle: <https://uma.guide/assets/chunks/uma-data.BVcwA_KI.js>
- Data array: `3`
- Snapshot: `races.global.json`, 376 entries, retrieved 2026-08-22 UTC.

The race rows retain the guide's source order and the verified Global picker
order. Each row is mapped to a year tab, half-month slot, picker page, and
visible row. The JSON execution profile performs the year/slot/scroll/row
actions; C# passes only the semantic race selection.

## Skills

- Guide: <https://uma.guide/skills/>
- Data bundle: <https://uma.guide/assets/chunks/uma-data.BVcwA_KI.js>
- Data array: `1`
- Snapshot: `skills.global.json`, 2,131 entries, retrieved 2026-08-22 UTC.

Each skill has an ASCII-safe offline search query and a result row calculated
from the Global picker ordering (descending rarity, then stable source order).
This keeps names containing accents, stars, or ◎/○/× usable with `adb input
text`; variant skills are disambiguated by the mapped result row. The JSON
profile focuses the search box, injects the query, submits it, scrolls as
needed, matches the generic result-row template, and confirms the picker.

The catalog describes the complete offline Global resource set. Availability
can still vary with the current game state/client build; if a mapped result is
not present, JSON template matching fails and the pipeline reports the missing
selection instead of silently applying another row.
