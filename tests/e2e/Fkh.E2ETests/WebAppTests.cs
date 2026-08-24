using Xunit;

namespace Fkh.E2ETests;

// HTTP smoke tests for the deployed web app. Runs when a web URL is configured (explicit
// FKH_E2E_WEB_URL or inferred from the backend URL). If the host is unreachable the test
// skips rather than fails, so deployments without a web app are not penalized.
public class WebAppTests
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    [Fact]
    public async Task Web_app_serves_the_spa_shell()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(E2EConfig.WebUrl), "No web URL configured or inferable.");

        HttpResponseMessage response;
        try
        {
            response = await Client.GetAsync(E2EConfig.WebUrl, TestContext.Current.CancellationToken);
        }
        catch (HttpRequestException ex)
        {
            Assert.Skip($"Web app not reachable at {E2EConfig.WebUrl}: {ex.Message}");
            return;
        }

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("id=\"root\"", html);
    }

    [Fact]
    public async Task Web_app_serves_the_service_worker()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(E2EConfig.WebUrl), "No web URL configured or inferable.");

        HttpResponseMessage response;
        try
        {
            response = await Client.GetAsync($"{E2EConfig.WebUrl}/sw.js", TestContext.Current.CancellationToken);
        }
        catch (HttpRequestException ex)
        {
            Assert.Skip($"Web app not reachable at {E2EConfig.WebUrl}: {ex.Message}");
            return;
        }

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}
