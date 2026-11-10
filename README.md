# CyberVerse - The Cyberpunk 2077 Multiplayer Framework

> [!IMPORTANT]  
> In it's current form, CyberVerse is _only_  relevant for developers/modders. Regular users may
> struggle getting it to run and we don't actively support that, either. There are a lot of missing
> pieces before we can start to recommend using CyberVerse to play on.

## What is Cyberverse exactly?
Cyberverse is a very much in progress implementation of a multiplayer into Cyberpunk 2077 v2.5 (see "Required Client dependencies").  
It being a "framework" additionally means that the server side is flexibly extended by Plugins that provide additional behavior. As such, it will be a good choice for Roleplaying Servers.

Besides being extendable, it also features a working sandbox mode, that already handles most of the hard topics
in Networking (such as entity sync) for you. You should be able to replace every component at will, though.

## Code Structure
CyberVerse actually consists of 4 projects: Two client side projects and two server side projects, or put
differently: Two c++ projects that mostly handle the network serialization and two application layers that
handle server side logic and replicating in game.  
Since it has different projects, people with multiple skillsets are welcome to collaborate, but please contact
us before implementing a bigger feature so we can talk about the concepts relevant to that.
Additionally, there's `shared/protocol/protocol.yaml`, the only definition of the packets (see "Adding a packet").

### Server.Native
This is the C++ support DLL of the game server. It handles the lowest level of networking only: it hands C# the raw
bytes of every received message and sends the bytes C# wrote. It knows no packet types, so adding a packet doesn't
touch it.

### Server.Managed
The main logic of the game server. It reads and writes packets with the code generated from the schema
(`server/Api/Protocol/`), performs high level logic and bookkeeping, runs the plugins (see "Plugins") and then enqueues
packets on its own again. `server/Api` is the part plugins compile against.

### Client.RED4extModule
This is the C++ side in the game that handles networking and bridges between the redscript utility functions
and implements packets that are sent from the server (clientbound) as well as sending packets when a certain
action has occured.

### Client.Redscript
This is the layer that is closest to the game logic, it's job is basically to listen for events and apply server
side changes into the game. Future use cases will also be providing a server browser and chat UI and maybe
interfacing with custom redscript plugins, written by the related server owners (such as custom packets/payloads,
triggering packets, callbacks on packets).

## Adding a packet
1. Add it to `shared/protocol/protocol.yaml` (direction, id, channel, reliable, fields; the file explains the format).
2. Run `dotnet run server/Tools/GenerateProtocol.cs` (on Linux without the .NET SDK: `tools/linux/test-server.sh --generate`,
   which also runs the tests). It writes `shared/protocol/Protocol.h` for the client and
   `server/Api/Protocol/Generated/Protocol.g.cs` for the server. Commit both with the schema; CI fails if they are
   out of date.
3. Server: `context.Packets.Register<MyPacket>(handler)` in a plugin for serverbound packets (several handlers per packet
   are fine), `IConnections.Send(connectionId, packet)` for clientbound ones. Client: `EnqueueMessage(packet)`, or a `case` in `NetworkGameSystem::PollIncomingMessages`.

Any change to the schema changes the protocol version, so client and server have to be built from the same schema.

## Plugins
Server features live in plugins: classes implementing `IPlugin` from `server/Api`. The login and player sync are
built-in plugins (`server/Managed/PacketHandling/`); `server/Plugins/Example` is a small one to copy.

