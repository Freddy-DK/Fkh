using System.Diagnostics;
using System.Text.Json;

/// <summary>
/// Provides a uniform way to obtain and refresh authentication tokens.
/// When --useOIDC is specified, fetches the token from GitHub Actions OIDC endpoint
/// and automatically refreshes it every 3 minutes. Otherwise falls back to the
/// standard token resolution chain (GH_TOKEN, gh auth token).
/// </summary>
sealed class TokenProvider
{
    private static readonly TimeSpan OidcRefreshInterval = TimeSpan.FromMinutes(3);

    private readonly bool _useOidc;
    private readonly string? _ghUser;

    private string? _cachedToken;
    private DateTime _tokenFetchedAt;

    /// <summary>
    /// Creates a TokenProvider.
    /// </summary>
    /// <param name="useOidc">If true, fetches OIDC token from GitHub Actions environment and skips all other mechanisms.</param>
    /// <param name="ghUser">GitHub user for gh auth token (only used when useOidc is false).</param>
    public TokenProvider(bool useOidc, string? ghUser = null)
    {
        _useOidc = useOidc;
        _ghUser = ghUser;
    }

    /// <summary>
    /// Gets a valid token. If --useOIDC is active and the cached token is older than 3 minutes,
    /// fetches a fresh one. For non-OIDC modes, returns the same token every time.
    /// </summary>
    public async Task<string> GetTokenAsync()
    {
        if (_useOidc)
        {
            if (_cachedToken is null || DateTime.UtcNow - _tokenFetchedAt >= OidcRefreshInterval)
            {
                _cachedToken = await FetchOidcTokenAsync();
                _tokenFetchedAt = DateTime.UtcNow;
            }
            return _cachedToken;
        }

        // Non-OIDC: resolve once and cache
        _cachedToken ??= ResolveStaticToken();
        return _cachedToken;
    }

    /// <summary>
    /// Synchronous version for contexts where async is not practical.
    /// For OIDC mode, calls the async method synchronously.
    /// </summary>
    public string GetToken()
    {
        if (_useOidc)
        {
            if (_cachedToken is null || DateTime.UtcNow - _tokenFetchedAt >= OidcRefreshInterval)
            {
                _cachedToken = FetchOidcTokenAsync().GetAwaiter().GetResult();
                _tokenFetchedAt = DateTime.UtcNow;
            }
            return _cachedToken;
        }

        _cachedToken ??= ResolveStaticToken();
        return _cachedToken;
    }

    private static async Task<string> FetchOidcTokenAsync()
    {
        var requestUrl = Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_URL");
        var requestToken = Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_TOKEN");

        if (string.IsNullOrWhiteSpace(requestUrl))
            throw new InvalidOperationException(
                "--useOIDC requires the ACTIONS_ID_TOKEN_REQUEST_URL environment variable (available in GitHub Actions with 'id-token: write' permission).");

        if (string.IsNullOrWhiteSpace(requestToken))
            throw new InvalidOperationException(
                "--useOIDC requires the ACTIONS_ID_TOKEN_REQUEST_TOKEN environment variable (available in GitHub Actions with 'id-token: write' permission).");

        var url = requestUrl.Contains('?')
            ? $"{requestUrl}&audience=api://AzureADTokenExchange"
            : $"{requestUrl}?audience=api://AzureADTokenExchange";

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"Bearer {requestToken}");

        var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Failed to fetch OIDC token from GitHub Actions ({(int)response.StatusCode}): {body}");

        // GitHub Actions OIDC endpoint returns { "value": "<token>" }
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("value", out var valueProp) && valueProp.ValueKind == JsonValueKind.String)
        {
            var token = valueProp.GetString();
            if (!string.IsNullOrWhiteSpace(token))
                return token;
        }

        throw new InvalidOperationException(
            "OIDC token response did not contain a 'value' property with a valid token.");
    }

    private string ResolveStaticToken()
    {
        // 1. GH_TOKEN environment variable
        var token = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
            return token;

        // 2. gh auth token CLI
        var psi = new ProcessStartInfo
        {
            FileName = "gh",
            ArgumentList = { "auth", "token" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(_ghUser))
        {
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(_ghUser);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start 'gh'.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Could not get GitHub token from 'gh auth token'. Run 'gh auth login' first. " +
                (string.IsNullOrWhiteSpace(stderr) ? string.Empty : $"Details: {stderr.Trim()}"));
        }

        token = stdout.Trim();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("'gh auth token' returned an empty token.");

        return token;
    }
}
