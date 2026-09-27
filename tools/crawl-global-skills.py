#!/usr/bin/env python3
"""Refresh the complete Global skill catalog used by Independent Training.

The public skill table supplies names, effects, costs, and conditions. The
wiki supplies game skill IDs. Previously verified in-game search mappings are
preserved by skill ID from the checked-in catalog when it is refreshed.
"""

from __future__ import annotations

import json
import re
import unicodedata
import urllib.request
from collections import Counter, defaultdict
from datetime import datetime, timezone
from html.parser import HTMLParser
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
SOURCE_URL = "https://gacha-data.com/umamusume/skills/"
WIKI_INDEX_URL = "https://umamusu.wiki/Game:List_of_Skills"
RUNTIME_CATALOG_PATH = ROOT / "resource" / "uma" / "database" / "global" / "skills.json"
OUTPUT_PATH = RUNTIME_CATALOG_PATH
USER_AGENT = "UmamusumeAssSkillCatalog/1.0 (public reference data crawler)"
TABLE_COLUMNS = [
    "name",
    "type",
    "effect",
    "skill_points",
    "evaluation_points",
    "duration_seconds",
    "other_tiers",
    "trainees",
    "hint_cards",
    "activates_when",
]
SPECIAL_SKILL_IDS = {
    # Event-limited Carnival Bonus is omitted from the wiki's main skill
    # index, but has a direct skill page at this ID.
    "Carnival Bonus": 1000011,
}


