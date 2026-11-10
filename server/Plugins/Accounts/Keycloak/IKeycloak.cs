namespace Cyberverse.Plugin.Accounts.Keycloak;

/// <summary>The start of a device login: the player opens VerificationUriComplete and logs in.</summary>
public sealed record DeviceAuthorization(string DeviceCode, string UserCode, string VerificationUriComplete,
    TimeSpan ExpiresIn, TimeSpan Interval);

public enum TokenStatus
{
    /// <summary>The tokens are in <see cref="TokenResult.Tokens"/>.</summary>
    Ok,

    /// <summary>Device login: the player hasn't finished in the browser yet.</summary>
    Pending,

    /// <summary>Device login: poll less often.</summary>
    SlowDown,

    /// <summary>The player declined, the code expired, or the refresh token no longer works (user disabled, logged out).</summary>
    Denied,
}

/// <summary>A user's tokens, after the access token was validated.</summary>
/// <param name="Sub">The Keycloak user id.</param>
/// <param name="Roles">The realm roles in the access token.</param>
public sealed record UserTokens(string Sub, string Username, IReadOnlySet<string> Roles, string? RefreshToken);

public sealed record TokenResult(TokenStatus Status, UserTokens? Tokens = null, string? Error = null);

/// <summary>What the accounts plugin needs from Keycloak. Network errors throw; Keycloak's answers don't.</summary>
public interface IKeycloak
{
    Task<DeviceAuthorization> StartDeviceLoginAsync(CancellationToken cancellation);

    Task<TokenResult> PollDeviceLoginAsync(string deviceCode, CancellationToken cancellation);

    Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken cancellation);
}
