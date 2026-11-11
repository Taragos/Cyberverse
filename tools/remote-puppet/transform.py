"""Builds the entity templates for other players' V (CYB-00032) from the game's cutscene V.

Remote players used to spawn as the game's player_ma/wa_tpp_cutscene_no_impostor.ent, whose customization
controllers copy the local player's look onto every remote V, and whose root animation graph is the inventory paper
doll, which doesn't read AnimFeature_Locomotion (remote Vs slid instead of walking). This script writes copies with:

- every gameuiCharacterCustomization*Controller removed, so nothing of the local look reaches the remote V;
- the root animated component's graph swapped for the NPC one of man_base.ent / woman_base.ent (humanoid.animgraph),
  which uses the same rig (man_base.rig / woman_base.rig) and reads AnimFeature_Locomotion; the NPC base's animation
  sets are added to V's own (vehicle seats etc.), V's entries win on a clash;
- an ungated locomotion set (idle_stand, walk_0, sprint_0, idle_to_walk_0, ...). The NPC bases have their locomotion
  only behind variables (LowCorpLocomotion etc.) that a story NPC's own template or appearance sets; without one the
  graph has nothing to play and the puppet stands in its bind pose (T-pose). Panam's template adds
  wa_gang_unarmed_locomotion_relaxed.anims the same way.

Both the editable component list and the compiled copy the engine loads (compiledData) are changed the same way.

Input: WolvenKit JSON of the two cutscene templates and of man_base.ent / woman_base.ent. Output: WolvenKit JSON of
the two new templates, to be turned back into .ent files with `wolvenkit convert deserialize`.

    python3 -I transform.py <json folder> <output folder>
"""
import json
import os
import sys

# Output file -> (cutscene V template, NPC base template).
TEMPLATES = {
    "remote_v_ma.ent.json": ("player_ma_tpp_cutscene_no_impostor.ent.json", "man_base.ent.json"),
    "remote_v_wa.ent.json": ("player_wa_tpp_cutscene_no_impostor.ent.json", "woman_base.ent.json"),
}
# Output file -> the locomotion set it plays without any variable, at Panam's priority.
LOCOMOTION = {
    "remote_v_ma.ent.json": "base\\animations\\npc\\gameplay\\man_average\\gang\\unarmed\\ma_gang_unarmed_locomotion_relaxed.anims",
    "remote_v_wa.ent.json": "base\\animations\\npc\\gameplay\\woman_average\\gang\\unarmed\\wa_gang_unarmed_locomotion_relaxed.anims",
}
LOCOMOTION_PRIORITY = 127
DROP_PREFIX = "gameuiCharacterCustomization"
ROOT = "root"


def cname(value):
    return value.get("$value") if isinstance(value, dict) else value


def has_handle_refs(obj, ids):
    """Whether obj refers to one of the given handle ids."""
    if isinstance(obj, dict):
        if obj.get("HandleRefId") in ids:
            return True
        return any(has_handle_refs(v, ids) for v in obj.values())
    if isinstance(obj, list):
        return any(has_handle_refs(v, ids) for v in obj)
    return False


def handle_ids(obj, out):
    if isinstance(obj, dict):
        if "HandleId" in obj:
            out.add(obj["HandleId"])
        for v in obj.values():
            handle_ids(v, out)
    elif isinstance(obj, list):
        for v in obj:
            handle_ids(v, out)
    return out


def anim_set(entry):
    return entry["animSet"]["DepotPath"]["$value"]


def find_root(components, where):
    roots = [c for c in components if c.get("$type") == "entAnimatedComponent" and cname(c.get("name")) == ROOT]
    if len(roots) != 1:
        raise SystemExit(f"{where}: expected one entAnimatedComponent named {ROOT}, found {len(roots)}")
    return roots[0]


def locomotion_entry(path):
    return {
        "$type": "animAnimSetupEntry",
        "animSet": {"DepotPath": {"$type": "ResourcePath", "$storage": "string", "$value": path}, "Flags": "Default"},
        "priority": LOCOMOTION_PRIORITY,
        "variableNames": [],
    }


def transform_components(components, npc_root, locomotion, where):
    """Changes one component list in place; returns the names of the components it dropped."""
    dropped = [c for c in components if c.get("$type", "").startswith(DROP_PREFIX)]
    ids = set()
    for c in dropped:
        handle_ids(c, ids)
    kept = [c for c in components if c not in dropped]
    if ids and has_handle_refs(kept, ids):
        raise SystemExit(f"{where}: another component refers to a dropped customization controller")
    components[:] = kept

    root = find_root(components, where)
    if root["rig"]["DepotPath"]["$value"] != npc_root["rig"]["DepotPath"]["$value"]:
        raise SystemExit(f"{where}: V's rig {root['rig']['DepotPath']['$value']} isn't the NPC base's rig")
    root["graph"] = json.loads(json.dumps(npc_root["graph"]))
    own = root["animations"]["gameplay"]
    known = {anim_set(e) for e in own}
    own.extend(json.loads(json.dumps(e)) for e in npc_root["animations"]["gameplay"] if anim_set(e) not in known)
    own.append(locomotion_entry(locomotion))
    return [cname(c["name"]) for c in dropped]


def check_cruids(package, where):
    """Fails if the compiled package's CruidDict isn't "chunk index -> component id", the layout this script rebuilds."""
    chunks, cruids = package["Chunks"], package["CruidDict"]
    if len(chunks) != len(cruids) or any(cruids.get(str(i)) != str(c.get("id", "0")) for i, c in enumerate(chunks)):
        raise SystemExit(f"{where}: compiledData's CruidDict isn't chunk index -> component id any more")


def add_dependencies(root):
    """Lists the root component's animation sets in resolvedDependencies, as the game's templates do."""
    deps = root["resolvedDependencies"]
    known = {d["DepotPath"]["$value"] for d in deps}
    for entry in find_root(root["components"], "components")["animations"]["gameplay"]:
        if anim_set(entry) not in known:
            known.add(anim_set(entry))
            deps.append(json.loads(json.dumps(entry["animSet"])))


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def main(src, dst):
    os.makedirs(dst, exist_ok=True)
    for out, (v_file, npc_file) in TEMPLATES.items():
        doc = load(os.path.join(src, v_file))
        npc = load(os.path.join(src, npc_file))["Data"]["RootChunk"]
        npc_root = find_root(npc["components"], npc_file)
        root = doc["Data"]["RootChunk"]
        editable = transform_components(root["components"], npc_root, LOCOMOTION[out], f"{v_file} components")
        package = root["compiledData"]["Data"]
        check_cruids(package, v_file)
        compiled = transform_components(package["Chunks"], npc_root, LOCOMOTION[out],
                                        f"{v_file} compiledData")
        # The package maps each chunk's index to its component id (CRUID; "0" for the entity); renumber after dropping.
        package["CruidDict"] = {str(i): str(c.get("id", "0")) for i, c in enumerate(package["Chunks"])}
        if sorted(editable) != sorted(compiled) or not editable:
            raise SystemExit(f"{v_file}: dropped {editable} from components but {compiled} from compiledData")
        add_dependencies(root)
        with open(os.path.join(dst, out), "w", encoding="utf-8") as f:
            json.dump(doc, f, indent=2)
        print(f"{out}: dropped {len(editable)} customization controllers, root graph "
              f"{npc_root['graph']['DepotPath']['$value']}", file=sys.stderr)


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    main(sys.argv[1], sys.argv[2])
