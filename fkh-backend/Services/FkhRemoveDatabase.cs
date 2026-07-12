using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhRemoveDatabase : FkhRemoveVersionedBlobBase
{
    public FkhRemoveDatabase(ILogger<FkhRemoveDatabase> logger) : base(logger) { }

    public Task<object> RemoveDatabaseAsync(Dictionary<string, string> parameters)
        => RemoveVersionedBlobAsync(
            containerName: "databases",
            itemKind: "database",
            referenceParameterName: "database",
            getBlobName: static (name, version) => $"{name}/{version}.bak",
            parameters);
}
