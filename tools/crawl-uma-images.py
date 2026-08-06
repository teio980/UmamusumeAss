#!/usr/bin/env python3
"""Download the full-size character images referenced by the local Uma database."""

from __future__ import annotations

import argparse
import json
import os
import sys
import tempfile
import time
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.parse import urlparse
from urllib.request import Request, urlopen


USER_AGENT = "UmamusumeAss character image crawler/1.0"


def parse_args() -> argparse.Namespace:
    repository_root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--database-dir",
        type=Path,
        default=repository_root / "resource" / "uma" / "database" / "global",
        help="directory containing trainees.json",
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
        / "trainees",
        help="directory in which original image files are written",
    )
    parser.add_argument(
        "--timeout",
        type=float,
        default=30,
        help="network timeout per image in seconds",
    )
    parser.add_argument(
        "--delay",
        type=float,
        default=0.05,
        help="delay between requests in seconds",
    )
    parser.add_argument(
        "--overwrite",
        action="store_true",
        help="download images that already exist",
    )
    parser.add_argument(
        "--limit",
        type=int,
        default=None,
        help="process only the first N database records",
    )
    return parser.parse_args()


def load_records(database_dir: Path) -> list[dict]:
    path = database_dir / "trainees.json"
    with path.open("r", encoding="utf-8-sig") as stream:
        records = json.load(stream)
    if not isinstance(records, list):
        raise ValueError(f"Expected an array in {path}")
    return records


def download(url: str, timeout: float) -> bytes:
    request = Request(url, headers={"User-Agent": USER_AGENT})
    with urlopen(request, timeout=timeout) as response:
        return response.read()


def extension_for(url: str) -> str:
    extension = Path(urlparse(url).path).suffix.lower()
    return extension if extension in {".webp", ".png", ".jpg", ".jpeg"} else ".bin"


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


def process_record(
    record: dict,
    output_dir: Path,
    timeout: float,
    overwrite: bool,
) -> dict:
    trainee_id = int(record["trainee_id"])
    url = record.get("image_url")
    extension = extension_for(url) if url else ".bin"
    target = output_dir / f"{trainee_id}{extension}"
    result = {
        "trainee_id": trainee_id,
        "name_en": record.get("name_en", ""),
        "source_url": url,
        "path": str(target).replace("\\", "/"),
        "status": "skipped" if target.exists() and not overwrite else "downloaded",
    }
    if target.exists() and not overwrite:
        return result
    if not url:
        result["status"] = "missing-url"
        return result

    try:
        data = download(url, timeout)
        if not data:
            raise ValueError("downloaded an empty file")
        write_atomic(data, target)
    except (HTTPError, URLError, OSError, ValueError) as exc:
        result["status"] = "failed"
        result["error"] = str(exc)
    return result


def main() -> int:
    args = parse_args()
    if args.timeout <= 0 or args.delay < 0:
        raise SystemExit("timeout must be positive and delay cannot be negative")
    if args.limit is not None and args.limit <= 0:
        raise SystemExit("limit must be positive")

    records = load_records(args.database_dir)
    if args.limit is not None:
        records = records[: args.limit]
    args.output_dir.mkdir(parents=True, exist_ok=True)

    results: list[dict] = []
    total = len(records)
    for index, record in enumerate(records, start=1):
        result = process_record(record, args.output_dir, args.timeout, args.overwrite)
        results.append(result)
        status = result["status"]
        suffix = f": {result['error']}" if status == "failed" else ""
        print(
            f"[{index:>3}/{total}] {record.get('name_en', '')} "
            f"({record.get('trainee_id')}): {status}{suffix}"
        )
        if args.delay > 0 and index < total and status != "skipped":
            time.sleep(args.delay)

    manifest = {
        "format": "uma-source-image-v1",
        "source": "https://www.umamusume.run",
        "images_are": "original downloaded files; no crop or resize applied",
        "count": len(results),
        "downloaded": sum(item["status"] == "downloaded" for item in results),
        "skipped": sum(item["status"] == "skipped" for item in results),
        "failed": sum(item["status"] == "failed" for item in results),
        "images": results,
    }
    with (args.output_dir / "manifest.json").open("w", encoding="utf-8") as stream:
        json.dump(manifest, stream, ensure_ascii=False, indent=2)
        stream.write("\n")

    failed = [item for item in results if item["status"] == "failed"]
    print(f"Saved {len(results) - len(failed)} original images to {args.output_dir}")
    if failed:
        print(f"{len(failed)} images failed; rerun the command to retry.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
