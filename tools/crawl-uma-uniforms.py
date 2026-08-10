#!/usr/bin/env python3
"""Download official Tracen Academy uniform art for the local Uma database."""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import tempfile
import time
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


BASE_URL = "https://umamusume.jp"
IMAGE_HOST = "https://images.microcms-assets.io"
USER_AGENT = "UmamusumeAss official uniform asset crawler/1.0"


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
        / "uniforms",
        help="directory in which uniform PNG files are written",
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


def fetch_text(url: str, timeout: float) -> str:
    return fetch(url, timeout).decode("utf-8")


def load_base_characters(database_dir: Path) -> list[dict]:
    with (database_dir / "base_characters.json").open(
        "r", encoding="utf-8-sig"
    ) as stream:
        records = json.load(stream)
    if not isinstance(records, list):
        raise ValueError("base_characters.json must contain an array")
    return records


def normalize_slug(value: str) -> str:
    return re.sub(r"[^a-z0-9]", "", value.lower())


def official_slugs(index_html: str) -> set[str]:
    slugs = set(
        re.findall(
            r'href=["\']/character/(?P<slug>[a-z0-9_-]+)/?["\']',
            index_html,
            re.IGNORECASE,
        )
    )
    return {slug for slug in slugs if slug != "_payload.json"}


def resolve_slug(name: str, slugs: set[str]) -> str | None:
    normalized_name = normalize_slug(name)
    for slug in slugs:
        if normalize_slug(slug) == normalized_name:
            return slug
    return None


def find_uniform_url(slug: str, page_html: str) -> str | None:
    escaped_slug = re.escape(slug)
    pattern = (
        rf"{re.escape(IMAGE_HOST)}/[^\"'\s]+/{escaped_slug}"
        rf"_01\.png(?:\?[^\"'\s]*)?"
    )
    match = re.search(pattern, page_html, re.IGNORECASE)
    if not match:
        return None
    return match.group(0).split("?", 1)[0]


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
    index_html = fetch_text(f"{BASE_URL}/character/", args.timeout)
    slugs = official_slugs(index_html)
    records = load_base_characters(args.database_dir)
    args.output_dir.mkdir(parents=True, exist_ok=True)

    results: list[dict] = []
    for index, record in enumerate(records, start=1):
        base_id = int(record["base_character_id"])
        name = record.get("name_en", "")
        slug = resolve_slug(name, slugs)
        result = {
            "base_character_id": base_id,
            "name_en": name,
            "official_slug": slug,
            "source_url": None,
            "path": str(args.output_dir / f"{base_id}.png").replace("\\", "/"),
            "status": "downloaded",
        }
        if slug is None:
            result["status"] = "missing-official-page"
            results.append(result)
            print(f"[{index:>2}/{len(records)}] {name}: missing official page")
            continue

        page_url = f"{BASE_URL}/character/{slug}/"
        try:
            page_html = fetch_text(page_url, args.timeout)
            image_url = find_uniform_url(slug, page_html)
            result["source_url"] = image_url
            if image_url is None:
                result["status"] = "missing-uniform-image"
            else:
                target = args.output_dir / f"{base_id}.png"
                if target.exists() and not args.overwrite:
                    result["status"] = "skipped"
                else:
                    write_atomic(fetch(image_url, args.timeout), target)
        except (HTTPError, URLError, OSError, UnicodeError, ValueError) as exc:
            result["status"] = "failed"
            result["error"] = str(exc)

        results.append(result)
        print(f"[{index:>2}/{len(records)}] {name} ({base_id}): {result['status']}")
        if args.delay > 0 and index < len(records):
            time.sleep(args.delay)

    manifest = {
        "format": "uma-official-uniform-v1",
        "source": BASE_URL,
        "description": "Official Tracen Academy uniform art; original PNG files, no crop or resize.",
        "count": len(results),
        "downloaded": sum(item["status"] == "downloaded" for item in results),
        "skipped": sum(item["status"] == "skipped" for item in results),
        "failed": sum(item["status"] == "failed" for item in results),
        "missing": sum(item["status"].startswith("missing-") for item in results),
        "images": results,
    }
    with (args.output_dir / "manifest.json").open("w", encoding="utf-8") as stream:
        json.dump(manifest, stream, ensure_ascii=False, indent=2)
        stream.write("\n")

    failures = [item for item in results if item["status"] in {"failed", "missing-uniform-image", "missing-official-page"}]
    print(f"Saved {len(results) - len(failures)} uniform images to {args.output_dir}")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
