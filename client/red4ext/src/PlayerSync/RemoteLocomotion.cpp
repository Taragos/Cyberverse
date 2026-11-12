#include "RemoteLocomotion.h"

#include "CompleteTypes.h"

#include "Main.h"
#include "Utils.h"

#include <RED4ext/Scripting/Natives/Generated/anim/AnimFeature_Locomotion.hpp>
#include <RED4ext/Scripting/Natives/Generated/anim/Locomotion_Style.hpp>
#include <RED4ext/Scripting/Natives/Generated/anim/MotionTableAction.hpp>

#include <Protocol.h>

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

/// Below this speed the legs stop at once, so they stop with the body.
constexpr float StopSpeed = 0.2f;
/// While moving, the speed is smoothed a little more over about this long (the owner already smooths over 0.1 s).
constexpr float SmoothSeconds = 0.1f;
/// The cycle length when the puppet has no animation under the expected names (no NPC tried so far had a jog_0).
constexpr float FallbackCycle = 0.8f;

/// Faster than this is a jog, and backward or sideways the run set; back to a walk below WalkBelow. V walks at 1.6
/// to 1.8 m/s with walk toggled on and jogs at 3.5 to 4.3 (logs of 2026-11-12); the line was 1.9 before, and a
/// toggled walk kept crossing it.
constexpr float JogAbove = 2.6f;
constexpr float WalkBelow = 2.2f;

/// The style for a smoothed speed. A style only changes once the speed is clearly past the line, so it doesn't
/// flicker when the speed hovers around a threshold. A sprinting owner sprints whatever the speed.
int32_t NextStyle(const int32_t style, const float speed, const bool sprinting)
{
    if (sprinting && speed > StopSpeed)
    {
        return Sprint;
    }
    switch (style)
    {
    case Idle: return speed > 0.6f ? (speed > JogAbove ? Jog : Walk) : Idle;
    case Walk: return speed < 0.3f ? Idle : speed > JogAbove ? Jog : Walk;
    case Jog: return speed < WalkBelow ? Walk : speed > 5.8f ? Sprint : Jog;
    case Sprint: return speed < StopSpeed ? Idle : speed < 5.0f ? Jog : Sprint;
    default: return Idle;
    }
}

/// The speed at which an animation's feet match the ground; the step cycle plays faster or slower around it.
/// Measured on the player's animations (foot speed while on the ground, tools/remote-puppet): walk 1.2 m/s forward
/// and sideways and 0.9 backward, the backward and sideways jogs 2.5, the forward jog 2.3, the crouch walk 1.25
/// forward, 1.15 sideways and 0.8 backward. The sprint is a guess.
float ReferenceSpeed(const int32_t style, const int32_t direction, const bool crouching, const bool running)
{
    if (style == Sprint)
    {
        return 6.5f;
    }
    if (style == Jog)
    {
        return 2.3f;
    }
    if (crouching)
    {
        return direction == 0 ? 1.25f : direction == 2 ? 0.8f : 1.15f;
    }
    if (running)
    {
        return 2.5f;
    }
    return direction == 2 ? 0.9f : 1.2f;
}

/// How much faster or slower than recorded a step cycle may play.
constexpr float MinRate = 0.5f;
constexpr float MaxRate = 2.2f;
/// How long the run set stays switched on after the spawn, for the game to load it.
constexpr float WarmUpSeconds = 1.5f;
/// For this long after a set was switched (crouch, run), the length asked for a name both sets have may still be
/// the other set's (seen in the logs: walk_270 at 4.43 s, the jog's, just after the run set went off).
constexpr float SwitchSettleSeconds = 0.5f;

