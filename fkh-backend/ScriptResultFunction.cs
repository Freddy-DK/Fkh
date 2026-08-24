using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class ScriptResultFunction : FunctionBase
{
    private readonly ILogger<ScriptResultFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhScriptJob _scriptJob;

    public ScriptResultFunction(ILogger<ScriptResultFunction> logger, GitHubAuthService gitHub, FkhScriptJob scriptJob)
    {
        _logger = logger;
        _gitHub = gitHub;
        _scriptJob = scriptJob;
    }

    [Function("ScriptResult")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ScriptResult")] HttpRequestData req)
        => ExecuteAsync(req, _logger, _gitHub, "ScriptResult", _scriptJob.GetResultAsync);
}
