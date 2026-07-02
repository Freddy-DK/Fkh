using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhGetFileDownloadSas : FkhBlobContainerSasService
{
    public FkhGetFileDownloadSas(ILogger<FkhGetFileDownloadSas> logger) : base(logger) { }

    public async Task<object> GetDownloadSasAsync(Dictionary<string, string> parameters)
        => await GetContainerSasAsync(
            "files",
            BlobSasPermissions.Read | BlobSasPermissions.List,
            createIfNotExists: false,
            accessDescription: "file read-only download",
            parameters);
}