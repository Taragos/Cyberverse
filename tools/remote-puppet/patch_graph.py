"""Makes a copy of the NPC animation graph that blends between locomotion animations (CYB-00010).

The game's humanoid.animgraph switches most locomotion animations without a blend: by the motion table action
(move, backward, strafe left, ...) and by the style (walk, jog, sprint). An NPC's own movement code hides that with
transition animations and exact timing. Remote players are driven from network data, where a change of direction or
speed arrives without warning, so the puppet snapped from one animation into the next. In this copy, for the remote V
templates only (transform.py points them at it):

- every switch on locomotion.action that had no blend gets ACTION_BLEND seconds;
- every switch on locomotion.style that had no blend gets STYLE_BLEND seconds.

Switches that already blend (0.1 to 0.5 s) keep their time. Nothing else changes.

Input: WolvenKit JSON of base\\gameplay\\anim_graphs\\humanoid.animgraph. Output: the patched JSON.

    python3 -I patch_graph.py <in.animgraph.json> <out.animgraph.json>
"""
import json
import sys

ACTION_BLEND = 0.2
STYLE_BLEND = 0.25
GROUP = "locomotion"


def cname(value):
    return value.get("$value") if isinstance(value, dict) else value


def main(src, dst):
    with open(src, encoding="utf-8") as f:
        doc = json.load(f)

    handles = {}
    nodes = []
    stack = [doc]
    while stack:
        obj = stack.pop()
        if isinstance(obj, dict):
            if "HandleId" in obj and "Data" in obj:
                handles[obj["HandleId"]] = obj["Data"]
            if obj.get("$type") in ("animAnimNode_EnumSwitch", "animAnimNode_Switch"):
                nodes.append(obj)
            stack.extend(obj.values())
        elif isinstance(obj, list):
            stack.extend(obj)

    def resolve(link):
        """The node a link (animIntLink, animFloatLink) points at."""
        node = link.get("node") if isinstance(link, dict) else None
        if isinstance(node, dict) and "HandleRefId" in node:
            return handles.get(node["HandleRefId"])
        if isinstance(node, dict) and "Data" in node:
            return node["Data"]
        return None

    def reads(node, name, depth=0):
        """Whether a value node is the locomotion input `name`, directly or through a converter."""
        if not isinstance(node, dict) or depth > 3:
            return False
        if node.get("$type") == "animAnimNode_IntInput":
            return cname(node.get("group")) == GROUP and cname(node.get("name")) == name
        return any(reads(resolve(v), name, depth + 1) for v in node.values() if isinstance(v, dict) and "node" in v)

    actions = styles = 0
    for node in nodes:
        if node.get("blendTime") != 0:
            continue
        if node["$type"] == "animAnimNode_EnumSwitch" and reads(resolve(node.get("selectIntNode")), "action"):
            node["blendTime"] = ACTION_BLEND
            actions += 1
        elif node["$type"] == "animAnimNode_Switch" and reads(resolve(node.get("weightNode")), "style"):
            node["blendTime"] = STYLE_BLEND
            styles += 1
    if not actions or not styles:
        raise SystemExit(f"{src}: found {actions} action and {styles} style switches without a blend; expected both")

    with open(dst, "w", encoding="utf-8") as f:
        json.dump(doc, f)
    print(f"{dst}: blend added to {actions} action and {styles} style switches", file=sys.stderr)


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    main(sys.argv[1], sys.argv[2])
