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

- Runtime snapshot: `resource/uma/database/global/skills.json`, alongside the
  existing Global trainee, support-card, and race data. The Independent
  Training skill picker and execution pipeline read this file directly.
- Refresh command: `python tools/crawl-global-skills.py`.
- Skill names, types, effects, SP costs, and conditions come from
  <https://gacha-data.com/umamusume/skills/>. Skill IDs are cross-referenced
  against <https://umamusu.wiki/Game:List_of_Skills>. The JSON stores source
  URLs, retrieval time, and the ID resolution for every row.
- The snapshot contains 725 skills: 448 Normal, 152 Rare, and 125 Unique.
  All appear in the picker. The 579 Normal/Rare rows with a positive SP cost
  are selectable. The other 146 retain `needSkillPoint: null` where the source
  has no price and remain visible but disabled.

The earlier 223 client-verified search queries and result rows are embedded in
the complete catalog. The game search uses each skill's `searchText`, then OCR
locates and clicks its name. Only those 223 verified entries can use a static
result-row fallback if OCR misses. New entries use name search and OCR; if the
game does not show a match, execution reports the skill name and query.

`needSkillPoint` is the cost of that skill entry. A higher tier can require
buying its lower tier first, so the combined purchase cost may be larger.
Availability may vary with the current game state or client version.
