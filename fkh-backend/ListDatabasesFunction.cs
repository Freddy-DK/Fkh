using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class ListDatabasesFunction : FunctionBase
{
    private readonly ILogger<ListDatabasesFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhListDatabases _listDatabases;

    public ListDatabasesFunction(
        ILogger<ListDatabasesFunction> logger,
        GitHubAuthService gitHub,
        FkhListDatabases listDatabases)
    {
        _logger = logger;
        _gitHub = gitHub;
        _listDatabases = listDatabases;
    }

    [Function("ListDatabases")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ListDatabases")] HttpRequestData req)
        => ExecuteAsync(req, _logger, _gitHub, "ListDatabases", _listDatabases.ListDatabasesAsync);
}
