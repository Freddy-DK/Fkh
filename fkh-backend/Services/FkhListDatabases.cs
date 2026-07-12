using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhListDatabases : FkhListVersionedBlobBase
{
    public FkhListDatabases(ILogger<FkhListDatabases> logger) : base(logger) { }

    public Task<object> ListDatabasesAsync(Dictionary<string, string> parameters)
        => ListVersionedBlobAsync(
            containerName: "databases",
            referenceParameterName: "database",
            parameters);
}
