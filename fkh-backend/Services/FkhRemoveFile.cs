using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhRemoveFile : FkhRemoveVersionedBlobBase
{
    public FkhRemoveFile(ILogger<FkhRemoveFile> logger) : base(logger) { }

    public Task<object> RemoveFileAsync(Dictionary<string, string> parameters)
        => RemoveVersionedBlobAsync(
            containerName: "files",
            itemKind: "file",
            referenceParameterName: "file",
            getBlobName: static (name, version) => $"{name}/{version}",
            parameters);
}
