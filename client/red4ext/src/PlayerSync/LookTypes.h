#pragma once
// Script-side view of a resolved look (CYB-00029): what LookTable returns, as objects RemoteLook.reds reads directly.
#include <RED4ext/RED4ext.hpp>
#include <RedLib.hpp>

#include "PlayerSync/LookTable.h"

namespace Cyberverse::PlayerSync
{
/// One part a look puts on V; see LookPartData. Empty strings for fields that don't apply.
struct LookPart : Red::IScriptable
{
    Red::CString kind;
    Red::CString name;
    Red::CString file;
    Red::CString appearance;
    Red::CString skinnedTo;
    Red::CString hangsFrom;

    RTTI_IMPL_TYPEINFO(Cyberverse::PlayerSync::LookPart);
    RTTI_IMPL_ALLOCATOR();
};

/// A face shape: a morph target in one region of the head.
struct LookMorph : Red::IScriptable
{
    Red::CString target;
    Red::CString region;

    RTTI_IMPL_TYPEINFO(Cyberverse::PlayerSync::LookMorph);
    RTTI_IMPL_ALLOCATOR();
};

/// A resolved look, or (from LookResetList) everything the table can change on a body. `loaded` is false when the
/// plugin has no look table.
struct LookChanges : Red::IScriptable
{
    bool loaded = false;
    Red::CString body;
    Red::DynArray<Red::Handle<LookPart>> parts;
    Red::DynArray<Red::Handle<LookMorph>> morphs;

    static Red::Handle<LookChanges> From(const LookData& data, bool loaded)
    {
        auto changes = Red::MakeHandle<LookChanges>();
        changes->loaded = loaded;
        changes->body = data.body.c_str();
        for (const auto& source : data.parts)
        {
            auto part = Red::MakeHandle<LookPart>();
            part->kind = source.kind.c_str();
            part->name = source.name.c_str();
            part->file = source.file.c_str();
            part->appearance = source.appearance.c_str();
            part->skinnedTo = source.skinnedTo.c_str();
            part->hangsFrom = source.hangsFrom.c_str();
            changes->parts.PushBack(part);
        }
        for (const auto& source : data.morphs)
        {
            auto morph = Red::MakeHandle<LookMorph>();
            morph->target = source.target.c_str();
            morph->region = source.region.c_str();
            changes->morphs.PushBack(morph);
        }
        return changes;
    }

    RTTI_IMPL_TYPEINFO(Cyberverse::PlayerSync::LookChanges);
    RTTI_IMPL_ALLOCATOR();
};
} // namespace Cyberverse::PlayerSync

RTTI_DEFINE_CLASS(Cyberverse::PlayerSync::LookPart, {
    RTTI_ALIAS("Cyberverse.Characters.LookPart");
    RTTI_PROPERTY(kind);
    RTTI_PROPERTY(name);
    RTTI_PROPERTY(file);
    RTTI_PROPERTY(appearance);
    RTTI_PROPERTY(skinnedTo);
    RTTI_PROPERTY(hangsFrom);
});

RTTI_DEFINE_CLASS(Cyberverse::PlayerSync::LookMorph, {
    RTTI_ALIAS("Cyberverse.Characters.LookMorph");
    RTTI_PROPERTY(target);
    RTTI_PROPERTY(region);
});

RTTI_DEFINE_CLASS(Cyberverse::PlayerSync::LookChanges, {
    RTTI_ALIAS("Cyberverse.Characters.LookChanges");
    RTTI_PROPERTY(loaded);
    RTTI_PROPERTY(body);
    RTTI_PROPERTY(parts);
    RTTI_PROPERTY(morphs);
});
