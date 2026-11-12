"""Checks a converted animation set before it is packed (CYB-00010): what went wrong in game when it was missing.

For every animation of a set (WolvenKit JSON of the built .anims):
- it is stored for exactly the rig's bones;
- every bone has a position and a rotation key (a bone without a position collapses onto its parent: the body
  becomes a jumble, or invisible while standing);
- no key names a bone or a float track the rig doesn't have (float tracks by the wrong index hid the body: the NPC
  rig's first four are the body part visibility);
- the body part visibility tracks, where set, are 1;
- there are no fallback poses (they would still be in the player's bone order);
- the set keeps its key data in one buffer (one per animation is loaded piece by piece: T-pose flashes).

The key data's layout (animAnimationBufferCompressed): numAnimKeys keys of 10 bytes (u16 time, u16 bits, 3 x u16),
numAnimKeysRaw of 16 (u16 time, u16 bits, 3 x f32), numConstAnimKeys of 16 (u16 bits, u16, 3 x f32), then
numConstTrackKeys and numTrackKeys of 8 (u16 track, u16 time, f32). bits: the bone in the low 13 bits, then 2 bits
for what the key sets (1: rotation, 0: position).

    python3 -I check_anims.py <rig.json> <set.anims.json>...
"""
import base64
import json
import struct
import sys

VISIBILITY = ("headBodyPartVis", "leftArmBodyPartVis", "rightArmBodyPartVis", "legsBodyPartVis")


def check(rig, path):
    bones = len(rig["boneNames"])
    tracks = [t["$value"] for t in rig["trackNames"]]
    with open(path, encoding="utf-8") as f:
        root = json.load(f)["Data"]["RootChunk"]
    problems = []
    if root["fallbackAnimFrameDescs"] or root["fallbackAnimDataBuffer"]:
        problems.append("the set has fallback poses")
    for entry in root["animations"]:
        animation = entry["Data"]["animation"]["Data"]
        name = animation["name"]["$value"]
        buffer = animation["animBuffer"]["Data"]
        if len(root["animationDataChunks"]) != 1:
            problems.append(f"the set has {len(root['animationDataChunks'])} data buffers, not one (merge_chunks.py)")
            break
        if buffer["$type"] != "animAnimationBufferCompressed":
            problems.append(f"{name}: buffer type {buffer['$type']}, which this check can't read")
            continue
        address = buffer["dataAddress"]
        data = base64.b64decode(root["animationDataChunks"][address["unkIndex"]]["buffer"]["Bytes"])
        data = data[address["fsetInBytes"]:address["fsetInBytes"] + address["zeInBytes"]]
        positions, rotations, offset = set(), set(), 0
        for count, size, bits_first in ((buffer["numAnimKeys"], 10, False), (buffer["numAnimKeysRaw"], 16, False),
                                        (buffer["numConstAnimKeys"], 16, True)):
            for _ in range(count):
                first, second = struct.unpack_from("<HH", data, offset)
                bits = first if bits_first else second
                (rotations if (bits >> 13) & 3 == 1 else positions).add(bits & 0x1FFF)
                offset += size
        track_keys = buffer["numConstTrackKeys"] + buffer["numTrackKeys"]
        if offset + 8 * track_keys != len(data):
            problems.append(f"{name}: the key data is {len(data)} bytes, the counts say {offset + 8 * track_keys}")
            continue
        expected = set(range(bones))
        if buffer["numJoints"] != bones or buffer["fallbackFrameIndices"]:
            problems.append(f"{name}: stored for {buffer['numJoints']} bones (the rig has {bones}) or has fallback frames")
        if positions != expected or rotations != expected:
            problems.append(f"{name}: {len(expected - positions)} bones without a position, "
                            f"{len(expected - rotations)} without a rotation, {len((positions | rotations) - expected)} unknown bones")
        for i in range(track_keys):
            track, _, value = struct.unpack_from("<HHf", data, offset + 8 * i)
            if track >= len(tracks):
                problems.append(f"{name}: a key for float track {track}; the rig has {len(tracks)}")
            elif tracks[track] in VISIBILITY and value != 1.0:
                problems.append(f"{name}: {tracks[track]} is {value}")
    return len(root["animations"]), problems


if __name__ == "__main__":
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    with open(sys.argv[1], encoding="utf-8") as f:
        rig = json.load(f)["Data"]["RootChunk"]
    failed = False
    for path in sys.argv[2:]:
        count, problems = check(rig, path)
        for problem in problems[:10]:
            print(f"{path}: {problem}", file=sys.stderr)
        if problems:
            failed = True
            print(f"{path}: {len(problems)} problems in {count} animations", file=sys.stderr)
    if failed:
        raise SystemExit(1)
    print(f"Checked {len(sys.argv) - 2} sets: every bone has its keys, tracks and visibility are right", file=sys.stderr)