- **Install:** `dotnet publish <plugin project> -o <server>/plugins/<AssemblyName>` and restart. Each folder in
  `plugins/` holds one plugin assembly named like the folder (or the folder's only `.dll`) with its dependencies, loaded
  in its own `AssemblyLoadContext`. There's no hot reload. The `plugins` console command lists what loaded.
- **Write one:** reference `server/Api/Cyberverse.Server.Api.csproj` with `Private="false" ExcludeAssets="runtime"` and
  `<EnableDynamicLoading>true</EnableDynamicLoading>`. In `OnLoad`, use the context: `Packets.Register<T>`,
  `Events.Subscribe<PlayerJoinedWorld>` (also `PlayerAuthenticated`, `PlayerLeft`, or your own event types),
  `Commands.Register`, `Services` (`IPlayerService`, `IConnections`, and services other plugins add in
  `ConfigureServices`), `LoadConfig<T>()` for `config/<name>.json`, and `Logger`. What you register through the
  context is removed when the plugin fails or unloads.
- **Order and failures:** `PluginInfo.DependsOn` names the plugins that load first. A plugin that throws, or whose
  dependency is missing or failed, is logged and skipped; the server keeps running. A handler or subscriber that
  throws is logged and the others still run.
- **Console:** type commands into the server's stdin: `help`, `plugins`, `stop`.
- **Docker:** mount `/app/plugins` and `/app/config`; the config folder must be writable by the container user
  (`$APP_UID`, 1654), since a plugin writes its default config there on the first start.

## Versions
A client may join when its protocol version (a hash of `protocol.yaml`) matches the server's, its mod version is the
same release, and its game version is allowed. Otherwise the server sends a message saying which version to install
(for now only in the client's RED4ext log), logs the reason and closes the connection.

- **Mod version:** `git describe --tags --dirty`, stamped into both builds (`shared/version/CyberverseVersion.cmake`,
  `server/Managed/Cyberverse.Server.csproj`). `v0.3.1-12-gaba70a7` belongs to release `v0.3.1`; untagged builds are all
  `dev` and meet each other. Tag releases as `vX.Y.Z`. The Docker build has no `.git`; pass
  `--build-arg CYBERVERSE_VERSION=$(git describe --tags --always --dirty --match "v[0-9]*")`.
- **Game version:** `server.json` in the server's working directory, or the environment variable
  `CYBERVERSE_ALLOWED_GAME_VERSIONS=2.5,2.31`. `"2.5"` allows every 2.5.x; an empty list allows any. The client sends
  the game's product version as RED4ext reports it, e.g. `2.5.0` for patch 2.5.
  ```json
  { "allowedGameVersions": ["2.5"] }
  ```
- **Login packets:** `InitAuth` and `InitAuthResult` are frozen, so any old client can still read why it was rejected
  (see the top of `protocol.yaml`).

## Required Client dependencies
Tested on Cyberpunk 2077 **2.5** (file version 3.0.88.46235, Steam, under Proton GE-Proton10-34) with:

| Mod | Version |
|---|---|
| [RED4ext](https://github.com/wopss/RED4ext) | 1.31.0 |
| [redscript](https://github.com/jac3km4/redscript) | 0.5.32 |
| [Codeware](https://github.com/psiberx/cp2077-codeware) | 1.21.2 |

The plugin only loads on the game version it was built for: `client/red4ext/src/Main.cpp` declares
`RED4EXT_V1_RUNTIME_VERSION_LATEST` of the pinned RED4ext.SDK, and RED4ext refuses a plugin whose runtime doesn't
match the game. After a game patch, bump `client/red4ext/deps/red4ext.sdk` (and `red-lib`) and update the mods above.
## Building on Linux
Everything builds on Linux with podman (or docker, with `CONTAINER=docker`), including the Windows client plugin,
which is cross-compiled with clang-cl, lld-link and the MSVC CRT/Windows SDK fetched by
[xwin](https://github.com/Jake-Shadle/xwin). The first build creates the build image and compiles the dependencies
(about 10 minutes); later builds take seconds. Building the image accepts Microsoft's license for the CRT and SDK files.

| Command | What it does |
|---|---|
| `tools/linux/build-client.sh` | Builds `Cyberverse.Red4Ext.dll` and `GameNetworkingSockets.dll` into `client/red4ext/build/linux-cross/src/` |
| `tools/linux/install-client.sh` | Copies the plugin and the redscript module into a mod folder of an MO2-style mod manager (Amethyst, MO2) and enables it. Set `CYBERVERSE_STAGING` and `CYBERVERSE_PROFILE` for your setup, then deploy in the mod manager |
| `tools/linux/run-server.sh` | Builds the server (native library in the build container, C# in the .NET SDK container) and starts it on UDP port 1337. Needs the .NET 10 runtime on the host |
| `tools/linux/build-server.sh --image` | Builds the deployable server image (`server/Dockerfile`) |

To join, start the game with `--cyberverse-server-address=<ip> --cyberverse-server-port=1337` (with Steam, after
`%command%` in the launch options). Under Proton, RED4ext also needs `WINEDLLOVERRIDES="winmm,version=n,b"`.

Files for the cross-build are in `client/red4ext/cross/`. Two choices there apply to the client in general:
GameNetworkingSockets uses Windows' BCrypt instead of OpenSSL in both the Windows and the Linux build (an overlay port,
since vcpkg can't build OpenSSL for Windows from Linux), and in the cross-build its dependencies (protobuf, abseil) are
linked into `GameNetworkingSockets.dll` statically.
