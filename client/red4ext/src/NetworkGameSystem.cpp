#include "NetworkGameSystem.h"

#include "CommandLine.h"
#include "SessionStore.h"
#include "TypeDump.h"
#include "Main.h"
#include "Utils.h"

#include "RED4ext/RTTISystem.hpp"
#include "RED4ext/Scripting/Natives/Generated/EulerAngles.hpp"
#include "RED4ext/Scripting/Natives/Generated/game/TeleportationFacility.hpp"
#include "RED4ext/Scripting/Utils.hpp"
#include "RED4ext/SystemUpdate.hpp"

#include <steam/isteamnetworkingsockets.h>
#include <steam/isteamnetworkingutils.h> // Required, see https://github.com/ValveSoftware/GameNetworkingSockets/issues/171^
#include <steam/steamnetworkingsockets.h>

#include <CyberverseVersion.h>
#include <Protocol.h>

#include <zpp_bits.h>

#include <lmcons.h> // UNLEN
#include <shellapi.h> // ShellExecuteW

#include <thread>

bool NetworkGameSystem::Load()
{
    if (SteamDatagramErrMsg errMsg; !GameNetworkingSockets_Init(nullptr, errMsg))
    {
        SDK->logger->ErrorF(PLUGIN, "Could not initialize GameNetworkingSockets: %s", errMsg);
        return false;
    }

    return true;
}
void NetworkGameSystem::Unload()
{
    GameNetworkingSockets_Kill();
}

