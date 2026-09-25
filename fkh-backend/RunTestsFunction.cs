using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class RunTestsFunction : FunctionBase
{
    private readonly ILogger<RunTestsFunction> _logger;
    private readonly GitHubAuthService _gitHub;
    private readonly FkhRunTests _runTests;

    public RunTestsFunction(ILogger<RunTestsFunction> logger, GitHubAuthService gitHub, FkhRunTests runTests)
    {
        _logger = logger;
        _gitHub = gitHub;
        _runTests = runTests;
    }

    [Function("RunTests")]
    public Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "RunTests")] HttpRequestData req)
        => ExecuteAsync(req, _logger, _gitHub, "RunTests", _runTests.RunTestsAsync);
}