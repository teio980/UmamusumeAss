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

- Source: the installed Global client's `master/master.mdb` (package
  `com.cygames.umamusume`, client `1.34.0` at snapshot time).
- Exporter: `tools/export-global-skills.py`, reading `skill_data`,
  `text_data` categories 47/48, and `single_mode_skill_need_point`.
- Selection rule: `skill_data.disable_singlemode = 0 AND
  skill_data.is_general_skill = 1`; the snapshot contains 223 rows from 710
  master skill rows. Unique/evolution/character-only rows are not exposed as
  Global Add Skills options.
- Snapshot: `skills.global.json`, retrieved 2026-08-24 UTC.

Each skill has an ASCII-normalized offline search query plus aliases. ASCII
punctuation from the client name is retained because the game's filter treats
commas, hyphens, apostrophes, parentheses, colons, and exclamation marks as
searchable characters. The JSON profile focuses the search box, injects the
query, submits it, and uses the shared Windows OCR layer to find and click the
actual Global result row. A static result row is not guessed; it is only a
legacy fallback for older snapshots that explicitly declare `gameSearchMapped`
and a positive `searchResultRow`.

The catalog describes the Global client's selectable general-skill subset at
the recorded client snapshot. Availability can still vary with the current
game state/client build; if OCR cannot find the requested result, the pipeline
reports the skill name and does not silently apply another row.
