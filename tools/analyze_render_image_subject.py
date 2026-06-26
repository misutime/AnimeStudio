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
    parser.add_argument("--min_motion_pixel_ratio", type=float, default=0.0)
    parser.add_argument("--background_distance_threshold", type=float, default=12.0)
    parser.add_argument("--motion_distance_threshold", type=float, default=24.0)
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


def foreground_mask(image, background, args):
    width, height = image.size
    pixels = image.load()
    mask = bytearray(width * height)
    for y in range(height):
        row_offset = y * width
        for x in range(width):
            pixel = pixels[x, y]
            if pixel[3] <= args.alpha_threshold:
                continue
            if color_distance(pixel, background) <= args.background_distance_threshold:
                continue
            mask[row_offset + x] = 1
    return mask


def analyze_motion_pair(left_path, right_path, args):
    left = Image.open(left_path).convert("RGBA")
    right = Image.open(right_path).convert("RGBA")
    if left.size != right.size:
        return {
            "left": str(left_path),
            "right": str(right_path),
            "status": "sizeMismatch",
            "motionPixelRatio": 0.0,
            "foregroundMotionRatio": 0.0,
        }

    width, height = left.size
    total_pixels = width * height
    left_background = estimate_background_rgba(left)
    right_background = estimate_background_rgba(right)
    left_mask = foreground_mask(left, left_background, args)
    right_mask = foreground_mask(right, right_background, args)
    left_pixels = left.load()
    right_pixels = right.load()

    motion_pixels = 0
    foreground_union_pixels = 0
    for y in range(height):
        row_offset = y * width
        for x in range(width):
            index = row_offset + x
            in_foreground = left_mask[index] or right_mask[index]
            if not in_foreground:
                continue
            foreground_union_pixels += 1
            if color_distance(left_pixels[x, y], right_pixels[x, y]) > args.motion_distance_threshold:
                motion_pixels += 1

    motion_pixel_ratio = motion_pixels / total_pixels if total_pixels else 0.0
    foreground_motion_ratio = motion_pixels / foreground_union_pixels if foreground_union_pixels else 0.0
    status = "ok" if motion_pixel_ratio >= args.min_motion_pixel_ratio else "tooLittleMotion"
    return {
        "left": str(left_path),
        "right": str(right_path),
        "status": status,
        "motionPixelCount": motion_pixels,
        "foregroundUnionPixelCount": foreground_union_pixels,
        "motionPixelRatio": round(motion_pixel_ratio, 6),
        "foregroundMotionRatio": round(foreground_motion_ratio, 6),
    }


def analyze_motion(images, args):
    if len(images) < 2:
        return {
            "status": "notEnoughFrames",
            "pairCount": 0,
            "maxMotionPixelRatio": 0.0,
            "maxForegroundMotionRatio": 0.0,
            "pairs": [],
        }

    pairs = []
    for i in range(len(images) - 1):
        pairs.append(analyze_motion_pair(images[i], images[i + 1], args))
    pairs.append(analyze_motion_pair(images[0], images[-1], args))
    valid_pairs = [pair for pair in pairs if pair["status"] != "sizeMismatch"]
    max_motion = max((pair["motionPixelRatio"] for pair in valid_pairs), default=0.0)
    max_foreground_motion = max((pair["foregroundMotionRatio"] for pair in valid_pairs), default=0.0)
    failed = [pair for pair in valid_pairs if pair["status"] != "ok"]
    status = "ok" if valid_pairs and not failed else ("sizeMismatch" if not valid_pairs else "failed")
    return {
        "status": status,
        "pairCount": len(pairs),
        "failedPairCount": len(failed),
        "maxMotionPixelRatio": round(max_motion, 6),
        "maxForegroundMotionRatio": round(max_foreground_motion, 6),
        "pairs": pairs,
        "rule": "PNG 帧间像素差只证明渲染结果有可见变化，不能证明动作自然、骨骼正确或可生产复用。",
    }


def main():
    args = parse_args()
    images = [Path(path) for path in args.images]
    rows = [analyze_image(path, args) for path in images]
    motion = analyze_motion(images, args)
    failed = [row for row in rows if row["status"] != "ok"]
    motion_failed = args.min_motion_pixel_ratio > 0 and motion["status"] != "ok"
    report = {
        "status": "ok" if not failed and not motion_failed else "failed",
        "imageCount": len(rows),
        "failedCount": len(failed),
        "minForegroundPixelRatio": round(min((row["foregroundPixelRatio"] for row in rows), default=0.0), 6),
        "minForegroundHeightRatio": round(min((row["foregroundHeightRatio"] for row in rows), default=0.0), 6),
        "motion": motion,
        "thresholds": {
            "minForegroundPixelRatio": args.min_foreground_pixel_ratio,
            "minForegroundHeightRatio": args.min_foreground_height_ratio,
            "minMotionPixelRatio": args.min_motion_pixel_ratio,
            "backgroundDistanceThreshold": args.background_distance_threshold,
            "motionDistanceThreshold": args.motion_distance_threshold,
            "alphaThreshold": args.alpha_threshold,
        },
        "images": rows,
        "rule": "PNG 主体占比和帧间像素差诊断只证明截图里有可见前景主体且多帧存在可见变化，不能替代人工视觉验收或动作正确性判断。",
    }
    Path(args.output).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    if failed or motion_failed:
        raise SystemExit(2)


if __name__ == "__main__":
    main()
