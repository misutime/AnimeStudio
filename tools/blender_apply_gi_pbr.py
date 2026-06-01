import argparse
import json
import sys
from pathlib import Path

import bpy


def find_input(principled, names):
    for name in names:
        socket = principled.inputs.get(name)
        if socket is not None:
            return socket
    return None


def clear_material(material):
    material.use_nodes = True
    nodes = material.node_tree.nodes
    for node in list(nodes):
        nodes.remove(node)


def image_node(nodes, image_path, colorspace):
    node = nodes.new("ShaderNodeTexImage")
    node.image = bpy.data.images.load(str(image_path), check_existing=True)
    node.image.colorspace_settings.name = colorspace
    node.location = (-650, 100)
    return node


def make_principled_material(material, base_color_path, normal_path, roughness):
    clear_material(material)
    nodes = material.node_tree.nodes
    links = material.node_tree.links

    output = nodes.new("ShaderNodeOutputMaterial")
    output.location = (350, 0)

    principled = nodes.new("ShaderNodeBsdfPrincipled")
    principled.location = (50, 0)
    links.new(principled.outputs["BSDF"], output.inputs["Surface"])

    base_input = find_input(principled, ["Base Color"])
    roughness_input = find_input(principled, ["Roughness"])
    metallic_input = find_input(principled, ["Metallic"])
    normal_input = find_input(principled, ["Normal"])

    if metallic_input is not None:
        metallic_input.default_value = 0.0
    if roughness_input is not None:
        roughness_input.default_value = roughness

    if base_color_path and base_color_path.exists() and base_input is not None:
        color = image_node(nodes, base_color_path, "sRGB")
        color.location = (-650, 120)
        links.new(color.outputs["Color"], base_input)

    if normal_path and normal_path.exists() and normal_input is not None:
        normal_tex = image_node(nodes, normal_path, "Non-Color")
        normal_tex.location = (-650, -180)
        normal_map = nodes.new("ShaderNodeNormalMap")
        normal_map.location = (-300, -180)
        normal_map.inputs["Strength"].default_value = 1.0
        links.new(normal_tex.outputs["Color"], normal_map.inputs["Color"])
        links.new(normal_map.outputs["Normal"], normal_input)


def load_gi_material_json(path):
    with path.open("r", encoding="utf-8") as f:
        return json.load(f)


def texture_name(data, slot):
    tex_envs = data.get("m_SavedProperties", {}).get("m_TexEnvs", {})
    entry = tex_envs.get(slot)
    if not entry:
        return None
    name = entry.get("m_Texture", {}).get("Name")
    return name or None


def float_value(data, key, default):
    floats = data.get("m_SavedProperties", {}).get("m_Floats", {})
    value = floats.get(key)
    return default if value is None else float(value)


def resolve_texture(folder, stem):
    if not stem:
        return None
    for ext in (".png", ".tga", ".jpg", ".jpeg", ".dds"):
        path = folder / f"{stem}{ext}"
        if path.exists():
            return path
    return None


def import_fbx_if_needed(fbx_path):
    if not bpy.data.objects:
        bpy.ops.import_scene.fbx(filepath=str(fbx_path))


def apply_materials(folder):
    materials_dir = folder / "Materials"
    main_json = None
    for path in materials_dir.glob("*.json"):
        if path.stem.lower() != "stages_shadowmesh":
            main_json = path
            break
    if main_json is None:
        raise FileNotFoundError(f"No usable material json found under {materials_dir}")

    data = load_gi_material_json(main_json)
    material_name = data.get("m_Name") or main_json.stem
    base_color = resolve_texture(folder, texture_name(data, "_StoneDiffuse") or texture_name(data, "_MainTex"))
    normal = resolve_texture(folder, texture_name(data, "_StoneNormal") or texture_name(data, "_BumpMap"))
    smoothness = float_value(data, "_StoneSmoothnessBase", 0.35)
    roughness = max(0.05, min(1.0, 1.0 - smoothness))

    targets = [
        mat for mat in bpy.data.materials
        if mat.name == material_name or material_name in mat.name
    ]
    if not targets:
        targets = [bpy.data.materials.new(material_name)]

    for mat in targets:
        make_principled_material(mat, base_color, normal, roughness)

    shadow = bpy.data.materials.get("Stages_ShadowMesh")
    if shadow is not None:
        shadow.diffuse_color = (0, 0, 0, 0)
        shadow.use_nodes = True
        shadow.blend_method = "BLEND"

    print(f"Applied GI approximate PBR material: {material_name}")
    print(f"Base Color: {base_color}")
    print(f"Normal: {normal}")
    print(f"Roughness: {roughness:.3f}")


def export_scene(folder, export_fbx, export_glb):
    if export_fbx:
        output = folder / f"{folder.name}_pbr.fbx"
        bpy.ops.export_scene.fbx(
            filepath=str(output),
            path_mode="COPY",
            embed_textures=False,
            use_selection=False,
            add_leaf_bones=False,
        )
        print(f"Exported FBX: {output}")

    if export_glb:
        output = folder / f"{folder.name}_pbr.glb"
        bpy.ops.export_scene.gltf(
            filepath=str(output),
            export_format="GLB",
            export_image_format="AUTO",
            export_materials="EXPORT",
        )
        print(f"Exported GLB: {output}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("folder", help="Folder containing FBX, PNG textures, and Materials/*.json")
    parser.add_argument("--import-fbx", action="store_true", help="Import the first FBX in the folder before applying materials")
    parser.add_argument("--export-fbx", action="store_true", help="Export a repaired FBX next to the source model")
    parser.add_argument("--export-glb", action="store_true", help="Export a repaired GLB next to the source model")
    argv = sys.argv
    if "--" in argv:
        argv = argv[argv.index("--") + 1:]
    else:
        argv = []
    args = parser.parse_args(argv)

    folder = Path(args.folder)
    if args.import_fbx:
        fbx_files = sorted(folder.glob("*.fbx"))
        if not fbx_files:
            raise FileNotFoundError(f"No FBX found in {folder}")
        import_fbx_if_needed(fbx_files[0])

    apply_materials(folder)
    export_scene(folder, args.export_fbx, args.export_glb)


if __name__ == "__main__":
    main()
