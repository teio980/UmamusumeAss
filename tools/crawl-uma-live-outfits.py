#!/usr/bin/env python3
"""Download standing in-game live-outfit character assets for the local Uma database."""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import tempfile
import time
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from urllib.request import Request, urlopen


WIKI_BASE_URL = "https://umamusu.wiki"
WIKI_API_URL = f"{WIKI_BASE_URL}/w/api.php"
DEFAULT_OUTFIT_ID = "000101"
USER_AGENT = "UmamusumeAss in-game standing outfit asset crawler/1.0"


def parse_args() -> argparse.Namespace:
    repository_root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--database-dir",
        type=Path,
        default=repository_root / "resource" / "uma" / "database" / "global",
        help="directory containing base_characters.json",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=repository_root
        / "resource"
        / "uma"
        / "assets"
        / "images"
        / "global"
        / "live_outfits",
        help="directory in which standing live outfit PNG files are written",
    )
    parser.add_argument(
        "--outfit-id",
        default=DEFAULT_OUTFIT_ID,
        help="game chara-stand outfit ID; 000101 is the default standing live outfit",
    )
    parser.add_argument("--timeout", type=float, default=30)
    parser.add_argument("--delay", type=float, default=0.05)
    parser.add_argument("--overwrite", action="store_true")
    return parser.parse_args()


def fetch(url: str, timeout: float) -> bytes:
    curl = shutil.which("curl.exe") or shutil.which("curl")
    if curl:
        completed = subprocess.run(
            [
                curl,
                "--fail",
                "--location",
                "--silent",
                "--show-error",
                "--max-time",
                str(max(1, int(timeout))),
                "--user-agent",
                USER_AGENT,
                url,
            ],
            check=True,
            capture_output=True,
        )
        return completed.stdout

    request = Request(url, headers={"User-Agent": USER_AGENT})
    with urlopen(request, timeout=timeout) as response:
        return response.read()


def fetch_json(url: str, timeout: float) -> dict:
    return json.loads(fetch(url, timeout).decode("utf-8"))


def load_base_characters(database_dir: Path) -> list[dict]:
    with (database_dir / "base_characters.json").open(
        "r", encoding="utf-8-sig"
    ) as stream:
        records = json.load(stream)
    if not isinstance(records, list):
        raise ValueError("base_characters.json must contain an array")
    return records


def find_game_stand_url(base_id: int, outfit_id: str, timeout: float) -> str | None:
    filename = f"Game Asset chara stand {base_id} {outfit_id}.png"
    query = (
        f"{WIKI_API_URL}?action=query&format=json&titles={quote(f'File:{filename}') }"
        "&prop=imageinfo&iiprop=url"
    )
    payload = fetch_json(query, timeout)
    pages = payload.get("query", {}).get("pages", {})
    for page in pages.values():
        image_info = page.get("imageinfo")
        if image_info and image_info[0].get("url"):
            return image_info[0]["url"]
    return None


def write_atomic(data: bytes, target: Path) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary_name = tempfile.mkstemp(
        prefix=f"{target.stem}.", suffix=".tmp", dir=target.parent
    )
    os.close(fd)
    temporary = Path(temporary_name)
    try:
        temporary.write_bytes(data)
        os.replace(temporary, target)
    finally:
        temporary.unlink(missing_ok=True)


def main() -> int:
    args = parse_args()
    if not args.outfit_id.isdigit() or len(args.outfit_id) != 6:
        raise ValueError("--outfit-id must be a six-digit game outfit ID")

    records = load_base_characters(args.database_dir)
    args.output_dir.mkdir(parents=True, exist_ok=True)

    results: list[dict] = []
    for index, record in enumerate(records, start=1):
        base_id = int(record["base_character_id"])
        name = record.get("name_en", "")
        target = args.output_dir / f"{base_id}.png"
        result = {
            "base_character_id": base_id,
            "name_en": name,
            "game_outfit_id": args.outfit_id,
            "source_url": None,
            "path": str(target).replace("\\", "/"),
            "status": "downloaded",
        }
        try:
            image_url = find_game_stand_url(base_id, args.outfit_id, args.timeout)
            result["source_url"] = image_url
            if image_url is None:
                result["status"] = "missing-game-asset"
            elif target.exists() and not args.overwrite:
                result["status"] = "skipped"
            else:
                write_atomic(fetch(image_url, args.timeout), target)
        except (HTTPError, URLError, OSError, UnicodeError, ValueError, json.JSONDecodeError) as exc:
            result["status"] = "failed"
            result["error"] = str(exc)

        results.append(result)
        print(f"[{index:>2}/{len(records)}] {name} ({base_id}): {result['status']}")
        if args.delay > 0 and index < len(records):
            time.sleep(args.delay)

    manifest = {
        "format": "uma-game-chara-stand-live-outfit-v1",
        "source": WIKI_BASE_URL,
        "game_outfit_id": args.outfit_id,
        "description": (
            "In-game 512x512 standing chara-stand assets for the default live outfit; "
            "original PNG files, no crop or resize."
        ),
        "count": len(results),
        "downloaded": sum(item["status"] == "downloaded" for item in results),
        "skipped": sum(item["status"] == "skipped" for item in results),
        "failed": sum(item["status"] == "failed" for item in results),
        "missing": sum(item["status"] == "missing-game-asset" for item in results),
        "images": results,
    }
    with (args.output_dir / "manifest.json").open("w", encoding="utf-8") as stream:
        json.dump(manifest, stream, ensure_ascii=False, indent=2)
        stream.write("\n")

    failures = [
        item
        for item in results
        if item["status"] in {"failed", "missing-game-asset"}
    ]
    print(f"Saved {len(results) - len(failures)} standing live outfit images to {args.output_dir}")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
