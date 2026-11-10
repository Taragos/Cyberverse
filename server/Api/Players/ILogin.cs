namespace Cyberverse.Server.Players;

/// <summary>A client whose versions were accepted and who sent <c>LoginWithSession</c>.</summary>
/// <param name="SessionToken">From an earlier <c>SessionIssued</c>; empty when the client has none.</param>
/// <param name="DeviceName">The PC's name as the client reports it. Not verified; only for showing sessions.</param>
/// <param name="ClientUsername">The username from InitAuth (the Windows account name). Not verified.</param>
public sealed record LoginRequest(uint ConnectionId, string SessionToken, string DeviceName, string ClientUsername);

/// <summary>Who a logged-in player is.</summary>
/// <param name="AccountId">Stable id of the account. The same account can't be online twice.</param>
/// <param name="DisplayName">The name shown to other players and in the logs.</param>
public sealed record LoginIdentity(string AccountId, string DisplayName);

/// <summary>
/// Decides who may log in, e.g. the accounts plugin. Add one in <see cref="Plugins.IPlugin.ConfigureServices"/>; there
/// can be at most one. Without one the server runs in development mode: everyone gets in under their client username.
/// </summary>
public interface ILoginProvider
{
    /// <summary>
    /// Called on the server thread. The provider answers later, on the server thread, with
    /// <see cref="ILoginService.Accept"/> or <see cref="ILoginService.Reject"/>; it may send packets to the client in
    /// between. The server gives up and disconnects after <see cref="ILoginService.Timeout"/>.
    /// </summary>
    void BeginLogin(LoginRequest request);

    /// <summary>The connection closed or timed out before the login was decided. Stop working on it.</summary>
    void CancelLogin(uint connectionId);
}

/// <summary>Logins in progress: connections whose versions were accepted but that have no player yet.</summary>
public interface ILoginService
{
    /// <summary>How long a login may take, browser login included.</summary>
    TimeSpan Timeout { get; }

    /// <summary>Whether the connection is waiting for a login decision.</summary>
    bool IsPending(uint connectionId);

    /// <summary>
    /// Lets the connection in as a player. Another connection logged in with the same account is disconnected. Returns
    /// false when the connection isn't pending (any more), e.g. because it closed.
    /// </summary>
    bool Accept(uint connectionId, LoginIdentity identity);

    /// <summary>Turns the connection away with a message for the player and closes it. False when it isn't pending.</summary>
    bool Reject(uint connectionId, string message);
}
