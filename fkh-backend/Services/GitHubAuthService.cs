using System.Net.Http.Headers;
using System.Text.Json;
using Fkh.Models;

namespace Fkh.Services;

public class GitHubAuthService
{
    private readonly HttpClient _httpClient;

    // Reuse a single HttpClient across invocations (best practice in Azure Functions)
    public GitHubAuthService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri("https://api.github.com");
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("AksNodeProvisioner", "1.0"));
    }

    /// <summary>
    /// Validates the GitHub token and returns the authenticated username.
    /// Returns null if the token is invalid or expired.
    /// </summary>
    public async Task<string?> GetAuthenticatedUsernameAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        var content = await response.Content.ReadAsStringAsync();
        var user = JsonSerializer.Deserialize<GitHubUser>(content,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return user?.Login;
    }

    /// <summary>
    /// Checks whether the given GitHub user is an active member of the specified team in the given org.
    /// Uses the user's own token — they can check their own membership with read:org scope.
    /// Returns true only on HTTP 200 with state == "active". All other responses (403, 404, etc.) return false.
    /// </summary>
    public async Task<bool> IsTeamMemberAsync(string token, string org, string team, string username)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/orgs/{org}/teams/{team}/memberships/{username}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return false;

        var content = await response.Content.ReadAsStringAsync();
        var membership = JsonSerializer.Deserialize<GitHubTeamMembership>(content,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Must be explicitly "active" — pending invitations don't count
        return membership?.State == "active";
    }

    /// <summary>
    /// Metadata for a single GitHub Actions artifact produced by a workflow run.
    /// </summary>
    public sealed record BuildArtifact(string Name, long Id, string ArchiveDownloadUrl, bool Expired);

    /// <summary>
    /// Resolves the workflow run id to pull artifacts from using the caller's own token. When
    /// <paramref name="buildId"/> is supplied it is treated as a run id (falling back to a CI/CD
    /// run number match); otherwise the most recent successful AL-Go "CI/CD" run on
    /// <paramref name="branch"/> is returned. All calls use the user's token, so a user without
    /// access to the repository simply cannot resolve or read the build.
    /// </summary>
    public async Task<long> ResolveCicdRunIdAsync(string token, string owner, string repo, string branch, string? buildId)
    {
        if (!string.IsNullOrWhiteSpace(buildId))
        {
            if (!long.TryParse(buildId.Trim(), out var requested))
                throw new InvalidOperationException($"Invalid buildID '{buildId}'. Expected a numeric workflow run id.");

            if (await RunExistsAsync(token, owner, repo, requested))
                return requested;

            var byNumber = await FindCicdRunAsync(token, owner, repo, branch, requestedRunNumber: requested);
            return byNumber
                ?? throw new InvalidOperationException(
                    $"Build '{buildId}' was not found in {owner}/{repo} on branch '{branch}', " +
                    "or you do not have access to it.");
        }

        var latest = await FindCicdRunAsync(token, owner, repo, branch, requestedRunNumber: null);
        return latest ?? throw new InvalidOperationException(
            $"No successful CI/CD build was found on branch '{branch}' in {owner}/{repo}, " +
            "or you do not have access to it.");
    }

    private async Task<bool> RunExistsAsync(string token, string owner, string repo, long runId)
    {
        using var request = BuildAuthorizedGet($"/repos/{owner}/{repo}/actions/runs/{runId}", token);
        using var response = await _httpClient.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    private async Task<long?> FindCicdRunAsync(string token, string owner, string repo, string branch, long? requestedRunNumber)
    {
        // status=success limits the result set to completed successful runs, newest first.
        var url = $"/repos/{owner}/{repo}/actions/runs?branch={Uri.EscapeDataString(branch)}&status=success&per_page=100";
        using var request = BuildAuthorizedGet(url, token);
        using var response = await _httpClient.SendAsync(request);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
            throw new InvalidOperationException(
                $"You do not have access to the workflow runs of {owner}/{repo} (HTTP {(int)response.StatusCode}).");
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Failed to list workflow runs for {owner}/{repo} (HTTP {(int)response.StatusCode}): {body}");
        }

        var doc = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        if (!doc.TryGetProperty("workflow_runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var run in runs.EnumerateArray())
        {
            if (!IsCicdRun(run))
                continue;

            if (requestedRunNumber is not null)
            {
                var runNumber = run.TryGetProperty("run_number", out var rn) && rn.TryGetInt64(out var n) ? n : -1;
                if (runNumber != requestedRunNumber.Value)
                    continue;
            }

            if (run.TryGetProperty("id", out var id) && id.TryGetInt64(out var runId))
                return runId;
        }

        return null;
    }

    private static bool IsCicdRun(JsonElement run)
    {
        var path = run.TryGetProperty("path", out var p) ? p.GetString() : null;
        return path?.EndsWith("CICD.yaml", StringComparison.OrdinalIgnoreCase) ?? false;
    }

    /// <summary>
    /// Lists all non-expired artifacts produced by a workflow run using the caller's own token.
    /// </summary>
    public async Task<List<BuildArtifact>> ListRunArtifactsAsync(string token, string owner, string repo, long runId)
    {
        var artifacts = new List<BuildArtifact>();
        var page = 1;

        while (true)
        {
            using var request = BuildAuthorizedGet(
                $"/repos/{owner}/{repo}/actions/runs/{runId}/artifacts?per_page=100&page={page}", token);
            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"Failed to list artifacts for run {runId} in {owner}/{repo} (HTTP {(int)response.StatusCode}): {body}");
            }

            var doc = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            var totalCount = doc.TryGetProperty("total_count", out var tc) && tc.TryGetInt32(out var t) ? t : 0;

            if (doc.TryGetProperty("artifacts", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                    if (string.IsNullOrEmpty(name)) continue;
                    var id = item.TryGetProperty("id", out var i) && i.TryGetInt64(out var idVal) ? idVal : 0;
                    var downloadUrl = item.TryGetProperty("archive_download_url", out var d) ? d.GetString() : null;
                    var expired = item.TryGetProperty("expired", out var e) && e.ValueKind == JsonValueKind.True;
                    if (string.IsNullOrEmpty(downloadUrl)) continue;
                    artifacts.Add(new BuildArtifact(name, id, downloadUrl, expired));
                }
            }

            if (artifacts.Count >= totalCount || page * 100 >= totalCount)
                break;
            page++;
        }

        return artifacts;
    }

    private static HttpRequestMessage BuildAuthorizedGet(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return request;
    }
}
