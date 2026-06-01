import sys
from pathlib import Path

import bpy


def make_channel_image(src, pixels, w, h, out, mode):
    image = bpy.data.images.new(mode, w, h, alpha=True, float_buffer=False)
    result = [0.0] * (w * h * 4)
    idx = "RGBA".index(mode)
    for i in range(w * h):
        v = pixels[i * 4 + idx]
        result[i * 4 : i * 4 + 4] = [v, v, v, 1.0]
    image.pixels.foreach_set(result)
    image.filepath_raw = str(out)
    image.file_format = "PNG"
    image.save()


def make_false_color(pixels, w, h, out):
    image = bpy.data.images.new("LSAB_false_color", w, h, alpha=True, float_buffer=False)
    result = [0.0] * (w * h * 4)
    for i in range(w * h):
        r, g, b, a = pixels[i * 4 : i * 4 + 4]
        # Keep blue as alpha-like mask, visualize R/G controls over it.
        result[i * 4 : i * 4 + 4] = [r, g, b, 1.0]
    image.pixels.foreach_set(result)
    image.filepath_raw = str(out)
    image.file_format = "PNG"
    image.save()


def main():
    argv = sys.argv
    if "--" in argv:
        argv = argv[argv.index("--") + 1 :]
    src = Path(argv[0])
    out_dir = Path(argv[1])
    out_dir.mkdir(parents=True, exist_ok=True)
    img = bpy.data.images.load(str(src), check_existing=False)
    w, h = img.size
    pixels = list(img.pixels)
    for mode in "RGBA":
        make_channel_image(src, pixels, w, h, out_dir / f"{src.stem}_{mode}.png", mode)
    make_false_color(pixels, w, h, out_dir / f"{src.stem}_RGB.png")


if __name__ == "__main__":
    main()
