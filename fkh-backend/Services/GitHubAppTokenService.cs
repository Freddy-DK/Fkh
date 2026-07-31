using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

namespace Fkh.Services;

public class GitHubAppTokenService
{
    private readonly HttpClient _http;
    private readonly ILogger<GitHubAppTokenService> _logger;
    private readonly string _appId;
    private readonly string _installationId;
    private readonly string _privateKeyPem;
    private readonly string _repoOwner;
    private readonly string _repoName;

    public GitHubAppTokenService(HttpClient http, ILogger<GitHubAppTokenService> logger)
    {
        _http = http;
        _logger = logger;
        _appId = Environment.GetEnvironmentVariable("GITHUB_APP_ID")
            ?? throw new InvalidOperationException("GITHUB_APP_ID is not configured.");
        _installationId = Environment.GetEnvironmentVariable("GITHUB_APP_INSTALLATION_ID")
            ?? throw new InvalidOperationException("GITHUB_APP_INSTALLATION_ID is not configured.");
        _privateKeyPem = Environment.GetEnvironmentVariable("GITHUB_APP_PRIVATE_KEY")
            ?? throw new InvalidOperationException("GITHUB_APP_PRIVATE_KEY is not configured.");
        _repoOwner = Environment.GetEnvironmentVariable("GITHUB_REPO_OWNER")
            ?? throw new InvalidOperationException("GITHUB_REPO_OWNER is not configured.");
        _repoName = Environment.GetEnvironmentVariable("GITHUB_REPO_NAME")
            ?? throw new InvalidOperationException("GITHUB_REPO_NAME is not configured.");
    }

    public async Task TriggerCreateImagesWorkflowAsync(string artifactUrl)
    {
        var token = await GetInstallationTokenAsync();

        if (await IsCreateImagesWorkflowRunningAsync(token, artifactUrl))
        {
            _logger.LogInformation(
                "A createImages workflow for {ArtifactUrl} is already queued or in progress. Skipping dispatch.",
                artifactUrl);
            return;
        }

        var url = $"https://api.github.com/repos/{_repoOwner}/{_repoName}/actions/workflows/CreateImages.yml/dispatches";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("FKH", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        request.Content = JsonContent.Create(new
        {
            @ref = "main",
            inputs = new { artifactUrls = artifactUrl }
        });

        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Failed to trigger workflow (HTTP {(int)response.StatusCode} from {url}): {body}");
        }

        _logger.LogInformation("Triggered createImages workflow for {ArtifactUrl}", artifactUrl);
    }

    /// <summary>
    /// Returns true when a Create Images workflow run for the given artifact URL is already
    /// queued or in progress, so we avoid dispatching a duplicate run on every retry.
    /// The workflow sets its run name to include the artifact URL (see run-name in CreateImages.yml).
    /// </summary>
    private async Task<bool> IsCreateImagesWorkflowRunningAsync(string token, string artifactUrl)
    {
        foreach (var status in new[] { "queued", "in_progress" })
        {
            var url = $"https://api.github.com/repos/{_repoOwner}/{_repoName}/actions/workflows/CreateImages.yml/runs?status={status}&per_page=100";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("FKH", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                // Don't block the dispatch if the check itself fails; just log and continue.
                _logger.LogWarning(
                    "Failed to list createImages workflow runs (HTTP {Status} from {Url}): {Body}",
                    (int)response.StatusCode, url, body);
                continue;
            }

            JsonElement doc;
            try
            {
                doc = await response.Content.ReadFromJsonAsync<JsonElement>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse createImages workflow runs response from {Url}. Continuing with dispatch.", url);
                continue;
            }
            if (doc.TryGetProperty("workflow_runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
            {
                foreach (var run in runs.EnumerateArray())
                {
                    var name = run.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var displayTitle = run.TryGetProperty("display_title", out var d) ? d.GetString() : null;

                    if ((name?.Contains(artifactUrl, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (displayTitle?.Contains(artifactUrl, StringComparison.OrdinalIgnoreCase) ?? false))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private async Task<string> GetInstallationTokenAsync()
    {
        var jwt = CreateJwt();

        var url = $"https://api.github.com/app/installations/{_installationId}/access_tokens";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("FKH", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Failed to get GitHub App installation token (HTTP {(int)response.StatusCode} from {url}): {body}");
        }

        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("GitHub returned an empty installation token.");
    }

    private string CreateJwt()
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(_privateKeyPem);

        var key = new RsaSecurityKey(rsa);
        var creds = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _appId,
            IssuedAt = now.AddSeconds(-60),   // clock skew tolerance
            Expires = now.AddMinutes(10),
            SigningCredentials = creds
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(descriptor);
        return handler.WriteToken(token);
    }
}
