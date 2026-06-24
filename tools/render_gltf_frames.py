import argparse
import json
import math
import os
import shutil
import tempfile
from contextlib import contextmanager
from pathlib import Path
from urllib.parse import unquote

import bpy
from mathutils import Vector


def parse_args():
    argv = list(__import__("sys").argv)
    if "--" in argv:
        argv = argv[argv.index("--") + 1 :]
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--size", type=int, default=1400)
    parser.add_argument("--views", default="full", help="Comma-separated views: full,upper,left_hand,right_hand")
    parser.add_argument("--mesh_filter", default="auto", help="auto, all, lod0. auto prefers *_lod0 meshes and hides obvious helper meshes.")
    parser.add_argument("--clay_if_untextured", action="store_true", help="Use a neutral gray material when imported meshes have no material.")
    parser.add_argument("--exposure", type=float, default=-0.5, help="Validation render exposure. Lower values keep pale skin/material details visible.")
    parser.add_argument("--light_energy", type=float, default=180.0, help="Area light strength used by validation renders.")
    return parser.parse_args(argv)


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete()
    for collection in (bpy.data.objects, bpy.data.meshes, bpy.data.materials, bpy.data.images):
        for item in list(collection):
            if item.users == 0:
                collection.remove(item)


def gltf_dependency_paths(input_path):
    gltf_path = Path(input_path)
    try:
        gltf = json.loads(gltf_path.read_text(encoding="utf-8"))
    except Exception:
        return [gltf_path]

    paths = [gltf_path]
    for entry in list(gltf.get("buffers") or []) + list(gltf.get("images") or []):
        uri = entry.get("uri") if isinstance(entry, dict) else None
        if not uri or uri.startswith("data:"):
            continue
        paths.append((gltf_path.parent / unquote(uri)).resolve())
    return paths


def windows_long_path(path):
    text = str(Path(path).resolve())
    if os.name != "nt":
        return text
    if text.startswith("\\\\?\\"):
        return text
    if text.startswith("\\\\"):
        return "\\\\?\\UNC\\" + text[2:]
    return "\\\\?\\" + text


def path_exists(path):
    return os.path.exists(windows_long_path(path))


def copy_gltf_dependencies_to_stage(original, staged_dir, dependency_paths):
    staged_dir.mkdir(parents=True, exist_ok=True)
    missing = []
    copied = 0
    for source in dependency_paths:
        source = Path(source).resolve()
        try:
            relative = source.relative_to(original.parent)
        except ValueError:
            relative = Path(source.name)

        destination = staged_dir / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        if not path_exists(source):
            missing.append(str(source))
            continue
        shutil.copy2(windows_long_path(source), windows_long_path(destination))
        copied += 1
    return copied, missing


def short_temp_root_near(path):
    resolved = Path(path).resolve()
    if os.name == "nt" and resolved.drive:
        root = Path(resolved.drive + "\\") / "_as_gltf_tmp"
    else:
        root = Path(tempfile.gettempdir()) / "as_gltf_tmp"
    root.mkdir(parents=True, exist_ok=True)
    return root


@contextmanager
def staged_input_if_needed(input_path, max_path_length=240):
    original = Path(input_path).resolve()
    dependency_paths = gltf_dependency_paths(original)
    longest_path = max((len(str(path)) for path in dependency_paths), default=len(str(original)))
    if longest_path <= max_path_length:
        yield str(original), {
            "stagedForImport": False,
            "maxDependencyPathLength": longest_path,
            "pathLengthThreshold": max_path_length,
        }
        return

    # Windows 下部分 DCC/Blender 图片加载路径仍会卡在 260 字符附近。
    # 这里仅为验证导入临时复制一份短路径，不改变 Library 的真实输出。
    temp_root = Path(tempfile.mkdtemp(prefix="g", dir=str(short_temp_root_near(original))))
    staged_dir = temp_root / "model"
    try:
        copied_count, missing = copy_gltf_dependencies_to_stage(original, staged_dir, dependency_paths)
    except Exception:
        shutil.rmtree(temp_root, ignore_errors=True)
        raise

    staged_input = staged_dir / original.name
    try:
        yield str(staged_input), {
            "stagedForImport": True,
            "reason": "dependencyPathTooLong",
            "originalInput": str(original),
            "importInput": str(staged_input),
            "temporaryRoot": str(temp_root),
            "maxDependencyPathLength": longest_path,
            "pathLengthThreshold": max_path_length,
            "copiedDependencyCount": copied_count,
            "missingDependencyCount": len(missing),
            "missingDependenciesPreview": missing[:16],
        }
    finally:
        shutil.rmtree(temp_root, ignore_errors=True)


