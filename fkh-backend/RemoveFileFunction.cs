using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class RemoveFileFunction : FunctionBase
{
    private readonly ILogger<RemoveFileFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhRemoveFile _removeFile;

    public RemoveFileFunction(
        ILogger<RemoveFileFunction> logger,
        GitHubAuthService gitHub,
        FkhRemoveFile removeFile)
    {
        _logger = logger;
        _gitHub = gitHub;
        _removeFile = removeFile;
    }

    [Function("RemoveFile")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "RemoveFile")] HttpRequestData req)
        => ExecuteAsync(req, _logger, _gitHub, "RemoveFile", _removeFile.RemoveFileAsync);
}
