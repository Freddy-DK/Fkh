using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class ImportTestToolkitFunction : FunctionBase
{
    private readonly ILogger<ImportTestToolkitFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhImportTestToolkit _importTestToolkit;

    public ImportTestToolkitFunction(
        ILogger<ImportTestToolkitFunction> logger,
        GitHubAuthService gitHub,
        FkhImportTestToolkit importTestToolkit)
    {
        _logger = logger;
        _gitHub = gitHub;
        _importTestToolkit = importTestToolkit;
    }

    [Function("ImportTestToolkit")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ImportTestToolkit")] HttpRequestData req)
        => ExecuteAsync(req, _logger, _gitHub, "ImportTestToolkit", _importTestToolkit.ImportTestToolkitAsync);
}