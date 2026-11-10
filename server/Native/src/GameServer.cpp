#include "GameServer.h"

#include <cassert>
#include <cstdio>
#include <steam/steamnetworkingsockets.h>
#include <thread>

bool GameServer::Initialize()
{
    if (SteamDatagramErrMsg errMsg; !GameNetworkingSockets_Init(nullptr, errMsg))
    {
        fprintf(stderr, "Could not initialize GameNetworkingSockets: %s\n", errMsg);
        return false;
    }

    return true;
}

static void SteamNetConnectionStatusChangedCallback(SteamNetConnectionStatusChangedCallback_t *pInfo)
{
    SINGLETON_GAMESERVER->OnConnectionStatusChanged(pInfo);
}

bool GameServer::ListenOn(const uint16_t nPort)
{
    m_pInterface = SteamNetworkingSockets();
    if (m_pInterface == nullptr)
    {
        fprintf(stderr, "Failed to initialize the networking library\n");
    }

    // Start listening
    SteamNetworkingIPAddr serverLocalAddr = {};
    serverLocalAddr.Clear();
    serverLocalAddr.m_port = nPort;

    // SteamNetConnectionStatusChangedCallback needs a singleton to get back to this instance, so we're overwriting it
    // (for now) maybe there's a better way like a dynamic jump table that just pushes ecx to a static value?
    SINGLETON_GAMESERVER = this;

    SteamNetworkingConfigValue_t opt = {};
    opt.SetPtr(k_ESteamNetworkingConfig_Callback_ConnectionStatusChanged,
               reinterpret_cast<void*>(SteamNetConnectionStatusChangedCallback));
    m_hListenSock = m_pInterface->CreateListenSocketIP(serverLocalAddr, 1, &opt);
    if (m_hListenSock == k_HSteamListenSocket_Invalid)
    {
        fprintf(stderr, "Failed to listen on port %d\n", nPort);
        return false;
    }

    m_hPollGroup = m_pInterface->CreatePollGroup();
    if (m_hPollGroup == k_HSteamNetPollGroup_Invalid)
    {
        fprintf(stderr, "Failed to listen on port %d\n", nPort);
        return false;
    }

    printf("Server listening on port %d\n", nPort);
    fflush(stdout); // stdout is fully buffered when piped (Docker, CI), which would hide this line until exit
    return true;
}

void GameServer::SetConnectionStatusChangedCallback(void (*cb)(uint32_t, uint32_t))
{
    m_ConnectionStatusCallback = cb;
}

void GameServer::OnConnectionStatusChanged(const SteamNetConnectionStatusChangedCallback_t* pInfo) const
{
    // What's the state of the connection?
    switch ( pInfo->m_info.m_eState )
    {
    case k_ESteamNetworkingConnectionState_None:
        // NOTE: We will get callbacks here when we destroy connections.  You can ignore these.
        break;

    case k_ESteamNetworkingConnectionState_ClosedByPeer:
    case k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
    {
        // Ignore if they were not previously connected.  (If they disconnected
        // before we accepted the connection.)
        if ( pInfo->m_eOldState == k_ESteamNetworkingConnectionState_Connected )
        {
            if (m_ConnectionStatusCallback != nullptr)
            {
                m_ConnectionStatusCallback(pInfo->m_info.m_eState, pInfo->m_hConn);
            }
        }
        else
        {
            assert( pInfo->m_eOldState == k_ESteamNetworkingConnectionState_Connecting );
        }

        // Clean up the connection.  This is important!
        // The connection is "closed" in the network sense, but
        // it has not been destroyed.  We must close it on our end, too
        // to finish up.  The reason information do not matter in this case,
        // and we cannot linger because it's already closed on the other end,
        // so we just pass 0's.
        m_pInterface->CloseConnection( pInfo->m_hConn, 0, nullptr, false );
        break;
    }

    case k_ESteamNetworkingConnectionState_Connecting:
    {
        // This must be a new connection
        //assert( m_mapClients.find( pInfo->m_hConn ) == m_mapClients.end() );

        printf("Connection request from %s\n", pInfo->m_info.m_szConnectionDescription);

        // A client is attempting to connect
        // Try to accept the connection.
        if ( m_pInterface->AcceptConnection( pInfo->m_hConn ) != k_EResultOK )
        {
            // This could fail.  If the remote host tried to connect, but then
            // disconnected, the connection may already be half closed.  Just
            // destroy whatever we have on our side.
            m_pInterface->CloseConnection( pInfo->m_hConn, 0, nullptr, false );
            printf( "Can't accept connection.  (It was already closed?)\n" );
            break;
        }

        // Assign the poll group
        if (!m_pInterface->SetConnectionPollGroup(pInfo->m_hConn, m_hPollGroup))
        {
            m_pInterface->CloseConnection( pInfo->m_hConn, 0, nullptr, false );
            printf( "Failed to set poll group?\n" );
            break;
        }

        if (m_ConnectionStatusCallback != nullptr)
        {
            m_ConnectionStatusCallback(pInfo->m_info.m_eState, pInfo->m_hConn);
        }
        break;
    }
    case k_ESteamNetworkingConnectionState_Connected:
        // We will get a callback immediately after accepting the connection.
        // Since we are the server, we can ignore this, it's not news to us.
        break;

    default:
        // Silences -Wswitch
        break;
    }
}

void GameServer::PollConnectionStateChanges() const
{
    if (m_pInterface != nullptr)
    {
        m_pInterface->RunCallbacks();
    }
}

bool GameServer::SendRaw(const uint32_t connectionId, const uint8_t* data, const uint32_t size,
                         const int32_t sendFlags) const
{
    // GameNetworkingSockets copies the data, so the caller's buffer is free again once this returns.
    const auto result = m_pInterface->SendMessageToConnection(connectionId, data, size, sendFlags, nullptr);
    if (result == k_EResultOK)
    {
        return true;
    }

    fprintf(stderr, "GameServer::SendRaw(connection %u, %u bytes) => Error %d\n", connectionId, size, result);
    return false;
}

void GameServer::PollIncomingMessages()
{
    // TODO: More reasonable quit condition
    while (true)
    {
        ISteamNetworkingMessage* pIncomingMsg = nullptr;
        const auto numMsgs = m_pInterface->ReceiveMessagesOnPollGroup(m_hPollGroup, &pIncomingMsg, 1);

        if (numMsgs == 0)
        {
            return;
        }

        if (numMsgs < 0)
        {
            fprintf(stderr, "Error polling messages: %d\n", numMsgs);
            return;
        }

        m_recvQueue.push(pIncomingMsg);
    }
}

void GameServer::Update(float deltaTime)
{
    PollIncomingMessages();
    PollConnectionStateChanges();
}

void GameServer::RunBlocking()
{
    const auto g_bQuit = false;
    while ( !g_bQuit )
    {
        Update(0.1f);
        std::this_thread::sleep_for( std::chrono::milliseconds( 100 ) );
    }
}

void GameServer::Destroy()
{
    GameNetworkingSockets_Kill();
}

Message GameServer::PollRecvQueue()
{
    if (m_recvQueue.empty())
    {
        return {};
    }

    const auto message = m_recvQueue.front();
    m_recvQueue.pop();
    return Message{static_cast<const uint8_t*>(message->GetData()), message, static_cast<uint32_t>(message->GetSize()),
                   message->m_conn};
}

void GameServer::ReleaseMessage(void* handle)
{
    static_cast<ISteamNetworkingMessage*>(handle)->Release();
}

GameServer *SINGLETON_GAMESERVER = nullptr;
