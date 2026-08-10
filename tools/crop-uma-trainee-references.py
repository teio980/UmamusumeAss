#!/usr/bin/env python3
"""Create face-only victory-outfit references from trainee outfit images."""

from __future__ import annotations

import argparse
import importlib.util
from pathlib import Path

from PIL import Image


MANUAL_FACE_BOXES: dict[str, tuple[int, int, int, int]] = {
    # These two poses do not produce a usable skin-colored component because
    # the face is shadowed or merged with the surrounding costume/hair.
    "102801": (190, 100, 350, 300),
    "103002": (150, 100, 330, 290),
}

RACE_TIGHT_BOX = (8, 6, 133, 118)


def load_crop_module():
    module_path = Path(__file__).with_name("crop-uma-live-outfit-references.py")
    spec = importlib.util.spec_from_file_location("uma_face_crop", module_path)
    if spec is None or spec.loader is None:
        raise ImportError(f"could not load crop helper: {module_path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def parse_args() -> argparse.Namespace:
    repository_root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--input-dir",
        type=Path,
        default=repository_root
        / "resource"
        / "uma"
        / "assets"
        / "images"
        / "global"
        / "trainees",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=repository_root / "resource" / "uma" / "system_reference",
    )
    parser.add_argument(
        "--example",
        type=Path,
        default=repository_root
        / "resource"
        / "uma"
        / "system_reference"
        / "1011_live.webp",
    )
    parser.add_argument("--overwrite", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    crop_module = load_crop_module()
    if not args.example.is_file():
        raise FileNotFoundError(f"example reference not found: {args.example}")
    with Image.open(args.example) as example:
        output_size = example.size

    args.output_dir.mkdir(parents=True, exist_ok=True)
    successes = 0
    failures: list[str] = []
    for source in sorted(args.input_dir.glob("*.webp")):
        if not source.stem.isdigit():
            continue
        target = args.output_dir / f"{source.stem}.webp"
        if target.exists() and not args.overwrite:
            print(f"{source.stem}: skipped")
            successes += 1
            continue
        try:
            with Image.open(source) as image:
                face_box = MANUAL_FACE_BOXES.get(source.stem)
                if face_box is None:
                    face_box = crop_module.find_face_component(image)
                center_y = max(110, (face_box[1] + face_box[3]) // 2 - 15)
                crop = crop_module.make_face_crop(
                    image,
                    face_box,
                    output_size,
                    center_y_override=center_y,
                )
                crop = crop.crop(RACE_TIGHT_BOX).resize(
                    output_size, Image.Resampling.LANCZOS
                )
                crop.save(target, format="WEBP", lossless=True, method=6)
            print(f"{source.stem}: {face_box} -> {output_size}")
            successes += 1
        except (OSError, ValueError) as exc:
            failures.append(f"{source.stem}: {exc}")

    print(f"Created {successes} face-only victory references in {args.output_dir}")
    for failure in failures:
        print(f"FAILED {failure}")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
