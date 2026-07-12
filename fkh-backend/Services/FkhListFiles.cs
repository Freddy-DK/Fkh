using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhListFiles : FkhListVersionedBlobBase
{
    public FkhListFiles(ILogger<FkhListFiles> logger) : base(logger) { }

    public Task<object> ListFilesAsync(Dictionary<string, string> parameters)
        => ListVersionedBlobAsync(
            containerName: "files",
            referenceParameterName: "file",
            parameters);
}
