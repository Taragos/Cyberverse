#include "LookTable.h"

#include "Main.h"

#include <RED4ext/RED4ext.hpp>

#include <windows.h>

#include <filesystem>
#include <fstream>
#include <sstream>
#include <vector>

namespace Cyberverse::PlayerSync
{
namespace
{
std::vector<std::string> Split(const std::string& text, const char separator)
{
    std::vector<std::string> parts;
    std::string current;
    std::istringstream stream(text);
    while (std::getline(stream, current, separator))
    {
        parts.push_back(current);
    }
    return parts;
}

/// The folder this plugin's DLL is in (red4ext/plugins/Cyberverse).
std::filesystem::path PluginFolder()
{
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(&PluginFolder), &module))
    {
        return {};
    }
    wchar_t path[MAX_PATH];
    const auto length = GetModuleFileNameW(module, path, MAX_PATH);
    if (length == 0 || length >= MAX_PATH)
    {
        return {};
    }
    return std::filesystem::path(path).parent_path();
}
} // namespace

LookTable& LookTable::Get()
{
    static LookTable table;
    return table;
}

void LookTable::LoadOnce()
{
    std::call_once(m_loaded, [this] {
        const auto path = PluginFolder() / "look-table.tsv";
        m_ok = Load(path.wstring());
    });
}

bool LookTable::IsLoaded()
{
    LoadOnce();
    return m_ok;
}

bool LookTable::Load(const std::wstring& path)
{
    std::ifstream file(std::filesystem::path(path), std::ios::binary);
    if (!file)
    {
        SDK->logger->Warn(PLUGIN, "No look table next to the plugin (look-table.tsv); other players keep a default V");
        return false;
    }

    std::string line;
    std::string header;
    size_t lines = 0;
    while (std::getline(file, line))
    {
        if (!line.empty() && line.back() == '\r')
        {
            line.pop_back();
        }
        if (line.empty())
        {
            continue;
        }
        if (line[0] == '#')
        {
            if (header.empty())
            {
                header = line;
            }
            continue;
        }

        // <body> TAB <option> TAB <index> TAB <mesh|morph|switch|app> TAB <data>; getline drops an empty last field (a
        // switcher value that turns nothing on).
        auto fields = Split(line, '\t');
        if (fields.size() == 4 && fields[3] == "switch")
        {
            fields.emplace_back();
        }
        if (fields.size() != 5)
        {
            continue;
        }
        const auto& body = fields[0];
        const auto key = body + "\t" + fields[1] + "\t" + fields[2];
        if (fields[3] == "switch")
        {
            auto targets = fields[4].empty() ? std::vector<std::string>{} : Split(fields[4], ',');
            m_switched[body].insert(targets.begin(), targets.end());
            m_switches[key] = std::move(targets);
        }
        else if (fields[3] == "mesh")
        {
            Value value;
            for (const auto& part : Split(fields[4], ';'))
            {
                // <kind>|<component>|<file>|<appearance>|<skinned to>|<hangs from>; getline drops a trailing empty
                // field, so 5 fields means "hangs from" is empty.
                auto values = Split(part, '|');
                if (values.size() == 5)
                {
                    values.emplace_back();
                }
                if (values.size() != 6)
                {
                    continue;
                }
                if (values[0] != "A")
                {
                    m_parts[body].insert(values[1]); // animated parts (hair_dangle) have nothing to hide
                }
                else
                {
                    m_hairFiles.insert(values[2]); // rig
                    if (!values[3].empty())
                    {
                        m_hairFiles.insert(values[3]); // animation graph
                    }
                }
                value.parts.push_back({values[0], values[1], values[2], values[3], values[4], values[5]});
            }
            m_values[key] = std::move(value);
        }
        else if (fields[3] == "app")
        {
            // <.app file>|<appearance name>
            const auto separator = fields[4].find('|');
            if (separator == std::string::npos)
            {
                continue;
            }
            m_apps[key] = {fields[4].substr(0, separator), fields[4].substr(separator + 1)};
            m_hairFiles.insert(m_apps[key].file);
        }
        else if (fields[3] == "morph")
        {
            // <target>|<region>
            const auto separator = fields[4].find('|');
            if (separator == std::string::npos)
            {
                continue;
            }
            LookMorphData morph{fields[4].substr(0, separator), fields[4].substr(separator + 1)};
            m_morphs[body].insert({morph.target, morph.region});
            m_values[key] = Value{{}, {std::move(morph)}};
        }
        else
        {
            continue;
        }
        ++lines;
    }

    SDK->logger->InfoF(PLUGIN, "Look table loaded: %zu values (%s)", lines, header.c_str());
    return lines > 0;
}

