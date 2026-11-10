#ifndef NETWORKMANAGERCONTROLLER_H
#define NETWORKMANAGERCONTROLLER_H
#include "RED4ext/Api/v1/Sdk.hpp"
#include "RED4ext/SystemUpdate.hpp"

#include "RED4ext/Scripting/IScriptable.hpp"
#include "RED4ext/Scripting/Natives/Generated/ink/ISystemRequestsHandler.hpp"
#include "RED4ext/Scripting/Stack.hpp"

#include "PlayerActionTracker.h"
#include "PlayerSync/InterpolationData.h"
#include "RED4ext/Scripting/Natives/Generated/AI/Command.hpp"
#include "RED4ext/Scripting/Natives/Generated/Vector4.hpp"
#include "RED4ext/Scripting/Natives/entEntityID.hpp"

#include <RedLib.hpp>
#include <Protocol.h>
#include <map>
#include <mutex>
#include <steam/isteamnetworkingsockets.h>
#include <steam/steamnetworkingtypes.h>

class NetworkGameSystem : public Red::IGameSystem
{
private:
    HSteamNetConnection m_hConnection;
    ISteamNetworkingSockets *m_pInterface;
    bool m_hasTriedToConnect = false;
    bool m_hasEnqueuedLoadLastCheckpoint = false;
    Red::Handle<Red::ink::ISystemRequestsHandler> m_systemRequestsHandler;
    bool m_gameRestored = false;
    std::map<uint64_t, RED4ext::ent::EntityID> m_networkedEntitiesLookup;
    std::map<RED4ext::ent::EntityID, InterpolationData> m_interpolationData;
    std::map<RED4ext::ent::EntityID, RED4ext::Handle<RED4ext::AICommand>> m_LastTeleportCommand;
    float m_TimeSinceLastPlayerPositionSync;
    // Written on a network worker thread, taken by the main menu on the UI thread.
    std::mutex m_loginRejectedMutex;
    std::string m_loginRejectedMessage;

private:
    void OnRegisterUpdates(RED4ext::UpdateRegistrar* aRegistrar) override;
    bool OnGameRestored() override;

private:
    bool ConnectToServer(const std::string& host, uint16_t port);
    static void ConnectionStatusChangedCallback(SteamNetConnectionStatusChangedCallback_t* pInfo);
    void OnNetworkUpdate(RED4ext::FrameInfo& frame_info, RED4ext::JobQueue& job_queue);
    void InterpolatePuppets(float deltaTime);
    void SetEntityPosition(RED4ext::ent::EntityID entityId, RED4ext::Vector4 worldPosition, float yaw);

protected:
    void PollIncomingMessages();
    void TrackPlayerPosition(float deltaTime);

public:
    bool FullyConnected = false;
    Red::Handle<PlayerActionTracker> playerActionTracker = Red::Handle(new PlayerActionTracker());

    /// This is called by Redscript when the connection wasn't established as the UI had loaded the available savegames.
    /// Thus we will _enqueue_ the "LoadLastCheckpoint" call, that Redscript had otherwise done, had we connected fast enough.
    void EnqueueLoadLastCheckpoint(const Red::Handle<RED4ext::ink::ISystemRequestsHandler>& handler)
    {
        m_systemRequestsHandler = handler;
        m_hasEnqueuedLoadLastCheckpoint = true;
    }

    /// Why the server turned the login away, in words for the player, once; afterwards (and before) "". The main menu
    /// polls this from the UI thread, because UI calls made from the network worker threads don't draw anything.
    Red::CString TakeLoginRejectedMessage();

    /// Writes a line from redscript into this plugin's RED4ext log.
    void Log(const Red::CString& message);

    /// Sends a packet from shared/protocol/Protocol.h; its channel and reliability come from the schema.
    template<typename T>
    bool EnqueueMessage(const T& packet);

    /// Called from the plugin load and unload events
    static bool Load();
    /// Called from the plugin load and unload events
    static void Unload();
private:
    RTTI_IMPL_TYPEINFO(NetworkGameSystem);
    RTTI_IMPL_ALLOCATOR();
};

RTTI_DEFINE_CLASS(NetworkGameSystem, {
    RTTI_METHOD(EnqueueLoadLastCheckpoint);
    RTTI_METHOD(TakeLoginRejectedMessage);
    RTTI_METHOD(Log);
    RTTI_PROPERTY(FullyConnected);
    RTTI_PROPERTY(playerActionTracker);
    RTTI_ALIAS("Cyberverse.Network.Managers.NetworkGameSystem");
});

// EnqueueMessage is defined in NetworkGameSystem.cpp, so the packet types sent from other files are instantiated there
// (see the end of EnqueueMessage) and only declared here. Add a line in both places when another file sends a new packet.
// TODO: Think about the concept of having EnqueueMessage public; maybe inline it instead.
extern template bool NetworkGameSystem::EnqueueMessage(const PlayerActionTracked& packet);
extern template bool NetworkGameSystem::EnqueueMessage(const PlayerSpawnCar& packet);
extern template bool NetworkGameSystem::EnqueueMessage(const PlayerUnmountCar& packet);
extern template bool NetworkGameSystem::EnqueueMessage(const PlayerEquipItem& packet);
extern template bool NetworkGameSystem::EnqueueMessage(const PlayerShoot& packet);

#endif //NETWORKMANAGERCONTROLLER_H
