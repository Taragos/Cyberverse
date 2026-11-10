#include <cstdint>

#include "message.h"
#include "GameServer.h"
#include "Log.h"

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



/// Where native log lines go (level: 1 Debug, 2 Info, 3 Warn, 4 Error). Set it before init_gameserver to catch
/// everything; the callback may be called from GameNetworkingSockets' thread. nullptr prints to stdout/stderr again.
extern "C" EXPORT void server_set_log_callback(void (*cb)(int32_t, const char*))
{
    SetLogCallback(cb);
}

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

/// Closes a connection after the reliable messages already sent to it have arrived. reason is a GameNetworkingSockets
/// end reason, from k_ESteamNetConnectionEnd_App_Min (1000) for application reasons; debug is a short text for logs.
/// No connection state callback follows, so the caller cleans up after the connection itself.
extern "C" EXPORT bool server_close_connection(const GameServer *server, const uint32_t connectionId, const int32_t reason,
                                              const char* debug)
{
    return server->CloseConnection(connectionId, reason, debug);
}

extern "C" EXPORT void server_set_connection_state_changed_cb(GameServer *server, void (*cb)(uint32_t, uint32_t))
{
    server->SetConnectionStatusChangedCallback(cb);
}
