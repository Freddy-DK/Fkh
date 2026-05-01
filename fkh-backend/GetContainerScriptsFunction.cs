using System.IO.Compression;
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Fkh;

public class GetContainerScriptsFunction
{
    [Function("GetContainerScripts")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "containerscripts")] HttpRequestData req)
    {
        var scriptsDir = Path.Combine(AppContext.BaseDirectory, "ContainerScripts");
        if (!Directory.Exists(scriptsDir))
        {
            var response = req.CreateResponse(HttpStatusCode.NotFound);
            await response.WriteStringAsync("ContainerScripts folder not found.");
            return response;
        }

        var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.GetFiles(scriptsDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = "ContainerScripts/" + Path.GetRelativePath(scriptsDir, file).Replace('\\', '/');
                var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var fileStream = File.OpenRead(file);
                await fileStream.CopyToAsync(entryStream);
            }
        }

        memoryStream.Position = 0;
        var resp = req.CreateResponse(HttpStatusCode.OK);
        resp.Headers.Add("Content-Type", "application/zip");
        resp.Headers.Add("Content-Disposition", "attachment; filename=ContainerScripts.zip");
        await resp.Body.WriteAsync(memoryStream.ToArray());
        return resp;
    }
}
