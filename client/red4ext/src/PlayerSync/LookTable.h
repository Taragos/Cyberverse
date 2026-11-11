#pragma once
// The look table (CYB-00029): turns a stored look ("v1 body=m name=value ...", see AppearanceEditor.reds) into the
// concrete changes that show it on another player's V: which mesh part shows which mesh with which appearance, and
// which face shapes are on. Generated from the game files by tools/look-table/build.sh into look-table.tsv, which ships
// next to the plugin; loaded once, on first use.
#include <map>
#include <mutex>
#include <set>
#include <string>
#include <unordered_map>
#include <vector>

namespace Cyberverse::PlayerSync
{
/// One part a look puts on V (a mesh, or a hairstyle's own small skeleton).
struct LookPartData
{
    /// S skinned mesh, G garment mesh, M morph target mesh, A animated part (a hairstyle's hair_dangle: file is its
    /// rig, appearance its animation graph).
    std::string kind;
    std::string name;
    std::string file;
    std::string appearance;
    /// bindNames of the part's skinning and parent transform; empty when it has none.
    std::string skinnedTo;
    std::string hangsFrom;
    /// Whether the engine builds this part from the value's .app appearance while it builds the puppet (LookParts),
    /// instead of the second pass reusing a part the puppet already has.
    bool atSpawn = false;
};

/// A face shape: a morph target in one region of the head, switched fully on.
struct LookMorphData
{
    std::string target;
    std::string region;
};

/// One appearance of a game .app file: the game's own parts for an option value (hair, beard, body, ...).
struct LookAppData
{
    std::string file;
    std::string appearance;
};

struct LookData
{
    std::string body = "m";
    std::vector<LookPartData> parts;
    std::vector<LookMorphData> morphs;
    /// The look's parts that are built at spawn (hair, beard, piercings, body, ...), as appearances of game files: the
    /// engine extracts the real parts from these (LookParts); `parts` lists the same parts by name, kind and binding.
    std::vector<LookAppData> apps;
};

class LookTable
{
public:
    static LookTable& Get();

    /// Whether the table loaded (it ships next to the plugin).
    bool IsLoaded();

    /// The changes for a stored look. Options the table doesn't know are skipped (and logged once). So are options a
    /// switcher can turn on that the look's switchers don't (e.g. hair_color38, the default hairstyle's colour, still
    /// listed after picking hairstyle 51). The body comes in as the synthetic option body_for_skin, the look's
    /// skin_color (0 when it has none), since stored looks carry no body_color (CYB-00032).
    LookData Resolve(const std::string& look);

    /// Everything the table can change on a body ("m" or "f"), to clear what a puppet had before the look: every mesh
    /// part name (animated parts have nothing to hide, so they're left out) and every face shape.
    LookData ResetList(const std::string& body);

    /// Every file the parts built at spawn need in memory before a puppet is built: the .app files of all options with
    /// "app" lines (about 480: hair, beard, piercings, body, ...) and the rig and animation graph of every hairstyle's
    /// skeleton (about 100), all small. The plugin loads them up
    /// front: a skeleton whose files aren't in memory when the engine sets the part up never follows the head.
    std::vector<std::string> HairFiles();

private:
    struct Value
    {
        std::vector<LookPartData> parts;
        std::vector<LookMorphData> morphs;
    };

    void LoadOnce();
    bool Load(const std::wstring& path);

    std::once_flag m_loaded;
    bool m_ok = false;
    // "<body>\t<option>\t<index>" -> what that value changes.
    std::unordered_map<std::string, Value> m_values;
    std::map<std::string, std::set<std::string>> m_parts;                       // body -> mesh part names
    std::map<std::string, std::set<std::pair<std::string, std::string>>> m_morphs; // body -> (target, region)
    // "<body>\t<switcher>\t<index>" -> the options that value turns on; body -> every option some switcher turns on.
    std::unordered_map<std::string, std::vector<std::string>> m_switches;
    std::map<std::string, std::set<std::string>> m_switched;
    std::unordered_map<std::string, LookAppData> m_apps; // "<body>\t<option>\t<index>" -> its .app appearance
    std::set<std::string> m_hairFiles;
    std::mutex m_unknownMutex;
    std::set<std::string> m_unknownLogged;
};
} // namespace Cyberverse::PlayerSync
