"""Prepares the player's own locomotion animations for the remote V puppet (CYB-00010).

The game's base/animations/player/<male|female>/body/player_locomotion.anims holds V's full-body walk, jog, sprint,
crouch, jump and slide in up to eight directions, under first-person names (fpp_walk_slow_0, fpp_jog_090, ...). It is
stored for the player's skeleton (player_man_skeleton.rig, 152 bones). The puppet uses the NPC skeleton (man_base.rig,
71 bones, all of them in the player's too, with the same parents), whose bone order differs from bone 25 on, and the
engine plays an animation set by bone index: played as it is, hands, feet and eyes deform. So the animations are
moved to the NPC skeleton by bone name, through glTF, and renamed to what the NPC animation graph asks for.

The same goes for the float tracks, which an animation also sets by index: the player's rig has 23 (cameraUpOffset,
chestUpOffset, ... first), the NPC's 13, starting with headBodyPartVis, leftArmBodyPartVis, rightArmBodyPartVis and
legsBodyPartVis. Left as they are, the player's camera and chest offsets (0) land on the NPC's body part visibility
and the character disappears while it walks. Track keys are moved by track name; tracks the NPC rig lacks are dropped.

Bone lengths: the first-person animations also move bones along their length, to keep V's arms in the camera's view
(walking backward stretches the shoulder bones from 17 to 22 cm, which widens the shoulders by 15 cm). From outside
that deforms the body, so every bone but Root, Trajectory and Hips keeps the NPC rig's own offset from its parent;
only its rotation comes from the animation. The offset is written as a constant key on every frame: an animation
needs a position for every bone, and a bone without one collapses onto its parent in the game.

Facing: in first person only the camera looks ahead. In V's sideways walk the whole body, head included, is turned
57 degrees into the movement (67 to the left; the jog turns the hips 68 to 76 and the head 31), and the crouch walks
are turned 17 to 30 degrees in every direction. From outside that reads as turning and walking forward, not as
stepping sideways. So in those animations (TURNED below) the upper body is twisted back until the head faces where
the character faces, frame by frame; hips and legs stay as they are, and so do the feet on the ground. In the walks
and crouch walks the twist is spread over the spine (TWIST), which leaves the chest about a third of the turn. In
the jogs only the neck and head take it (TWIST_JOG): with the hips 70 degrees round, a chest pulled straight looked
ridiculous in the game (2026-11-12). Every Animation Redone 12.0 does the same to its sideways jogs, less evenly.

Centre: the first-person animations hold the body off the root, which is where the character stands and the point
it turns around: the hips are 21 cm behind it standing, 25 walking, 27 jogging, 28 to 34 crouched, 3 sprinting.
From outside a character turning on the spot swings around in a small circle (seen in the game, 2026-11-12). So
each animation's hips are moved by their mean horizontal offset, onto the root.

The set's fallback poses (a few frames per animation, kept outside the animation data, with their own bone index
lists) can't be moved this way, so the converted sets have none, like the game's johnny__locomotion.anims.

    wolvenkit export  player_locomotion.anims -> .glb            (build.sh)
    retarget_anims.py glb   drops the joints and tracks the NPC rig lacks, renames the animations
    retarget_anims.py json  the set the import rebuilds: the original's JSON with the NPC rig, the same names and
                            no fallback poses
    AnimImport        .glb -> .anims                             (build.sh)

Names, standing set:
    fpp_walk_slow_<dir>         -> walk_<dir>            (the NPC walk)
    fpp_walk_fast_<dir>         -> walk_fast_<dir>
    fpp_<walk|jog>_<dir>_to_idle_<l|r> -> <walk|jog>_to_idle_<dir>, the right foot one with _r
    fpp_<anything else>         -> <anything else>       (jog_090, sprint_0, idle_stand, crouch_walk_0, ...)
    other names (jump_0, slide_loop, fall_jog) stay

Names, run set (--run). The NPC graph plays backward and sideways movement in its walk style only, so the player's
backward and sideways jogs get the walk names in a set of their own, switched in while a character moves that way
at more than walking speed:
    fpp_jog_<090|180|270>       -> walk_<090|180|270>
    fpp_idle_stand              -> idle_stand  (the same idle as the standing set's: with it, switching the set
                                   on while the character stands makes the game load the set without showing
                                   anything; the client does that once after the spawn)
    anything else               -> cv_unused_<name>

Names, crouch set (--crouch). NPCs crouch by swapping in another set with the same names, so the crouch animations
take the standing names and everything else gets a name nothing asks for:
    fpp_crouch_idle             -> idle_stand
    fpp_crouch_walk_<dir>       -> walk_<dir>
    anything else               -> cv_unused_<name>

    python3 -I retarget_anims.py glb  <in.glb> <player rig.json>,<npc rig.json> <out.glb> [--crouch|--run]
    python3 -I retarget_anims.py json <in.anims.json> <npc rig depot path> <out.anims.json> [--crouch|--run]
"""
import json
import math
import re
import struct
import sys