LookData LookTable::Resolve(const std::string& look)
{
    LoadOnce();
    LookData result;
    if (!m_ok)
    {
        return result;
    }

    std::vector<std::pair<std::string, std::string>> options;
    for (const auto& pair : Split(look, ' '))
    {
        const auto equals = pair.find('=');
        if (equals == std::string::npos)
        {
            continue; // "v1"
        }
        auto name = pair.substr(0, equals);
        auto value = pair.substr(equals + 1);
        if (name == "body")
        {
            result.body = value == "f" ? "f" : "m";
        }
        else
        {
            options.emplace_back(std::move(name), std::move(value));
        }
    }
    const auto& body = result.body;

    // Which options are in use: those no switcher controls, plus whatever the switchers in use turn on (switchers can
    // chain: beard=3 turns on beard3, which turns on beard_color3_0). The look keeps stale values of options its
    // switchers turned off again; those must not show.
    static const std::set<std::string> none;
    const auto switchedIt = m_switched.find(body);
    const auto& switched = switchedIt != m_switched.end() ? switchedIt->second : none;
    std::set<std::string> active;
    for (const auto& [name, value] : options)
    {
        if (!switched.contains(name))
        {
            active.insert(name);
        }
    }
    for (bool grew = true; grew;)
    {
        grew = false;
        for (const auto& [name, value] : options)
        {
            if (!active.contains(name))
            {
                continue;
            }
            const auto targets = m_switches.find(body + "\t" + name + "\t" + value);
            if (targets == m_switches.end())
            {
                continue;
            }
            for (const auto& target : targets->second)
            {
                grew |= active.insert(target).second;
            }
        }
    }

    for (const auto& [name, value] : options)
    {
        const auto key = body + "\t" + name + "\t" + value;
        if (!active.contains(name) || m_switches.contains(key))
        {
            continue; // stale (no switcher in use turns it on), or a switcher (done above)
        }
        const auto found = m_values.find(key);
        if (found != m_values.end())
        {
            result.parts.insert(result.parts.end(), found->second.parts.begin(), found->second.parts.end());
            result.morphs.insert(result.morphs.end(), found->second.morphs.begin(), found->second.morphs.end());
            if (const auto app = m_apps.find(key); app != m_apps.end())
            {
                result.apps.push_back(app->second);
            }
            continue;
        }
        std::lock_guard lock(m_unknownMutex);
        if (m_unknownLogged.insert(body + " " + name).second)
        {
            SDK->logger->InfoF(PLUGIN, "Look table: nothing for %s=%s (body %s)", name.c_str(), value.c_str(),
                               body.c_str());
        }
    }
    return result;
}

std::vector<std::string> LookTable::HairFiles()
{
    LoadOnce();
    return {m_hairFiles.begin(), m_hairFiles.end()};
}

LookData LookTable::ResetList(const std::string& body)
{
    LoadOnce();
    LookData result;
    result.body = body;
    if (const auto parts = m_parts.find(body); parts != m_parts.end())
    {
        for (const auto& part : parts->second)
        {
            result.parts.push_back({"", part, "", "", "", ""});
        }
    }
    if (const auto morphs = m_morphs.find(body); morphs != m_morphs.end())
    {
        for (const auto& [target, region] : morphs->second)
        {
            result.morphs.push_back({target, region});
        }
    }
    return result;
}
} // namespace Cyberverse::PlayerSync
