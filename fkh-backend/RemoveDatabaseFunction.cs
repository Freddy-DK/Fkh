using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class RemoveDatabaseFunction : FunctionBase
{
    private readonly ILogger<RemoveDatabaseFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhRemoveDatabase _removeDatabase;

    public RemoveDatabaseFunction(
        ILogger<RemoveDatabaseFunction> logger,
        GitHubAuthService gitHub,
        FkhRemoveDatabase removeDatabase)
    {
        _logger = logger;
        _gitHub = gitHub;
        _removeDatabase = removeDatabase;
    }

    [Function("RemoveDatabase")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "RemoveDatabase")] HttpRequestData req)
        => ExecuteAsync(req, _logger, _gitHub, "RemoveDatabase", _removeDatabase.RemoveDatabaseAsync);
}
