using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class ListSecretsFunction : FunctionBase
{
    private readonly ILogger<ListSecretsFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhKeyVault _keyVault;

    public ListSecretsFunction(ILogger<ListSecretsFunction> logger, GitHubAuthService gitHub, FkhKeyVault keyVault)
    {
        _logger = logger;
        _gitHub = gitHub;
        _keyVault = keyVault;
    }

    [Function("ListSecrets")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ListSecrets")] HttpRequestData req)
        => ExecuteAsync(req, _logger, _gitHub, "ListSecrets", _keyVault.ListSecretsAsync, skipClusterCheck: true);
}
