using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class GetSecretFunction : FunctionBase
{
    private readonly ILogger<GetSecretFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhKeyVault _keyVault;

    public GetSecretFunction(ILogger<GetSecretFunction> logger, GitHubAuthService gitHub, FkhKeyVault keyVault)
    {
        _logger = logger;
        _gitHub = gitHub;
        _keyVault = keyVault;
    }

    [Function("GetSecret")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "GetSecret")] HttpRequestData req)
        => ExecuteAsync(req, _logger, _gitHub, "GetSecret", _keyVault.GetSecretAsync, skipClusterCheck: true);
}