def mesh_objects(include_hidden=False):
    return [
        obj
        for obj in bpy.context.scene.objects
        if obj.type == "MESH" and (include_hidden or not obj.hide_render)
    ]


def is_helper_mesh(obj):
    name = obj.name.strip().lower()
    # Blender/导入器可能带入默认测试体。它们不是角色主体，会把验收相机拉到远景。
    helper_names = {"cube", "sphere", "ico sphere", "uv sphere", "棱角球"}
    return name in helper_names


def choose_focus_meshes(mode):
    all_meshes = mesh_objects(include_hidden=True)
    visible_meshes = [obj for obj in all_meshes if not is_helper_mesh(obj)]
    mode = (mode or "auto").lower()
    if mode == "all":
        return visible_meshes or all_meshes

    lod0_meshes = [obj for obj in visible_meshes if "_lod0" in obj.name.lower()]
    if mode == "lod0":
        return lod0_meshes or visible_meshes or all_meshes

    return lod0_meshes or visible_meshes or all_meshes


def apply_focus_visibility(focus_meshes):
    focus_names = {obj.name for obj in focus_meshes}
    for obj in mesh_objects(include_hidden=True):
        hidden = obj.name not in focus_names
        obj.hide_render = hidden
        obj.hide_viewport = hidden


def apply_clay_material_if_needed(objects, enabled):
    if not enabled:
        return False
    has_material = any(obj.material_slots for obj in objects)
    if has_material:
        return False

    mat = bpy.data.materials.new("AnimeStudio_Validation_Clay")
    mat.diffuse_color = (0.56, 0.56, 0.54, 1.0)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    if bsdf:
        bsdf.inputs["Base Color"].default_value = (0.56, 0.56, 0.54, 1.0)
        bsdf.inputs["Roughness"].default_value = 0.72

    for obj in objects:
        obj.data.materials.append(mat)
    return True


def bounds_for_objects(objects):
    points = []
    for obj in objects:
        for corner in obj.bound_box:
            points.append(obj.matrix_world @ Vector(corner))
    if not points:
        return Vector((0, 0, 0)), 1.0
    mn = Vector((min(p.x for p in points), min(p.y for p in points), min(p.z for p in points)))
    mx = Vector((max(p.x for p in points), max(p.y for p in points), max(p.z for p in points)))
    center = (mn + mx) * 0.5
    radius = max((mx - mn).length * 0.5, 0.01)
    return center, radius


def scene_bounds():
    # 验收截图要看清可见模型。Armature/Empty/辅助体 的 bbox 常比 mesh 大很多，
    # 会把相机拉远，导致人物只剩一个小点。
    return bounds_for_objects(mesh_objects())


def serializable_bounds(objects):
    points = []
    for obj in objects:
        for corner in obj.bound_box:
            points.append(obj.matrix_world @ Vector(corner))
    if not points:
        return None
    mn = Vector((min(p.x for p in points), min(p.y for p in points), min(p.z for p in points)))
    mx = Vector((max(p.x for p in points), max(p.y for p in points), max(p.z for p in points)))
    size = mx - mn
    return {
        "min": [round(v, 6) for v in mn],
        "max": [round(v, 6) for v in mx],
        "size": [round(v, 6) for v in size],
        "radius": round(size.length * 0.5, 6),
    }


def ensure_camera():
    existing = bpy.data.objects.get("Camera")
    if existing:
        bpy.context.scene.camera = existing
        return existing

    cam_data = bpy.data.cameras.new("Camera")
    cam = bpy.data.objects.new("Camera", cam_data)
    bpy.context.collection.objects.link(cam)
    bpy.context.scene.camera = cam

    light_data = bpy.data.lights.new("Key", "AREA")
    light = bpy.data.objects.new("Key", light_data)
    bpy.context.collection.objects.link(light)
    light_data.energy = 180
    return cam


def find_object_world_position(*name_parts):
    lowered = [part.lower() for part in name_parts if part]
    for obj in bpy.context.scene.objects:
        name = obj.name.lower()
        if all(part in name for part in lowered):
            return obj.matrix_world.translation.copy()
        if obj.type != "ARMATURE" or not obj.pose:
            continue
        for bone in obj.pose.bones:
            bone_name = bone.name.lower()
            if all(part in bone_name for part in lowered):
                return obj.matrix_world @ bone.matrix.translation
    return None