STOP = re.compile(r"^fpp_(walk|jog)_(\d+)_to_idle_([lr])$")


def standing_name(name):
    stop = STOP.match(name)
    if stop:
        kind, direction, foot = stop.groups()
        return f"{kind}_to_idle_{direction}" + ("_r" if foot == "r" else "")
    if name.startswith("fpp_walk_slow_"):
        return "walk_" + name[len("fpp_walk_slow_"):]
    if name.startswith("fpp_"):
        return name[len("fpp_"):]
    return name


def crouch_name(name):
    if name == "fpp_crouch_idle":
        return "idle_stand"
    if name.startswith("fpp_crouch_walk_"):
        return "walk_" + name[len("fpp_crouch_walk_"):]
    return "cv_unused_" + name


def run_name(name):
    if name in ("fpp_jog_090", "fpp_jog_180", "fpp_jog_270"):
        return "walk_" + name[len("fpp_jog_"):]
    if name == "fpp_idle_stand":
        return "idle_stand"
    return "cv_unused_" + name


NAMES = {"": standing_name, "--crouch": crouch_name, "--run": run_name}
# Bones that move along their length in the animations: the character's position over the ground.
FREE_BONES = ("Root", "Trajectory", "Hips")


# Animations whose upper body is twisted back to face forward (by their names in the player's set), and the share of
# the head's turn each bone takes back: Head's parents from the spine up, in order. The jogs keep their spine.
TURNED = re.compile(r"^fpp_(?:(?:walk_slow|walk_fast|(?P<jog>jog))_(?P<loop>\d+)|idle_to_(?:walk|(?P<to_jog>jog))_(?P<start>\d+)"
                    r"|(?:walk|(?P<from_jog>jog))_(?P<stop>\d+)_to_idle_[lr]|(?P<crouch>crouch)_walk_\d+)$")
TWIST = (("Spine", 0.15), ("Spine1", 0.15), ("Spine2", 0.2), ("Spine3", 0.2), ("Neck", 0.1), ("Neck1", 0.1), ("Head", 0.1))
TWIST_JOG = (("Spine", 0.0), ("Spine1", 0.0), ("Spine2", 0.0), ("Spine3", 0.0), ("Neck", 0.3), ("Neck1", 0.3), ("Head", 0.4))


def twist_shares(name):
    """The shares to twist an animation (the player's name for it) back with; None for one that faces forward
    already: anything but the sideways and diagonal walks and jogs, their starts and stops, and the crouch walks."""
    match = TURNED.match(name)
    if not match:
        return None
    if match["crouch"]:
        return TWIST
    if (match["loop"] or match["start"] or match["stop"]) in ("0", "180"):
        return None
    return TWIST_JOG if match["jog"] or match["to_jog"] or match["from_jog"] else TWIST


def q_mul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz)


def q_inverse(q):
    return (-q[0], -q[1], -q[2], q[3])


def q_rotate(q, v):
    x, y, z, _ = q_mul(q_mul(q, (v[0], v[1], v[2], 0.0)), q_inverse(q))
    return (x, y, z)


def q_at(times, values, t):
    """The rotation at time t of keys that are interpolated linearly."""
    if t <= times[0]:
        return values[0]
    if t >= times[-1]:
        return values[-1]
    i = next(i for i, key in enumerate(times) if key >= t)
    a, b = values[i - 1], values[i]
    f = (t - times[i - 1]) / (times[i] - times[i - 1])
    if sum(x * y for x, y in zip(a, b)) < 0:
        b = tuple(-x for x in b)
    q = tuple(x + f * (y - x) for x, y in zip(a, b))
    length = math.sqrt(sum(x * x for x in q))
    return tuple(x / length for x in q)


def rename_all(names, crouch, where):
    renamed = [NAMES[crouch](n) for n in names]
    if len(set(renamed)) != len(renamed):
        raise SystemExit(f"{where}: two animations would get the same name")
    return renamed


