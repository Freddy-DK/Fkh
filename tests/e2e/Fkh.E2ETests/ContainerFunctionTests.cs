using System.Text.Json;
using Xunit;

namespace Fkh.E2ETests;

// One test per container-scoped backend function, all run against a single shared container
// created by ContainerFixture. Extensive-only.
[Trait("Category", "Extensive")]
public class ContainerFunctionTests : E2ETest, IClassFixture<ContainerFixture>
{
    private static readonly TimeSpan Op = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(30);

    private readonly ContainerFixture _fx;
    public ContainerFunctionTests(ContainerFixture fx) => _fx = fx;

    private string Name => _fx.ContainerName;
    private void RequireContainer() => Assert.SkipUnless(_fx.Ready, _fx.SkipReason);
    private static string Unique(string prefix) => $"{prefix}{DateTime.UtcNow:HHmmss}{Random.Shared.Next(100, 999)}";

    [Fact]
    public void WaitForContainer_succeeds()
    {
        RequireContainer();
        FkhCli.RunJson(Op, "WaitForContainer", "--name", Name);
    }

    [Fact]
    public void Container_appears_in_ListContainers()
    {
        RequireContainer();
        var listed = FkhCli.RunJson("ListContainers");
        Assert.True(JsonContainsName(listed, Name), $"'{Name}' not found in ListContainers output.");
    }

    [Fact]
    public void GetContainerLog_returns_output()
    {
        RequireContainer();
        FkhCli.RunJson(Op, "GetContainerLog", "--name", Name, "--tail", "50");
    }

    [Fact]
    public void GetContainerEventLog_downloads_evtx()
    {
        RequireContainer();
        var output = Path.Combine(Path.GetTempPath(), $"{Name}-eventlog.evtx");
        // Event log responses are saved to file (not JSON), so use Run and assert success.
        var result = FkhCli.Run(Op, "GetContainerEventLog", "--name", Name, "--output", output);
        Assert.True(result.ExitCode == 0, $"GetContainerEventLog failed: {result.StdErr}");
    }

    [Fact]
    public void InvokeSqlCmd_executes_query()
    {
        RequireContainer();
        FkhCli.RunJson(Op, "InvokeSqlCmd", "--name", Name, "--query", "SELECT 1 AS one");
    }

    [Fact]
    public void InvokeScript_runs_powershell()
    {
        RequireContainer();
        FkhCli.RunJson(Op, "InvokeScript", "--name", Name, "--command", "Get-Date | Out-String");
    }

    [Fact]
    public void GetAppInfo_returns_apps()
    {
        RequireContainer();
        FkhCli.RunJson(Op, "GetAppInfo", "--name", Name);
    }

    [Fact]
    public void GetUser_returns_users()
    {
        RequireContainer();
        FkhCli.RunJson(Op, "GetUser", "--name", Name);
    }

    [Fact]
    public void NewUser_creates_a_user()
    {
        RequireContainer();
        var username = Unique("e2euser");
        FkhCli.RunJson(Op, "NewUser", "--name", Name, "--username", username, "--permissions", "SUPER");
    }

    [Fact]
    public void ImportTestToolkit_imports_framework()
    {
        RequireContainer();
        FkhCli.RunJson(Slow, "ImportTestToolkit", "--name", Name, "--includeTestFrameworkOnly");
    }

    [Fact]
    public void CopyFile_round_trips_through_container()
    {
        RequireContainer();
        var local = Path.Combine(Path.GetTempPath(), $"{Unique("e2e")}.txt");
        File.WriteAllText(local, "fkh e2e round-trip");
        try
        {
            const string remote = "C:\\run\\my.txt";
            FkhCli.RunJson(Op, "CopyFileToContainer", "--name", Name, "--containerFilename", remote, "--file", local);
            FkhCli.RunJson(Op, "CopyFileFromContainer", "--name", Name, "--containerFilename", remote);
        }
        finally
        {
            File.Delete(local);
        }
    }

    [Fact]
    public void AutoStop_can_be_set_extended_and_cleared()
    {
        RequireContainer();
        FkhCli.RunJson(Op, "SetAutoStop", "--name", Name, "--autostop", "4h");
        FkhCli.RunJson(Op, "ExtendAutoStop", "--name", Name);
        FkhCli.RunJson(Op, "ClearAutoStop", "--name", Name);
    }

    [Fact]
    public void WinRm_access_can_be_allowed_and_revoked()
    {
        RequireContainer();
        try
        {
            FkhCli.RunJson(Slow, "AllowWinRmAccess", "--name", Name);
        }
        finally
        {
            FkhCli.Run(Op, "RevokeWinRmAccess", "--name", Name);
        }
    }

    [Fact]
    public void Sql_access_can_be_allowed_and_revoked()
    {
        RequireContainer();
        try
        {
            FkhCli.RunJson(Slow, "AllowSqlAccess");
        }
        finally
        {
            FkhCli.Run(Op, "RevokeSqlAccess");
        }
    }

    [Fact]
    public void Container_can_be_stopped_and_started()
    {
        RequireContainer();
        FkhCli.RunJson(Op, "StopContainer", "--name", Name);
        FkhCli.RunJson(Slow, "StartContainer", "--name", Name);
        // Leave the shared container ready for other tests.
        FkhCli.RunJson(TimeSpan.FromMinutes(90), "WaitForContainer", "--name", Name);
    }

    [Fact]
    public void Database_can_be_backed_up_and_removed()
    {
        RequireContainer();
        var backupName = Unique("e2edb");
        FkhCli.RunJson(Slow, "BackupDatabase", "--name", Name, "--backupName", backupName, "--backupVersion", "1.0");
        // Cleanup the uploaded backup (RemoveDatabase requires confirmation and admin).
        var removal = FkhCli.Run(Op, "RemoveDatabase", "--database", $"{backupName}/1.0", "--confirm");
        Assert.True(removal.ExitCode == 0, $"Failed to clean up backup '{backupName}': {removal.StdErr}");
    }

    // ── Tenant-lifecycle and single-tenant conversion are destructive/complex and can leave the
    // shared container in an unusable state; exercise them manually rather than in the shared run.

    [Fact]
    public void BackupTenantDatabase_is_not_run_in_shared_suite()
        => Assert.Skip("Tenant backup/restore is exercised manually to avoid disrupting the shared container.");

    [Fact]
    public void RestoreTenantDatabase_is_not_run_in_shared_suite()
        => Assert.Skip("Tenant backup/restore is exercised manually to avoid disrupting the shared container.");

    [Fact]
    public void DismountTenant_is_not_run_in_shared_suite()
        => Assert.Skip("Tenant mount/dismount is exercised manually to avoid disrupting the shared container.");

    [Fact]
    public void MountTenant_is_not_run_in_shared_suite()
        => Assert.Skip("Tenant mount/dismount is exercised manually to avoid disrupting the shared container.");

    [Fact]
    public void ConvertToSingleTenant_is_not_run_in_shared_suite()
        => Assert.Skip("ConvertToSingleTenant is a one-way destructive transform; exercise it manually.");

    private static bool JsonContainsName(JsonElement element, string name)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return string.Equals(element.GetString(), name, StringComparison.OrdinalIgnoreCase);
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    if (JsonContainsName(prop.Value, name)) return true;
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (JsonContainsName(item, name)) return true;
                return false;
            default:
                return false;
        }
    }
}
