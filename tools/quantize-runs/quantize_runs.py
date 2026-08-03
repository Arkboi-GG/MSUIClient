#!/usr/bin/env python3
"""Compact historical run evidence into indexed visual atlases and lossless record ZIPs."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import re
import shutil
import sys
import zipfile
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont, ImageOps

IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp"}
TILE = (192, 108)
GRID = (10, 16)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def safe_name(value: str) -> str:
    value = re.sub(r"[^A-Za-z0-9._-]+", "-", value).strip("-.")
    return value or "root"


def groups(repo: Path) -> list[tuple[str, Path, list[Path]]]:
    result: list[tuple[str, Path, list[Path]]] = []
    live = repo / "live-runs"
    if live.is_dir():
        for child in sorted(x for x in live.iterdir() if x.is_dir() and not x.name.lower().startswith("n4-")):
            files = sorted(x for x in child.rglob("*") if x.is_file())
            if files:
                result.append((f"live-runs/{child.name}", child, files))
    for name in ("dumps", "portrait-batch", "variant-batch"):
        root = repo / name
        if root.is_dir():
            files = sorted(x for x in root.rglob("*") if x.is_file())
            if files:
                result.append((name, root, files))
    return result


def make_atlases(images: list[Path], root: Path, out: Path) -> list[dict]:
    rows: list[dict] = []
    per_page = GRID[0] * GRID[1]
    font = ImageFont.load_default()
    for page_start in range(0, len(images), per_page):
        page_images = images[page_start:page_start + per_page]
        atlas_name = f"atlas-{page_start // per_page + 1:03d}.webp"
        atlas = Image.new("RGB", (TILE[0] * GRID[0], TILE[1] * GRID[1]), (10, 10, 12))
        draw = ImageDraw.Draw(atlas)
        for local_index, path in enumerate(page_images):
            with Image.open(path) as source:
                source = ImageOps.exif_transpose(source).convert("RGB")
                width, height = source.size
                thumb = source.copy()
                thumb.thumbnail((TILE[0] - 4, TILE[1] - 4), Image.Resampling.LANCZOS)
                x = (local_index % GRID[0]) * TILE[0]
                y = (local_index // GRID[0]) * TILE[1]
                px = x + (TILE[0] - thumb.width) // 2
                py = y + (TILE[1] - thumb.height) // 2
                atlas.paste(thumb, (px, py))
                draw.rectangle((x, y, x + 34, y + 14), fill=(0, 0, 0))
                draw.text((x + 2, y + 2), str(page_start + local_index + 1), fill=(255, 220, 80), font=font)
                rows.append({
                    "path": path.relative_to(root).as_posix(),
                    "sha256": sha256(path),
                    "bytes": path.stat().st_size,
                    "width": width,
                    "height": height,
                    "atlas": atlas_name,
                    "cell": local_index,
                })
        atlas.save(out / atlas_name, "WEBP", quality=72, method=4)
    return rows


def archive_records(records: list[Path], root: Path, out: Path) -> list[dict]:
    rows: list[dict] = []
    if not records:
        return rows
    with zipfile.ZipFile(out / "records.zip", "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in records:
            relative = path.relative_to(root).as_posix()
            digest = sha256(path)
            archive.write(path, relative)
            rows.append({"path": relative, "sha256": digest, "bytes": path.stat().st_size})
        archive.writestr("_quantized_records_manifest.csv", "path,sha256,bytes\n" + "".join(
            f'"{row["path"].replace(chr(34), chr(34) * 2)}",{row["sha256"]},{row["bytes"]}\n'
            for row in rows))
    return rows


def write_image_csv(path: Path, rows: list[dict]) -> None:
    with path.open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=("path", "sha256", "bytes", "width", "height", "atlas", "cell"))
        writer.writeheader()
        writer.writerows(rows)


def build(repo: Path, output: Path) -> dict:
    if output.exists():
        raise SystemExit(f"refusing to overwrite existing output: {output}")
    output.mkdir(parents=True)
    summary = {"version": 1, "repo": str(repo), "groups": [], "sourceFiles": 0,
               "sourceBytes": 0, "imageFiles": 0, "recordFiles": 0}
    for ordinal, (label, root, files) in enumerate(groups(repo), 1):
        out = output / f"{ordinal:03d}-{safe_name(label)}"
        out.mkdir()
        images = [x for x in files if x.suffix.lower() in IMAGE_EXTENSIONS]
        records = [x for x in files if x.suffix.lower() not in IMAGE_EXTENSIONS]
        image_rows = make_atlases(images, root, out)
        record_rows = archive_records(records, root, out)
        write_image_csv(out / "images.csv", image_rows)
        manifest = {
            "label": label,
            "sourceRoot": str(root.resolve()),
            "images": image_rows,
            "records": record_rows,
        }
        (out / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        summary["groups"].append(str((out / "manifest.json").relative_to(output)).replace("\\", "/"))
        summary["sourceFiles"] += len(files)
        summary["sourceBytes"] += sum(x.stat().st_size for x in files)
        summary["imageFiles"] += len(images)
        summary["recordFiles"] += len(records)
        print(f"[{ordinal}] {label}: {len(images)} images, {len(records)} records", flush=True)
    (output / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    return summary


def validate_and_apply(repo: Path, output: Path) -> dict:
    summary = json.loads((output / "summary.json").read_text(encoding="utf-8"))
    targets: list[Path] = []
    expected_bytes = 0
    for relative_manifest in summary["groups"]:
        manifest_path = output / relative_manifest
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        source_root = Path(manifest["sourceRoot"]).resolve()
        source_root.relative_to(repo)
        group_out = manifest_path.parent
        for row in manifest["images"]:
            if not (group_out / row["atlas"]).is_file():
                raise SystemExit(f"missing atlas: {group_out / row['atlas']}")
        if manifest["records"]:
            archive_path = group_out / "records.zip"
            if not archive_path.is_file():
                raise SystemExit(f"missing records archive: {archive_path}")
            with zipfile.ZipFile(archive_path) as archive:
                archived = set(archive.namelist())
                for row in manifest["records"]:
                    if row["path"] not in archived:
                        raise SystemExit(f"record absent from archive: {row['path']}")
        for row in manifest["images"] + manifest["records"]:
            target = (source_root / row["path"]).resolve()
            target.relative_to(source_root)
            if not target.is_file() or sha256(target) != row["sha256"]:
                raise SystemExit(f"source changed or missing; nothing deleted: {target}")
            targets.append(target)
            expected_bytes += int(row["bytes"])
    for target in targets:
        target.unlink()
    for root_name in ("live-runs", "dumps", "portrait-batch", "variant-batch"):
        root = repo / root_name
        if root.is_dir():
            for directory in sorted((x for x in root.rglob("*") if x.is_dir()), key=lambda x: len(x.parts), reverse=True):
                try:
                    directory.rmdir()
                except OSError:
                    pass
    result = {"deletedFiles": len(targets), "deletedBytes": expected_bytes}
    (output / "apply-result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("repo")
    parser.add_argument("output")
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    repo = Path(args.repo).resolve()
    output = Path(args.output).resolve()
    output.relative_to(repo)
    if args.apply:
        result = validate_and_apply(repo, output)
        print(json.dumps(result))
    else:
        summary = build(repo, output)
        print(json.dumps(summary))
    return 0


if __name__ == "__main__":
    sys.exit(main())
