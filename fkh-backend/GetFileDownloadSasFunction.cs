using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class GetFileDownloadSasFunction : FunctionBase
{
    private readonly ILogger<GetFileDownloadSasFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhGetFileDownloadSas _getFileDownloadSas;

    public GetFileDownloadSasFunction(
        ILogger<GetFileDownloadSasFunction> logger,
        GitHubAuthService gitHub,
        FkhGetFileDownloadSas getFileDownloadSas)
    {
        _logger = logger;
        _gitHub = gitHub;
        _getFileDownloadSas = getFileDownloadSas;
    }

    [Function("GetFileDownloadSas")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "GetFileDownloadSas")] HttpRequestData req)
        => ExecuteAsync(req, _logger, _gitHub, "GetFileDownloadSas", _getFileDownloadSas.GetDownloadSasAsync);
}