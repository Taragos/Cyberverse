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
#include <vector>
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
    // The browser login the server asked for (LoginRequired), for the main menu; guarded by m_loginRejectedMutex.
    std::string m_loginPrompt;
    // The server we connect to; the session token is saved per server.
    std::string m_serverHost;
    uint16_t m_serverPort = 0;
    // The account's characters from the last CharacterList, for the select popup (CYB-00009).
    std::mutex m_charactersMutex;
    std::vector<CharacterInfo> m_characters;
    int32_t m_maxCharacters = 0;

private:
    void OnRegisterUpdates(RED4ext::UpdateRegistrar* aRegistrar) override;
    bool OnGameRestored() override;

private:
    bool ConnectToServer(const std::string& host, uint16_t port);
    static void ConnectionStatusChangedCallback(SteamNetConnectionStatusChangedCallback_t* pInfo);
    /// Sends InitAuth and LoginWithSession, the latter with the token saved for this server, if any.
    void SendLogin();
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

    /// While the server waits for the player to log in in the browser: the link and code to show, once; else "". The
    /// browser opens by itself; this is for the main menu (CYB-00023) to show them too.
    Red::CString TakeLoginPrompt();

    /// Writes a line from redscript into this plugin's RED4ext log.
    void Log(const Red::CString& message);

    /// The characters of the last CharacterList, last played first. Index getters instead of returning arrays: a
    /// returned DynArray<CString> arrived in redscript with a wrong size (CYB-00009). Out of range gives "".
    int32_t GetCharacterCount();
    Red::CString GetCharacterId(int32_t index);
    Red::CString GetCharacterName(int32_t index);
    /// How many characters the account may have.
    int32_t GetMaxCharacters();
    /// Ask the server to create, play or delete a character. False when not connected.
    bool CreateCharacter(const Red::CString& firstName, const Red::CString& lastName);
    bool SelectCharacter(const Red::CString& characterId);
    bool DeleteCharacter(const Red::CString& characterId);
    /// Stores the look of the character being played (the encoded editor options). False when not connected.
    bool SendCharacterAppearance(const Red::CString& appearance);

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
    RTTI_METHOD(TakeLoginPrompt);
    RTTI_METHOD(Log);
    RTTI_METHOD(GetCharacterCount);
    RTTI_METHOD(GetCharacterId);
    RTTI_METHOD(GetCharacterName);
    RTTI_METHOD(GetMaxCharacters);
    RTTI_METHOD(CreateCharacter);
    RTTI_METHOD(SelectCharacter);
    RTTI_METHOD(DeleteCharacter);
    RTTI_METHOD(SendCharacterAppearance);
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
