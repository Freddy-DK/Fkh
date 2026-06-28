using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class GetCurrentUserFunction : FunctionBase
{
    private readonly ILogger<GetCurrentUserFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhGetCurrentUser _getCurrentUser;

    public GetCurrentUserFunction(ILogger<GetCurrentUserFunction> logger, GitHubAuthService gitHub, FkhGetCurrentUser getCurrentUser)
    {
        _logger = logger;
        _gitHub = gitHub;
        _getCurrentUser = getCurrentUser;
    }

    [Function("GetCurrentUser")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "GetCurrentUser")] HttpRequestData req)
        => ExecuteAsync(req, _logger, _gitHub, "GetCurrentUser", _getCurrentUser.GetCurrentUserAsync, skipClusterCheck: true);
}