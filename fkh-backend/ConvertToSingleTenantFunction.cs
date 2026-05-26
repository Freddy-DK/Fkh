using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class ConvertToSingleTenantFunction : FunctionBase
{
    private readonly ILogger<ConvertToSingleTenantFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhConvertToSingleTenant _convertToSingleTenant;

    public ConvertToSingleTenantFunction(
        ILogger<ConvertToSingleTenantFunction> logger,
        GitHubAuthService gitHub,
        FkhConvertToSingleTenant convertToSingleTenant)
    {
        _logger = logger;
        _gitHub = gitHub;
        _convertToSingleTenant = convertToSingleTenant;
    }

    [Function("ConvertToSingleTenant")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ConvertToSingleTenant")] HttpRequestData req)
        => ExecuteFireAndForgetAsync(req, _logger, _gitHub, "ConvertToSingleTenant", _convertToSingleTenant.ConvertToSingleTenantAsync);
}
