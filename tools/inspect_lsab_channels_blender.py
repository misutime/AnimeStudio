import json
import sys
from pathlib import Path

import bpy


def load_image(path):
    image = bpy.data.images.load(str(path), check_existing=False)
    image.colorspace_settings.name = "sRGB"
    pixels = list(image.pixels)
    return image, pixels


def stats_for_pixels(pixels):
    result = {}
    names = "RGBA"
    count = len(pixels) // 4
    for ci, name in enumerate(names):
        values = [pixels[i * 4 + ci] for i in range(count)]
        ints = [round(v * 255) for v in values]
        result[name] = {
            "min": min(ints),
            "max": max(ints),
            "avg": round(sum(ints) / count, 2),
            "unique": len(set(ints)),
        }
    return result


def resize_nearest(src_pixels, src_w, src_h, dst_w, dst_h, mode):
    dst = [0.0] * (dst_w * dst_h * 4)
    for y in range(dst_h):
        sy = min(src_h - 1, int(y * src_h / dst_h))
        for x in range(dst_w):
            sx = min(src_w - 1, int(x * src_w / dst_w))
            si = (sy * src_w + sx) * 4
            di = (y * dst_w + x) * 4
            if mode == "RGB":
                dst[di : di + 4] = src_pixels[si : si + 4]
                dst[di + 3] = 1.0
            else:
                ci = "RGBA".index(mode)
                v = src_pixels[si + ci]
                dst[di : di + 4] = [v, v, v, 1.0]
    return dst


def paste(dst, dst_w, tile_pixels, tile_w, tile_h, x0, y0):
    for y in range(tile_h):
        for x in range(tile_w):
            si = (y * tile_w + x) * 4
            di = ((y0 + y) * dst_w + (x0 + x)) * 4
            dst[di : di + 4] = tile_pixels[si : si + 4]


def main():
    argv = sys.argv
    if "--" in argv:
        argv = argv[argv.index("--") + 1 :]
    out = Path(argv[0])
    paths = [Path(p) for p in argv[1:]]
    tile = 180
    gap = 10
    row_h = tile + gap
    sheet_w = tile * 5
    sheet_h = row_h * len(paths)
    sheet_pixels = [0.07, 0.07, 0.07, 1.0] * (sheet_w * sheet_h)
    report = []
    for row, path in enumerate(paths):
        image, pixels = load_image(path)
        w, h = image.size
        report.append(
            {
                "path": str(path),
                "size": [w, h],
                "stats": stats_for_pixels(pixels),
            }
        )
        for col, mode in enumerate(["RGB", "R", "G", "B", "A"]):
            tile_pixels = resize_nearest(pixels, w, h, tile, tile, mode)
            paste(sheet_pixels, sheet_w, tile_pixels, tile, tile, col * tile, row * row_h)
    sheet = bpy.data.images.new("LSAB_channels", sheet_w, sheet_h, alpha=True, float_buffer=False)
    sheet.pixels.foreach_set(sheet_pixels)
    sheet.filepath_raw = str(out)
    sheet.file_format = "PNG"
    sheet.save()
    print("LSAB_REPORT_BEGIN")
    print(json.dumps(report, ensure_ascii=False, indent=2))
    print("LSAB_REPORT_END")


if __name__ == "__main__":
    main()
