using System.Net;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Fkh;

/// <summary>
/// Proxies GitHub OAuth device flow requests to avoid browser CORS restrictions.
/// GitHub's /login/device/code and /login/oauth/access_token endpoints do not
/// return CORS headers, so browser-based SPAs cannot call them directly.
/// These two endpoints simply forward the request body to GitHub and return the response.
/// </summary>
public class DeviceFlowProxyFunction
{
    private static readonly HttpClient _http = new();

    [Function("DeviceFlowCode")]
    public async Task<HttpResponseData> DeviceFlowCodeAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/device/code")] HttpRequestData req)
    {
        return await ProxyToGitHubAsync(req, "https://github.com/login/device/code");
    }

    [Function("DeviceFlowToken")]
    public async Task<HttpResponseData> DeviceFlowTokenAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/device/token")] HttpRequestData req)
    {
        return await ProxyToGitHubAsync(req, "https://github.com/login/oauth/access_token");
    }

    private static async Task<HttpResponseData> ProxyToGitHubAsync(HttpRequestData req, string targetUrl)
    {
        var body = await req.ReadAsStringAsync() ?? "";

        using var ghRequest = new HttpRequestMessage(HttpMethod.Post, targetUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        ghRequest.Headers.Accept.ParseAdd("application/json");

        using var ghResponse = await _http.SendAsync(ghRequest);
        var responseBody = await ghResponse.Content.ReadAsStringAsync();

        var response = req.CreateResponse((HttpStatusCode)ghResponse.StatusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(responseBody);
        return response;
    }
}