namespace
{
// The protocol wants UTF-8; the Windows APIs give UTF-16.
std::string WideToUtf8(const wchar_t* text, int length)
{
    std::string result;
    if (length <= 0)
    {
        return result;
    }

    result.resize(WideCharToMultiByte(CP_UTF8, 0, text, length, nullptr, 0, nullptr, nullptr));
    WideCharToMultiByte(CP_UTF8, 0, text, length, result.data(), static_cast<int>(result.size()), nullptr, nullptr);
    return result;
}

std::wstring Utf8ToWide(const std::string& text)
{
    std::wstring result;
    if (text.empty())
    {
        return result;
    }

    const auto length = static_cast<int>(text.size());
    result.resize(MultiByteToWideChar(CP_UTF8, 0, text.data(), length, nullptr, 0));
    MultiByteToWideChar(CP_UTF8, 0, text.data(), length, result.data(), static_cast<int>(result.size()));
    return result;
}

/// Opens the server's login link in the default browser. Only http(s): the link comes from the server, and anything
/// else (file:, custom protocol handlers) could start programs on this PC.
void OpenLoginInBrowser(const std::string& url)
{
    if (url.rfind("https://", 0) != 0 && url.rfind("http://", 0) != 0)
    {
        SDK->logger->WarnF(PLUGIN, "Not opening the login link, it isn't http(s): %s", url.c_str());
        return;
    }

    // Off the game thread: starting the browser can take a moment.
    std::thread([wide = Utf8ToWide(url)] {
        ShellExecuteW(nullptr, L"open", wide.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
    }).detach();
}
} // namespace

bool NetworkGameSystem::ConnectToServer(const std::string& host, uint16_t port)
{
    SDK->logger->InfoF(PLUGIN, "Trying to connect to server at %s:%d", host.c_str(), port);
    m_serverHost = host;
    m_serverPort = port;

    if (m_pInterface != nullptr)
    {
        SDK->logger->Warn(PLUGIN, "Trying to connect while already being connected. Aborting");
        return false;
    }

    m_pInterface = SteamNetworkingSockets();

    if (m_pInterface == nullptr)
    {
        SDK->logger->Error(PLUGIN, "Failed to initialize the networking library");
    }

    // Since I don't want to parse the ip manually and support both IP versions, I need to create a string first...
    const auto connection_string_size = host.length() + 2 + 5; // 2 (':' + \0) and 5 (65535)
    const auto connection_string = new char[connection_string_size];
    memset(connection_string, '\0', connection_string_size);
    sprintf_s(connection_string, connection_string_size, "%s:%d", host.c_str(), port);

    SteamNetworkingIPAddr address = {};
    if (!address.ParseString(connection_string))
    {
        SDK->logger->WarnF(PLUGIN, "Failed to parse connection string \"%s\"", connection_string);
        m_pInterface = nullptr; // prevent polling
        return false;
    }

    SteamNetworkingConfigValue_t opt = {};
    opt.SetPtr(k_ESteamNetworkingConfig_Callback_ConnectionStatusChanged,
               reinterpret_cast<void*>(ConnectionStatusChangedCallback));

    // m_hConnection = m_pInterface->ConnectToHostedDedicatedServer(identity, 0, 1, &opt);
    m_hConnection = m_pInterface->ConnectByIPAddress(address, 1, &opt);

    if (m_hConnection == k_HSteamNetConnection_Invalid)
    {
        SDK->logger->WarnF(PLUGIN, "Could not create connection for string \"%s\": invalid", connection_string);
        return false;
    }

    return true;
}

void NetworkGameSystem::OnNetworkUpdate(RED4ext::FrameInfo& frame_info, RED4ext::JobQueue& job_queue)
{
    // TODO: make this framerate indepedent, maybe also use multiple UpdateTickGroups.
    if (!m_hasTriedToConnect)
    {
        // We auto-connect on the first tick with the CLI address. We don't connect earlier because the message loop
        // isn't run there yet and we are prone to time out.
        const auto commandLine = GetCommandLineA();
        const auto host = ParseHostFromCommandLine(commandLine);
        const auto port = ParsePortFromCommandLine(commandLine);
        if (const auto profile = ParseProfileFromCommandLine(commandLine); profile.has_value() && !profile->empty())
        {
            m_profile = profile.value();
            SDK->logger->InfoF(PLUGIN, "Using the saved login of profile '%s'", m_profile.c_str());
        }
        if (host.has_value() && port.has_value())
        {
            ConnectToServer(host.value(), port.value());
        }
        if (const auto types = ParseDumpTypesFromCommandLine(commandLine); types.has_value())
        {
            Cyberverse::TypeDump::DumpClasses(types.value());
        }
        m_hasTriedToConnect = true; // We lie here, to prevent parsing the cli every time.
    }

    if (m_pInterface == nullptr)
    {
        return;
    }

    PollIncomingMessages();
    TrackPlayerPosition(frame_info.deltaTime);
    InterpolatePuppets(frame_info.deltaTime);

    m_pInterface->RunCallbacks(); // This shall be called in a loop.
}

void NetworkGameSystem::OnRegisterUpdates(RED4ext::UpdateRegistrar* aRegistrar)
{
    // TODO: If we have no connection information passed on the command line, we have no reason to even register.
    IGameSystem::OnRegisterUpdates(aRegistrar);
    aRegistrar->RegisterUpdate(RED4ext::UpdateTickGroup::FrameBegin, this, "NetworkUpdate",
        [this](RED4ext::FrameInfo &frame_info, RED4ext::JobQueue &job_queue) {
            this->OnNetworkUpdate(frame_info, job_queue);
    });
}

void NetworkGameSystem::ConnectionStatusChangedCallback(SteamNetConnectionStatusChangedCallback_t* pInfo)
{
    SDK->logger->InfoF(PLUGIN, "Connection Status Changed (%d): %s", pInfo->m_info.m_eState,
                       pInfo->m_info.m_szEndDebug);

    if (pInfo->m_info.m_eState == k_ESteamNetworkingConnectionState_Connected)
    {
        SDK->logger->Info(PLUGIN, "Socket connected, authenticating");
        // TODO: Maybe we could manage this singleton access better? But then, the Game's GameSystem Container is the owner of "this"
        Red::GetGameSystem<NetworkGameSystem>()->SendLogin();
    } else {
        Red::GetGameSystem<NetworkGameSystem>()->FullyConnected = false;
    }
}

void NetworkGameSystem::SendLogin()
{
    // The Windows account name: only a hint for the server's logs, the account comes from the browser login.
    wchar_t wide_name[UNLEN + 1];
    DWORD wide_len = UNLEN + 1; // In: buffer size, out: length including the null terminator
    InitAuth auth_packet = {};
    if (GetUserNameW(wide_name, &wide_len) && wide_len > 1)
    {
        auth_packet.username = WideToUtf8(wide_name, static_cast<int>(wide_len - 1));
    }

    auth_packet.protocolVersion = PROTOCOL_VERSION_CURRENT;
    auth_packet.modVersion = CYBERVERSE_VERSION;
    // The product version, e.g. 2.31.0; hotfixes of one patch share it.
    const auto* game = SDK->runtime;
    auth_packet.gameVersion = std::to_string(game->major) + "." + std::to_string(game->minor) + "." +
                              std::to_string(game->patch);
    SDK->logger->InfoF(PLUGIN, "Cyberverse %s, game %s, protocol 0x%08X", CYBERVERSE_VERSION,
                       auth_packet.gameVersion.c_str(), PROTOCOL_VERSION_CURRENT);
    EnqueueMessage(auth_packet);

    // Right behind InitAuth on the same reliable channel, so it arrives second.
    LoginWithSession login = {};
    login.sessionToken = SessionStore::Load(SessionStore::PathFor(m_serverHost, m_serverPort, m_profile));
    if (login.sessionToken.size() > 128)
    {
        login.sessionToken.clear(); // not one the server issued
    }
    wchar_t computer[MAX_COMPUTERNAME_LENGTH + 1];
    DWORD computer_len = MAX_COMPUTERNAME_LENGTH + 1; // In: buffer size, out: length without the null terminator
    if (GetComputerNameW(computer, &computer_len))
    {
        login.deviceName = WideToUtf8(computer, static_cast<int>(computer_len)).substr(0, 64);
    }
    SDK->logger->Info(PLUGIN, login.sessionToken.empty() ? "Logging in without a saved session"
                                                         : "Logging in with the saved session");
    EnqueueMessage(login);
}

Red::CString NetworkGameSystem::TakeLoginPrompt()
{
    std::lock_guard lock(m_loginRejectedMutex);
    Red::CString prompt(m_loginPrompt.c_str());
    m_loginPrompt.clear();
    return prompt;
}

void NetworkGameSystem::Log(const Red::CString& message)
{
    // The thread id tells the UI thread from job workers (CYB-00023).
    SDK->logger->InfoF(PLUGIN, "[script t%lu] %s", GetCurrentThreadId(), message.c_str());
}

int32_t NetworkGameSystem::GetCharacterCount()
{
    std::lock_guard lock(m_charactersMutex);
    return static_cast<int32_t>(m_characters.size());
}

Red::CString NetworkGameSystem::GetCharacterId(const int32_t index)
{
    std::lock_guard lock(m_charactersMutex);
    if (index < 0 || index >= static_cast<int32_t>(m_characters.size()))
    {
        return {};
    }
    return {m_characters[index].characterId.c_str()};
}

Red::CString NetworkGameSystem::GetCharacterName(const int32_t index)
{
    std::lock_guard lock(m_charactersMutex);
    if (index < 0 || index >= static_cast<int32_t>(m_characters.size()))
    {
        return {};
    }
    return {m_characters[index].name.c_str()};
}

int32_t NetworkGameSystem::GetMaxCharacters()
{
    std::lock_guard lock(m_charactersMutex);
    return m_maxCharacters;
}

bool NetworkGameSystem::CreateCharacter(const Red::CString& firstName, const Red::CString& lastName)
{
    if (!FullyConnected)
    {
        return false;
    }

    CharacterCreate create = {};
    create.firstName = firstName.c_str();
    create.lastName = lastName.c_str();
    return EnqueueMessage(create); // The look follows from the editor after the first Play (SendCharacterAppearance).
}

bool NetworkGameSystem::SelectCharacter(const Red::CString& characterId)
{
    if (!FullyConnected)
    {
        return false;
    }

    CharacterSelect select = {};
    select.characterId = characterId.c_str();
    return EnqueueMessage(select);
}

bool NetworkGameSystem::DeleteCharacter(const Red::CString& characterId)
{
    if (!FullyConnected)
    {
        return false;
    }

    CharacterDelete request = {};
    request.characterId = characterId.c_str();
    return EnqueueMessage(request);
}

bool NetworkGameSystem::SendCharacterAppearance(const Red::CString& appearance)
{
    if (!FullyConnected)
    {
        return false;
    }

    CharacterAppearance request = {};
    request.appearance = appearance.c_str();
    return EnqueueMessage(request);
}

Red::CString NetworkGameSystem::TakeLoginRejectedMessage()
{
    std::lock_guard lock(m_loginRejectedMutex);
    Red::CString message(m_loginRejectedMessage.c_str());
    m_loginRejectedMessage.clear();
    return message;
}

template<typename T>
bool NetworkGameSystem::EnqueueMessage(const T& content)
{
    auto frame = MessageFrame{};
    T::FillMessageFrame(frame);

    auto [data, out] = zpp::bits::data_out();

    auto zpp_result = out(frame);
    if (zpp::bits::failure(zpp_result))
    {
        // Failed to serialize the frame(!)
        return false;
    }

    zpp_result = out(content);
    if (zpp::bits::failure(zpp_result))
    {
        // Failed to serialize the content
        return false;
    }

    assert(data.size() < std::numeric_limits<uint32_t>::max());

    const auto flags = T::Reliable ? k_nSteamNetworkingSend_Reliable : k_nSteamNetworkingSend_Unreliable;
    const auto result = m_pInterface->SendMessageToConnection(
        m_hConnection, data.data(), static_cast<uint32_t>(data.size()), flags, nullptr);

    if (result == k_EResultOK)
    {
        return true;
    }

    SDK->logger->ErrorF(PLUGIN, "NetworkGameSystem::EnqueueMessage(%d) => Error %d\n", static_cast<int>(T::MessageType), result);
    return false;
}

// The packet types other files send (declared extern in NetworkGameSystem.h).
template bool NetworkGameSystem::EnqueueMessage(const PlayerActionTracked& packet);
template bool NetworkGameSystem::EnqueueMessage(const PlayerSpawnCar& packet);
template bool NetworkGameSystem::EnqueueMessage(const PlayerUnmountCar& packet);
template bool NetworkGameSystem::EnqueueMessage(const PlayerEquipItem& packet);
template bool NetworkGameSystem::EnqueueMessage(const PlayerShoot& packet);

void NetworkGameSystem::SetEntityPosition(const RED4ext::ent::EntityID entityId, RED4ext::Vector4 worldPosition, float yaw)
{
    const auto entity = Cyberverse::Utils::GetDynamicEntity(entityId);
    // TODO: For most, this is actually a NPCPuppet, for those that aren't, this will crash.

    if (entity.has_value())
    {
        // TODO: Find out if this is an NPCPuppet...
        // AI
        RED4ext::Handle<RED4ext::AICommand> commandRef;
        Red::CallVirtual(this, "TeleportPuppet", commandRef, entity.value(), worldPosition, yaw);
        m_LastTeleportCommand[entityId] = commandRef;

        // random game objects
        RED4ext::EulerAngles angles = { 0.0f, 0.0f, yaw };
        const auto teleportFacility = Red::GetGameSystem<RED4ext::TeleportationFacility>();
        if (!Red::CallVirtual(teleportFacility, "Teleport", entity.value() /*"game object"*/, worldPosition, angles))
        {
            SDK->logger->Warn(PLUGIN, "Failed to teleport");
        }

        // Try: scriptInterface.PushAnimationEvent(n"Jump");
        // if (!Red::CallVirtual(entity.value(), "PushAnimationEvent", "Jump"))
        // {
        //     SDK->logger->Info(PLUGIN, "Could not push jump event.");
        // }
    } else
    {
        SDK->logger->Warn(PLUGIN, "Cannot SetEntityPosition, because the entity hasn't been found");
    }
}


void NetworkGameSystem::PollIncomingMessages()
{
    // TODO: More resonable quit condition.
    while (true)
    {
        ISteamNetworkingMessage* pIncomingMsg = nullptr;
        const auto numMsgs = m_pInterface->ReceiveMessagesOnConnection(m_hConnection, &pIncomingMsg, 1);
        if (numMsgs == 0)
        {
            break;
        }

        if (numMsgs < 0)
        {
            SDK->logger->ErrorF(PLUGIN, "Error polling messages: %d", numMsgs);
            return;
        }

        // TODO: Handle or enqueue the message
        //SDK->logger->InfoF(PLUGIN, "Received a packet with %d bytes", pIncomingMsg->m_cbSize);

        auto [data, in] = zpp::bits::data_in();
        const auto begin = (std::byte*)pIncomingMsg->GetData();
        data.assign(begin, begin + pIncomingMsg->GetSize());

        MessageFrame frame = {};
        if (zpp::bits::failure(in(frame)))
        {
            SDK->logger->Error(PLUGIN, "Faulty packet");
            pIncomingMsg->Release();
            continue;
        }

        // TODO: This should both be more generic probably _AND_ we need to consider how we want to hand this off to C#,
        // given that they may want to control _all_ packet logic. This however depends on how flexible and moddable we
        // want our, e.g. auth handling, to be.
        switch (static_cast<EMessageTypeClientbound>(frame.message_type))
        {
        case EMessageTypeClientbound::InitAuthResult:
        {
            InitAuthResult auth_result_packet = {};
            if (zpp::bits::failure(in(auth_result_packet)))
            {
                SDK->logger->Error(PLUGIN, "Faulty packet: InitAuthResult");
                pIncomingMsg->Release();
                continue;
            }

            if (auth_result_packet.authResult != EAuthResult::Ok)
            {
                // Any other result, including ones added after this build, means the server closes the connection.
                // Its message says why, in words for the player.
                SDK->logger->WarnF(PLUGIN, "Login rejected (%d, server %s, protocol 0x%08X; ours 0x%08X): %s",
                                   static_cast<int>(auth_result_packet.authResult),
                                   auth_result_packet.modVersion.c_str(), auth_result_packet.protocolVersion,
                                   PROTOCOL_VERSION_CURRENT, auth_result_packet.message.c_str());
                const auto& message = auth_result_packet.message.empty()
                                          ? std::string("The server turned the login away without saying why.")
                                          : auth_result_packet.message;
                {
                    std::lock_guard lock(m_loginRejectedMutex);
                    m_loginRejectedMessage = message;
                }
                m_hasEnqueuedLoadLastCheckpoint = false;
            }

            switch (auth_result_packet.authResult)
            {
            case EAuthResult::Ok:
                SDK->logger->Info(PLUGIN, "Login accepted");
                FullyConnected = true;
                if (m_hasEnqueuedLoadLastCheckpoint && m_systemRequestsHandler)
                {
                    SDK->logger->Info(PLUGIN, "Loading the savegame");
                    Red::CallVirtual(m_systemRequestsHandler, "LoadLastCheckpoint", false);
                }
                // TODO: Follow-Up action
                break;
            default:
                break; // logged above
            }
        }
        break;

        case EMessageTypeClientbound::LoginRequired:
        {
            LoginRequired login_required = {};
            if (zpp::bits::failure(in(login_required)))
            {
                SDK->logger->Error(PLUGIN, "Faulty packet: LoginRequired");
                pIncomingMsg->Release();
                continue;
            }

            SDK->logger->InfoF(PLUGIN, "Log in in the browser at %s (code %s, valid for %u seconds)",
                               login_required.verificationUri.c_str(), login_required.userCode.c_str(),
                               login_required.expiresInSeconds);
            {
                std::lock_guard lock(m_loginRejectedMutex);
                m_loginPrompt = "Log in in your browser to join. If it didn't open, go to " +
                                login_required.verificationUri + " and check that it shows the code " +
                                login_required.userCode + ".";
            }
            OpenLoginInBrowser(login_required.verificationUri);
        }
        break;

        case EMessageTypeClientbound::SessionIssued:
        {
            SessionIssued session = {};
            if (zpp::bits::failure(in(session)))
            {
                SDK->logger->Error(PLUGIN, "Faulty packet: SessionIssued");
                pIncomingMsg->Release();
                continue;
            }

            const auto path = SessionStore::PathFor(m_serverHost, m_serverPort, m_profile);
            if (SessionStore::Save(path, session.sessionToken))
            {
                SDK->logger->Info(PLUGIN, "Saved the session; the next join from this PC skips the browser");
            }
            else
            {
                SDK->logger->WarnF(PLUGIN, "Could not save the session to %s", path.string().c_str());
            }
        }
        break;

        case EMessageTypeClientbound::SpawnEntity:
        {
            SpawnEntity spawn_entity = {};
            if (zpp::bits::failure(in(spawn_entity)))
            {
                SDK->logger->Error(PLUGIN, "Faulty packet: SpawnEntity");
                pIncomingMsg->Release();
                continue;
            }

            if (m_networkedEntitiesLookup.contains(spawn_entity.networkedEntityId))
            {
                SDK->logger->WarnF(PLUGIN, "Already have a spawned entity for %llu", spawn_entity.networkedEntityId);
                continue;
            }

            // TODO: separate spawning component
            // TODO: SpawnTransientEntity should return the EntityId for a Map<NetworkedEntityId, LocalEntityId>, especially for further updates.
            SDK->logger->InfoF(PLUGIN, "Spawning entity %llu", spawn_entity.recordId);
            RED4ext::TweakDBID entityName = { spawn_entity.recordId };
            RED4ext::Vector4 worldPosition = { spawn_entity.spawnPosition.x, spawn_entity.spawnPosition.y, spawn_entity.spawnPosition.z, 1.0 };
            RED4ext::Quaternion worldOrientation = { 0.0, 0.0, 0.0, 1.0 };

            RED4ext::ent::EntityID entityId;
            if (!Red::CallVirtual(this, "SpawnTransientEntity", entityId, entityName, worldPosition, worldOrientation))
            {
                SDK->logger->Warn(PLUGIN, "Failed to spawn entity!");
            }

            // TODO: Validation. already contained? Error!
            SDK->logger->InfoF(PLUGIN, "Got Entity Id %llu for networkId %llu", entityId.hash, spawn_entity.networkedEntityId);
            m_networkedEntitiesLookup.insert(std::make_pair(spawn_entity.networkedEntityId, entityId));
        }
        break;

        case EMessageTypeClientbound::TeleportEntity:
        {
            TeleportEntity teleport = {};
            if (zpp::bits::failure(in(teleport)))
            {
                SDK->logger->Error(PLUGIN, "Faulty packet: TeleportEntity");
                pIncomingMsg->Release();
                continue;
            }

            if (!m_networkedEntitiesLookup.contains(teleport.networkedEntityId))
            {
                SDK->logger->WarnF(PLUGIN, "Entity Teleport packet for unknown networkedEntityId %llu. Map size %d",
                                   teleport.networkedEntityId, m_networkedEntitiesLookup.size());
                break;
            }

            const auto entityId = m_networkedEntitiesLookup[teleport.networkedEntityId];
            const RED4ext::Vector4 worldPosition = { teleport.targetPosition.x, teleport.targetPosition.y, teleport.targetPosition.z, 1.0 };

            // TODO: If teleport flag is set
            //SetEntityPosition(entityId, worldPosition, teleport.yaw);

            const auto entity = Cyberverse::Utils::GetDynamicEntity(entityId);
            if (!entity.has_value())
            {
                SDK->logger->Info(PLUGIN, "Skipping TeleportEntity");
                break;
            }

            const auto yawSource = Cyberverse::Utils::Quaternion_ToEulerAngles(Cyberverse::Utils::Entity_GetWorldOrientation(entity.value())).Yaw;
            const auto positionSource = Cyberverse::Utils::Entity_GetWorldPosition(entity.value());

            if (positionSource.X == 0.0 && positionSource.Y == 0.0 && positionSource.Z == 0.0)
            {
                // TODO: let's pray that this doesn't have a side effect when players DO reside at (0, 0, 0)
                // The first interpolation would go from (0, 0, 0) to target position, but that would despawn and destroy
                // the entity, since it's too far away.
                SDK->logger->Warn(PLUGIN, "Server Bug: Teleport Entity without the teleport flag for a fresh spawned entity");
                SetEntityPosition(entityId, worldPosition, teleport.yaw);
                break;
            }

            SDK->logger->TraceF(PLUGIN, "New Interpolation Data (entity %llu): yaw %f -> %f, position: (%f, %f, %f) -> (%f, %f, %f)", teleport.networkedEntityId, yawSource, teleport.yaw, positionSource.X, positionSource.Y, positionSource.Z, teleport.targetPosition.x, teleport.targetPosition.y, teleport.targetPosition.z);

            if (m_LastTeleportCommand.contains(entityId))
            {
                // TODO: Alternatively we could query the command and if it's still running just skipping enqueing a new comand
                //  but that probably means more lags/teleports again, because the gap between teleports becomes larger.
                //  but then, does stopping really change a thing? The best would be to _update_ the existing command,
                //  but is that working? who knows!
                const auto commandRef = m_LastTeleportCommand[entityId];
                Red::CallVirtual(this, "StopAICommand", entity.value(), commandRef);
            }

            // TODO: This should probably be a map<id, list<data>>, so that a server that eagerly sends too much data doesn't increase interpolation delay
            //  in contrast, we can just increase the time velocity of the interpolator
            const auto interpolation_data = InterpolationData(Cyberverse::Utils::Vector4To3(positionSource), teleport.targetPosition, yawSource, teleport.yaw, 0.1f);
            m_interpolationData[entityId] = interpolation_data;
        }
        break;

        case EMessageTypeClientbound::DestroyEntity:
        {
            DestroyEntity destroy_entity = {};
            if (zpp::bits::failure(in(destroy_entity)))
            {
                SDK->logger->Error(PLUGIN, "Faulty packet: DestroyEntity");
                pIncomingMsg->Release();
                continue;
            }

            if (!m_networkedEntitiesLookup.contains(destroy_entity.networkedEntityId))
            {
                SDK->logger->WarnF(PLUGIN, "Have no spawned entity for %llu", destroy_entity.networkedEntityId);
                continue;
            }

            const auto entityId = m_networkedEntitiesLookup[destroy_entity.networkedEntityId];
            if (!Red::CallVirtual(this, "DestroyTransientEntity", entityId))
            {
                SDK->logger->Warn(PLUGIN, "Failed to destroy entity!");
            }

            m_networkedEntitiesLookup.erase(entityId);
        }
        break;

        case EMessageTypeClientbound::CharacterList:
        {
            CharacterList list = {};
            if (zpp::bits::failure(in(list)))
            {
                SDK->logger->Error(PLUGIN, "Faulty packet: CharacterList");
                break;
            }

            SDK->logger->InfoF(PLUGIN, "Got %zu of at most %u characters", list.characters.size(), list.maxCharacters);
            {
                std::lock_guard lock(m_charactersMutex);
                m_characters = std::move(list.characters);
                m_maxCharacters = list.maxCharacters;
            }
            // The select popup reads the list through GetCharacterCount, GetCharacterId and GetCharacterName.
            Red::CallVirtual(this, "OnCharacterList");
        }
        break;

        case EMessageTypeClientbound::CharacterRequestFailed:
        {
            CharacterRequestFailed failed = {};
            if (zpp::bits::failure(in(failed)))
            {
                SDK->logger->Error(PLUGIN, "Faulty packet: CharacterRequestFailed");
                break;
            }

            SDK->logger->InfoF(PLUGIN, "Character request refused: %s", failed.message.c_str());
            Red::CallVirtual(this, "OnCharacterRequestFailed", Red::CString(failed.message.c_str()));
        }
        break;

        case EMessageTypeClientbound::CharacterSpawned:
        {
            CharacterSpawned spawned = {};
            if (zpp::bits::failure(in(spawned)))
            {
                SDK->logger->Error(PLUGIN, "Faulty packet: CharacterSpawned");
                break;
            }

            SDK->logger->InfoF(PLUGIN, "Playing %s (%s)%s", spawned.name.c_str(), spawned.characterId.c_str(),
                               spawned.teleport ? ", moving to its saved place" : "");
            const RED4ext::Vector4 position = { spawned.position.x, spawned.position.y, spawned.position.z, 1.0f };
            Red::CallVirtual(this, "OnCharacterSpawned", Red::CString(spawned.name.c_str()), spawned.teleport,
                             position, spawned.yaw, Red::CString(spawned.appearance.c_str()));
        }
        break;

        default:
            printf("Message Type: %d\n", frame.message_type);
            break;
        }

        pIncomingMsg->Release();
    }
}

bool NetworkGameSystem::OnGameRestored()
{
    const auto res = IGameSystem::OnGameRestored();
    SDK->logger->Info(PLUGIN, "Game restored: We're in the world");
    const auto player = Cyberverse::Utils::GetPlayer();

    // Broken attempts:
    // const auto transform = Cyberverse::Utils::Entity_GetWorldTransform(player);
    // const auto position = Cyberverse::Utils::WorldPosition_ToVector4(transform.Position);
    // SDK->logger->InfoF(PLUGIN, "Player at (%f, %f, %f)", transform.Position.x.Bits, transform.Position.y.Bits,
    // transform.Position.z.Bits);

    const auto position = Cyberverse::Utils::Entity_GetWorldPosition(player);
    SDK->logger->InfoF(PLUGIN, "Player at (%f, %f, %f, %f)", position.X, position.Y, position.Z, position.W);

    PlayerJoinWorld join_packet = {};
    join_packet.position = { position.X, position.Y, position.Z };

    // TODO: Maybe we could manage this singleton access better? But then, the Game's GameSystem Container is the owner
    // of "this"
    Red::GetGameSystem<NetworkGameSystem>()->EnqueueMessage(join_packet);

    m_gameRestored = true;
    return res;
}

void NetworkGameSystem::TrackPlayerPosition(float deltaTime)
{
    // Nothing to send to before the login is accepted, or after the server closed the connection.
    if (!FullyConnected)
    {
        return;
    }

    m_TimeSinceLastPlayerPositionSync += deltaTime;
    if (m_TimeSinceLastPlayerPositionSync < 0.1f /* update rate*/)
    {
        return;
    }

    m_TimeSinceLastPlayerPositionSync = 0.0f;

    const auto player = Cyberverse::Utils::GetPlayer();
    const auto [X, Y, Z, W] = Cyberverse::Utils::Entity_GetWorldPosition(player);
    const auto orientation = Cyberverse::Utils::Entity_GetWorldOrientation(player);
    const auto [Roll, Pitch, Yaw] = Cyberverse::Utils::Quaternion_ToEulerAngles(orientation);

    const PlayerPositionUpdate position_update = { {  X, Y, Z }, Yaw};
    this->EnqueueMessage(position_update);
}

void NetworkGameSystem::InterpolatePuppets(const float deltaTime)
{
    for (auto it = m_interpolationData.begin(); it != m_interpolationData.end();)
    {
        auto& entityId = it->first;
        auto& interpolator = it->second;

        const auto interpolationProgress = std::min(1.0f, interpolator.CalcInterpolationProgress(deltaTime));
        const auto targetDestination = Cyberverse::Utils::LerpLocal(interpolator.positionSource, interpolator.positionTarget, interpolationProgress);

        auto angleDirection1 = interpolator.rotationTarget - interpolator.rotationSource;
        auto angleDirection2 = interpolator.rotationSource - interpolator.rotationTarget;

        if (angleDirection1 < 0.0f)
        {
            angleDirection1 += 360.0f;
        }

        if (angleDirection2 < 0.0f)
        {
            angleDirection2 += 360.0f;
        }

        auto actualAngleDistance = 0.0f;
        if (angleDirection1 < angleDirection2)
        {
            actualAngleDistance = angleDirection1;
        } else
        {
            actualAngleDistance = -angleDirection2;
        }

        // TODO: Better interpolation here, e.g. if we go from 5 -> 355°, we should only do 10°, not 350.
        const float targetYaw = interpolator.rotationSource + interpolationProgress * actualAngleDistance;
        const auto targetDestinationVec4 = Cyberverse::Utils::Vector3To4(targetDestination); // TODO: Get rid of this function call
        SDK->logger->TraceF(PLUGIN, "Interpolation Progress: %f, yaw: %f. dest (%f, %f, %f)", interpolationProgress, targetYaw, targetDestinationVec4.X, targetDestinationVec4.Y, targetDestinationVec4.Z);

        SetEntityPosition(entityId, targetDestinationVec4, targetYaw);

        if (interpolationProgress >= 1.0f)
        {
            it = m_interpolationData.erase(it);
        } else {
            ++it;
        }
    }
}
