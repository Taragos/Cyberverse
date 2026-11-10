using Cyberverse.Server.Protocol;
using NLog;

namespace Cyberverse.Plugin.Accounts.Tests;

/// <summary>The login flow against a real database and a Keycloak the test controls.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class LoginFlowTests(PostgresFixture postgres)
{
    private const string Player = "cyberverse-player";
    private readonly FakeKeycloak _keycloak = new();

    private async Task<Harness> NewHarness() => new(await postgres.NewDatabaseAsync(), _keycloak);

    /// <summary>A browser login as the user; returns the session token the client got.</summary>
    private async Task<string> BrowserLogin(Harness h, uint connectionId, string sub, string username = "alice",
        string device = "PC", params string[] roles)
    {
        h.Begin(connectionId, device: device);
        await h.WaitForLoginRequired(connectionId);
        _keycloak.Approve(_keycloak.Started.Last(), sub, username, roles.Length == 0 ? [Player] : roles);
        await h.WaitForDecision(connectionId);
        Assert.True(h.Logins.Accepted.ContainsKey(connectionId), h.Logins.Rejected.GetValueOrDefault(connectionId));
        return h.IssuedToken(connectionId);
    }

    [ContainerFact]
    public async Task ABrowserLoginCreatesTheAccountAndASession()
    {
        var h = await NewHarness();

        h.Begin(1, device: "Gaming-PC");
        var required = await h.WaitForLoginRequired(1);
        Assert.Equal("CODE-1", required.UserCode);
        Assert.Contains("user_code=CODE-1", required.VerificationUri);
        Assert.Equal(600u, required.ExpiresInSeconds);
        Assert.True(h.Logins.IsPending(1));

        _keycloak.Approve("CODE-1", "sub-alice", "alice", Player);
        await h.WaitForDecision(1);

        var identity = h.Logins.Accepted[1];
        Assert.Equal("alice", identity.DisplayName);
        var account = await h.Store.FindAccountAsync("alice");
        Assert.NotNull(account);
        Assert.Equal((account.Id.ToString(), "sub-alice"), (identity.AccountId, account.KeycloakSub));
        var session = Assert.Single(await h.Store.SessionsAsync(account.Id));
        Assert.Equal("Gaming-PC", session.DeviceName);
        Assert.Equal(h.Now.AddDays(30), session.ExpiresAt, TimeSpan.FromSeconds(1));
        // Only the hash is stored.
        Assert.NotNull(await h.Store.FindSessionAsync(h.IssuedToken(1)));
        Assert.NotEqual(h.IssuedToken(1).Length, session.TokenHash.Length);
    }

    [ContainerFact]
    public async Task AnotherPcLogsIntoTheSameAccount()
    {
        var h = await NewHarness();
        var first = await BrowserLogin(h, 1, "sub-alice", device: "Desktop");
        h.Now = h.Now.AddMinutes(1);
        var second = await BrowserLogin(h, 2, "sub-alice", device: "Laptop");

        Assert.NotEqual(first, second);
        Assert.Equal(h.Logins.Accepted[1].AccountId, h.Logins.Accepted[2].AccountId);
        var account = (await h.Store.FindAccountAsync("sub-alice"))!;
        Assert.Equal(["Laptop", "Desktop"], (await h.Store.SessionsAsync(account.Id)).Select(x => x.DeviceName));
    }

    [ContainerFact]
    public async Task ASavedSessionSkipsTheBrowser()
    {
        var h = await NewHarness();
        var token = await BrowserLogin(h, 1, "sub-alice");
        h.Now = h.Now.AddDays(20);

        h.Begin(2, token);
        await h.WaitForDecision(2);

        Assert.Equal(h.Logins.Accepted[1].AccountId, h.Logins.Accepted[2].AccountId);
        Assert.Empty(h.Connections.SentTo<LoginRequired>(2));
        Assert.Single(_keycloak.Started);
        Assert.Equal(1, _keycloak.Refreshes); // the role was checked
        // Using it moves the expiry.
        var session = (await h.Store.FindSessionAsync(token))!;
        Assert.Equal(h.Now.AddDays(30), session.ExpiresAt, TimeSpan.FromSeconds(1));
    }

    [ContainerFact]
    public async Task ExpiredOrRevokedSessionsNeedTheBrowserAgain()
    {
        var h = await NewHarness();
        var expiring = await BrowserLogin(h, 1, "sub-alice", device: "Old");
        var revoked = await BrowserLogin(h, 2, "sub-alice", device: "Other");
        var session = (await h.Store.FindSessionAsync(revoked))!;
        Assert.NotNull(await h.Store.DeleteSessionAsync(session.Id));
        h.Now = h.Now.AddDays(31);

        h.Begin(3, expiring);
        h.Begin(4, revoked);
        h.Begin(5, "made-up-token");

        foreach (var connection in new uint[] { 3, 4, 5 })
        {
            await h.WaitForLoginRequired(connection);
            Assert.True(h.Logins.IsPending(connection));
        }

        Assert.Equal(1, await h.Store.DeleteExpiredSessionsAsync()); // the expired one was still there
    }

    [ContainerFact]
    public async Task AUserWithoutTheRoleIsTurnedAway()
    {
        var h = await NewHarness();
        h.Begin(1);
        await h.WaitForLoginRequired(1);
        _keycloak.Approve("CODE-1", "sub-bob", "bob", "some-other-role");
        await h.WaitForDecision(1);

        Assert.Equal(LoginFlow.NotWhitelisted, h.Logins.Rejected[1]);
        Assert.Null(await h.Store.FindAccountAsync("sub-bob"));
        Assert.Empty(h.Connections.SentTo<SessionIssued>(1));
    }

    [ContainerFact]
    public async Task LosingTheRoleEndsTheSessions()
    {
        var h = await NewHarness();
        var token = await BrowserLogin(h, 1, "sub-alice");
        _keycloak.SetRoles("sub-alice");

        h.Begin(2, token);
        await h.WaitForDecision(2);

        Assert.Equal(LoginFlow.NotWhitelisted, h.Logins.Rejected[2]);
        var account = (await h.Store.FindAccountAsync("sub-alice"))!;
        Assert.Empty(await h.Store.SessionsAsync(account.Id));
        Assert.Null(account.RefreshToken);
    }

    [ContainerFact]
    public async Task AnEndedKeycloakSessionNeedsTheBrowserAgain()
    {
        var h = await NewHarness();
        var token = await BrowserLogin(h, 1, "sub-alice");
        _keycloak.EndSessions("sub-alice");

        h.Begin(2, token);
        await h.WaitForLoginRequired(2);
        _keycloak.Approve(_keycloak.Started.Last(), "sub-alice", "alice", Player);
        await h.WaitForDecision(2);

        Assert.Equal(h.Logins.Accepted[1].AccountId, h.Logins.Accepted[2].AccountId);
        Assert.Null(await h.Store.FindSessionAsync(token));
    }

    [ContainerFact]
    public async Task BannedAccountsAreTurnedAwayOnBothPaths()
    {
        var h = await NewHarness();
        var token = await BrowserLogin(h, 1, "sub-alice");
        var account = (await h.Store.FindAccountAsync("sub-alice"))!;
        await h.Store.AddBanAsync(account.Id, "Cheating", "console", h.Now.AddDays(7));

        h.Begin(2, token);
        await h.WaitForDecision(2);
        h.Begin(3);
        await h.WaitForLoginRequired(3);
        _keycloak.Approve(_keycloak.Started.Last(), "sub-alice", "alice", Player);
        await h.WaitForDecision(3);

        Assert.StartsWith("You are banned from this server until", h.Logins.Rejected[2]);
        Assert.EndsWith("Reason: Cheating", h.Logins.Rejected[3]);

        // Expired bans don't count.
        h.Now = h.Now.AddDays(8);
        h.Begin(4, token);
        await h.WaitForDecision(4);
        Assert.True(h.Logins.Accepted.ContainsKey(4));
    }

    [ContainerFact]
    public async Task WhenKeycloakIsDownARecentRoleCheckStillCounts()
    {
        var h = await NewHarness();
        var token = await BrowserLogin(h, 1, "sub-alice");
        _keycloak.Unreachable = true;

        h.Now = h.Now.AddHours(23);
        h.Begin(2, token);
        await h.WaitForDecision(2);
        h.Now = h.Now.AddHours(2);
        h.Begin(3, token);
        await h.WaitForDecision(3);

        Assert.True(h.Logins.Accepted.ContainsKey(2));
        Assert.Equal(LoginFlow.LoginFailed, h.Logins.Rejected[3]);
        // An outage doesn't cost the player their session.
        Assert.NotNull(await h.Store.FindSessionAsync(token));
    }

    [ContainerFact]
    public async Task ADeniedBrowserLoginIsRejected()
    {
        var h = await NewHarness();
        h.Begin(1);
        await h.WaitForLoginRequired(1);
        _keycloak.Deny("CODE-1");
        await h.WaitForDecision(1);

        Assert.Equal(LoginFlow.BrowserLoginDenied, h.Logins.Rejected[1]);
    }

    [ContainerFact]
    public async Task ACancelledLoginNeverDecides()
    {
        var h = await NewHarness();
        h.Begin(1);
        await h.WaitForLoginRequired(1);

        h.Flow.CancelLogin(1);
        _keycloak.Approve("CODE-1", "sub-alice", "alice", Player);
        await h.Flow.WhenIdle();
        h.Thread.Pump();

        Assert.True(h.Logins.IsPending(1));
        Assert.Empty(h.Connections.SentTo<SessionIssued>(1));
    }

    [ContainerFact]
    public async Task MaintenanceDeletesExpiredSessionsAndKicksPlayersWhoLostTheRole()
    {
        var h = await NewHarness();
        await BrowserLogin(h, 1, "sub-alice");
        await BrowserLogin(h, 2, "sub-bob", "bob");
        await BrowserLogin(h, 3, "sub-carol", "carol");
        _keycloak.SetRoles("sub-bob");
        // Carol's role was checked recently; Alice and Bob are due.
        h.Now = h.Now.AddHours(25);
        await h.Store.UpdateRoleCheckAsync((await h.Store.FindAccountAsync("carol"))!.Id, "unused");
        var kicked = new List<Guid>();

        await AccountsPlugin.RunMaintenanceAsync(h.Store, h.Flow, h.Config, h.Thread, LogManager.GetLogger("test"),
            kicked.Add, h.Now, CancellationToken.None);
        h.Thread.Pump();

        var bob = (await h.Store.FindAccountAsync("bob"))!;
        Assert.Equal([bob.Id], kicked);
        Assert.Empty(await h.Store.SessionsAsync(bob.Id));
        Assert.Single(await h.Store.SessionsAsync((await h.Store.FindAccountAsync("alice"))!.Id));

        h.Now = h.Now.AddDays(31);
        await AccountsPlugin.RunMaintenanceAsync(h.Store, h.Flow, h.Config, h.Thread, LogManager.GetLogger("test"),
            kicked.Add, h.Now, CancellationToken.None);
        Assert.Empty(await h.Store.SessionsAsync((await h.Store.FindAccountAsync("alice"))!.Id));
    }
}
