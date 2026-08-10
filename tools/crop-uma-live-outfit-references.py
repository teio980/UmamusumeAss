#!/usr/bin/env python3
"""Create face-only live-outfit references from in-game standing chara-stand PNGs."""

from __future__ import annotations

import argparse
from collections import deque
from pathlib import Path

from PIL import Image


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
        / "live_outfits",
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
        help="face-only reference whose output dimensions should be reused",
    )
    parser.add_argument("--overwrite", action="store_true")
    return parser.parse_args()


def is_face_pixel(pixel: tuple[int, int, int, int]) -> bool:
    red, green, blue, alpha = pixel
    return (
        alpha >= 128
        and red >= 180
        and green >= 110
        and blue >= 90
        and red - green >= 20
        and green - blue >= 5
    )


def find_face_component(image: Image.Image) -> tuple[int, int, int, int]:
    rgba = image.convert("RGBA")
    width, height = rgba.size
    scan_height = min(height, 300)
    pixels = rgba.load()
    mask = bytearray(width * scan_height)
    for y in range(scan_height):
        row_start = y * width
        for x in range(width):
            if is_face_pixel(pixels[x, y]):
                mask[row_start + x] = 1

    visited = bytearray(len(mask))
    components: list[tuple[int, tuple[int, int, int, int]]] = []
    for y in range(scan_height):
        for x in range(width):
            start = y * width + x
            if not mask[start] or visited[start]:
                continue

            queue = deque([start])
            visited[start] = 1
            area = 0
            min_x = width
            min_y = scan_height
            max_x = 0
            max_y = 0
            while queue:
                current = queue.popleft()
                current_y, current_x = divmod(current, width)
                area += 1
                min_x = min(min_x, current_x)
                min_y = min(min_y, current_y)
                max_x = max(max_x, current_x)
                max_y = max(max_y, current_y)
                for delta_y, delta_x in (
                    (1, 0),
                    (-1, 0),
                    (0, 1),
                    (0, -1),
                ):
                    next_x = current_x + delta_x
                    next_y = current_y + delta_y
                    if not (0 <= next_x < width and 0 <= next_y < scan_height):
                        continue
                    next_index = next_y * width + next_x
                    if mask[next_index] and not visited[next_index]:
                        visited[next_index] = 1
                        queue.append(next_index)

            component_width = max_x - min_x + 1
            component_height = max_y - min_y + 1
            if area >= 500 and component_width >= 40 and component_height >= 40:
                components.append(
                    (area, (min_x, min_y, max_x + 1, max_y + 1))
                )

    if not components:
        raise ValueError("could not locate a face-colored component")
    return max(components, key=lambda item: item[0])[1]


def make_face_crop(
    image: Image.Image,
    face_box: tuple[int, int, int, int],
    output_size: tuple[int, int],
    *,
    center_y_override: int | None = None,
) -> Image.Image:
    """Crop the standardized head window used by the in-game 512px cards.

    The color-component locator is useful as a sanity check, but skin-colored
    hair, arms, and costume highlights can make its outer box too large. The
    source cards share the same centered portrait layout, so a fixed face
    window is safer: it contains the complete facial features and only a
    minimal edge margin while ending above the choker and costume.
    """
    source_width, source_height = image.size
    output_width, output_height = output_size
    aspect_ratio = output_width / output_height
    face_left, face_top, face_right, face_bottom = face_box
    face_width = face_right - face_left

    # Most components are the face; a few blonde/brown hairstyles merge with
    # the skin mask and become unusually wide. Use the centered portrait
    # fallback for those outliers, while centering normal faces individually.
    if face_width > 190:
        center_x = source_width // 2
        crop_width = round(170 * aspect_ratio)
    else:
        center_x = (face_left + face_right) // 2
        crop_width = max(150, face_width + 10)
    crop_width = min(crop_width, source_width)
    crop_height = min(source_height, max(1, round(crop_width / aspect_ratio)))
    del face_top, face_bottom
    center_y = 178 if center_y_override is None else center_y_override
    crop_top = center_y - crop_height // 2
    crop_top = max(0, min(crop_top, source_height - crop_height))
    crop_left = center_x - crop_width // 2
    crop_left = max(0, min(crop_left, source_width - crop_width))
    crop_box = (
        crop_left,
        crop_top,
        crop_left + crop_width,
        crop_top + crop_height,
    )
    face_reference = image.crop(crop_box).resize(output_size, Image.Resampling.LANCZOS)
    # Remove the remaining outer hair margin after normalization. Keeping this
    # second pass in reference coordinates makes every saved template match
    # the tight face-only shape of the 1011 example.
    tight_box = (
        round(output_width * 12 / 141),
        round(output_height * 18 / 147),
        round(output_width * 129 / 141),
        round(output_height * 142 / 147),
    )
    return face_reference.crop(tight_box).resize(
        output_size, Image.Resampling.LANCZOS
    )


def main() -> int:
    args = parse_args()
    if not args.example.is_file():
        raise FileNotFoundError(f"example reference not found: {args.example}")
    with Image.open(args.example) as example:
        output_size = example.size

    args.output_dir.mkdir(parents=True, exist_ok=True)
    successes = 0
    failures: list[str] = []
    for source in sorted(args.input_dir.glob("*.png")):
        if not source.stem.isdigit():
            continue
        target = args.output_dir / f"{source.stem}_live.webp"
        if target.exists() and not args.overwrite:
            print(f"{source.stem}: skipped")
            successes += 1
            continue
        try:
            with Image.open(source) as image:
                face_box = find_face_component(image)
                crop = make_face_crop(image, face_box, output_size)
                crop.save(target, format="WEBP", lossless=True, method=6)
            print(f"{source.stem}: {face_box} -> {output_size}")
            successes += 1
        except (OSError, ValueError) as exc:
            failures.append(f"{source.stem}: {exc}")

    print(f"Created {successes} face-only references in {args.output_dir}")
    for failure in failures:
        print(f"FAILED {failure}")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
