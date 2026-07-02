using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhGetDatabaseUploadSas : FkhBlobContainerSasService
{
    public FkhGetDatabaseUploadSas(ILogger<FkhGetDatabaseUploadSas> logger) : base(logger) { }

    public async Task<object> GetUploadSasAsync(Dictionary<string, string> parameters)
    {
        var containerName = parameters.TryGetValue("containerName", out var cn) ? cn : "databases";
        return await GetContainerSasAsync(
            containerName,
            BlobSasPermissions.Read | BlobSasPermissions.Write | BlobSasPermissions.List,
            createIfNotExists: true,
            accessDescription: "upload",
            parameters);
    }
}
