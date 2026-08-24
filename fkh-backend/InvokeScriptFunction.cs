using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class InvokeScriptFunction : FunctionBase
{
    private readonly ILogger<InvokeScriptFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhInvokeScript _invokeScript;

    public InvokeScriptFunction(ILogger<InvokeScriptFunction> logger, GitHubAuthService gitHub, FkhInvokeScript invokeScript)
    {
        _logger = logger;
        _gitHub = gitHub;
        _invokeScript = invokeScript;
    }

    [Function("InvokeScript")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "InvokeScript")] HttpRequestData req)
    {
        var contentType = req.Headers.GetValues("Content-Type").FirstOrDefault() ?? "";
        if (contentType.Contains("multipart/form-data"))
        {
            return ExecuteWithFileAsync(req, _logger, _gitHub, "InvokeScript", _invokeScript.InvokeScriptWithFileAsync);
        }
        return ExecuteAsync(req, _logger, _gitHub, "InvokeScript", p => _invokeScript.InvokeScriptAsync(p));
    }
}