def fetch_text(url: str) -> str:
    request = urllib.request.Request(
        url,
        headers={"User-Agent": USER_AGENT, "Accept": "text/html"},
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        if response.status != 200:
            raise RuntimeError(f"HTTP {response.status} while fetching {url}")
        return response.read().decode("utf-8", "replace")


def clean_text(value: str) -> str:
    return " ".join(value.split())


def ascii_query(value: str) -> str:
    normalized = unicodedata.normalize("NFKD", value)
    return clean_text(normalized.encode("ascii", "ignore").decode("ascii"))


def aliases_for(name: str, query: str) -> list[str]:
    values = [query]
    compact = re.sub(r"[^A-Za-z0-9]", "", query)
    if compact and compact.lower() != query.replace(" ", "").lower():
        values.append(compact)
    return list(dict.fromkeys(item for item in values if item))


class SkillTableParser(HTMLParser):
    """Read rows and the first cell's icon from the GachaData table."""

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.rows: list[dict[str, Any]] = []
        self._in_row = False
        self._in_cell = False
        self._row: list[str] = []
        self._cell: list[str] = []
        self._icon_src = ""
        self._icons: list[str] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        attributes = dict(attrs)
        if tag == "tr":
            self._in_row = True
            self._row = []
            self._icons = []
        elif self._in_row and tag in {"td", "th"}:
            self._in_cell = True
            self._cell = []
        elif self._in_cell and tag == "img":
            self._icons.append(attributes.get("src") or "")
        elif self._in_cell and tag == "br":
            self._cell.append(" ")

    def handle_endtag(self, tag: str) -> None:
        if self._in_row and tag in {"td", "th"}:
            self._row.append(clean_text("".join(self._cell)))
            self._in_cell = False
        elif tag == "tr" and self._in_row:
            if self._row:
                self.rows.append({"cells": self._row, "icons": self._icons})
            self._in_row = False

    def handle_data(self, data: str) -> None:
        if self._in_cell:
            self._cell.append(data)


class WikiIndexParser(HTMLParser):
    """Read skill-name-to-ID links and displayed cumulative point costs."""

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.rows: list[list[dict[str, Any]]] = []
        self._in_row = False
        self._in_cell = False
        self._row: list[dict[str, Any]] = []
        self._cell_text: list[str] = []
        self._links: list[dict[str, str]] = []
        self._link: dict[str, Any] | None = None

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        attributes = dict(attrs)
        if tag == "tr":
            self._in_row = True
            self._row = []
        elif self._in_row and tag in {"td", "th"}:
            self._in_cell = True
            self._cell_text = []
            self._links = []
        elif self._in_cell and tag == "a":
            self._link = {"href": attributes.get("href") or "", "text": ""}
        elif self._in_cell and tag == "br":
            self._cell_text.append(" ")

    def handle_endtag(self, tag: str) -> None:
        if tag == "a" and self._link is not None:
            self._link["text"] = clean_text(self._link["text"])
            self._links.append(self._link)
            self._link = None
        elif self._in_row and tag in {"td", "th"}:
            self._row.append(
                {"text": clean_text("".join(self._cell_text)), "links": self._links}
            )
            self._in_cell = False
        elif tag == "tr" and self._in_row:
            if self._row:
                self.rows.append(self._row)
            self._in_row = False

    def handle_data(self, data: str) -> None:
        if self._in_cell:
            self._cell_text.append(data)
            if self._link is not None:
                self._link["text"] += data


def parse_int(value: str) -> int | None:
    match = re.search(r"-?\d+", value.replace(",", ""))
    return int(match.group()) if match else None


def parse_float(value: str) -> float | None:
    match = re.search(r"-?(?:\d+\.?\d*|\.\d+)", value)
    return float(match.group()) if match else None


def icon_id(icons: list[str]) -> int | None:
    for source in icons:
        match = re.search(r"/(\d+)\.(?:webp|png)(?:\?|$)", source)
        if match:
            return int(match.group(1))
    return None


def scrape_skill_rows() -> list[dict[str, Any]]:
    parser = SkillTableParser()
    parser.feed(fetch_text(SOURCE_URL))
    table = [row for row in parser.rows if len(row["cells"]) == len(TABLE_COLUMNS)]
    expected_headers = [
        "Name",
        "Type",
        "Effect",
        "SP",
        "Points",
        "Duration (s)",
        "Other tiers",
        "Trainees",
        "Hint cards",
        "Activates when",
    ]
    if not table or table[0]["cells"] != expected_headers:
        raise RuntimeError("Could not recognize the skill table on GachaData")

    records: list[dict[str, Any]] = []
    for source_row, row in enumerate(table[1:], start=1):
        cells = row["cells"]
        if not cells[0] or not cells[1]:
            continue
        skill_point_cost = parse_int(cells[3])
        evaluation_points = parse_int(cells[4])
        records.append(
            {
                "source_row": source_row,
                "skill_id": None,
                "skill_id_candidates": [],
                "icon_id": icon_id(row["icons"]),
                "name": cells[0],
                "type": cells[1],
                "effect": cells[2],
                "skill_point_cost": skill_point_cost,
                "evaluation_points": evaluation_points,
                "duration_seconds": parse_float(cells[5]),
                "other_tiers": cells[6] or None,
                "trainees": [item.strip() for item in cells[7].split(",") if item.strip()],
                "hint_cards": cells[8] or None,
                "activates_when": cells[9] or None,
                "skill_id_resolution": None,
            }
        )
    if len(records) < 600:
        raise RuntimeError(f"Skill table unexpectedly short: {len(records)} rows")
    return records


def scrape_wiki_ids() -> dict[str, list[dict[str, Any]]]:
    parser = WikiIndexParser()
    parser.feed(fetch_text(WIKI_INDEX_URL))
    by_name: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for row in parser.rows:
        if len(row) < 4:
            continue
        for link in row[1]["links"]:
            match = re.fullmatch(r"/Game:Skills/(\d+)", link["href"])
            if not match or not link["text"]:
                continue
            by_name[link["text"]].append(
                {"skill_id": int(match.group(1)), "listed_total_cost": parse_int(row[3]["text"])}
            )
    return by_name


def local_ids_by_name() -> dict[str, dict[str, Any]]:
    try:
        payload = json.loads(RUNTIME_CATALOG_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}
    return {
        item["skillName"]: item
        for item in payload.get("skills", [])
        if isinstance(item, dict)
        and item.get("skillName")
        and item.get("skillId")
        and item.get("gameSearchMapped") is True
        and int(item.get("searchResultRow") or 0) > 0
    }


def attach_skill_ids(
    records: list[dict[str, Any]],
    wiki_ids: dict[str, list[dict[str, Any]]],
    local_ids: dict[str, dict[str, Any]],
) -> tuple[int, int]:
    resolved = 0
    unresolved = 0
    for record in records:
        candidates = wiki_ids.get(record["name"], [])
        ids = sorted({item["skill_id"] for item in candidates})
        expected_cost = record["skill_point_cost"] or 0

        # Preserve the already verified installed-client mapping and cost for
        # skills previously selectable by the runtime picker.
        local = local_ids.get(record["name"])
        if local and record["type"] == "Normal":
            local_cost = local.get("needSkillPoint")
            if local_cost != record["skill_point_cost"]:
                raise RuntimeError(
                    f"Cost mismatch for {record['name']}: local {local_cost}, "
                    f"reference {record['skill_point_cost']}"
                )
            record["skill_id"] = local["skillId"]
            record["skill_id_candidates"] = [local["skillId"]]
            record["skill_id_resolution"] = "installed-global-client-name"
            resolved += 1
            continue

        if len(candidates) == 1:
            chosen = candidates[0]
            record["skill_id"] = chosen["skill_id"]
            record["skill_id_candidates"] = [chosen["skill_id"]]
            record["skill_id_resolution"] = "wiki-exact-name"
        elif candidates:
            cost_matches = [
                item for item in candidates if item["listed_total_cost"] == expected_cost
            ]
            if len(cost_matches) == 1:
                chosen = cost_matches[0]
                record["skill_id"] = chosen["skill_id"]
                record["skill_id_candidates"] = [chosen["skill_id"]]
                record["skill_id_resolution"] = "wiki-exact-name-and-cost"
            elif record["type"] == "Rare":
                positive_cost_matches = [
                    item for item in candidates if (item["listed_total_cost"] or 0) > 0
                ]
                if len(positive_cost_matches) == 1:
                    chosen = positive_cost_matches[0]
                    record["skill_id"] = chosen["skill_id"]
                    record["skill_id_candidates"] = [chosen["skill_id"]]
                    record["skill_id_resolution"] = "wiki-exact-name-and-rarity"
                else:
                    record["skill_id_candidates"] = ids
            else:
                record["skill_id_candidates"] = ids
        else:
            special_id = SPECIAL_SKILL_IDS.get(record["name"])
            if special_id is not None:
                record["skill_id"] = special_id
                record["skill_id_candidates"] = [special_id]
                record["skill_id_resolution"] = "wiki-direct-skill-page"
            else:
                record["skill_id_candidates"] = []

        if record["skill_id"] is None:
            unresolved += 1
        else:
            resolved += 1
    return resolved, unresolved


def to_runtime_skill(
    record: dict[str, Any],
    verified_skills: dict[str, dict[str, Any]],
) -> dict[str, Any]:
    old = verified_skills.get(record["name"]) if record["type"] == "Normal" else None
    skill_id = record["skill_id"]
    if skill_id is None:
        raise RuntimeError(f"No game skill ID for {record['name']}")
    if old is not None and old["skillId"] != skill_id:
        raise RuntimeError(f"Verified skill ID changed for {record['name']}")

    name = record["name"]
    search_text = (old.get("searchText") if old else None) or ascii_query(name) or name
    point_cost = record["skill_point_cost"]
    selectable = (
        record["type"] in {"Normal", "Rare"}
        and isinstance(point_cost, int)
        and point_cost > 0
    )
    rarity = {"Normal": 1, "Rare": 2, "Unique": 3}.get(record["type"], 0)
    return {
        "skillId": skill_id,
        "skillName": name,
        "originalName": old.get("originalName", "") if old else "",
        "aliases": old.get("aliases", []) if old else aliases_for(name, search_text),
        "rarity": rarity,
        "skillType": record["type"],
        "gradeValue": record["evaluation_points"] or 0,
        "skillCategory": old.get("skillCategory", "") if old else "",
        "skillCategoryId": old.get("skillCategoryId", 0) if old else 0,
        "effectSummary": record["effect"],
        "needSkillPoint": point_cost,
        "activationCondition": record["activates_when"] or "",
        "iconId": record["icon_id"] or 0,
        "searchText": search_text,
        "gameSearchMapped": bool(old and old.get("gameSearchMapped")),
        "searchResultRow": old.get("searchResultRow", 0) if old else 0,
        "availableInGlobal": True,
        "availabilitySource": (
            old.get("availabilitySource", "global-client-master")
            if old else "gacha-data-public-table"
        ),
        "singleModeEnabled": selectable,
        "isGeneralSkill": old.get("isGeneralSkill") if old else None,
        "sourceRow": record["source_row"],
        "skillIdResolution": record["skill_id_resolution"],
        "durationSeconds": record["duration_seconds"],
        "otherTiers": record["other_tiers"],
        "trainees": record["trainees"],
        "hintCards": record["hint_cards"],
    }


def main() -> None:
    records = scrape_skill_rows()
    wiki_ids = scrape_wiki_ids()
    verified_skills = local_ids_by_name()
    resolved_count, unresolved_count = attach_skill_ids(
        records, wiki_ids, verified_skills
    )
    if unresolved_count:
        raise RuntimeError(f"{unresolved_count} skill IDs could not be resolved")
    skills = [to_runtime_skill(record, verified_skills) for record in records]
    skill_ids = [skill["skillId"] for skill in skills]
    if len(skill_ids) != len(set(skill_ids)):
        raise RuntimeError("The complete catalog contains duplicate skill IDs")
    type_counts = Counter(record["type"] for record in records)
    selectable_count = sum(skill["singleModeEnabled"] for skill in skills)
    verified_mapping_count = sum(skill["gameSearchMapped"] for skill in skills)
    payload = {
        "schema": "uma.independent-training.skills.v5",
        "region": "global",
        "source": {
            "sourceName": "GachaData Umamusume Skills",
            "sourceType": "unofficial-community-skill-reference",
            "sourceUrl": SOURCE_URL,
            "region": "global",
            "retrievedAtUtc": datetime.now(timezone.utc)
            .replace(microsecond=0)
            .isoformat()
            .replace("+00:00", "Z"),
            "skillIdCrosswalk": {
                "sourceName": "Umamusume Wiki skill index",
                "sourceUrl": WIKI_INDEX_URL,
                "resolution": (
                    "IDs are matched by exact English skill name; duplicate names "
                    "are disambiguated using listed total skill-point cost. Existing "
                    "verified Global client IDs are preferred when present."
                ),
            },
            "directSkillIdReferences": [
                {
                    "skillName": name,
                    "skillId": skill_id,
                    "sourceUrl": f"https://umamusu.wiki/Game:Skills/{skill_id}",
                }
                for name, skill_id in SPECIAL_SKILL_IDS.items()
            ],
            "counts": {
                "skillRows": len(skills),
                "byType": dict(sorted(type_counts.items())),
                "withSkillPointCost": sum(
                    record["skill_point_cost"] is not None for record in records
                ),
                "selectableSkillRows": selectable_count,
                "verifiedSearchMappings": verified_mapping_count,
                "withSkillId": resolved_count,
            },
            "notes": [
                "The complete catalog includes Normal, Rare, and Unique entries in the existing Global database directory.",
                "needSkillPoint is the per-entry SP shown by the source table, excluding prerequisite-tier costs.",
                "Blank source SP cells are kept as null, not converted to zero.",
                "A positive SP on a Normal or Rare skill makes it selectable; new skills use OCR search without an unverified static row fallback.",
            ],
        },
        "skills": skills,
        "verifiedMappingCount": verified_mapping_count,
        "searchRule": (
            "Previously verified search queries and result rows are retained. "
            "Other selectable skills use ASCII-normalized name search and OCR; "
            "only verified rows permit a fixed-row fallback."
        ),
    }
    OUTPUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    temporary_path = OUTPUT_PATH.with_suffix(".json.tmp")
    temporary_path.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    temporary_path.replace(OUTPUT_PATH)
    print(
        f"Wrote {len(records)} skill records to {OUTPUT_PATH}\n"
        f"Types: {dict(sorted(type_counts.items()))}\n"
        f"Selectable: {selectable_count}; verified mappings: {verified_mapping_count}; "
        f"skill IDs: {resolved_count} resolved"
    )


if __name__ == "__main__":
    main()
