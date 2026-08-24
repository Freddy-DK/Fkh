using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class ScriptStatusFunction : FunctionBase
{
    private readonly ILogger<ScriptStatusFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhScriptJob _scriptJob;

    public ScriptStatusFunction(ILogger<ScriptStatusFunction> logger, GitHubAuthService gitHub, FkhScriptJob scriptJob)
    {
        _logger = logger;
        _gitHub = gitHub;
        _scriptJob = scriptJob;
    }

    [Function("ScriptStatus")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ScriptStatus")] HttpRequestData req)
        => ExecuteAsync(req, _logger, _gitHub, "ScriptStatus", _scriptJob.GetStatusAsync);
}
