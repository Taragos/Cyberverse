using Cyberverse.Plugin.Accounts.Keycloak;

namespace Cyberverse.Plugin.Accounts.Tests;

/// <summary>The whole login against a real Keycloak with the realm from server/deploy, browser part included.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class KeycloakLoginTests(PostgresFixture postgres, KeycloakFixture keycloak)
{
    private const string Password = "correct horse battery";

    private async Task<(Harness Harness, HttpClient Http)> Start()
    {
        await keycloak.StartAsync();
        var config = keycloak.Config(await postgres.NewDatabaseAsync());
        var http = new HttpClient();
        return (new Harness(config.Database, new KeycloakClient(config, http), config, realDelays: true), http);
    }

    [ContainerFact]
    public async Task WhitelistedPlayersGetInAndKeepTheirSessionUntilTheRoleIsGone()
    {
        var (h, http) = await Start();
        using var _ = http;
        var username = "player" + Guid.NewGuid().ToString("N")[..8];
        var userId = await keycloak.CreateUserAsync(username, Password);

        // Registered but not whitelisted.
        h.Begin(1);
        var required = await h.WaitForLoginRequired(1);
        Assert.StartsWith(keycloak.Issuer, required.VerificationUri);
        await keycloak.ApproveDeviceLoginAsync(required.VerificationUri, username, Password);
        await h.WaitForDecision(1);
        Assert.Equal(LoginFlow.NotWhitelisted, h.Logins.Rejected[1]);

        // An admin assigns the role; the next browser login gets in.
        await keycloak.SetPlayerRoleAsync(userId, true);
        h.Begin(2);
        required = await h.WaitForLoginRequired(2);
        await keycloak.ApproveDeviceLoginAsync(required.VerificationUri, username, Password);
        await h.WaitForDecision(2);
        Assert.Equal(username, h.Logins.Accepted[2].DisplayName);
        var account = (await h.Store.FindAccountAsync(userId))!;
        Assert.NotNull(account.RefreshToken);

        // The saved session gets in without the browser; the role is checked with the offline refresh token.
        var token = h.IssuedToken(2);
        h.Begin(3, token);
        await h.WaitForDecision(3);
        Assert.Equal(h.Logins.Accepted[2].AccountId, h.Logins.Accepted[3].AccountId);
        Assert.Empty(h.Connections.SentTo<Cyberverse.Server.Protocol.LoginRequired>(3));

        // The role is taken away: the session no longer works.
        await keycloak.SetPlayerRoleAsync(userId, false);
        h.Begin(4, token);
        await h.WaitForDecision(4);
        Assert.Equal(LoginFlow.NotWhitelisted, h.Logins.Rejected[4]);
        Assert.Empty(await h.Store.SessionsAsync(account.Id));
    }

    [ContainerFact]
    public async Task AWrongClientSecretIsRefused()
    {
        await keycloak.StartAsync();
        var config = keycloak.Config("unused");
        config.ClientSecret = "wrong";
        using var http = new HttpClient();

        // A wrong client secret: Keycloak refuses to start the device login at all.
        await Assert.ThrowsAsync<KeycloakException>(() => new KeycloakClient(config, http).StartDeviceLoginAsync(default));
    }
}
