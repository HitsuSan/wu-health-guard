"""Combines the per-size PNGs from render.js into one .ico. Usage: python3 make_ico.py <pngDir> <out.ico>"""
import sys
from pathlib import Path

from PIL import Image

png_dir, out = Path(sys.argv[1]), sys.argv[2]
images = sorted((Image.open(p).convert("RGBA") for p in png_dir.glob("*.png")), key=lambda im: im.width)
largest = images[-1]
# Each size is rendered separately (crisper than downscaling one image); Pillow writes them all.
largest.save(out, format="ICO", sizes=[(im.width, im.height) for im in images], append_images=images[:-1])
print(f"wrote {out}: {', '.join(str(im.width) for im in images)}")
