"""Builds the look table for remote players' V (CYB-00029) from the game's character customization data.

A stored look is `v1 body=m name=value ...`: option names from the game's customization tables with the selected
index. To show it on another player's V, each option has to become concrete changes on the puppet:

- appearance option (gameuiAppearanceInfo): its .app file's appearance named after the selected definition lists mesh
  components; each one becomes "this component shows this mesh with this mesh appearance".
- morph option (gameuiMorphInfo): the selected morph target name, plus the region it belongs to in the head's
  morph target file.
- switcher option (gameuiSwitcherInfo): turns other options on, e.g. hairstyle index 51 turns on hair_color51. The
  stored look also keeps options a switcher no longer turns on (hair_color38, the default hairstyle's colour, stays
  after picking another hairstyle), so the client applies an option a switcher can turn on only when the look's
  switcher value does.

Input: WolvenKit JSON of male_cco/female_cco.inkcharcustomization, of every .app they reference, and of the head
morph target files. Output: a tab-separated table the client plugin loads, one line per option value:

    <body m|f> TAB <option> TAB <index> TAB mesh TAB <kind>|<component>|<file>|<appearance>|<skinned to>|<hangs from>[;...]

<kind> is the component class: S skinned mesh, G garment mesh, M morph target mesh, C cloth, A animated (a hairstyle's
hair_dangle: file is its .rig, appearance its .animgraph), ? other. <skinned to> and <hangs from> are the bindNames of
the part's skinning and parentTransform (another part of the same appearance, e.g. hair_dangle, or "root").
    <body m|f> TAB <option> TAB <index> TAB morph TAB <target>|<region>
    <body m|f> TAB <option> TAB <index> TAB switch TAB <option>[,<option>...]   (may be empty: turns nothing on)
    <body m|f> TAB <option> TAB <index> TAB app TAB <.app file>|<appearance name>

"app" lines exist for hair (values with hh_* parts or a skeleton): the client doesn't rebuild those parts from the
"mesh" line, it has the engine extract the game's own parts from that appearance of that file; the "mesh" line then
only tells it which parts those are.

Lines starting with # are comments; the first one names the game version and the format version.

    python3 -I generate.py <gamedata json folder> <output.tsv> <game version>
"""
import json
import os
import sys

BODIES = {"m": "male_cco.inkcharcustomization.json", "f": "female_cco.inkcharcustomization.json"}
MORPH_FILES = {"m": "h0_000_pma__morphs.morphtarget.json", "f": "h0_000_pwa__morphs.morphtarget.json"}
# The component class as one letter in the table: the client changes each kind its own way.
KINDS = {
    "entSkinnedMeshComponent": "S",
    "entGarmentSkinnedMeshComponent": "G",
    "entMorphTargetSkinnedMeshComponent": "M",
    "entSkinnedClothComponent": "C",
    "entAnimatedComponent": "A",
}
SECTIONS = ("headCustomizationOptions", "bodyCustomizationOptions", "armsCustomizationOptions")


def cname(value):
    return value.get("$value") if isinstance(value, dict) else value


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)["Data"]["RootChunk"]


def app_index(app_folder):
    """Basename -> JSON file. WolvenKit writes all converted files into one folder, so a basename seen twice is
    ambiguous; those are reported and left out."""
    seen = {}
    for name in os.listdir(app_folder):
        if name.endswith(".app.json"):
            seen.setdefault(name[: -len(".json")], []).append(os.path.join(app_folder, name))
    return {base: paths[0] for base, paths in seen.items() if len(paths) == 1}


def handles(document):
    """HandleId -> object for one WolvenKit JSON file; later uses of an object only hold {"HandleRefId": id}."""
    found = {}

    def walk(node):
        if isinstance(node, dict):
            if "HandleId" in node and "Data" in node:
                found.setdefault(node["HandleId"], node["Data"])
            for value in node.values():
                walk(value)
        elif isinstance(node, list):
            for value in node:
                walk(value)

    walk(document)
    return found


def bind_name(reference, objects):
    """The bindName of a skinning or parentTransform binding, inline or by HandleRefId; "" when there's none."""
    if not isinstance(reference, dict):
        return ""
    data = reference.get("Data") or objects.get(reference.get("HandleRefId")) or {}
    name = cname(data.get("bindName"))
    return name if name and name != "None" else ""


def app_appearances(path, cache):
    """appearance name -> [parts] for one .app. A part is [kind, component name, file, appearance, skinned to, hangs
    from]; for an animated part (kind A, e.g. a hairstyle's hair_dangle with its own small skeleton) file is the rig
    and appearance the animation graph."""
    if path in cache:
        return cache[path]
    with open(path, encoding="utf-8") as f:
        document = json.load(f)
    objects = handles(document)
    root = document["Data"]["RootChunk"]
    result = {}
    for handle in root.get("appearances") or []:
        definition = handle.get("Data", {})
        parts = []
        for component in definition.get("components") or []:
            kind = KINDS.get(component.get("$type"))
            name = cname(component.get("name"))
            skin = bind_name(component.get("skinning"), objects)
            parent = bind_name(component.get("parentTransform"), objects)
            if kind == "A":
                rig = component.get("rig", {}).get("DepotPath", {}).get("$value")
                graph = component.get("graph", {}).get("DepotPath", {}).get("$value")
                if rig and rig != "0":
                    parts.append(["A", name, rig, graph if graph and graph != "0" else "", "", parent])
                continue
            # Plain and garment meshes name their file in `mesh`, morph target meshes (face, eyes, beard) in
            # `morphResource`.
            ref = component.get("mesh") or component.get("morphResource") or {}
            mesh = ref.get("DepotPath", {}).get("$value")
            if not mesh or mesh == "0":
                continue
            parts.append([kind or "?", name, mesh, cname(component.get("meshAppearance")), skin, parent])
        result[cname(definition.get("name"))] = parts
    cache[path] = result
    return result


