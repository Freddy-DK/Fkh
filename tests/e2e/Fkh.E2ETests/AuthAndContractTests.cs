using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Fkh.E2ETests;

// Security/contract tests against the deployed backend. These need no valid auth and no container,
// so they run whenever a backend is configured. Between failed-auth calls we make a successful
// authenticated `fkh --version` call, which clears the caller IP's brute-force counter so the
// sweep does not lock out the runner.
public class AuthAndContractTests : E2ETest
{
    private const string ProtocolVersion = "1";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static async Task<HttpStatusCode> PostAsync(string route, string? bearerToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{E2EConfig.BackendUrl}/{route}");
        request.Headers.Add("X-Fkh-Protocol-Version", ProtocolVersion);
        request.Headers.Add("X-Fkh-Client", "E2E Tests");
        if (bearerToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Content = new StringContent("{\"parameters\":{}}", Encoding.UTF8, "application/json");

        using var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    // A successful authenticated call clears this IP's failed-attempt counter on the backend.
    private static void ClearBruteForceCounter() => FkhCli.Run(TimeSpan.FromMinutes(2), "--version");

    private static async Task<List<string>> GetAllRoutesAsync()
    {
        var body = await Client.GetStringAsync($"{E2EConfig.BackendUrl}/functions", TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        var routes = doc.RootElement.GetProperty("functions")
            .EnumerateArray()
            .Select(f => f.GetProperty("route").GetString()!)
            .ToList();

        // Hidden functions are absent from the public catalog but must still enforce auth.
        routes.AddRange(["GetContainerDetails", "GetDatabaseUploadSas", "GetDatabaseDownloadSas", "GetFileUploadSas", "GetFileDownloadSas", "Status"]);
        return routes;
    }

    [Fact]
    public async Task Every_function_rejects_unauthenticated_requests()
    {
        Assert.SkipUnless(E2EConfig.IsConfigured, "FKH_E2E_BACKEND_URL is not set.");

        var routes = await GetAllRoutesAsync();
        Assert.NotEmpty(routes);

        var offenders = new List<string>();
        foreach (var route in routes)
        {
            var status = await PostAsync(route, bearerToken: null);
            E2ELog.Line($"  {route}: {(int)status}");
            // 401 (missing token) or 403 (blocked) — anything else means auth wasn't enforced first.
            if (status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                offenders.Add($"{route}={(int)status}");
            ClearBruteForceCounter();
        }

        Assert.True(offenders.Count == 0,
            $"Functions that did not reject unauthenticated calls with 401/403: {string.Join(", ", offenders)}");
    }

    [Fact]
    public async Task Representative_functions_reject_an_invalid_token()
    {
        Assert.SkipUnless(E2EConfig.IsConfigured, "FKH_E2E_BACKEND_URL is not set.");

        // A handful only — each bad token triggers a GitHub token validation on the backend.
        foreach (var route in new[] { "ListContainers", "CreateContainer", "GetCurrentUser", "RemoveContainer" })
        {
            var status = await PostAsync(route, bearerToken: "this-is-not-a-valid-token");
            E2ELog.Line($"  {route} (bad token): {(int)status}");
            Assert.True(status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                $"{route} accepted an invalid token (status {(int)status}).");
            ClearBruteForceCounter();
        }
    }

    [Fact]
    public async Task Nonexistent_function_returns_404()
    {
        Assert.SkipUnless(E2EConfig.IsConfigured, "FKH_E2E_BACKEND_URL is not set.");

        var status = await PostAsync("ThisFunctionDoesNotExist9000", bearerToken: null);
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public void Illegal_parameter_value_is_rejected_with_400()
    {
        Assert.SkipUnless(E2EConfig.IsConfigured, "FKH_E2E_BACKEND_URL is not set.");

        // Authenticated call with an invalid container name — the backend validates and returns 400.
        var result = FkhCli.Run(TimeSpan.FromMinutes(5), "CreateContainer",
            "--name", "bad!name",
            "--artifactUrl", "x",
            "--adminUsername", "admin",
            "--adminPassword", "dummy");

        Assert.NotEqual(0, result.ExitCode);
        var text = result.StdOut + result.StdErr;
        Assert.True(text.Contains("400") || text.Contains("may only contain", StringComparison.OrdinalIgnoreCase),
            $"Expected a 400 validation error for an illegal name. Output:\n{text}");
    }
}
