using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class ListFilesFunction : FunctionBase
{
    private readonly ILogger<ListFilesFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhListFiles _listFiles;

    public ListFilesFunction(
        ILogger<ListFilesFunction> logger,
        GitHubAuthService gitHub,
        FkhListFiles listFiles)
    {
        _logger = logger;
        _gitHub = gitHub;
        _listFiles = listFiles;
    }

    [Function("ListFiles")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ListFiles")] HttpRequestData req)
        => ExecuteAsync(req, _logger, _gitHub, "ListFiles", _listFiles.ListFilesAsync);
}