def rig_names(path, key):
    with open(path, encoding="utf-8") as f:
        return [n["$value"] for n in json.load(f)["Data"]["RootChunk"][key]]


def move_track_keys(extras, key, player_tracks, npc_tracks, where):
    """Renumbers one of an animation's track key lists (in the glTF extras) to the NPC rig's tracks."""
    moved = []
    for track_key in extras.get(key) or []:
        if not 0 <= track_key["trackIndex"] < len(player_tracks):
            raise SystemExit(f"{where}: {key} names track {track_key['trackIndex']}, the player's rig has {len(player_tracks)}")
        name = player_tracks[track_key["trackIndex"]]
        if name in npc_tracks:
            moved.append({**track_key, "trackIndex": npc_tracks.index(name)})
    extras[key] = moved


def retarget_glb(src, rigs, dst, crouch):
    player_rig, rig_json = rigs.split(",")
    player_tracks = rig_names(player_rig, "trackNames")
    npc_tracks = rig_names(rig_json, "trackNames")
    with open(src, "rb") as f:
        data = f.read()
    magic, _, _ = struct.unpack_from("<4sII", data, 0)
    length, kind = struct.unpack_from("<I4s", data, 12)
    if magic != b"glTF" or kind != b"JSON":
        raise SystemExit(f"{src}: not a binary glTF file")
    doc = json.loads(data[20:20 + length])
    bin_length, bin_kind = struct.unpack_from("<I4s", data, 20 + length)
    if bin_kind != b"BIN\0" or len(doc["buffers"]) != 1:
        raise SystemExit(f"{src}: expected one binary chunk after the JSON")
    binary = bytearray(data[28 + length:28 + length + doc["buffers"][0]["byteLength"]])

    bones = rig_names(rig_json, "boneNames")
    with open(rig_json, encoding="utf-8") as f:
        offsets = [t["Translation"] for t in json.load(f)["Data"]["RootChunk"]["boneTransforms"]]
    names = [n.get("name") for n in doc["nodes"]]
    missing = [b for b in bones if b not in names]
    if missing:
        raise SystemExit(f"{src}: the NPC rig's bones {missing} aren't in the player's animations")
    keep = {names.index(b) for b in bones}

    if len(doc["skins"]) != 1:
        raise SystemExit(f"{src}: expected one skin, found {len(doc['skins'])}")
    doc["skins"][0]["joints"] = [names.index(b) for b in bones]
    doc["skins"][0].pop("inverseBindMatrices", None)
    for node in doc["nodes"]:
        if "children" in node:
            node["children"] = [c for c in node["children"] if c in keep]
            if not node["children"]:
                del node["children"]  # glTF allows no empty arrays
    # The NPC rig's own bone offsets, in glTF's axes (the game's (x, y, z) is glTF's (x, z, -y)).
    fixed = {}
    for bone, offset in zip(bones, offsets):
        if bone not in FREE_BONES:
            fixed[names.index(bone)] = (offset["X"], offset["Z"], -offset["Y"])
            doc["nodes"][names.index(bone)]["translation"] = list(fixed[names.index(bone)])

    constants = {}

    def constant(node, count):
        """An accessor with `count` copies of the node's fixed offset, added to the binary chunk."""
        if (node, count) not in constants:
            binary.extend(b"\0" * (-len(binary) % 4))
            doc["bufferViews"].append({"buffer": 0, "byteOffset": len(binary), "byteLength": 12 * count})
            binary.extend(struct.pack("<3f", *fixed[node]) * count)
            doc["accessors"].append({"bufferView": len(doc["bufferViews"]) - 1, "componentType": 5126,
                                     "count": count, "type": "VEC3"})
            constants[(node, count)] = len(doc["accessors"]) - 1
        return constants[(node, count)]

    def floats(index, width):
        """An accessor's values, as tuples of `width` floats (plain floats for width 1)."""
        accessor = doc["accessors"][index]
        view = doc["bufferViews"][accessor["bufferView"]]
        if accessor["componentType"] != 5126 or view.get("byteStride") or "sparse" in accessor:
            raise SystemExit(f"{src}: accessor {index} isn't a plain float array; has the export format changed?")
        flat = struct.unpack_from(f"<{accessor['count'] * width}f", binary,
                                  view.get("byteOffset", 0) + accessor.get("byteOffset", 0))
        return flat if width == 1 else [flat[i:i + width] for i in range(0, len(flat), width)]

    parents = {c: i for i, node in enumerate(doc["nodes"]) for c in node.get("children", [])}
    spine = [names.index(bone) for bone, _ in TWIST]
    head = spine[-1]
    chain = [head]  # Head and its parents, Head first
    while chain[-1] in parents:
        chain.append(parents[chain[-1]])
    if [parents[node] for node in spine[1:]] != spine[:-1] or [bone for bone, _ in TWIST_JOG] != [bone for bone, _ in TWIST]:
        raise SystemExit(f"{src}: the bones in TWIST aren't a chain from the spine to the head any more")
    eyes = [doc["nodes"][names.index(eye)]["translation"] for eye in ("LeftEye", "RightEye")]
    across = tuple(l - r for l, r in zip(*eyes))  # from the right eye to the left, in the head's space

    def face_forward(animation, name, shares):
        """Twists the upper body back, frame by frame, until the head faces the way the character does."""
        twist = [(node, share) for node, (_, share) in zip(spine, shares) if share]
        rotations = {}
        for channel in animation["channels"]:
            if channel["target"]["path"] == "rotation" and channel["target"]["node"] in chain:
                sampler = animation["samplers"][channel["sampler"]]
                rotations[channel["target"]["node"]] = (channel, floats(sampler["input"], 1), floats(sampler["output"], 4))
        lacking = [names[node] for node, _ in twist if node not in rotations]
        if lacking:
            raise SystemExit(f"{src}: {name} has no rotation for {lacking}")
        # Every frame of the animation: the key times of the bone with the most keys.
        frames = max((animation["samplers"][c["sampler"]] for c, _, _ in rotations.values()),
                     key=lambda sampler: doc["accessors"][sampler["input"]]["count"])
        if frames.get("interpolation", "LINEAR") != "LINEAR":
            raise SystemExit(f"{src}: {name} has no linearly interpolated rotation to take the frame times from")
        times = floats(frames["input"], 1)
        new = {node: [] for node, _ in twist}
        turns = []
        for t in times:
            local = {node: q_at(rotations[node][1], rotations[node][2], t) if node in rotations
                     else tuple(doc["nodes"][node].get("rotation", (0.0, 0.0, 0.0, 1.0))) for node in chain}
            world = {}
            total = (0.0, 0.0, 0.0, 1.0)
            for node in reversed(chain):
                total = q_mul(total, local[node])
                world[node] = total
            # The character faces -Z (the game's +Y); its left is then -X. The head's turn to the left, around +Y:
            x, _, z = q_rotate(world[head], across)
            turn = math.atan2(z, -x)
            turns.append(turn)
            for node, share in twist:
                half = -turn * share / 2  # turned back
                above = world[parents[node]]
                back = q_mul(q_mul(q_inverse(above), (0.0, math.sin(half), 0.0, math.cos(half))), above)
                new[node].append(q_mul(back, local[node]))
        for node, _ in twist:
            binary.extend(b"\0" * (-len(binary) % 4))
            doc["bufferViews"].append({"buffer": 0, "byteOffset": len(binary), "byteLength": 16 * len(times)})
            binary.extend(b"".join(struct.pack("<4f", *q) for q in new[node]))
            doc["accessors"].append({"bufferView": len(doc["bufferViews"]) - 1, "componentType": 5126,
                                     "count": len(times), "type": "VEC4"})
            animation["samplers"].append({**frames, "output": len(doc["accessors"]) - 1})
            rotations[node][0]["sampler"] = len(animation["samplers"]) - 1
        return math.degrees(sum(turns) / len(turns))

    hips = names.index("Hips")

    def centre(animation, name):
        """Moves the hips by their mean horizontal offset, so the body stands on its root."""
        channel = next((c for c in animation["channels"]
                        if c["target"]["node"] == hips and c["target"]["path"] == "translation"), None)
        if channel is None:
            raise SystemExit(f"{src}: {name} has no position for Hips")
        sampler = dict(animation["samplers"][channel["sampler"]])
        keys = floats(sampler["output"], 3)
        mean_x = sum(k[0] for k in keys) / len(keys)
        mean_z = sum(k[2] for k in keys) / len(keys)
        binary.extend(b"\0" * (-len(binary) % 4))
        doc["bufferViews"].append({"buffer": 0, "byteOffset": len(binary), "byteLength": 12 * len(keys)})
        binary.extend(b"".join(struct.pack("<3f", x - mean_x, y, z - mean_z) for x, y, z in keys))
        doc["accessors"].append({"bufferView": len(doc["bufferViews"]) - 1, "componentType": 5126,
                                 "count": len(keys), "type": "VEC3"})
        sampler["output"] = len(doc["accessors"]) - 1
        animation["samplers"].append(sampler)
        channel["sampler"] = len(animation["samplers"]) - 1

    straightened = []
    for animation, name in zip(doc["animations"], rename_all([a["name"] for a in doc["animations"]], crouch, src)):
        shares = twist_shares(animation["name"])
        if shares:
            straightened.append(f"{name} {face_forward(animation, animation['name'], shares):+.0f}")
        centre(animation, animation["name"])
        animation["name"] = name
        animation["channels"] = [c for c in animation["channels"] if c["target"]["node"] in keep]
        if not animation["channels"]:
            raise SystemExit(f"{src}: {name} moves none of the NPC rig's bones")
        positioned = set()
        for channel in animation["channels"]:
            node = channel["target"]["node"]
            if channel["target"]["path"] != "translation":
                continue
            positioned.add(node)
            if node in fixed:
                # A sampler of its own, with the channel's key times and the fixed offset at each.
                sampler = dict(animation["samplers"][channel["sampler"]])
                sampler["output"] = constant(node, doc["accessors"][sampler["input"]]["count"])
                animation["samplers"].append(sampler)
                channel["sampler"] = len(animation["samplers"]) - 1
        if positioned != keep:
            raise SystemExit(f"{src}: {name} has no position for {len(keep - positioned)} of the NPC rig's bones")
        extras = animation.get("extras")
        if not isinstance(extras, dict) or "constTrackKeys" not in extras or "fallbackFrameIndices" not in extras:
            raise SystemExit(f"{src}: {name} has no WolvenKit extras with track keys; has the export format changed?")
        move_track_keys(extras, "constTrackKeys", player_tracks, npc_tracks, f"{src}: {name}")
        move_track_keys(extras, "trackKeys", player_tracks, npc_tracks, f"{src}: {name}")
        extras["fallbackFrameIndices"] = []

    binary.extend(b"\0" * (-len(binary) % 4))
    doc["buffers"][0]["byteLength"] = len(binary)
    text = json.dumps(doc, separators=(",", ":")).encode()
    text += b" " * (-len(text) % 4)
    out = (struct.pack("<4sII", b"glTF", 2, 12 + 8 + len(text) + 8 + len(binary))
           + struct.pack("<I4s", len(text), b"JSON") + text + struct.pack("<I4s", len(binary), b"BIN\0") + binary)
    with open(dst, "wb") as f:
        f.write(out)
    print(f"{dst}: {len(doc['animations'])} animations on {len(bones)} of {len(names) - 1} joints; "
          f"{len(straightened)} turned to face forward", file=sys.stderr)


