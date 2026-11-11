#include "LookParts.h"

#include "Main.h"

#include <RED4ext/Package.hpp>
#include <RED4ext/ResourceLoader.hpp>

#include <RED4ext/CNamePool.hpp>

#include <chrono>
#include <unordered_set>

namespace Cyberverse::PlayerSync
{
namespace
{
// The .app types (appearanceAppearanceResource, appearanceAppearanceDefinition) are read through the game's type
// information: their SDK headers pull in many types the SDK leaves incomplete.
using AppToken = RED4ext::SharedPtr<RED4ext::ResourceToken<>>;
constexpr auto WaitLimit = std::chrono::milliseconds(2000);

template<typename T>
T* FieldOf(RED4ext::CClass* type, void* instance, const char* name)
{
    auto* property = type ? type->GetProperty(name) : nullptr;
    return property ? property->GetValuePtr<T>(instance) : nullptr;
}

/// The stored parts of the named appearance of a loaded .app; null if the file isn't loaded or has no such appearance.
RED4ext::DeferredDataBuffer* StoredParts(const AppToken& token, const std::string& name)
{
    if (!token || !token->IsLoaded() || !token->resource)
    {
        return nullptr;
    }
    auto* resource = token->resource.GetPtr();
    auto* appearances =
        FieldOf<RED4ext::DynArray<Red::Handle<Red::ISerializable>>>(resource->GetType(), resource, "appearances");
    if (!appearances)
    {
        return nullptr;
    }
    const RED4ext::CName wanted(name.c_str());
    for (const auto& definition : *appearances)
    {
        if (!definition)
        {
            continue;
        }
        auto* type = definition->GetType();
        const auto* definitionName = FieldOf<RED4ext::CName>(type, definition.GetPtr(), "name");
        if (definitionName && *definitionName == wanted)
        {
            return FieldOf<RED4ext::DeferredDataBuffer>(type, definition.GetPtr(), "compiledData");
        }
    }
    return nullptr;
}

AppToken Request(const std::string& file)
{
    auto* loader = RED4ext::ResourceLoader::Get();
    return loader ? loader->LoadAsync(RED4ext::ResourcePath(file.c_str())) : AppToken{};
}

/// The name with our prefix, registered with the game's name pool (a CName built from text alone is only a hash).
RED4ext::CName Renamed(const RED4ext::CName name)
{
    return RED4ext::CNamePool::Add((std::string(LookParts::Prefix) + name.ToString()).c_str());
}

/// A binding's target name (skinning, parentTransform, controlBinding), or null if the part has no such binding.
RED4ext::CName* BindNameOf(RED4ext::CClass* type, void* part, const char* field)
{
    auto* binding = FieldOf<Red::Handle<Red::ISerializable>>(type, part, field);
    if (!binding || !*binding)
    {
        return nullptr;
    }
    return FieldOf<RED4ext::CName>((*binding)->GetType(), binding->GetPtr(), "bindName");
}
} // namespace

LookParts& LookParts::Get()
{
    static LookParts parts;
    return parts;
}

void LookParts::Preload()
{
    std::lock_guard lock(m_mutex);
    if (m_preloaded)
    {
        return;
    }
    m_preloaded = true;
    if (auto* loader = RED4ext::ResourceLoader::Get())
    {
        for (const auto& file : LookTable::Get().HairFiles())
        {
            m_files.push_back(loader->LoadAsync(RED4ext::ResourcePath(file.c_str())));
        }
    }
    SDK->logger->InfoF(PLUGIN, "Loading %zu hair files", m_files.size());
}

void LookParts::Prepare(const LookData& look)
{
    for (const auto& app : look.apps)
    {
        auto* stored = StoredParts(Request(app.file), app.appearance);
        if (stored && stored->state == RED4ext::DeferredDataBufferState::Unloaded)
        {
            stored->LoadAsync();
        }
    }
}

Red::DynArray<Red::Handle<Red::IScriptable>> LookParts::Extract(const LookData& look)
{
    Red::DynArray<Red::Handle<Red::IScriptable>> parts;
    auto* componentType = Red::GetClass("entIComponent");
    for (const auto& app : look.apps)
    {
        auto token = Request(app.file);
        if (token && !token->IsFinished())
        {
            SDK->logger->WarnF(PLUGIN, "Hair file %s wasn't loaded yet; waiting", app.file.c_str());
            Red::WaitForResource(token, WaitLimit);
        }
        auto* storedParts = StoredParts(token, app.appearance);
        if (!storedParts)
        {
            SDK->logger->WarnF(PLUGIN, "No appearance %s in %s; that hair is left out", app.appearance.c_str(),
                               app.file.c_str());
            continue;
        }

        auto& stored = *storedParts;
        if (stored.state != RED4ext::DeferredDataBufferState::Loaded || !stored.raw)
        {
            SDK->logger->WarnF(PLUGIN, "Stored parts of %s (%s) weren't loaded yet; waiting", app.appearance.c_str(),
                               app.file.c_str());
            if (auto loading = stored.LoadAsync())
            {
                Red::WaitForJob(loading->job, WaitLimit);
            }
        }
        if (stored.state != RED4ext::DeferredDataBufferState::Loaded || !stored.raw || !stored.raw->data)
        {
            SDK->logger->WarnF(PLUGIN, "Stored parts of %s (%s) didn't load; that hair is left out",
                               app.appearance.c_str(), app.file.c_str());
            continue;
        }

        // The engine's own way from stored parts to live ones.
        RED4ext::ObjectPackageReader reader(stored);
        reader.ReadHeader();
        if (reader.header.IsEmpty())
        {
            continue;
        }
        RED4ext::ObjectPackageExtractor extractor{RED4ext::ObjectPackageExtractorParams(reader.header)};
        extractor.ExtractSync();

        // Rename, and let the bindings between these parts follow (a mesh skinned to this hairstyle's hair_dangle).
        // Bindings to parts of the puppet itself ("root") stay.
        std::vector<Red::Handle<Red::IScriptable>> extracted;
        std::unordered_set<uint64_t> names;
        for (const auto& object : extractor.results)
        {
            if (!object || !componentType || !object->GetType()->IsA(componentType))
            {
                continue;
            }
            if (auto* name = FieldOf<RED4ext::CName>(object->GetType(), object.GetPtr(), "name"))
            {
                names.insert(name->hash);
                extracted.push_back(Red::Cast<Red::IScriptable>(object));
            }
        }
        for (const auto& part : extracted)
        {
            auto* type = part->GetType();
            for (const char* field : {"skinning", "parentTransform", "controlBinding"})
            {
                if (auto* bindName = BindNameOf(type, part.GetPtr(), field); bindName && names.contains(bindName->hash))
                {
                    *bindName = Renamed(*bindName);
                }
            }
        }
        for (const auto& part : extracted)
        {
            auto* name = FieldOf<RED4ext::CName>(part->GetType(), part.GetPtr(), "name");
            *name = Renamed(*name);
            parts.PushBack(part);
        }
    }
    return parts;
}
} // namespace Cyberverse::PlayerSync