/// The direction of movement relative to where the puppet faces: 0 forward, 1 left, 2 backward, 3 right. Keeps
/// `current` until the movement is 10 degrees into another quarter, so it doesn't flicker on a diagonal. Assumes yaw
/// 0 faces +Y and a larger yaw turns left (counter-clockwise), as the game's own yaw does; to be checked in game.
int32_t NextDirection(const int32_t current, const float yaw, const float vx, const float vy)
{
    if (vx * vx + vy * vy < 0.25f)
    {
        return current; // too slow to tell
    }
    constexpr float Degrees = 57.2957795f;
    const auto fx = -std::sin(yaw / Degrees);
    const auto fy = std::cos(yaw / Degrees);
    const auto angle = std::atan2(fx * vy - fy * vx, fx * vx + fy * vy) * Degrees; // >0: to the left
    const auto nearest = static_cast<int32_t>(std::lround(angle / 90.0f) + 4) % 4;
    if (nearest == current)
    {
        return current;
    }
    auto off = std::fmod(std::fabs(angle - nearest * 90.0f), 360.0f);
    off = std::min(off, 360.0f - off);
    return off < 35.0f ? nearest : current;
}

/// The name suffix of a direction's animation, as the NPC graph maps the actions: MTA_StrafeLeft plays walk_270,
/// MTA_StrafeRight walk_090, MTA_BackwardMove walk_180 (humanoid.animgraph's switches on locomotion.action).
const char* DirectionSuffix(const int32_t direction)
{
    static constexpr const char* Suffixes[] = {"0", "270", "180", "090"};
    return Suffixes[direction & 3];
}

