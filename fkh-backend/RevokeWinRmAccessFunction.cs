using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class RevokeWinRmAccessFunction : FunctionBase
{
    private readonly ILogger<RevokeWinRmAccessFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhAllowWinRmAccess _winRmAccess;

    public RevokeWinRmAccessFunction(ILogger<RevokeWinRmAccessFunction> logger, GitHubAuthService gitHub, FkhAllowWinRmAccess winRmAccess)
    {
        _logger = logger;
        _gitHub = gitHub;
        _winRmAccess = winRmAccess;
    }

    [Function("RevokeWinRmAccess")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "RevokeWinRmAccess")] HttpRequestData req)
        => ExecuteAsync(req, _logger, _gitHub, "RevokeWinRmAccess", _winRmAccess.RevokeWinRmAccessAsync);
}
