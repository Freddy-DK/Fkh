using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class GetFileUploadSasFunction : FunctionBase
{
    private readonly ILogger<GetFileUploadSasFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhGetFileUploadSas _getFileUploadSas;

    public GetFileUploadSasFunction(
        ILogger<GetFileUploadSasFunction> logger,
        GitHubAuthService gitHub,
        FkhGetFileUploadSas getFileUploadSas)
    {
        _logger = logger;
        _gitHub = gitHub;
        _getFileUploadSas = getFileUploadSas;
    }

    [Function("GetFileUploadSas")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "GetFileUploadSas")] HttpRequestData req)
        => ExecuteAsync(req, _logger, _gitHub, "GetFileUploadSas", _getFileUploadSas.GetUploadSasAsync);
}