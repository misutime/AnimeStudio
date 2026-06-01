import argparse
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont, ImageOps


def channel_stats(image):
    channels = image.convert("RGBA").split()
    result = {}
    for name, channel in zip("RGBA", channels):
        extrema = channel.getextrema()
        hist = channel.histogram()
        total = sum(hist)
        avg = sum(i * v for i, v in enumerate(hist)) / total if total else 0
        unique = sum(1 for v in hist if v)
        result[name] = {
            "min": extrema[0],
            "max": extrema[1],
            "avg": round(avg, 2),
            "unique": unique,
        }
    return result


def label(draw, xy, text):
    draw.rectangle((xy[0], xy[1], xy[0] + 420, xy[1] + 26), fill=(0, 0, 0, 180))
    draw.text((xy[0] + 6, xy[1] + 5), text, fill=(255, 255, 255, 255))


def make_sheet(paths, out_path, tile=220):
    rows = []
    for path in paths:
        image = Image.open(path).convert("RGBA")
        channels = image.split()
        thumbs = []
        original = ImageOps.contain(image, (tile, tile))
        bg = Image.new("RGBA", (tile, tile), (30, 30, 30, 255))
        bg.alpha_composite(original, ((tile - original.width) // 2, (tile - original.height) // 2))
        thumbs.append(("RGB", bg))
        for name, channel in zip("RGBA", channels):
            gray = ImageOps.contain(channel.convert("RGB"), (tile, tile))
            canvas = Image.new("RGB", (tile, tile), (30, 30, 30))
            canvas.paste(gray, ((tile - gray.width) // 2, (tile - gray.height) // 2))
            thumbs.append((name, canvas.convert("RGBA")))
        rows.append((path, thumbs))

    width = tile * 5
    row_h = tile + 54
    sheet = Image.new("RGBA", (width, row_h * len(rows)), (18, 18, 18, 255))
    draw = ImageDraw.Draw(sheet)
    for row_index, (path, thumbs) in enumerate(rows):
        y = row_index * row_h
        short_name = Path(path).name
        label(draw, (0, y), short_name[:90])
        for i, (name, thumb) in enumerate(thumbs):
            x = i * tile
            sheet.alpha_composite(thumb, (x, y + 34))
            label(draw, (x, y + 34), name)
    sheet.save(out_path)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    parser.add_argument("paths", nargs="+")
    args = parser.parse_args()

    paths = [Path(p) for p in args.paths]
    report = []
    for path in paths:
        image = Image.open(path)
        report.append(
            {
                "path": str(path),
                "size": image.size,
                "mode": image.mode,
                "stats": channel_stats(image),
            }
        )
    make_sheet(paths, args.out)
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
