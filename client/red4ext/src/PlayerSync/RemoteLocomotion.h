#pragma once
// Walk, jog and sprint animations for remote players' puppets (CYB-00010). We set the puppet's position ourselves (see
// NetworkGameSystem::SetEntityPosition), so the NPC's own locomotion never runs and it would slide in its idle pose.
// Instead, every frame, each puppet gets the game's native AnimFeature_Locomotion (motion table action and style plus
// the playback time of the step cycle), which its animation graph turns into legs.
//
// The speed and movement state come from the owner's game (EntityMovement): sprint shows as sprint whatever the
// speed, walk and jog are told apart by the speed. The velocity against the facing picks forward, backward, strafe
// left or right (motion table actions); only the walk style has those. Remote V plays the player's own locomotion
// moved to the NPC skeleton (tools/remote-puppet: retarget_anims.py), with its crouch animations switched in by
// NetworkGameSystem.ApplyStance; behind it the NPC gang set.
#include <RED4ext/RED4ext.hpp>
#include <RED4ext/Scripting/Natives/entEntityID.hpp>
#include <RedLib.hpp>

#include <map>
#include <string>

namespace Cyberverse::PlayerSync
{
class RemoteLocomotion
{
public:
    /// The puppet's movement as shown this frame (see RemoteMotion): speed in m/s, state an EMovementState, yaw in
    /// degrees, and the horizontal velocity (0, 0 when unknown), which tells forward, backward and sideways apart.
    void SetMovement(const RED4ext::ent::EntityID& entityId, float speed, uint8_t movementState, float yaw, float vx,
                     float vy);

    /// Advances every puppet's animation and applies it. `system` is the NetworkGameSystem, whose script methods
    /// ApplyLocomotion, ApplyStance, ApplyRun and AnimationLength do the engine calls.
    void Tick(Red::IScriptable* system, float deltaTime);

    void Forget(const RED4ext::ent::EntityID& entityId);

private:
    struct State
    {
        float networkSpeed = 0.0f; // m/s, from the owner
        uint8_t movementState = 0; // EMovementState, from the owner
        bool crouching = false;    // as last applied to the NPC stance
        bool running = false;      // the run set is switched in (backward or sideways, faster than a walk)
        float sinceSwitch = 10.0f; // seconds since the crouch or run set was last switched
        int32_t loggedStyle = -1;  // what the last log line showed (probe)
        bool loggedCrouching = false;
        bool loggedRunning = false;
        float speed = 0.0f;        // smoothed
        int32_t direction = 0;     // 0 forward, 1 left, 2 backward, 3 right, relative to where the puppet faces
        float yaw = 0.0f;
        float vx = 0.0f;
        float vy = 0.0f;
        std::map<std::string, float> lengths; // animation lengths by the name they were asked under, once known
        int32_t style = 0;         // anim::Locomotion_Style
        int32_t movingStyle = 2;   // the last moving style, for the stop
        float cycle = 0.0f;        // step cycle, 0..1, kept across style changes
        int32_t action = 0;        // anim::MotionTableAction while a start or stop plays
        float transitionLeft = 0.0f;
        float transitionTime = 0.0f;
        // The run set is switched on for a moment once the puppet's animations are there, so the game loads it
        // before it is needed (its idle is the standing set's, so nothing shows): 0 not yet, 1 on, 2 done.
        int32_t warmUp = 0;
        float warmUpLeft = 0.0f;
        bool lengthsKnown = false;
        float walkLength = -1.0f;
        float jogLength = -1.0f;
        float sprintLength = -1.0f;
        float startLength = -1.0f;
    };

    std::map<RED4ext::ent::EntityID, State> m_states;
};
} // namespace Cyberverse::PlayerSync
