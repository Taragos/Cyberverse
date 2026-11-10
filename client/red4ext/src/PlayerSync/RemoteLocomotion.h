#pragma once
// Walk, jog and sprint animations for remote players' puppets (CYB-00010). We set the puppet's position ourselves (see
// NetworkGameSystem::SetEntityPosition), so the NPC's own locomotion never runs and it would slide in its idle pose.
// Instead, every frame, each puppet gets the game's native AnimFeature_Locomotion (motion table action and style plus
// the playback time of the step cycle), which its animation graph turns into legs.
//
// The speed comes from the network positions for now; the movement state from the protocol replaces it later. The
// cycle lengths and reference speeds suit the story NPCs used as puppets today and get tuned on V (CYB-00029).
#include <RED4ext/RED4ext.hpp>
#include <RED4ext/Scripting/Natives/entEntityID.hpp>
#include <RedLib.hpp>

#include <map>

namespace Cyberverse::PlayerSync
{
class RemoteLocomotion
{
public:
    /// A new network position for a puppet; `now` is the plugin's clock in seconds.
    void OnNetworkPosition(const RED4ext::ent::EntityID& entityId, const RED4ext::Vector3& position, float now);

    /// Advances every puppet's animation and applies it. `system` is the NetworkGameSystem, whose script methods
    /// ApplyLocomotion and AnimationLength do the engine calls.
    void Tick(Red::IScriptable* system, float deltaTime, float now);

    void Forget(const RED4ext::ent::EntityID& entityId);

private:
    struct State
    {
        RED4ext::Vector3 lastPosition{};
        float lastTime = -1.0f;
        float networkSpeed = 0.0f; // m/s between the last two network positions, horizontal
        float speed = 0.0f;        // smoothed
        int32_t style = 0;         // anim::Locomotion_Style
        int32_t movingStyle = 2;   // the last moving style, for the stop
        float cycle = 0.0f;        // step cycle, 0..1, kept across style changes
        int32_t action = 0;        // anim::MotionTableAction while a start or stop plays
        float transitionLeft = 0.0f;
        float transitionTime = 0.0f;
        bool lengthsKnown = false;
        float walkLength = -1.0f;
        float jogLength = -1.0f;
        float sprintLength = -1.0f;
        float startLength = -1.0f;
    };

    std::map<RED4ext::ent::EntityID, State> m_states;
};
} // namespace Cyberverse::PlayerSync
