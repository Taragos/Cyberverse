#include <cstdint>

#include "message.h"
#include "GameServer.h"

// https://stackoverflow.com/a/2164853
#if defined(_MSC_VER)
    //  Microsoft
    #define EXPORT __declspec(dllexport)
    #define IMPORT __declspec(dllimport)
#elif defined(__GNUC__)
    //  GCC
    #define EXPORT __attribute__((visibility("default")))
    #define IMPORT
#else
    //  do nothing and hope for the best?
    #define EXPORT
    #define IMPORT
    #pragma warning Unknown dynamic link import/export semantics.
#endif

// TODO: Currently we can't use CYBERVERSE_PUBLIC,
// #if CYBERVERSE_COMPILING
// #   define CYBERVERSE_PUBLIC EXPORT
// #else
// #   define CYBERVERSE_PUBLIC IMPORT
// #endif



extern "C" EXPORT bool init_gameserver() {
    return GameServer::Initialize();
}

extern "C" EXPORT void destroy_gameserver()
{
    GameServer::Destroy();
}
extern "C" EXPORT GameServer *server_create(const uint16_t server_port)
{
    const auto server = new GameServer();
    if (!server->ListenOn(server_port))
    {
        return nullptr;
    }

    return server;
}

/// This method is not recommended (at the moment) as it does never return until you kill the process
extern "C" EXPORT void server_run_blocking(GameServer *server)
{
    server->RunBlocking();
}

extern "C" EXPORT void server_update(GameServer *server, float deltaTime)
{
    server->Update(deltaTime);
}

/// The next received message, or one with handle == nullptr if there is none. Its data stays valid until
/// message_release(handle), which the caller must call exactly once.
extern "C" EXPORT Message server_poll_message(GameServer *server) {
    return server->PollRecvQueue();
}

extern "C" EXPORT void message_release(void* handle) {
    GameServer::ReleaseMessage(handle);
}

/// Sends a whole message (frame header and packet) as is. The data is copied, so the caller may reuse it right away.
/// sendFlags are GameNetworkingSockets' k_nSteamNetworkingSend_* flags.
extern "C" EXPORT bool server_send_message(const GameServer *server, const uint32_t connectionId, const uint8_t* data,
                                           const uint32_t size, const int32_t sendFlags)
{
    return server->SendRaw(connectionId, data, size, sendFlags);
}

extern "C" EXPORT void server_set_connection_state_changed_cb(GameServer *server, void (*cb)(uint32_t, uint32_t))
{
    server->SetConnectionStatusChangedCallback(cb);
}
