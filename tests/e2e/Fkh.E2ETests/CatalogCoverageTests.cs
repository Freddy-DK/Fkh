using System.Text.Json;
using Xunit;

namespace Fkh.E2ETests;

internal enum E2ETier
{
    // Read-only: safe to invoke against a live backend with no lasting side effects.
    Safe,

    // Provisions, mutates, or removes real resources (or is cluster-wide). Only exercised
    // under FKH_E2E_EXTENSIVE=true, and only where a dedicated test drives it.
    Extensive,
}

// Every backend catalog function must be categorized here. CatalogCoverageTests fails if the
// live catalog exposes a function that is missing from this registry, forcing new functions to
// be classified (and considered for E2E coverage).
internal static class E2ERegistry
{
    public static readonly IReadOnlyDictionary<string, E2ETier> Functions = new Dictionary<string, E2ETier>(StringComparer.OrdinalIgnoreCase)
    {
        // Read-only
        ["ListContainers"] = E2ETier.Safe,
        ["ListImages"] = E2ETier.Safe,
        ["ListVMs"] = E2ETier.Safe,
        ["ListDatabases"] = E2ETier.Safe,
        ["ListFiles"] = E2ETier.Safe,
        ["ListPrepulled"] = E2ETier.Safe,
        ["ListSecrets"] = E2ETier.Safe,
        ["GetVersion"] = E2ETier.Safe,
        ["GetCurrentUser"] = E2ETier.Safe,
        ["Status"] = E2ETier.Safe,
        ["GetSettings"] = E2ETier.Safe,
        ["GetContainerDetails"] = E2ETier.Safe,
        ["GetAppInfo"] = E2ETier.Safe,
        ["GetContainerLog"] = E2ETier.Safe,
        ["GetContainerEventLog"] = E2ETier.Safe,
        ["GetDatabaseDownloadSas"] = E2ETier.Safe,
        ["GetFileDownloadSas"] = E2ETier.Safe,
        ["GetUser"] = E2ETier.Safe,
        ["GetSecret"] = E2ETier.Safe,
        ["CopyFileFromContainer"] = E2ETier.Safe,

        // Mutating / provisioning / destructive / cluster-wide
        ["CreateContainer"] = E2ETier.Extensive,
        ["RemoveContainer"] = E2ETier.Extensive,
        ["StopContainer"] = E2ETier.Extensive,
        ["StopAllContainers"] = E2ETier.Extensive,
        ["StartContainer"] = E2ETier.Extensive,
        ["ExtendAutoStop"] = E2ETier.Extensive,
        ["SetAutoStop"] = E2ETier.Extensive,
        ["ClearAutoStop"] = E2ETier.Extensive,
        ["AllowSqlAccess"] = E2ETier.Extensive,
        ["RevokeSqlAccess"] = E2ETier.Extensive,
        ["AllowWinRmAccess"] = E2ETier.Extensive,
        ["RevokeWinRmAccess"] = E2ETier.Extensive,
        ["CreateImage"] = E2ETier.Extensive,
        ["RemoveImage"] = E2ETier.Extensive,
        ["WaitForContainer"] = E2ETier.Extensive,
        ["InvokeSqlCmd"] = E2ETier.Extensive,
        ["InvokeScript"] = E2ETier.Extensive,
        ["ImportTestToolkit"] = E2ETier.Extensive,
        ["GetDatabaseUploadSas"] = E2ETier.Extensive,
        ["GetFileUploadSas"] = E2ETier.Extensive,
        ["RemoveDatabase"] = E2ETier.Extensive,
        ["RemoveFile"] = E2ETier.Extensive,
        ["AddPrepull"] = E2ETier.Extensive,
        ["RemovePrepull"] = E2ETier.Extensive,
        ["SetSettings"] = E2ETier.Extensive,
        ["ClearSettings"] = E2ETier.Extensive,
        ["StopFkh"] = E2ETier.Extensive,
        ["StartFkh"] = E2ETier.Extensive,
        ["BackupDatabase"] = E2ETier.Extensive,
        ["BackupTenantDatabase"] = E2ETier.Extensive,
        ["RestoreTenantDatabase"] = E2ETier.Extensive,
        ["CopyFileToContainer"] = E2ETier.Extensive,
        ["NewUser"] = E2ETier.Extensive,
        ["DismountTenant"] = E2ETier.Extensive,
        ["MountTenant"] = E2ETier.Extensive,
        ["ConvertToSingleTenant"] = E2ETier.Extensive,
        ["SetSecret"] = E2ETier.Extensive,
    };
}

public class CatalogCoverageTests : E2ETest
{
    [Fact]
    public async Task Every_catalog_function_is_registered_for_e2e()
    {
        Assert.SkipUnless(E2EConfig.IsConfigured, "FKH_E2E_BACKEND_URL is not set.");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var body = await client.GetStringAsync($"{E2EConfig.BackendUrl}/functions", TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);

        var liveNames = doc.RootElement.GetProperty("functions")
            .EnumerateArray()
            .Select(f => f.GetProperty("name").GetString()!)
            .ToList();

        Assert.NotEmpty(liveNames);

        var uncategorized = liveNames
            .Where(n => !E2ERegistry.Functions.ContainsKey(n))
            .ToList();

        Assert.True(uncategorized.Count == 0,
            $"Catalog functions missing from E2ERegistry: {string.Join(", ", uncategorized)}");
    }
}
