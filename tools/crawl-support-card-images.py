#!/usr/bin/env python3
"""Download the support card images referenced by the local Uma database."""

from __future__ import annotations

import argparse
from io import BytesIO
import json
import os
import sys
import tempfile
import time
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

try:
    from PIL import Image
except ImportError as exc:
    raise SystemExit(
        "Pillow is required to create support-card selection templates; "
        "install tools/requirements-support-card-images.txt"
    ) from exc


USER_AGENT = "UmamusumeAss support card image crawler/1.0"
TEMPLATE_SIZE = (130, 117)


def parse_args() -> argparse.Namespace:
    repository_root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--database-dir",
        type=Path,
        default=repository_root / "resource" / "uma" / "database" / "global",
        help="directory containing support_cards.json",
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
        / "support_cards",
        help="directory in which support card image files are written",
    )
    parser.add_argument(
        "--template-dir",
        type=Path,
        default=repository_root
        / "resource"
        / "uma"
        / "assets"
        / "templates"
        / "global"
        / "support_cards",
        help="directory in which cropped card-selection templates are written",
    )
    parser.add_argument("--timeout", type=float, default=30)
    parser.add_argument("--delay", type=float, default=0.05)
    parser.add_argument("--overwrite", action="store_true")
    parser.add_argument("--limit", type=int, default=None)
    return parser.parse_args()


def load_records(database_dir: Path) -> list[dict]:
    path = database_dir / "support_cards.json"
    with path.open("r", encoding="utf-8-sig") as stream:
        records = json.load(stream)
    if not isinstance(records, list):
        raise ValueError(f"Expected an array in {path}")
    return records


def download(url: str, timeout: float) -> bytes:
    request = Request(url, headers={"User-Agent": USER_AGENT})
    with urlopen(request, timeout=timeout) as response:
        data = response.read()
    if len(data) < 12 or data[:4] != b"RIFF" or data[8:12] != b"WEBP":
        raise ValueError("response is not a WebP image")
    return data


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


def write_selection_template(source_path: Path, target: Path) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    with Image.open(source_path) as source:
        image = source.convert("RGB")
        width, height = image.size
        target_ratio = TEMPLATE_SIZE[0] / TEMPLATE_SIZE[1]

        if width / height > target_ratio:
            crop_width = round(height * target_ratio)
            left = (width - crop_width) // 2
            box = (left, 0, left + crop_width, height)
        else:
            crop_height = round(width / target_ratio)
            top = (height - crop_height) // 2
            box = (0, top, width, top + crop_height)

        resampling = getattr(Image, "Resampling", Image).LANCZOS
        buffer = BytesIO()
        image.crop(box).resize(TEMPLATE_SIZE, resampling).save(
            buffer, format="PNG", optimize=True
        )
        write_atomic(buffer.getvalue(), target)


def process_record(
    record: dict,
    output_dir: Path,
    template_dir: Path,
    timeout: float,
    overwrite: bool,
) -> dict:
    support_card_id = int(record["support_card_id"])
    url = record.get("image_url")
    target = output_dir / f"{support_card_id}.webp"
    result = {
        "support_card_id": support_card_id,
        "name_en": record.get("name_en", ""),
        "source_url": url,
        "path": str(target).replace("\\", "/"),
        "status": "skipped" if target.exists() and not overwrite else "downloaded",
    }
    if not target.exists() or overwrite:
        if not url:
            result["status"] = "missing-url"
            return result

        try:
            data = download(url, timeout)
            write_atomic(data, target)
        except (HTTPError, URLError, OSError, ValueError) as exc:
            result["status"] = "failed"
            result["error"] = str(exc)
            return result

    template = template_dir / str(support_card_id) / "card.png"
    result["template_path"] = str(template).replace("\\", "/")
    result["template_status"] = (
        "skipped" if template.exists() and not overwrite else "created"
    )
    if not template.exists() or overwrite:
        try:
            write_selection_template(target, template)
        except (OSError, ValueError) as exc:
            result["template_status"] = "failed"
            result["template_error"] = str(exc)
    if not url and not target.exists():
        result["status"] = "missing-url"
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
    args.template_dir.mkdir(parents=True, exist_ok=True)

    results: list[dict] = []
    total = len(records)
    for index, record in enumerate(records, start=1):
        result = process_record(
            record,
            args.output_dir,
            args.template_dir,
            args.timeout,
            args.overwrite,
        )
        results.append(result)
        status = result["status"]
        if status == "failed":
            suffix = f": {result['error']}"
        elif result.get("template_status") == "failed":
            suffix = f": {result['template_error']}"
        else:
            suffix = f"; selection template {result.get('template_status', 'unavailable')}"
        print(
            f"[{index:>3}/{total}] card {record.get('support_card_id')}: "
            f"{status}{suffix}"
        )
        if args.delay > 0 and index < total and status == "downloaded":
            time.sleep(args.delay)

    manifest = {
        "format": "uma-source-support-card-image-v1",
        "sources": sorted({item["source_url"].split("/", 3)[2]
                           for item in results if item.get("source_url")}),
        "images_are": "original downloaded files; no crop or resize applied",
        "count": len(results),
        "downloaded": sum(item["status"] == "downloaded" for item in results),
        "skipped": sum(item["status"] == "skipped" for item in results),
        "failed": sum(item["status"] == "failed" for item in results),
        "selection_templates_created": sum(
            item.get("template_status") == "created" for item in results
        ),
        "selection_templates_skipped": sum(
            item.get("template_status") == "skipped" for item in results
        ),
        "selection_templates_failed": sum(
            item.get("template_status") == "failed" for item in results
        ),
        "images": results,
    }
    with (args.output_dir / "manifest.json").open("w", encoding="utf-8") as stream:
        json.dump(manifest, stream, ensure_ascii=False, indent=2)
        stream.write("\n")

    failed = [
        item for item in results
        if item["status"] not in {"downloaded", "skipped"}
        or item.get("template_status") == "failed"
    ]
    available_images = sum(
        item["status"] in {"downloaded", "skipped"} for item in results
    )
    print(
        f"Available card images: {available_images}; "
        f"created selection templates: {manifest['selection_templates_created']}."
    )
    if failed:
        print(f"{len(failed)} images failed; rerun the command to retry.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
