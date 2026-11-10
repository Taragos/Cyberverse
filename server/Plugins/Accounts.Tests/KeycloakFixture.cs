using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Testcontainers.Keycloak;

namespace Cyberverse.Plugin.Accounts.Tests;

/// <summary>
/// Keycloak with the realm from server/deploy/keycloak, as Docker Compose imports it. Started on first use, since only
/// a few tests need it and it takes a while.
/// </summary>
public sealed partial class KeycloakFixture : IAsyncLifetime
{
    public const string Realm = "cyberverse";
    public const string ClientSecret = "test-secret";

    private readonly SemaphoreSlim _startLock = new(1, 1);
    private KeycloakContainer? _container;
    private readonly HttpClient _http = new();

    public string BaseUrl => _container!.GetBaseAddress().TrimEnd('/');
    public string Issuer => $"{BaseUrl}/realms/{Realm}";

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _http.Dispose();
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }

    public async Task StartAsync()
    {
        await _startLock.WaitAsync();
        try
        {
            if (_container != null)
            {
                return;
            }

            var realm = Path.Combine(AppContext.BaseDirectory, "cyberverse-realm.json");
            var container = new KeycloakBuilder("quay.io/keycloak/keycloak:26.8.2")
                .WithResourceMapping(realm, "/opt/keycloak/data/import/")
                .WithCommand("--import-realm")
                .WithEnvironment("CYBERVERSE_CLIENT_SECRET", ClientSecret)
                .Build();
            await container.StartAsync();
            _container = container;

            // Poll every second instead of every 5, so the device login tests are quick.
            await AdminAsync(HttpMethod.Put, "", new { oauth2DevicePollingInterval = 1 });
        }
        finally
        {
            _startLock.Release();
        }
    }

    public AccountsConfig Config(string database) => new()
    {
        Database = database,
        Issuer = Issuer,
        InternalUrl = null,
        ClientSecret = ClientSecret,
    };

    /// <summary>Creates a user with a password; returns the Keycloak user id (the tokens' sub).</summary>
    public async Task<string> CreateUserAsync(string username, string password)
    {
        using var response = await AdminAsync(HttpMethod.Post, "/users", new
        {
            username,
            email = $"{username}@example.com",
            emailVerified = true,
            firstName = username,
            lastName = "Test",
            enabled = true,
            credentials = new[] { new { type = "password", value = password, temporary = false } },
        });
        return response.Headers.Location!.Segments.Last();
    }

    public async Task SetPlayerRoleAsync(string userId, bool granted)
    {
        using var roleResponse = await AdminAsync(HttpMethod.Get, "/roles/cyberverse-player", null);
        var role = await roleResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var _ = await AdminAsync(granted ? HttpMethod.Post : HttpMethod.Delete,
            $"/users/{userId}/role-mappings/realm", new[] { role });
    }

    /// <summary>What a player does in the browser: open the link, log in. Keycloak then hands the server the tokens.</summary>
    public async Task ApproveDeviceLoginAsync(string verificationUri, string username, string password)
    {
        var browser = new Browser();
        var page = await browser.SendAsync(HttpMethod.Get, verificationUri, null);

        // Login form, then possibly a code confirmation or consent page; the link already carries the code.
        for (var step = 0; step < 4; step++)
        {
            var action = FormAction().Match(page);
            if (!action.Success)
            {
                break;
            }

            var url = new Uri(browser.Url!, WebUtility.HtmlDecode(action.Groups[1].Value)).ToString();
            Dictionary<string, string> form = page.Contains("name=\"password\"")
                ? new() { ["username"] = username, ["password"] = password, ["credentialId"] = "" }
                : page.Contains("name=\"device_user_code\"")
                    ? new() { ["device_user_code"] = UserCode().Match(page).Groups[1].Value }
                    : new() { ["accept"] = "Yes" };
            page = await browser.SendAsync(HttpMethod.Post, url, new FormUrlEncodedContent(form));
        }

        if (!page.Contains("Device Login Successful", StringComparison.OrdinalIgnoreCase))
        {
            var file = Path.Combine(AppContext.BaseDirectory, "last-page.html");
            File.WriteAllText(file, page);
            throw new InvalidOperationException($"The browser login didn't finish; last page in {file}");
        }
    }

    /// <summary>
    /// Follows redirects and keeps cookies like a browser. HttpClient's CookieContainer won't send Keycloak's Secure
    /// cookies over the test's plain http; browsers do for localhost.
    /// </summary>
    private sealed class Browser
    {
        private readonly HttpClient _http = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
        private readonly Dictionary<string, string> _cookies = new();

        /// <summary>The page last shown, after redirects; forms post relative to it.</summary>
        public Uri? Url { get; private set; }

        public async Task<string> SendAsync(HttpMethod method, string url, HttpContent? content)
        {
            for (var redirects = 0; redirects < 10; redirects++)
            {
                using var request = new HttpRequestMessage(method, url) { Content = content };
                if (_cookies.Count > 0)
                {
                    request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(x => $"{x.Key}={x.Value}")));
                }

                using var response = await _http.SendAsync(request);
                if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    foreach (var header in setCookies)
                    {
                        var pair = header.Split(';')[0].Split('=', 2);
                        var expired = header.Contains("Max-Age=0", StringComparison.OrdinalIgnoreCase);
                        if (expired || pair[1].Length == 0)
                        {
                            _cookies.Remove(pair[0]);
                        }
                        else
                        {
                            _cookies[pair[0]] = pair[1];
                        }
                    }
                }

                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    url = new Uri(new Uri(url), location).ToString();
                    method = HttpMethod.Get;
                    content = null;
                    continue;
                }

                Url = new Uri(url);
                var page = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    var file = Path.Combine(AppContext.BaseDirectory, "last-page.html");
                    File.WriteAllText(file, page);
                    throw new InvalidOperationException($"{method} {url}: {(int)response.StatusCode}, page in {file}");
                }

                return page;
            }

            throw new InvalidOperationException("Too many redirects");
        }
    }

    [GeneratedRegex("<form[^>]*action=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex FormAction();

    [GeneratedRegex("name=\"device_user_code\"[^>]*value=\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex UserCode();

    private async Task<HttpResponseMessage> AdminAsync(HttpMethod method, string path, object? body)
    {
        using var tokenResponse = await _http.PostAsync($"{BaseUrl}/realms/master/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "admin-cli",
                ["username"] = KeycloakBuilder.DefaultUsername,
                ["password"] = KeycloakBuilder.DefaultPassword,
            }));
        tokenResponse.EnsureSuccessStatusCode();
        var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();

        var request = new HttpRequestMessage(method, $"{BaseUrl}/admin/realms/{Realm}{path}");
        request.Headers.Authorization = new("Bearer", token);
        if (body != null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Keycloak admin {method} {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        return response;
    }
}
