using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Cyberverse.Plugin.Accounts.Keycloak;

/// <summary>
/// The OAuth 2.0 device grant (RFC 8628) against a Keycloak realm, as the confidential client of the server. Every access
/// token is validated (signature from the realm's keys, issuer, lifetime, issued to this client) before its claims are
/// used.
/// </summary>
public sealed class KeycloakClient(AccountsConfig config, HttpClient http) : IKeycloak
{
    // offline_access: a refresh token that survives the browser session, so the role can be checked for weeks.
    private const string Scope = "openid offline_access";
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";

    private readonly JsonWebTokenHandler _handler = new();
    private readonly SemaphoreSlim _keysLock = new(1, 1);
    private IList<SecurityKey>? _keys;

    private string Endpoint(string name) => $"{config.BaseUrl}/protocol/openid-connect/{name}";

    public async Task<DeviceAuthorization> StartDeviceLoginAsync(CancellationToken cancellation)
    {
        using var response = await PostAsync("auth/device", [new("scope", Scope)], cancellation);
        var json = await ReadJsonAsync(response, cancellation);
        if (!response.IsSuccessStatusCode)
        {
            throw new KeycloakException($"Keycloak refused the device login: {Describe(json)}");
        }

        return new DeviceAuthorization(
            json.GetProperty("device_code").GetString()!,
            json.GetProperty("user_code").GetString()!,
            json.GetProperty("verification_uri_complete").GetString()!,
            TimeSpan.FromSeconds(json.GetProperty("expires_in").GetInt32()),
            TimeSpan.FromSeconds(json.TryGetProperty("interval", out var interval) ? interval.GetInt32() : 5));
    }

    public Task<TokenResult> PollDeviceLoginAsync(string deviceCode, CancellationToken cancellation) =>
        RequestTokensAsync([new("grant_type", DeviceGrant), new("device_code", deviceCode)], cancellation);

    public Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken cancellation) =>
        RequestTokensAsync([new("grant_type", "refresh_token"), new("refresh_token", refreshToken)], cancellation);

    private async Task<TokenResult> RequestTokensAsync(List<KeyValuePair<string, string>> form, CancellationToken cancellation)
    {
        using var response = await PostAsync("token", form, cancellation);
        var json = await ReadJsonAsync(response, cancellation);
        if (!response.IsSuccessStatusCode)
        {
            var error = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var e) ? e.GetString() : null;
            return error switch
            {
                "authorization_pending" => new TokenResult(TokenStatus.Pending),
                "slow_down" => new TokenResult(TokenStatus.SlowDown),
                "access_denied" or "expired_token" or "invalid_grant" => new TokenResult(TokenStatus.Denied, Error: Describe(json)),
                _ => throw new KeycloakException($"Keycloak answered {(int)response.StatusCode}: {Describe(json)}"),
            };
        }

        var accessToken = json.GetProperty("access_token").GetString()!;
        var refreshToken = json.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null;
        var token = await ValidateAsync(accessToken, cancellation);
        return new TokenResult(TokenStatus.Ok, new UserTokens(
            token.Subject,
            token.TryGetPayloadValue<string>("preferred_username", out var username) ? username : token.Subject,
            ReadRealmRoles(token),
            refreshToken));
    }

    private async Task<JsonWebToken> ValidateAsync(string accessToken, CancellationToken cancellation)
    {
        for (var attempt = 0; ; attempt++)
        {
            var parameters = new TokenValidationParameters
            {
                ValidIssuer = config.Issuer.TrimEnd('/'),
                // Keycloak's access tokens name the client in azp; aud is "account" unless a mapper adds more.
                ValidateAudience = false,
                IssuerSigningKeys = await GetKeysAsync(refresh: attempt > 0, cancellation),
                ClockSkew = TimeSpan.FromSeconds(30),
            };
            var result = await _handler.ValidateTokenAsync(accessToken, parameters);
            if (result.IsValid)
            {
                var token = (JsonWebToken)result.SecurityToken;
                if (!token.TryGetPayloadValue<string>("azp", out var azp) || azp != config.ClientId)
                {
                    throw new KeycloakException($"The access token was issued to {azp ?? "nobody"}, not {config.ClientId}");
                }

                return token;
            }

            // Keycloak rotated its keys: fetch them once more.
            if (attempt == 0 && result.Exception is SecurityTokenSignatureKeyNotFoundException)
            {
                continue;
            }

            throw new KeycloakException($"Keycloak's access token is invalid: {result.Exception?.Message}", result.Exception);
        }
    }

    private async Task<IList<SecurityKey>> GetKeysAsync(bool refresh, CancellationToken cancellation)
    {
        await _keysLock.WaitAsync(cancellation);
        try
        {
            if (_keys == null || refresh)
            {
                var json = await http.GetStringAsync(Endpoint("certs"), cancellation);
                _keys = new JsonWebKeySet(json).GetSigningKeys();
            }

            return _keys;
        }
        finally
        {
            _keysLock.Release();
        }
    }

    private static HashSet<string> ReadRealmRoles(JsonWebToken token)
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);
        if (token.TryGetPayloadValue<JsonElement>("realm_access", out var access) &&
            access.ValueKind == JsonValueKind.Object &&
            access.TryGetProperty("roles", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var role in list.EnumerateArray())
            {
                if (role.GetString() is { } name)
                {
                    roles.Add(name);
                }
            }
        }

        return roles;
    }

    private async Task<HttpResponseMessage> PostAsync(string endpoint, List<KeyValuePair<string, string>> form,
        CancellationToken cancellation)
    {
        if (!config.AllowHttp && !config.BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new KeycloakException($"{config.BaseUrl} isn't https; set allowHttp for a local setup");
        }

        form.Add(new("client_id", config.ClientId));
        form.Add(new("client_secret", config.ClientSecret));
        return await http.PostAsync(Endpoint(endpoint), new FormUrlEncodedContent(form), cancellation);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        var text = await response.Content.ReadAsStringAsync(cancellation);
        try
        {
            return JsonDocument.Parse(text).RootElement;
        }
        catch (JsonException)
        {
            throw new KeycloakException($"Keycloak answered {(int)response.StatusCode} without JSON: {text[..Math.Min(text.Length, 200)]}");
        }
    }

    private static string Describe(JsonElement json) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var error)
            ? json.TryGetProperty("error_description", out var description) ? $"{error} ({description})" : error.ToString()
            : json.ToString();
}

public sealed class KeycloakException(string message, Exception? inner = null) : Exception(message, inner);
