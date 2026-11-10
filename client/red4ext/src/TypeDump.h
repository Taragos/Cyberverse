#pragma once
// Development aid: logs what the game's RTTI says about some classes (parent, fields, methods with their parameter and
// return types, derived classes). Start the game with --cyberverse-dump-types=ClassA,ClassB; the lines go to this
// plugin's RED4ext log once the main menu runs. Used to find the character customization API (CYB-00009), whose
// methods aren't in the SDK headers or on the web.

#include "Main.h"

#include <RED4ext/RED4ext.hpp>
#include <RED4ext/RTTISystem.hpp>
#include <RED4ext/RTTITypes.hpp>
#include <RED4ext/Scripting/CProperty.hpp>
#include <RED4ext/Scripting/Functions.hpp>

#include <sstream>
#include <string>

namespace Cyberverse::TypeDump
{
inline std::string TypeName(const RED4ext::rtti::IType* type)
{
    return type ? type->GetName().ToString() : "Void";
}

inline std::string Signature(const RED4ext::CBaseFunction* function)
{
    std::ostringstream line;
    line << (function->flags.isStatic ? "static " : "") << (function->flags.isNative ? "native " : "")
         << (function->flags.isEvent ? "cb " : "") << function->shortName.ToString() << "(";
    bool first = true;
    for (const auto* param : function->params)
    {
        line << (first ? "" : ", ") << (param->flags.isOut ? "out " : "") << (param->flags.isOptional ? "opt " : "")
             << param->name.ToString() << ": " << TypeName(param->type);
        first = false;
    }
    line << ") -> " << (function->returnType ? TypeName(function->returnType->type) : "Void");
    return line.str();
}

inline void DumpClass(const std::string& name)
{
    auto* rtti = RED4ext::CRTTISystem::Get();
    auto* type = rtti->GetClass(RED4ext::CName(name.c_str()));
    if (!type)
    {
        SDK->logger->InfoF(PLUGIN, "[types] %s: no such class", name.c_str());
        return;
    }

    SDK->logger->InfoF(PLUGIN, "[types] class %s extends %s", name.c_str(),
                       type->parent ? type->parent->GetName().ToString() : "-");
    for (const auto* prop : type->props)
    {
        SDK->logger->InfoF(PLUGIN, "[types]   let %s: %s", prop->name.ToString(), TypeName(prop->type).c_str());
    }
    for (const auto* function : type->funcs)
    {
        SDK->logger->InfoF(PLUGIN, "[types]   %s", Signature(function).c_str());
    }
    for (const auto* function : type->staticFuncs)
    {
        SDK->logger->InfoF(PLUGIN, "[types]   %s", Signature(function).c_str());
    }

    RED4ext::DynArray<RED4ext::CClass*> derived;
    rtti->GetDerivedClasses(type, derived);
    for (const auto* child : derived)
    {
        SDK->logger->InfoF(PLUGIN, "[types]   derived: %s", child->GetName().ToString());
    }
}

/// Dumps each class of a comma-separated list.
inline void DumpClasses(const std::string& list)
{
    std::stringstream names(list);
    std::string name;
    while (std::getline(names, name, ','))
    {
        if (!name.empty())
        {
            DumpClass(name);
        }
    }
}
} // namespace Cyberverse::TypeDump
