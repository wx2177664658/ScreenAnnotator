"""Build multi-size Windows .ico from PNG (PNG-compressed entries, Vista+)."""
from __future__ import annotations

import io
import struct
from pathlib import Path

from PIL import Image


def png_bytes(img: Image.Image) -> bytes:
    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def write_ico(path: Path, images: list[Image.Image]) -> None:
    # ICONDIR
    header = struct.pack("<HHH", 0, 1, len(images))
    entries = []
    payloads = []
    offset = 6 + 16 * len(images)
    for im in images:
        data = png_bytes(im)
        w = 0 if im.width >= 256 else im.width
        h = 0 if im.height >= 256 else im.height
        # width, height, colors, reserved, planes, bitcount, bytesInRes, imageOffset
        entry = struct.pack("<BBBBHHII", w, h, 0, 0, 1, 32, len(data), offset)
        entries.append(entry)
        payloads.append(data)
        offset += len(data)
    path.write_bytes(header + b"".join(entries) + b"".join(payloads))


def main() -> None:
    src = Path(r"e:\cursor\cursor-workerspace\test4\docs\assets\app-icon-v2-minimal.png")
    out_dir = Path(r"e:\cursor\cursor-workerspace\test4\src\ScreenAnnotator\Assets")
    out_dir.mkdir(parents=True, exist_ok=True)
    out = out_dir / "app.ico"

    base = Image.open(src).convert("RGBA")
    sizes = [16, 32, 48, 64, 128, 256]
    images = [base.resize((s, s), Image.Resampling.LANCZOS) for s in sizes]
    write_ico(out, images)

    # Also keep source-sized PNG for tray / docs
    base.save(out_dir / "app-icon.png")

    data = out.read_bytes()
    count = struct.unpack_from("<H", data, 4)[0]
    print(f"wrote {out} ({out.stat().st_size} bytes, {count} images)")
    off = 6
    for i in range(count):
        w, h, _, _, _, _, nbytes, _ = struct.unpack_from("<BBBBHHII", data, off)
        print(f"  [{i}] {256 if w == 0 else w}x{256 if h == 0 else h}  {nbytes} bytes")
        off += 16


if __name__ == "__main__":
    main()
