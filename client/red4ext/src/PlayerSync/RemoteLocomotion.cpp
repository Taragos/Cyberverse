#include "RemoteLocomotion.h"

#include "CompleteTypes.h"

#include "Main.h"
#include "Utils.h"

#include <RED4ext/Scripting/Natives/Generated/anim/AnimFeature_Locomotion.hpp>
#include <RED4ext/Scripting/Natives/Generated/anim/Locomotion_Style.hpp>
#include <RED4ext/Scripting/Natives/Generated/anim/MotionTableAction.hpp>

#include <algorithm>
#include <cmath>
#include <initializer_list>

namespace Cyberverse::PlayerSync
{
namespace
{
using Style = RED4ext::anim::Locomotion_Style;
using Action = RED4ext::anim::MotionTableAction;

constexpr auto Idle = static_cast<int32_t>(Style::LS_Idle);
constexpr auto Walk = static_cast<int32_t>(Style::LS_Walk);
constexpr auto Jog = static_cast<int32_t>(Style::LS_Jog);
constexpr auto Sprint = static_cast<int32_t>(Style::LS_Sprint);

/// No new position for this long means the player stands still.
constexpr float StaleAfter = 0.3f;
/// Below this network speed the legs stop at once, so they stop with the body.
constexpr float StopSpeed = 0.2f;
/// While moving, the speed is smoothed over about this long (per-packet speeds jump by +-1 m/s).
constexpr float SmoothSeconds = 0.25f;
/// The cycle length when the puppet has no animation under the expected names (no NPC tried so far had a jog_0).
constexpr float FallbackCycle = 0.8f;

/// The style for a smoothed speed. A style only changes once the speed is clearly past the line, so it doesn't
/// flicker when the speed hovers around a threshold.
int32_t NextStyle(const int32_t style, const float speed)
{
    switch (style)
    {
    case Idle: return speed > 0.6f ? (speed > 2.8f ? Jog : Walk) : Idle;
    case Walk: return speed < 0.3f ? Idle : speed > 2.8f ? Jog : Walk;
    case Jog: return speed < 2.2f ? Walk : speed > 5.8f ? Sprint : Jog;
    case Sprint: return speed < 5.0f ? Jog : Sprint;
    default: return Idle;
    }
}

/// The speed at which a style's animation matches the ground; the step cycle plays faster or slower around it.
float ReferenceSpeed(const int32_t style)
{
    return style == Walk ? 1.6f : style == Jog ? 3.8f : 6.5f;
}

/// The puppet's animation length under the first of the names it knows; -1 when none.
float AnimationLength(Red::IScriptable* system, const Red::Handle<Red::Entity>& entity,
                      std::initializer_list<const char*> names)
{
    for (const auto* name : names)
    {
        float length = -1.0f;
        Red::CallVirtual(system, "AnimationLength", length, entity, Red::CName(name));
        if (length > 0.0f)
        {
            return length;
        }
    }
    return -1.0f;
}
} // namespace

void RemoteLocomotion::OnNetworkPosition(const RED4ext::ent::EntityID& entityId, const RED4ext::Vector3& position,
                                         const float now)
{
    auto& state = m_states[entityId];
    if (state.lastTime >= 0.0f)
    {
        const auto dx = position.X - state.lastPosition.X;
        const auto dy = position.Y - state.lastPosition.Y;
        state.networkSpeed = std::sqrt(dx * dx + dy * dy) / std::max(0.01f, now - state.lastTime);
    }
    state.lastPosition = position;
    state.lastTime = now;
}

void RemoteLocomotion::Forget(const RED4ext::ent::EntityID& entityId)
{
    m_states.erase(entityId);
}

void RemoteLocomotion::Tick(Red::IScriptable* system, const float deltaTime, const float now)
{
    for (auto& [entityId, state] : m_states)
    {
        const auto entity = Utils::GetDynamicEntity(entityId);
        if (!entity.has_value())
        {
            continue;
        }

        if (!state.lengthsKnown)
        {
            state.lengthsKnown = true;
            state.walkLength = AnimationLength(system, entity.value(), {"walk_0"});
            state.jogLength = AnimationLength(system, entity.value(), {"jog_0", "run_0"});
            state.sprintLength = AnimationLength(system, entity.value(), {"sprint_0"});
            state.startLength = AnimationLength(system, entity.value(), {"idle_to_walk_0"});
            // -1 everywhere means the puppet has none of the NPC locomotion animations (CYB-00032).
            SDK->logger->InfoF(PLUGIN, "Remote locomotion for %llu: walk %.2f s, jog %.2f s, sprint %.2f s, start %.2f s",
                               entityId.hash, state.walkLength, state.jogLength, state.sprintLength,
                               state.startLength);
        }

        const auto target = now - state.lastTime > StaleAfter ? 0.0f : state.networkSpeed;
        if (target < StopSpeed)
        {
            state.speed = 0.0f;
        }
        else
        {
            state.speed += (target - state.speed) * std::min(1.0f, deltaTime / SmoothSeconds);
        }

        const auto style = NextStyle(state.style, state.speed);
        if (style != state.style)
        {
            if (state.style == Idle)
            {
                // Leaving idle: the motion table plays its start animation.
                state.action = static_cast<int32_t>(Action::MTA_Start);
                state.transitionLeft = state.startLength > 0.0f ? std::min(state.startLength, 0.6f) : 0.3f;
                state.transitionTime = 0.0f;
                state.cycle = 0.0f;
            }
            else if (style == Idle)
            {
                // Stopping: a short stop; the game's walk_to_idle_0 (1.8 s) is far longer than the body takes.
                state.action = static_cast<int32_t>(Action::MTA_Stop);
                state.transitionLeft = 0.3f;
                state.transitionTime = 0.0f;
            }
            if (style != Idle)
            {
                state.movingStyle = style;
            }
            state.style = style;
        }

        auto feature = Red::MakeScriptedHandle<RED4ext::anim::AnimFeature_Locomotion>();
        if (!feature)
        {
            return;
        }

        if (state.transitionLeft > 0.0f)
        {
            state.transitionLeft -= deltaTime;
            state.transitionTime += deltaTime;
            feature->action = state.action;
            feature->style = state.action == static_cast<int32_t>(Action::MTA_Stop) ? state.movingStyle : state.style;
            feature->animationPlaybackTime = state.transitionTime;
        }
        else if (state.style == Idle)
        {
            feature->action = static_cast<int32_t>(Action::MTA_None);
            feature->style = Idle;
        }
        else
        {
            const auto known = state.style == Walk ? state.walkLength : state.style == Jog ? state.jogLength
                                                                                           : state.sprintLength;
            const auto length = known > 0.0f ? known : FallbackCycle;
            const auto rate = std::clamp(state.speed / ReferenceSpeed(state.style), 0.5f, 1.8f);
            state.cycle = std::fmod(state.cycle + deltaTime * rate / length, 1.0f);
            feature->action = static_cast<int32_t>(Action::MTA_Move);
            feature->style = state.style;
            feature->animationPlaybackTime = state.cycle * length;
        }
        feature->footScaleFactor = 1.0f;
        feature->speedProgress = 1.0f;

        const auto base = *reinterpret_cast<Red::Handle<RED4ext::anim::AnimFeature>*>(&feature);
        Red::CallVirtual(system, "ApplyLocomotion", entity.value(), base);
    }
}
} // namespace Cyberverse::PlayerSync
