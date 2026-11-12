#ifndef NETWORKMANAGERCONTROLLER_H
#define NETWORKMANAGERCONTROLLER_H
#include "RED4ext/Api/v1/Sdk.hpp"
#include "RED4ext/ResourceLoader.hpp"
#include "RED4ext/SystemUpdate.hpp"

#include "RED4ext/Scripting/IScriptable.hpp"
#include "RED4ext/Scripting/Natives/Generated/ink/ISystemRequestsHandler.hpp"
#include "RED4ext/Scripting/Stack.hpp"

#include "PlayerActionTracker.h"
#include "PlayerSync/InterpolationData.h"
#include "PlayerSync/RemoteMotion.h"
#include "PlayerSync/LookTypes.h"
#include "PlayerSync/RemoteLocomotion.h"
#include "RED4ext/Scripting/Natives/Generated/AI/Command.hpp"
#include "RED4ext/Scripting/Natives/Generated/Vector4.hpp"
#include "RED4ext/Scripting/Natives/entEntityID.hpp"

#include <RedLib.hpp>
#include <Protocol.h>
#include <map>
#include <mutex>
#include <string>
#include <unordered_map>
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
    // Remote player characters' looks by entity id hash (CYB-00029); read by LookOf while the engine builds them.
    std::recursive_mutex m_looksMutex; // recursive: the spawn request holds it and may call back into LookOf
    // Spawns of other players' V waiting for their look (see the SpawnEntity handler), by network id.
    struct HeldSpawn
    {
        SpawnEntity packet;
        float since; // m_clock when it arrived
    };
    std::map<uint64_t, HeldSpawn> m_heldSpawns;
    static bool IsPlayerRecord(uint64_t recordId);
    RED4ext::ent::EntityID SpawnNow(const SpawnEntity& packet, const std::string* look);
    void SpawnHeldEntities();
    void DestroyNow(uint64_t networkedEntityId);
    // Each spawned entity's spawn packet by network id, its position kept up to date by TeleportEntity: what a
    // respawn needs (a remote V whose look changed at a mirror, CYB-00029).
    std::map<uint64_t, SpawnEntity> m_spawned;
    std::unordered_map<uint64_t, std::string> m_looks;
    float m_remoteLookTimer = 0.0f; // seconds since TickRemoteLooks last ran
    std::map<RED4ext::ent::EntityID, InterpolationData> m_interpolationData;
    std::map<RED4ext::ent::EntityID, RED4ext::Handle<RED4ext::AICommand>> m_LastTeleportCommand;
    float m_TimeSinceLastPlayerPositionSync = 0.0f;
    // The local player's movement as sent (CYB-00010): the sequence of the last update, and the speed smoothed every
    // frame from the position.
    uint32_t m_movementSequence = 0;
    RED4ext::Vector4 m_lastLocalPosition{};
    bool m_hasLastLocalPosition = false;
    float m_localSpeed = 0.0f;
    // Other players' characters (EntityMovement): where they are shown, about 100 ms behind (CYB-00010).
    Cyberverse::PlayerSync::RemoteMotion<RED4ext::ent::EntityID> m_remoteMotion;
    std::map<RED4ext::ent::EntityID, Cyberverse::PlayerSync::MotionSample> m_shownMotion; // as last applied
    // Remote puppets' walk animation (CYB-00010), and the plugin's clock in seconds.
    Cyberverse::PlayerSync::RemoteLocomotion m_remoteLocomotion;
    float m_clock = 0.0f;
    /// A steady clock in seconds, for the movement updates' send and receive times.
    static double SteadySeconds();
    // Written on a network worker thread, taken by the main menu on the UI thread.
    std::mutex m_loginRejectedMutex;
    std::string m_loginRejectedMessage;
    // The browser login the server asked for (LoginRequired), for the main menu; guarded by m_loginRejectedMutex.
    std::string m_loginPrompt;
    // The server we connect to; the session token is saved per server.
    std::string m_serverHost;
    uint16_t m_serverPort = 0;
    // --cyberverse-profile: which saved login to use; empty for the default one (CYB-00031).
    std::string m_profile;
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
    /// Moves other players' characters to where RemoteMotion shows them now and hands their movement to the animation.
    void MoveRemoteCharacters();
    void SetEntityPosition(RED4ext::ent::EntityID entityId, RED4ext::Vector4 worldPosition, float yaw);

protected:
    void PollIncomingMessages();
    void TrackPlayerPosition(float deltaTime);
    /// The local player's EMovementState, from the game's detailed locomotion state.
    EMovementState LocalMovementState();

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
    // Other players' looks (CYB-00029): the changes for a stored look, and everything the table can change on a body
    // ("m" or "f"); see PlayerSync/LookTable.h.
    Red::Handle<Cyberverse::PlayerSync::LookChanges> ResolveLook(const Red::CString& look);
    Red::Handle<Cyberverse::PlayerSync::LookChanges> LookResetList(const Red::CString& body);
    // Points a mesh part at another file, by path (redscript can't build a ResRef from a string); Codeware's
    // IComponent.ChangeResource does the work. Only for parts the engine has set up (with an owner).
    bool ChangePartResource(const Red::Handle<Red::IScriptable>& component, const Red::CString& path);
    // The look's hair as the game's own parts, extracted from its .app files and renamed with "cyberverse_" (see
    // PlayerSync/LookParts.h). Only parts added while the engine builds the entity (Codeware's Entity/Initialize) get
    // set up; later ones never render.
    Red::DynArray<Red::Handle<Red::IScriptable>> HairParts(const Red::CString& look);
    // The last look received for a spawned entity; empty if none. Thread-safe: read while the engine builds it.
    Red::CString LookOf(const RED4ext::ent::EntityID& entity);

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
    RTTI_METHOD(ResolveLook);
    RTTI_METHOD(LookResetList);
    RTTI_METHOD(ChangePartResource);
    RTTI_METHOD(HairParts);
    RTTI_METHOD(LookOf);
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
