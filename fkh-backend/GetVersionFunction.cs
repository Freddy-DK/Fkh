using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class GetVersionFunction : FunctionBase
{
    private readonly ILogger<GetVersionFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhGetVersion _version;

    public GetVersionFunction(ILogger<GetVersionFunction> logger, GitHubAuthService gitHub, FkhGetVersion version)
    {
        _logger = logger;
        _gitHub = gitHub;
        _version = version;
    }

    [Function("GetVersion")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "GetVersion")] HttpRequestData req)
        => ExecuteAsync(req, _logger, _gitHub, "GetVersion", _version.GetVersionAsync, skipClusterCheck: true);
}
