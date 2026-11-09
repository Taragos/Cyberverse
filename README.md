# CyberVerse - The Cyberpunk 2077 Multiplayer Framework

> [!IMPORTANT]  
> In it's current form, CyberVerse is _only_  relevant for developers/modders. Regular users may
> struggle getting it to run and we don't actively support that, either. There are a lot of missing
> pieces before we can start to recommend using CyberVerse to play on.

## What is Cyberverse exactly?
Cyberverse is a very much in progress implementation of a multiplayer into Cyberpunk 2077 v2.1.  
It being a "framework" additionally means that the server side is flexibly extended by Plugins that provide additional behavior. As such, it will be a good choice for Roleplaying Servers.

Besides being extendable, it also features a working sandbox mode, that already handles most of the hard topics
in Networking (such as entity sync) for you. You should be able to replace every component at will, though.

## Code Structure
CyberVerse actually consists of 4 projects: Two client side projects and two server side projects, or put
differently: Two c++ projects that mostly handle the network serialization and two application layers that
handle server side logic and replicating in game.  
Since it has different projects, people with multiple skillsets are welcome to collaborate, but please contact
us before implementing a bigger feature so we can talk about the concepts relevant to that.
Additionally, there's `shared\protocol` that handles the serialization objects.

### Server.Native
This is the C++ support DLL of the game server, it really exclusively handles the lowest levels of networking,
including serialization, which is why you (currently, sadly) need to add switch-case statements when implementing
a new packet in protocol.

### Server.Managed
The main logic of the game server, also needs to know about the new enum and structs for new packets. When 
adhereing to a C-ABI, they can comfortably marshalled via P/Invoke. It will then perform high level logic, 
bookkeeping, call potential plugins and then enqueue packets on it's own again.

### Client.RED4extModule
This is the C++ side in the game that handles networking and bridges between the redscript utility functions
and implements packets that are sent from the server (clientbound) as well as sending packets when a certain
action has occured.

### Client.Redscript
This is the layer that is closest to the game logic, it's job is basically to listen for events and apply server
side changes into the game. Future use cases will also be providing a server browser and chat UI and maybe
interfacing with custom redscript plugins, written by the related server owners (such as custom packets/payloads,
triggering packets, callbacks on packets).

## Required Client dependencies
- RED4ext
- Redscript
- Codeware
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
