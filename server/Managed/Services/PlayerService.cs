using System.Diagnostics.CodeAnalysis;
using Cyberverse.Server.Players;
using Cyberverse.Server.Types;

namespace Cyberverse.Server.Services;

public class PlayerService : IPlayerService
{
    public readonly Dictionary<uint, Player> ConnectedPlayers = new();

    public IReadOnlyCollection<Player> Players => ConnectedPlayers.Values;

    public bool TryGetPlayer(uint connectionId, [NotNullWhen(true)] out Player? player) =>
        ConnectedPlayers.TryGetValue(connectionId, out player);
}
