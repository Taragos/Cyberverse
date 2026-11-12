"""Puts all of a converted animation set's key data into one buffer (CYB-00010).

WolvenKit's animation import writes each animation's key data into a buffer of its own (animationDataChunks, one per
animation). The game loads such a buffer when an animation from it is first played, and until it has arrived the
animation has nothing to show: the converted sets have no fallback poses (see retarget_anims.py), so the puppet
stands in its bind pose (a T-pose flash the first time it walks left, then right, and so on; seen in the game on
2026-11-12). The game's own player_locomotion.anims keeps everything in one buffer. Done the same way here, the
whole set arrives with the first animation played, which for the standing set is the idle at the spawn.

Input and output: WolvenKit JSON of the built .anims (`wolvenkit convert serialize` / `deserialize`).

    python3 -I merge_chunks.py <in.anims.json> <out.anims.json>
"""
import base64
import json
import sys


def merge(src, dst):
    with open(src, encoding="utf-8") as f:
        doc = json.load(f)
    root = doc["Data"]["RootChunk"]
    chunks = [base64.b64decode(c["buffer"]["Bytes"]) for c in root["animationDataChunks"]]
    merged = bytearray()
    for number, entry in enumerate(root["animations"], 1):
        buffer = entry["Data"]["animation"]["Data"]["animBuffer"]["Data"]
        if buffer["$type"] != "animAnimationBufferCompressed" or buffer["inplaceCompressedBuffer"] or buffer["defferedBuffer"]:
            raise SystemExit(f"{src}: an animation doesn't keep its keys in a data chunk; has the import changed?")
        address = buffer["dataAddress"]
        data = chunks[address["unkIndex"]][address["fsetInBytes"]:address["fsetInBytes"] + address["zeInBytes"]]
        if len(data) != address["zeInBytes"]:
            raise SystemExit(f"{src}: an animation's data address is outside its chunk")
        address["unkIndex"] = 0
        address["fsetInBytes"] = len(merged)
        merged += data
        # WolvenKit's own copy of the keys; buffer 0 is the chunk, as in the game's sets.
        if buffer.get("tempBuffer"):
            buffer["tempBuffer"]["BufferId"] = str(number)
    chunk = root["animationDataChunks"][0]
    chunk["buffer"]["BufferId"] = "0"
    chunk["buffer"]["Bytes"] = base64.b64encode(bytes(merged)).decode()
    root["animationDataChunks"] = [chunk]
    with open(dst, "w", encoding="utf-8") as f:
        json.dump(doc, f)
    print(f"{dst}: {len(root['animations'])} animations in one buffer of {len(merged)} bytes (were {len(chunks)})",
          file=sys.stderr)


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    merge(sys.argv[1], sys.argv[2])
