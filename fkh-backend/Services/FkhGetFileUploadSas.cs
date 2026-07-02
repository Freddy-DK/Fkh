using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhGetFileUploadSas : FkhBlobContainerSasService
{
    public FkhGetFileUploadSas(ILogger<FkhGetFileUploadSas> logger) : base(logger) { }

    public async Task<object> GetUploadSasAsync(Dictionary<string, string> parameters)
        => await GetContainerSasAsync(
            "files",
            BlobSasPermissions.Read | BlobSasPermissions.Write | BlobSasPermissions.List,
            createIfNotExists: true,
            accessDescription: "file upload",
            parameters);
}