def view_target(view):
    center, radius = scene_bounds()
    view = (view or "full").lower()
    if view == "upper":
        head = find_object_world_position("head")
        hips = find_object_world_position("bip001") or center
        if head:
            return (head + hips) * 0.5, max(radius * 0.42, 0.35)
    elif view == "left_hand":
        hand = find_object_world_position("_l_hand") or find_object_world_position("left", "hand")
        if hand:
            return hand, max(radius * 0.16, 0.16)
    elif view == "right_hand":
        hand = find_object_world_position("_r_hand") or find_object_world_position("right", "hand")
        if hand:
            return hand, max(radius * 0.16, 0.16)
    return center, radius


def update_camera(cam, view="full"):
    center, radius = view_target(view)
    cam.location = center + Vector((0, -radius * 3.0, radius * 0.15))
    direction = center - cam.location
    cam.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    cam_data = cam.data
    cam_data.type = "ORTHO"
    cam_data.ortho_scale = max(radius * 2.35, 0.1)
    bpy.context.scene.camera = cam

    light = bpy.data.objects.get("Key")
    if light:
        light.location = center + Vector((radius * 0.5, -radius * 1.4, radius * 1.8))
        light.data.size = max(radius, 1.0)


def animation_frames():
    actions = [a for a in bpy.data.actions if a.frame_range[1] > a.frame_range[0]]
    if not actions:
        return [1, 1, 1]
    start = min(a.frame_range[0] for a in actions)
    end = max(a.frame_range[1] for a in actions)
    mid = start + (end - start) * 0.5
    return [math.floor(start), math.floor(mid), math.floor(end)]


def main():
    args = parse_args()
    out_dir = Path(args.output)
    out_dir.mkdir(parents=True, exist_ok=True)

    with staged_input_if_needed(args.input) as (import_input, staging_report):
        clear_scene()
        bpy.ops.import_scene.gltf(filepath=import_input)
        bpy.context.view_layer.update()
        imported_meshes = mesh_objects(include_hidden=True)
        focus_meshes = choose_focus_meshes(args.mesh_filter)
        apply_focus_visibility(focus_meshes)
        clay_applied = apply_clay_material_if_needed(focus_meshes, args.clay_if_untextured)
        cam = ensure_camera()
        light = bpy.data.objects.get("Key")
        if light:
            # 验证截图优先看清局部结构，灯光宁可保守一点，避免浅色皮肤/手部过曝。
            light.data.energy = max(args.light_energy, 0.0)

        scene = bpy.context.scene
        available_engines = {item.identifier for item in scene.render.bl_rna.properties["engine"].enum_items}
        if clay_applied and "BLENDER_WORKBENCH" in available_engines:
            scene.render.engine = "BLENDER_WORKBENCH"
            if hasattr(scene, "display") and hasattr(scene.display, "shading"):
                scene.display.shading.light = "STUDIO"
                scene.display.shading.color_type = "SINGLE"
                scene.display.shading.single_color = (0.45, 0.45, 0.43)
                scene.display.shading.show_cavity = True
        else:
            scene.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in available_engines else "BLENDER_EEVEE"
        scene.render.resolution_x = args.size
        scene.render.resolution_y = args.size
        scene.view_settings.view_transform = "Standard"
        scene.view_settings.look = "None"
        scene.view_settings.exposure = args.exposure
        scene.view_settings.gamma = 1
        if hasattr(scene, "eevee") and hasattr(scene.eevee, "taa_render_samples"):
            scene.eevee.taa_render_samples = 32

        views = [view.strip() for view in args.views.split(",") if view.strip()]
        # 对动画 glTF 来说第 1 张是动画首帧，不等于模型真正 rest pose。
        # 真正 rest pose 应该单独渲染干净模型 glTF。
        labels = ["first", "mid", "end"]
        frame_report = []
        for view in views:
            for label, frame in zip(labels, animation_frames()):
                scene.frame_set(frame)
                bpy.context.view_layer.update()
                update_camera(cam, view)
                scene.render.filepath = str(out_dir / f"{view}_{label}_frame_{frame}.png")
                bpy.ops.render.render(write_still=True)
                frame_report.append({
                    "view": view,
                    "label": label,
                    "frame": frame,
                    "bounds": serializable_bounds(mesh_objects()),
                    "file": scene.render.filepath,
                })

        report = {
            "input": args.input,
            "importInput": import_input,
            "longPathStaging": staging_report,
            "meshFilter": args.mesh_filter,
            "importedMeshCount": len(imported_meshes),
            "focusMeshCount": len(focus_meshes),
            "hiddenMeshCount": len([obj for obj in imported_meshes if obj.hide_render]),
            "clayMaterialApplied": clay_applied,
            "focusMeshesPreview": [obj.name for obj in focus_meshes[:32]],
            "hiddenMeshesPreview": [obj.name for obj in imported_meshes if obj.hide_render][:32],
            "frames": frame_report,
        }
        (out_dir / "render_gltf_frames_report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
