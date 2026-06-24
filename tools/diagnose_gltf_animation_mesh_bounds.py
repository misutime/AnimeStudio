import argparse
import json
from pathlib import Path

import bpy
from mathutils import Vector


def parse_args():
    argv = list(__import__("sys").argv)
    if "--" in argv:
        argv = argv[argv.index("--") + 1 :]
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--frames", default="", help="Comma-separated frame numbers. Empty means first/mid/end action frames.")
    parser.add_argument("--mesh_filter", default="lod0", help="lod0, all, or visible")
    return parser.parse_args(argv)


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete()


def mesh_objects(mode):
    meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    mode = (mode or "lod0").lower()
    if mode == "all":
        return meshes
    if mode == "visible":
        return [obj for obj in meshes if not obj.hide_render]
    lod0 = [obj for obj in meshes if "_lod0" in obj.name.lower()]
    return lod0 or meshes


def animation_frames(explicit):
    if explicit:
        return [int(x.strip()) for x in explicit.split(",") if x.strip()]
    actions = [a for a in bpy.data.actions if a.frame_range[1] > a.frame_range[0]]
    if not actions:
        return [0]
    start = min(a.frame_range[0] for a in actions)
    end = max(a.frame_range[1] for a in actions)
    mid = start + (end - start) * 0.5
    return [int(start), int(mid), int(end)]


def bounds_for_object(obj):
    points = [obj.matrix_world @ Vector(corner) for corner in obj.bound_box]
    mn = Vector((min(p.x for p in points), min(p.y for p in points), min(p.z for p in points)))
    mx = Vector((max(p.x for p in points), max(p.y for p in points), max(p.z for p in points)))
    size = mx - mn
    center = (mn + mx) * 0.5
    return {
        "min": [round(v, 6) for v in mn],
        "max": [round(v, 6) for v in mx],
        "size": [round(v, 6) for v in size],
        "center": [round(v, 6) for v in center],
        "radius": round(size.length * 0.5, 6),
    }


def delta(a, b):
    return round(sum((a[i] - b[i]) ** 2 for i in range(3)) ** 0.5, 6)


def main():
    args = parse_args()
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)

    clear_scene()
    bpy.ops.import_scene.gltf(filepath=args.input)
    bpy.context.view_layer.update()

    meshes = mesh_objects(args.mesh_filter)
    frames = animation_frames(args.frames)
    per_frame = []
    for frame in frames:
        bpy.context.scene.frame_set(frame)
        bpy.context.view_layer.update()
        per_frame.append({
            "frame": frame,
            "meshes": {obj.name: bounds_for_object(obj) for obj in meshes},
        })

    first = per_frame[0]["meshes"] if per_frame else {}
    rows = []
    for name, base in first.items():
        max_center_delta = 0.0
        max_radius_delta = 0.0
        max_radius_ratio = 1.0
        max_axis = 0.0
        for frame_item in per_frame[1:]:
            current = frame_item["meshes"].get(name)
            if not current:
                continue
            max_center_delta = max(max_center_delta, delta(base["center"], current["center"]))
            max_radius_delta = max(max_radius_delta, abs(current["radius"] - base["radius"]))
            if base["radius"] > 1e-6:
                max_radius_ratio = max(max_radius_ratio, current["radius"] / base["radius"])
            max_axis = max(max_axis, max(current["size"]))
        rows.append({
            "mesh": name,
            "baseRadius": base["radius"],
            "maxCenterDelta": round(max_center_delta, 6),
            "maxRadiusDelta": round(max_radius_delta, 6),
            "maxRadiusRatio": round(max_radius_ratio, 6),
            "maxAxisSize": round(max_axis, 6),
        })

    rows.sort(key=lambda x: (x["maxRadiusRatio"], x["maxRadiusDelta"], x["maxCenterDelta"]), reverse=True)
    report = {
        "input": args.input,
        "meshFilter": args.mesh_filter,
        "frames": frames,
        "meshCount": len(meshes),
        "topChangingMeshes": rows[:32],
        "perFrame": per_frame,
        "rule": "逐帧 mesh bbox 只能定位疑似拉丝/飞出部件；最终仍要结合截图、骨骼和 Unity 关系判定。",
    }
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