def everywhere(apps, cache, _index={}):
    """Appearance name -> parts across all .app files, for options without a file of their own (skin_color). A name in
    several files (the head's skin types d02..d05 repeat the skin colours) takes the file with the shortest name, the
    base one; the skin type's own option then decides which head variant shows (not handled yet, CYB-00029)."""
    if not _index:
        for base in sorted(apps, key=lambda name: (len(name), name)):
            for name, parts in app_appearances(apps[base], cache).items():
                _index.setdefault(name, parts)
    return _index


def morph_regions(path):
    if not os.path.exists(path):
        return {}
    root = load(path)
    return {cname(t.get("name")): cname(t.get("regionName")) for t in root.get("targets", [])}


def table(folder):
    apps = app_index(os.path.join(folder, "apps"))
    cache = {}
    out = {"version": 1, "bodies": {}}
    missing_apps = set()
    for body, file_name in BODIES.items():
        root = load(os.path.join(folder, file_name))
        regions = morph_regions(os.path.join(folder, MORPH_FILES[body]))
        options = {}
        for section in SECTIONS:
            for handle in root[section]:
                info = handle.get("Data", {})
                kind = info.get("$type")
                name = cname(info.get("name"))
                if "fpp" in name.lower():
                    continue  # first-person only: never visible on another player's V
                if kind == "gameuiAppearanceInfo":
                    depot = info.get("resource", {}).get("DepotPath", {}).get("$value")
                    base = depot.rsplit("\\", 1)[-1] if depot else None
                    if str(depot) == "0":
                        # No file of its own (e.g. skin_color): its values are appearance names from other files.
                        appearances = everywhere(apps, cache)
                    elif not base or base not in apps:
                        missing_apps.add(depot)
                        continue
                    else:
                        appearances = app_appearances(apps[base], cache)
                    values = {}
                    files = {}
                    for definition in info.get("definitions", []):
                        parts = appearances.get(cname(definition.get("name")))
                        if parts:
                            values[str(definition.get("index"))] = parts
                            if str(depot) != "0" and any(p[0] == "A" or p[1].startswith("hh_") for p in parts):
                                files[str(definition.get("index"))] = (depot, cname(definition.get("name")))
                    if values:
                        options[name] = {"kind": "appearance", "values": values, "apps": files}
                elif kind == "gameuiSwitcherInfo":
                    values = {}
                    for option in info.get("options", []):
                        names = [cname(n) for n in option.get("names", [])]
                        values[str(option.get("index"))] = [n for n in names if n and "fpp" not in n.lower()]
                    if values:
                        options[name] = {"kind": "switch", "values": values}
                elif kind == "gameuiMorphInfo":
                    values = {}
                    for morph in info.get("morphNames", []):
                        target = cname(morph.get("morphName"))
                        if target and target != "None":
                            values[str(morph.get("index"))] = [target, regions.get(target, cname(info.get("uiSlot")))]
                    if values:
                        options[name] = {"kind": "morph", "values": values}
        out["bodies"][body] = options
    return out, sorted(p for p in missing_apps if p)


def main():
    folder, output, game_version = sys.argv[1], sys.argv[2], sys.argv[3]
    result, missing = table(folder)
    lines = [f"# Cyberverse look table v1, game {game_version}; generated by tools/look-table/build.sh, don't edit"]
    for body in sorted(result["bodies"]):
        options = result["bodies"][body]
        for name in sorted(options):
            option = options[name]
            for index in sorted(option["values"], key=int):
                value = option["values"][index]
                if option["kind"] == "appearance":
                    data = ";".join("|".join(part) for part in value)
                    lines.append(f"{body}\t{name}\t{index}\tmesh\t{data}")
                    if index in option["apps"]:
                        lines.append(f"{body}\t{name}\t{index}\tapp\t{'|'.join(option['apps'][index])}")
                elif option["kind"] == "switch":
                    lines.append(f"{body}\t{name}\t{index}\tswitch\t{','.join(value)}")
                else:
                    lines.append(f"{body}\t{name}\t{index}\tmorph\t{value[0]}|{value[1]}")
    with open(output, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    for body, options in result["bodies"].items():
        kinds = {}
        for option in options.values():
            kinds[option["kind"]] = kinds.get(option["kind"], 0) + 1
        print(f"{body}: {len(options)} options {kinds}", file=sys.stderr)
    print(f"{len(lines) - 1} lines; {len(missing)} referenced .app files not found or ambiguous", file=sys.stderr)
    for path in missing:
        print(f"  missing: {path}", file=sys.stderr)


if __name__ == "__main__":
    main()
