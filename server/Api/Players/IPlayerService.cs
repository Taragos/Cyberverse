using System.Diagnostics.CodeAnalysis;
using Cyberverse.Server.Types;

namespace Cyberverse.Server.Players;

/// <summary>The authenticated players.</summary>
public interface IPlayerService
{
    IReadOnlyCollection<Player> Players { get; }

    bool TryGetPlayer(uint connectionId, [NotNullWhen(true)] out Player? player);
}
