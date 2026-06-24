import argparse
import json
import math
import struct
from pathlib import Path
from urllib.parse import unquote


COMPONENT_COUNTS = {
    "SCALAR": 1,
    "VEC2": 2,
    "VEC3": 3,
    "VEC4": 4,
}

COMPONENT_FORMATS = {
    5126: ("f", 4),
    5123: ("H", 2),
    5125: ("I", 4),
}


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", required=True)
    parser.add_argument("--candidate", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--samples", type=int, default=21)
    parser.add_argument("--top", type=int, default=80)
    return parser.parse_args()


def load_gltf(path):
    path = Path(path)
    gltf = json.loads(path.read_text(encoding="utf-8-sig"))
    buffers = []
    for buffer in gltf.get("buffers") or []:
        uri = buffer.get("uri")
        if not uri or uri.startswith("data:"):
            raise ValueError("Only external .bin buffers are supported")
        buffers.append((path.parent / unquote(uri)).read_bytes())
    return path, gltf, buffers


def accessor_values(gltf, buffers, accessor_index):
    accessor = gltf["accessors"][accessor_index]
    view = gltf["bufferViews"][accessor["bufferView"]]
    fmt, size = COMPONENT_FORMATS[accessor["componentType"]]
    count = COMPONENT_COUNTS[accessor["type"]]
    stride = view.get("byteStride") or size * count
    offset = view.get("byteOffset", 0) + accessor.get("byteOffset", 0)
    blob = buffers[view.get("buffer", 0)]
    rows = []
    for i in range(accessor["count"]):
        start = offset + i * stride
        values = struct.unpack_from("<" + fmt * count, blob, start)
        rows.append([float(x) for x in values])
    return rows


def node_paths(gltf):
    nodes = gltf.get("nodes") or []
    scene_index = gltf.get("scene", 0)
    scene_nodes = ((gltf.get("scenes") or [{}])[scene_index].get("nodes") or list(range(len(nodes))))
    result = {}

    def walk(index, parent):
        node = nodes[index]
        name = node.get("name") or f"node_{index}"
        path = name if not parent else parent + "/" + name
        result[index] = path
        for child in node.get("children") or []:
            walk(child, path)

    for root in scene_nodes:
        walk(root, "")
    return result


def animation_tracks(gltf, buffers):
    paths = node_paths(gltf)
    tracks = {}
    animations = gltf.get("animations") or []
    if not animations:
        return tracks
    animation = animations[0]
    samplers = animation.get("samplers") or []
    for channel in animation.get("channels") or []:
        target = channel.get("target") or {}
        node = target.get("node")
        path = target.get("path")
        if node is None or path not in {"translation", "rotation", "scale"}:
            continue
        sampler = samplers[channel["sampler"]]
        times = [row[0] for row in accessor_values(gltf, buffers, sampler["input"])]
        values = accessor_values(gltf, buffers, sampler["output"])
        tracks[(paths.get(node, str(node)), path)] = (times, values)
    return tracks


def normalize(q):
    length = math.sqrt(sum(x * x for x in q))
    if length <= 1e-8:
        return [0.0, 0.0, 0.0, 1.0]
    return [x / length for x in q]


def slerp(a, b, t):
    a = normalize(a)
    b = normalize(b)
    dot = sum(a[i] * b[i] for i in range(4))
    if dot < 0:
        b = [-x for x in b]
        dot = -dot
    dot = max(-1.0, min(1.0, dot))
    if dot > 0.9995:
        return normalize([a[i] + (b[i] - a[i]) * t for i in range(4)])
    theta0 = math.acos(dot)
    theta = theta0 * t
    sin_theta = math.sin(theta)
    sin_theta0 = math.sin(theta0)
    s0 = math.cos(theta) - dot * sin_theta / sin_theta0
    s1 = sin_theta / sin_theta0
    return normalize([a[i] * s0 + b[i] * s1 for i in range(4)])


def sample(track, time, kind):
    times, values = track
    if not times:
        return None
    if time <= times[0]:
        return values[0]
    if time >= times[-1]:
        return values[-1]
    for i in range(1, len(times)):
        if time > times[i]:
            continue
        span = times[i] - times[i - 1]
        t = 0.0 if span <= 0 else (time - times[i - 1]) / span
        if kind == "rotation":
            return slerp(values[i - 1], values[i], t)
        return [values[i - 1][c] + (values[i][c] - values[i - 1][c]) * t for c in range(len(values[i]))]
    return values[-1]


def rotation_error_degrees(a, b):
    a = normalize(a)
    b = normalize(b)
    dot = abs(sum(a[i] * b[i] for i in range(4)))
    dot = max(-1.0, min(1.0, dot))
    return 2.0 * math.acos(dot) * 180.0 / math.pi


def vector_error(a, b):
    return math.sqrt(sum((a[i] - b[i]) ** 2 for i in range(min(len(a), len(b)))))


def build_sample_times(ref_tracks, cand_tracks, sample_count):
    max_time = 0.0
    for tracks in (ref_tracks, cand_tracks):
        for times, _ in tracks.values():
            if times:
                max_time = max(max_time, times[-1])
    if max_time <= 0:
        return [0.0]
    count = max(2, sample_count)
    return [max_time * i / (count - 1) for i in range(count)]


def main():
    args = parse_args()
    _, ref_gltf, ref_buffers = load_gltf(args.reference)
    _, cand_gltf, cand_buffers = load_gltf(args.candidate)
    ref_tracks = animation_tracks(ref_gltf, ref_buffers)
    cand_tracks = animation_tracks(cand_gltf, cand_buffers)
    common = sorted(set(ref_tracks) & set(cand_tracks))
    sample_times = build_sample_times(ref_tracks, cand_tracks, args.samples)
    rows = []
    for key in common:
        path, target = key
        errors = []
        for time in sample_times:
            rv = sample(ref_tracks[key], time, target)
            cv = sample(cand_tracks[key], time, target)
            if rv is None or cv is None:
                continue
            errors.append(rotation_error_degrees(rv, cv) if target == "rotation" else vector_error(rv, cv))
        if not errors:
            continue
        rows.append({
            "path": path,
            "target": target,
            "maxError": max(errors),
            "avgError": sum(errors) / len(errors),
        })
    rows.sort(key=lambda x: (x["maxError"], x["avgError"]), reverse=True)
    report = {
        "reference": args.reference,
        "candidate": args.candidate,
        "sampleTimes": sample_times,
        "referenceTrackCount": len(ref_tracks),
        "candidateTrackCount": len(cand_tracks),
        "commonTrackCount": len(common),
        "missingInCandidateCount": len(set(ref_tracks) - set(cand_tracks)),
        "extraInCandidateCount": len(set(cand_tracks) - set(ref_tracks)),
        "topErrors": rows[: args.top],
        "summary": {
            "maxRotationDegrees": max((x["maxError"] for x in rows if x["target"] == "rotation"), default=0.0),
            "avgRotationDegrees": sum(x["avgError"] for x in rows if x["target"] == "rotation") / max(1, sum(1 for x in rows if x["target"] == "rotation")),
            "maxTranslation": max((x["maxError"] for x in rows if x["target"] == "translation"), default=0.0),
            "maxScale": max((x["maxError"] for x in rows if x["target"] == "scale"), default=0.0),
        },
    }
    Path(args.output).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
