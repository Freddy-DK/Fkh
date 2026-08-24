using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class PublishAppsFromBuildFunction : FunctionBase
{
    private readonly ILogger<PublishAppsFromBuildFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhPublishAppsFromBuild _publishAppsFromBuild;

    public PublishAppsFromBuildFunction(
        ILogger<PublishAppsFromBuildFunction> logger,
        GitHubAuthService gitHub,
        FkhPublishAppsFromBuild publishAppsFromBuild)
    {
        _logger = logger;
        _gitHub = gitHub;
        _publishAppsFromBuild = publishAppsFromBuild;
    }

    [Function("PublishAppsFromBuild")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "PublishAppsFromBuild")] HttpRequestData req)
        => ExecuteAsync(req, _logger, _gitHub, "PublishAppsFromBuild", _publishAppsFromBuild.PublishAppsFromBuildAsync);
}