/// The motion table action for moving in a direction.
int32_t MoveAction(const int32_t direction)
{
    switch (direction)
    {
    case 1: return static_cast<int32_t>(Action::MTA_StrafeLeft);
    case 2: return static_cast<int32_t>(Action::MTA_BackwardMove);
    case 3: return static_cast<int32_t>(Action::MTA_StrafeRight);
    default: return static_cast<int32_t>(Action::MTA_Move);
    }
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

void RemoteLocomotion::SetMovement(const RED4ext::ent::EntityID& entityId, const float speed,
                                   const uint8_t movementState, const float yaw, const float vx, const float vy)
{
    auto& state = m_states[entityId];
    state.networkSpeed = speed;
    state.movementState = movementState;
    state.yaw = yaw;
    state.vx = vx;
    state.vy = vy;
}

void RemoteLocomotion::Forget(const RED4ext::ent::EntityID& entityId)
{
    m_states.erase(entityId);
}

void RemoteLocomotion::Tick(Red::IScriptable* system, const float deltaTime)
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

        // An animation set's data is loaded when one of its animations is first played, and until then that
        // animation is a T-pose. The run set would first play in the middle of a sideways jog, so it is switched on
        // once here, while its idle (the same as the standing one) shows nothing.
        if (state.warmUp == 0 && AnimationLength(system, entity.value(), {"idle_stand"}) > 0.0f)
        {
            state.warmUp = 1;
            state.warmUpLeft = WarmUpSeconds;
            Red::CallVirtual(system, "ApplyRun", entity.value(), true);
        }
        else if (state.warmUp == 1 && (state.warmUpLeft -= deltaTime) <= 0.0f)
        {
            state.warmUp = 2;
            Red::CallVirtual(system, "ApplyRun", entity.value(), state.running);
        }

        const auto crouching = state.movementState == static_cast<uint8_t>(EMovementState::Crouch);
        if (crouching != state.crouching)
        {
            state.crouching = crouching;
            Red::CallVirtual(system, "ApplyStance", entity.value(), crouching);
            state.sinceSwitch = 0.0f;
            state.transitionLeft = 0.0f;
        }
        state.sinceSwitch += deltaTime;

        const auto target = state.networkSpeed;
        if (target < StopSpeed)
        {
            state.speed = 0.0f;
        }
        else
        {
            state.speed += (target - state.speed) * std::min(1.0f, deltaTime / SmoothSeconds);
        }

        const auto sprinting = state.movementState == static_cast<uint8_t>(EMovementState::Sprint);
        // Sprinting is forward only.
        const auto direction = sprinting ? 0 : NextDirection(state.direction, state.yaw, state.vx, state.vy);
        const auto turned = direction != state.direction;
        state.direction = direction;
        // The graph has backward and sideways animations for the walk style only (its jog and sprint switches have
        // no entry for those actions: a T-pose), and the crouch set only replaces the walk and the idle.
        auto style = NextStyle(state.style, state.speed, sprinting);
        if (style != Idle && (direction != 0 || crouching))
        {
            style = Walk;
        }
        // Backward or sideways faster than a walk: the run set puts the player's backward and sideways jogs under
        // the walk names.
        const auto running = style == Walk && !crouching && direction != 0 &&
                             state.speed > (state.running ? WalkBelow : JogAbove);
        if (running != state.running)
        {
            state.running = running;
            Red::CallVirtual(system, "ApplyRun", entity.value(), running || state.warmUp == 1);
            state.sinceSwitch = 0.0f;
        }
        if (style != state.style)
        {
            if (crouching || direction != 0)
            {
                // No start or stop: the crouch set has none, and the sideways ones aren't in every switch.
            }
            else if (state.style == Idle)
            {
                // Leaving idle: the motion table plays its start animation.
                state.action = static_cast<int32_t>(Action::MTA_Start);
                // Short: a long start makes the legs lag behind the body (1 to 2 s on V, 2026-11-11).
                state.transitionLeft = state.startLength > 0.0f ? std::min(state.startLength, 0.3f) : 0.2f;
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
            // The animation the graph picks for this style and direction; its length sets the step cycle. Without a
            // jog animation (an NPC set only), jog plays the sprint animation slower.
            auto shown = state.style;
            std::string name = shown == Walk  ? std::string("walk_") + DirectionSuffix(direction)
                               : shown == Jog ? std::string("jog_0")
                                              : std::string("sprint_0");
            // The length is asked under a name only one set has, where there is one: the crouch and run sets hold
            // their animations under the walk names, and a length asked under such a name can be another set's.
            // Played with a length longer than the animation, the playback time runs past its end (a suspect for
            // the T-pose flashes after a switch). The standing set has the same animations as crouch_walk_<dir>
            // and jog_<dir>.
            const auto ownName = state.crouching ? std::string("crouch_walk_") + DirectionSuffix(direction)
                                 : state.running ? std::string("jog_") + DirectionSuffix(direction)
                                                 : name;
            auto known = -1.0f;
            if (const auto cached = state.lengths.find(ownName); cached != state.lengths.end())
            {
                known = cached->second;
            }
            else
            {
                known = AnimationLength(system, entity.value(), {ownName.c_str(), name.c_str()});
                // A walk name asked just after a switch may still give the other set's length: not kept.
                if (known > 0.0f && (ownName != name || state.sinceSwitch > SwitchSettleSeconds))
                {
                    state.lengths[ownName] = known;
                }
            }
            auto length = known;
            if (length <= 0.0f && shown == Jog && state.sprintLength > 0.0f)
            {
                shown = Sprint;
                length = state.sprintLength;
            }
            if (length <= 0.0f)
            {
                length = FallbackCycle;
            }
            if (turned || shown != state.loggedStyle || state.crouching != state.loggedCrouching ||
                state.running != state.loggedRunning)
            {
                state.loggedStyle = shown;
                state.loggedCrouching = state.crouching;
                state.loggedRunning = state.running;
                SDK->logger->InfoF(PLUGIN, "Remote locomotion for %llu: style %d, direction %d, crouching %d, "
                                           "running %d, animation %s (%.2f s), %.1f m/s",
                                   entityId.hash, shown, direction, state.crouching ? 1 : 0,
                                   state.running ? 1 : 0, name.c_str(), known, state.speed);
            }
            const auto rate = std::clamp(
                state.speed / ReferenceSpeed(shown, direction, crouching, state.running), MinRate, MaxRate);
            state.cycle = std::fmod(state.cycle + deltaTime * rate / length, 1.0f);
            feature->action = MoveAction(direction);
            feature->style = shown;
            feature->animationPlaybackTime = state.cycle * length;
        }
        feature->footScaleFactor = 1.0f;
        // Lets animation sets behind a variable (the crouch set) take over; the graph reads it with the feature.
        feature->areAnimWrappersUnlocked = true;
        feature->speedProgress = 1.0f;

        const auto base = *reinterpret_cast<Red::Handle<RED4ext::anim::AnimFeature>*>(&feature);
        Red::CallVirtual(system, "ApplyLocomotion", entity.value(), base);
    }
}
} // namespace Cyberverse::PlayerSync
