using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class CleanupFkhFunction : FunctionBase
{
    private readonly ILogger<CleanupFkhFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhCleanupFkh _cleanupFkh;

    public CleanupFkhFunction(
        ILogger<CleanupFkhFunction> logger,
        GitHubAuthService gitHub,
        FkhCleanupFkh cleanupFkh)
    {
        _logger = logger;
        _gitHub = gitHub;
        _cleanupFkh = cleanupFkh;
    }

    [Function("cleanupFkh")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "cleanupFkh")] HttpRequestData req)
        => ExecuteAsync(req, _logger, _gitHub, "cleanupFkh", _cleanupFkh.CleanupFkhAsync);
}