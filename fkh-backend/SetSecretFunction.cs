using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class SetSecretFunction : FunctionBase
{
    private readonly ILogger<SetSecretFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhKeyVault _keyVault;

    public SetSecretFunction(ILogger<SetSecretFunction> logger, GitHubAuthService gitHub, FkhKeyVault keyVault)
    {
        _logger = logger;
        _gitHub = gitHub;
        _keyVault = keyVault;
    }

    [Function("SetSecret")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "SetSecret")] HttpRequestData req)
        => ExecuteAsync(req, _logger, _gitHub, "SetSecret", _keyVault.SetSecretAsync, skipClusterCheck: true);
}
