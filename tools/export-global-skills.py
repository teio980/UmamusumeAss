#!/usr/bin/env python3
"""Export the skill picker catalog from a Global client's master.mdb.

The game client is the authority for the Global skill set.  This exporter
deliberately does not scrape a JP/Global guide: names, descriptions, costs,
availability flags, and skill conditions are read from the same master.mdb
that the installed Global client uses.

The generated JSON is a checked-in runtime snapshot.  The master database is
not redistributed by this repository; use ``adb pull`` (or a local backup)
and pass its path with ``--master``.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sqlite3
import unicodedata
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


SKILL_CATEGORY_NAMES = {
    0: "passive",
    1: "debuff",
    2: "speed",
    3: "acceleration",
    4: "recovery",
    5: "unique",
    101: "special",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Export Global Add Skills data from master.mdb"
    )
    parser.add_argument("--master", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--region", default="global")
    parser.add_argument("--client-package", default="com.cygames.umamusume")
    parser.add_argument("--client-version", default="")
    parser.add_argument("--device", default="")
    parser.add_argument("--retrieved-at-utc", default="")
    return parser.parse_args()


def utc_now() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace(
        "+00:00", "Z"
    )


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def clean_text(value: Any) -> str:
    if value is None:
        return ""
    return " ".join(str(value).replace("\\n", " ").split())


def ascii_query(value: str) -> str:
    """Return a Global-client search query that is safe for adb input.

    The game's skill filter treats punctuation as part of the searchable name.
    In particular, removing commas from numeric names (``1,500,000 CC``) or
    replacing hyphens/apostrophes with spaces makes an otherwise valid skill
    impossible to find.  Keep ASCII punctuation and let the runtime's ADB
    escaping handle shell metacharacters; only discard non-ASCII decoration
    such as the trailing ``○`` marker.
    """

    normalized = unicodedata.normalize("NFKD", value)
    ascii_value = normalized.encode("ascii", "ignore").decode("ascii")
    return " ".join(ascii_value.split())


def aliases_for(name: str, query: str) -> list[str]:
    values = [query]
    compact = re.sub(r"[^A-Za-z0-9]", "", query)
    if compact and compact.lower() != query.replace(" ", "").lower():
        values.append(compact)
    return list(dict.fromkeys(item for item in values if item))


def combine_conditions(row: sqlite3.Row) -> str:
    conditions = [clean_text(row["condition_1"]), clean_text(row["condition_2"])]
    return "@".join(item for item in conditions if item)


def main() -> None:
    args = parse_args()
    master = args.master.resolve()
    if not master.is_file():
        raise SystemExit(f"master database not found: {master}")

    retrieved_at = args.retrieved_at_utc.strip() or utc_now()
    database_hash = file_sha256(master)
    database_stat = master.stat()

    connection = sqlite3.connect(master)
    connection.row_factory = sqlite3.Row
    try:
        total_rows = connection.execute("SELECT COUNT(*) FROM skill_data").fetchone()[0]
        available_rows = connection.execute(
            """
            SELECT COUNT(*) FROM skill_data
            WHERE disable_singlemode = 0 AND is_general_skill = 1
            """
        ).fetchone()[0]
        rows = connection.execute(
            """
            SELECT
                skill.id,
                skill.rarity,
                skill.grade_value,
                skill.skill_category,
                skill.group_id,
                skill.condition_1,
                skill.condition_2,
                skill.icon_id,
                skill.disp_order,
                skill.disable_singlemode,
                skill.is_general_skill,
                skill.start_date,
                skill.end_date,
                COALESCE(name.text, '') AS skill_name,
                COALESCE(description.text, '') AS effect_summary,
                COALESCE(cost.need_skill_point, 0) AS need_skill_point
            FROM skill_data AS skill
            LEFT JOIN text_data AS name
                ON name.category = 47 AND name."index" = skill.id
            LEFT JOIN text_data AS description
                ON description.category = 48 AND description."index" = skill.id
            LEFT JOIN single_mode_skill_need_point AS cost
                ON cost.id = skill.id
            WHERE skill.disable_singlemode = 0
              AND skill.is_general_skill = 1
            ORDER BY skill.disp_order, skill.id
            """
        ).fetchall()
    finally:
        connection.close()

    skill_ids = [int(row["id"]) for row in rows]
    skill_names = [clean_text(row["skill_name"]) for row in rows]
    if len(skill_ids) != len(set(skill_ids)):
        raise SystemExit("Global master export contains duplicate skill IDs")
    if len(skill_names) != len(set(skill_names)):
        raise SystemExit("Global master export contains duplicate skill names")
    if any(int(row["rarity"] or 0) != 1 for row in rows):
        raise SystemExit(
            "Global Add Skills selection rule changed: expected rarity=1 rows"
        )

    skills: list[dict[str, Any]] = []
    for row in rows:
        name = clean_text(row["skill_name"])
        if not name:
            raise SystemExit(f"skill {row['id']} has no Global client name")
        query = ascii_query(name) or name
        skills.append(
            {
                "skillId": int(row["id"]),
                "skillName": name,
                "originalName": "",
                "aliases": aliases_for(name, query),
                "rarity": int(row["rarity"] or 0),
                "gradeValue": int(row["grade_value"] or 0),
                "skillCategory": SKILL_CATEGORY_NAMES.get(
                    int(row["skill_category"] or 0),
                    f"category-{int(row['skill_category'] or 0)}",
                ),
                "skillCategoryId": int(row["skill_category"] or 0),
                "effectSummary": clean_text(row["effect_summary"]),
                "needSkillPoint": int(row["need_skill_point"] or 0),
                "activationCondition": combine_conditions(row),
                "iconId": int(row["icon_id"] or 0),
                "searchText": query,
                # Static result rows are intentionally not guessed.  The JSON
                # pipeline uses OCR to locate the actual row after filtering.
                "gameSearchMapped": False,
                "searchResultRow": 0,
                "availableInGlobal": True,
                "availabilitySource": "global-client-master",
                "singleModeEnabled": True,
                "isGeneralSkill": bool(row["is_general_skill"]),
                "masterStartDate": int(row["start_date"] or 0),
                "masterEndDate": int(row["end_date"] or 0),
            }
        )

    payload = {
        "schema": "uma.independent-training.skills.v4",
        "region": args.region,
        "source": {
            "sourceName": "Installed Umamusume Global client master.mdb",
            "sourceType": "game-client-master-db",
            "sourceUrl": "",
            "region": args.region,
            "retrievedAtUtc": retrieved_at,
            "clientPackage": args.client_package,
            "clientVersion": args.client_version,
            "device": args.device,
            "masterPath": master.name,
            "masterSha256": database_hash,
            "masterSizeBytes": database_stat.st_size,
            "masterModifiedAtUtc": datetime.fromtimestamp(
                database_stat.st_mtime, timezone.utc
            )
            .replace(microsecond=0)
            .isoformat()
            .replace("+00:00", "Z"),
            "selectionRule": (
                "skill_data.disable_singlemode = 0 AND "
                "skill_data.is_general_skill = 1"
            ),
            "counts": {
                "masterSkillRows": total_rows,
                "availableSkillRows": available_rows,
                "exportedSkillRows": len(skills),
                "excludedSkillRows": total_rows - len(skills),
            },
        },
        "skills": skills,
        "searchRule": (
            "searchText is an ASCII-normalized query retaining client-name "
            "punctuation; "
            "the actual result row is located and clicked by OCR at runtime. "
            "gameSearchMapped remains false until a static row is independently verified."
        ),
        "searchSourceUrl": "",
        "searchSourceArray": 0,
    }

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(
        f"Saved {len(skills)} Global client skills "
        f"({total_rows} master rows, {total_rows - len(skills)} excluded) to {args.out}"
    )


if __name__ == "__main__":
    main()