def retarget_json(src, rig_path, dst, crouch):
    with open(src, encoding="utf-8") as f:
        doc = json.load(f)
    root = doc["Data"]["RootChunk"]
    root["rig"]["DepotPath"]["$value"] = rig_path
    root["fallbackAnimDataBuffer"] = None
    for key in ("fallbackAnimDescIndexes", "fallbackAnimFrameDescs", "fallbackDataAddresses", "fallbackDataAddressIndexes"):
        root[key] = []
    for key in ("fallbackNumFloatTrackData", "fallbackNumPositionData", "fallbackNumRotationData"):
        root[key] = 0
    for entry in root["animations"]:
        entry["Data"]["animation"]["Data"]["animBuffer"]["Data"]["fallbackFrameIndices"] = []
    entries = [e["Data"]["animation"]["Data"]["name"] for e in root["animations"]]
    for entry, name in zip(entries, rename_all([e["$value"] for e in entries], crouch, src)):
        entry["$value"] = name
    with open(dst, "w", encoding="utf-8") as f:
        json.dump(doc, f)


if __name__ == "__main__":
    crouch = sys.argv[-1] if sys.argv[-1] in ("--crouch", "--run") else ""  # which set's names
    arguments = sys.argv[1:-1] if crouch else sys.argv[1:]
    if len(arguments) != 4 or arguments[0] not in ("glb", "json"):
        raise SystemExit(__doc__)
    (retarget_glb if arguments[0] == "glb" else retarget_json)(*arguments[1:], crouch)
