using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class GetContainerDetailsFunction : FunctionBase
{
    private readonly ILogger<GetContainerDetailsFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhGetContainerDetails _getContainerDetails;

    public GetContainerDetailsFunction(ILogger<GetContainerDetailsFunction> logger, GitHubAuthService gitHub, FkhGetContainerDetails getContainerDetails)
    {
        _logger = logger;
        _gitHub = gitHub;
        _getContainerDetails = getContainerDetails;
    }

    [Function("GetContainerDetails")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "GetContainerDetails")] HttpRequestData req)
        => ExecuteAsync(req, _logger, _gitHub, "GetContainerDetails", _getContainerDetails.GetContainerDetailsAsync);
}
