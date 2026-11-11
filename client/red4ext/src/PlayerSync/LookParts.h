#pragma once
// The game's own hair parts for another player's V (CYB-00029). A hair option value is one appearance of a game .app
// file; the engine's package extractor turns the parts stored in it into live components, with every field and id the
// game gave them. The plugin only renames them (so they can't clash with what the puppet's own customization
// controllers make from the local player's look) and hands them to the script, which adds them while the engine
// builds the puppet (RemoteLookSpawn in RemoteLook.reds).
#include <RED4ext/RED4ext.hpp>
#include <RedLib.hpp>

#include "PlayerSync/LookTable.h"

#include <mutex>
#include <string>
#include <vector>

namespace Cyberverse::PlayerSync
{
class LookParts
{
public:
    /// The prefix of every part we add to a puppet.
    static constexpr const char* Prefix = "cyberverse_";

    static LookParts& Get();

    /// Starts loading every hair file of the look table and keeps them in memory. Once, when the plugin connects.
    void Preload();

    /// Starts loading the stored parts of a look's hair appearances, so Extract finds them ready. Any thread.
    void Prepare(const LookData& look);

    /// The look's hair as live parts, renamed with Prefix (bindings between them follow). Waits (up to 2 s per file)
    /// for anything Preload and Prepare haven't brought in yet. Any thread; the parts belong to no entity yet.
    Red::DynArray<Red::Handle<Red::IScriptable>> Extract(const LookData& look);

private:
    std::mutex m_mutex;
    bool m_preloaded = false;
    std::vector<RED4ext::SharedPtr<RED4ext::ResourceToken<>>> m_files;
};
} // namespace Cyberverse::PlayerSync
