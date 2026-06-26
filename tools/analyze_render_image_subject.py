import argparse
import json
import math
import statistics
from pathlib import Path

from PIL import Image


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--images", nargs="+", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--min_foreground_pixel_ratio", type=float, default=0.015)
    parser.add_argument("--min_foreground_height_ratio", type=float, default=0.12)
    parser.add_argument("--background_distance_threshold", type=float, default=12.0)
    parser.add_argument("--alpha_threshold", type=int, default=16)
    return parser.parse_args()


def estimate_background_rgba(image):
    width, height = image.size
    pixels = image.load()
    step = max(1, min(width, height) // 200)
    samples = []
    for x in range(0, width, step):
        samples.append(pixels[x, 0])
        samples.append(pixels[x, height - 1])
    for y in range(0, height, step):
        samples.append(pixels[0, y])
        samples.append(pixels[width - 1, y])
    return tuple(int(statistics.median([sample[i] for sample in samples])) for i in range(4))


def color_distance(left, right):
    return math.sqrt(
        (left[0] - right[0]) ** 2
        + (left[1] - right[1]) ** 2
        + (left[2] - right[2]) ** 2
    )


def analyze_image(path, args):
    image = Image.open(path).convert("RGBA")
    width, height = image.size
    pixels = image.load()
    background = estimate_background_rgba(image)

    min_x = width
    min_y = height
    max_x = -1
    max_y = -1
    foreground_pixels = 0

    for y in range(height):
        for x in range(width):
            pixel = pixels[x, y]
            if pixel[3] <= args.alpha_threshold:
                continue
            if color_distance(pixel, background) <= args.background_distance_threshold:
                continue
            foreground_pixels += 1
            if x < min_x:
                min_x = x
            if y < min_y:
                min_y = y
            if x > max_x:
                max_x = x
            if y > max_y:
                max_y = y

    total_pixels = width * height
    if foreground_pixels <= 0:
        return {
            "file": str(path),
            "status": "empty",
            "width": width,
            "height": height,
            "backgroundRgba": list(background),
            "foregroundPixelRatio": 0.0,
            "foregroundHeightRatio": 0.0,
            "foregroundWidthRatio": 0.0,
            "foregroundBboxRatio": 0.0,
            "foregroundPixelCount": 0,
        }

    bbox_width = max_x - min_x + 1
    bbox_height = max_y - min_y + 1
    foreground_pixel_ratio = foreground_pixels / total_pixels
    foreground_height_ratio = bbox_height / height
    foreground_width_ratio = bbox_width / width
    foreground_bbox_ratio = (bbox_width * bbox_height) / total_pixels
    status = "ok"
    if foreground_pixel_ratio < args.min_foreground_pixel_ratio:
        status = "tooFewForegroundPixels"
    elif foreground_height_ratio < args.min_foreground_height_ratio:
        status = "foregroundTooSmall"

    return {
        "file": str(path),
        "status": status,
        "width": width,
        "height": height,
        "backgroundRgba": list(background),
        "foregroundBbox": [min_x, min_y, max_x, max_y],
        "foregroundPixelCount": foreground_pixels,
        "foregroundPixelRatio": round(foreground_pixel_ratio, 6),
        "foregroundHeightRatio": round(foreground_height_ratio, 6),
        "foregroundWidthRatio": round(foreground_width_ratio, 6),
        "foregroundBboxRatio": round(foreground_bbox_ratio, 6),
    }


def main():
    args = parse_args()
    images = [Path(path) for path in args.images]
    rows = [analyze_image(path, args) for path in images]
    failed = [row for row in rows if row["status"] != "ok"]
    report = {
        "status": "ok" if not failed else "failed",
        "imageCount": len(rows),
        "failedCount": len(failed),
        "minForegroundPixelRatio": round(min((row["foregroundPixelRatio"] for row in rows), default=0.0), 6),
        "minForegroundHeightRatio": round(min((row["foregroundHeightRatio"] for row in rows), default=0.0), 6),
        "thresholds": {
            "minForegroundPixelRatio": args.min_foreground_pixel_ratio,
            "minForegroundHeightRatio": args.min_foreground_height_ratio,
            "backgroundDistanceThreshold": args.background_distance_threshold,
            "alphaThreshold": args.alpha_threshold,
        },
        "images": rows,
        "rule": "PNG 主体占比诊断只证明截图里有可见前景主体，不能替代人工视觉验收或动作正确性判断。",
    }
    Path(args.output).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    if failed:
        raise SystemExit(2)


if __name__ == "__main__":
    main()
