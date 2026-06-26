using System.Net;
using Fkh.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Fkh;

public class GetSupportedClientVersionsFunction
{
    [Function("GetSupportedClientVersions")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "GetSupportedClientVersions")] HttpRequestData req)
    {
        var clients = await ProtocolVersionConfig.GetSupportedClientVersionsAsync();
        if (clients is null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new
            {
                message = $"No supported client versions configured for protocol version {ProtocolVersionConfig.CurrentVersion}."
            });
            return notFound;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            protocolVersion = ProtocolVersionConfig.CurrentVersion,
            clients
        });
        return response;
    }
}