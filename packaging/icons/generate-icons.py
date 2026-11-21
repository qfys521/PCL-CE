#!/usr/bin/env python3
"""Regenerate the hicolor icon set committed under packaging/icons/.

The icons are rendered from the launcher logo and committed so that the
AppImage build is reproducible and does not need Pillow at build time.
Requires Pillow: python3 -m pip install Pillow

Usage:
    python3 packaging/icons/generate-icons.py
"""

from pathlib import Path

from PIL import Image

REPO = Path(__file__).resolve().parents[2]
SOURCE = REPO / "Plain Craft Launcher 2" / "Images" / "Heads" / "Logo-CE.png"
OUTPUT = REPO / "packaging" / "icons" / "hicolor"
SIZES = (16, 24, 32, 48, 64, 128, 256, 512)


def main() -> None:
    image = Image.open(SOURCE).convert("RGBA")
    if image.size != (512, 512):
        raise SystemExit(f"unexpected source size {image.size}, expected 512x512")
    for size in SIZES:
        directory = OUTPUT / f"{size}x{size}" / "apps"
        directory.mkdir(parents=True, exist_ok=True)
        image.resize((size, size), Image.LANCZOS).save(directory / "pcl-ce.png", optimize=True)
        print(f"packaging/icons/hicolor/{size}x{size}/apps/pcl-ce.png")


if __name__ == "__main__":
    main()
