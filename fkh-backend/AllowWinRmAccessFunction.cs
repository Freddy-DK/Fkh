using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class AllowWinRmAccessFunction : FunctionBase
{
    private readonly ILogger<AllowWinRmAccessFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhAllowWinRmAccess _winRmAccess;

    public AllowWinRmAccessFunction(ILogger<AllowWinRmAccessFunction> logger, GitHubAuthService gitHub, FkhAllowWinRmAccess winRmAccess)
    {
        _logger = logger;
        _gitHub = gitHub;
        _winRmAccess = winRmAccess;
    }

    [Function("AllowWinRmAccess")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "AllowWinRmAccess")] HttpRequestData req)
        => ExecuteAsync(req, _logger, _gitHub, "AllowWinRmAccess", _winRmAccess.AllowWinRmAccessAsync);
}
