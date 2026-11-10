#pragma once
#include "api/message.h"

#include <queue>

#include <steam/isteamnetworkingsockets.h>
#include <steam/steamnetworkingtypes.h>

// The server only moves bytes: packets are defined in shared/protocol/protocol.yaml and read and written in C#.
class GameServer {
private:
    HSteamListenSocket m_hListenSock = 0;
    HSteamNetPollGroup m_hPollGroup = 0;
    ISteamNetworkingSockets *m_pInterface = nullptr;

    // Received messages wait here between Update and C# polling them, so they keep their order relative to the
    // connection state callbacks that Update runs.
    std::queue<ISteamNetworkingMessage*> m_recvQueue;
    void (*m_ConnectionStatusCallback)(uint32_t, uint32_t) = nullptr;

protected:
    void PollConnectionStateChanges() const;
    void PollIncomingMessages();
public:
    static bool Initialize();
    static void Destroy();

public:
    void OnConnectionStatusChanged(const SteamNetConnectionStatusChangedCallback_t* pInfo) const;
public:
    /* DLL API start */
    bool ListenOn(uint16_t nPort = 1337);
    void RunBlocking();
    void Update(float deltaTime);

    [[nodiscard]] Message PollRecvQueue();
    static void ReleaseMessage(void* handle);
    bool SendRaw(uint32_t connectionId, const uint8_t* data, uint32_t size, int32_t sendFlags) const;
    bool CloseConnection(uint32_t connectionId, int32_t reason, const char* debug) const;
    void SetConnectionStatusChangedCallback(void (*cb)(uint32_t, uint32_t));
    /* DLL API stop */
};

extern GameServer *SINGLETON_GAMESERVER;
