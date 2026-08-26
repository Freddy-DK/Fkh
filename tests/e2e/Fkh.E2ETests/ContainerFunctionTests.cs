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
    public void InvokeScript_returns_the_scripts_output_value()
    {
        RequireContainer();
        var token = Unique("fkhret");
        var result = FkhCli.RunJson(Op, "InvokeScript", "--name", Name, "--command", $"Write-Output '{token}'");
        Assert.Contains(token, ScriptOutput(result));
    }

    [Fact]
    public void InvokeScript_round_trips_typed_parameters_and_return_types()
    {
        RequireContainer();
        // Pass an int, a string (with a space), and a switch/bool into the script, then return a
        // structured object as JSON so we can verify the different value types survive the round trip.
        const string script =
            "param([int]$Number,[string]$Text,[switch]$Flag)\n" +
            "[PSCustomObject]@{ number = $Number; text = $Text; flag = [bool]$Flag; doubled = $Number * 2; items = @('a','b','c') } | ConvertTo-Json -Compress";

        var result = FkhCli.RunJson(Op, "InvokeScript",
            "--name", Name,
            "--command", script,
            "--scriptParams", "-Number 42 -Text 'hello world' -Flag");

        using var returned = JsonDocument.Parse(ScriptOutput(result));
        var root = returned.RootElement;
        Assert.Equal(42, root.GetProperty("number").GetInt32());
        Assert.Equal("hello world", root.GetProperty("text").GetString());
        Assert.True(root.GetProperty("flag").GetBoolean());
        Assert.Equal(84, root.GetProperty("doubled").GetInt32());
        Assert.Equal(3, root.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void InvokeScript_returns_a_value_from_a_short_running_script()
        => InvokeSleepingScriptAndAssertReturn(seconds: 10, timeout: Op);

    [Fact]
    public void InvokeScript_returns_a_value_from_a_medium_running_script()
        => InvokeSleepingScriptAndAssertReturn(seconds: 40, timeout: Op);

    [Fact]
    public void InvokeScript_returns_a_value_from_a_long_running_script()
        => InvokeSleepingScriptAndAssertReturn(seconds: 360, timeout: Slow);

    // Runs a script that sleeps for the given duration and then emits a unique token, and asserts the
    // token is returned to the client. Exercises the backend's detached-job polling (202/Retry-After)
    // for scripts that outlive a single request.
    private void InvokeSleepingScriptAndAssertReturn(int seconds, TimeSpan timeout)
    {
        var token = Unique("fkhsleep");
        var script = $"Start-Sleep -Seconds {seconds}; Write-Output '{token}'";
        var result = FkhCli.RunJson(timeout, "InvokeScript", "--name", Name, "--command", script);
        Assert.Contains(token, ScriptOutput(result));
    }

    // InvokeScript returns { container, output }; the script's captured stdout is the 'output' string.
    private static string ScriptOutput(JsonElement result) => result.GetProperty("output").GetString() ?? "";

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
    public void Client_copy_commands_round_trip_a_file_and_match_content()
    {
        RequireContainer();
        const long sizeBytes = 20L * 1024 * 1024; // 20 MiB
        var local = Path.Combine(Path.GetTempPath(), $"{Unique("e2e")}.bin");
        var download = Path.Combine(Path.GetTempPath(), $"{Unique("e2e")}-dl.bin");
        var expectedHash = E2EFiles.WriteRandomFile(local, sizeBytes);
        try
        {
            const string remote = "C:\\run\\clientcopy.bin";
            var up = FkhCli.Run(Op, "copytocontainer", "--name", Name, "--localFilename", local, "--containerFilename", remote);
            Assert.True(up.ExitCode == 0, $"copytocontainer failed: {up.StdErr}");

            var down = FkhCli.Run(Op, "copyfromcontainer", "--name", Name, "--containerFilename", remote, "--localFilename", download);
            Assert.True(down.ExitCode == 0, $"copyfromcontainer failed: {down.StdErr}");

            Assert.True(File.Exists(download), "copyfromcontainer did not write the local file.");
            Assert.Equal(sizeBytes, new FileInfo(download).Length);
            Assert.Equal(expectedHash, E2EFiles.ComputeSha256(download));
        }
        finally
        {
            if (File.Exists(local)) File.Delete(local);
            if (File.Exists(download)) File.Delete(download);
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
    public void WinRm_access_gates_tcp_reachability_to_the_container()
    {
        RequireContainer();
        const int port = 5986;
        var budget = TimeSpan.FromMinutes(3);

        // The container's own public endpoint (web client FQDN) exposes only the web/BC ports, never
        // WinRM — a grant opens 5986 on a separate load balancer. Verify it is closed there with no grant.
        var containerHost = ContainerHost();
        Assert.False(E2ENet.IsTcpOpen(containerHost, port),
            $"WinRM port {port} should not be reachable on container endpoint {containerHost} without a grant.");

        string host;
        try
        {
            // After AllowWinRmAccess the port becomes reachable on the grant's endpoint.
            var granted = FkhCli.RunJson(Slow, "AllowWinRmAccess", "--name", Name);
            host = WinRmHost(granted);
            Assert.True(E2ENet.WaitUntilOpen(host, port, budget),
                $"WinRM port {host}:{port} was not reachable after AllowWinRmAccess.");
        }
        finally
        {
            FkhCli.Run(Op, "RevokeWinRmAccess", "--name", Name);
        }

        // After RevokeWinRmAccess the port is closed again.
        Assert.True(E2ENet.WaitUntilClosed(host, port, budget),
            $"WinRM port {host}:{port} was still reachable after RevokeWinRmAccess.");
    }

    [Fact]
    public void Sql_access_gates_tcp_reachability_to_sql_server()
    {
        RequireContainer();
        const int port = 1433;
        var budget = TimeSpan.FromMinutes(3);

        string host;
        try
        {
            // After AllowSqlAccess the SQL port becomes reachable on the grant's endpoint.
            var granted = FkhCli.RunJson(Slow, "AllowSqlAccess");
            host = SqlHost(granted);
            Assert.True(E2ENet.WaitUntilOpen(host, port, budget),
                $"SQL port {host}:{port} was not reachable after AllowSqlAccess.");
        }
        finally
        {
            FkhCli.Run(Op, "RevokeSqlAccess");
        }

        // After RevokeSqlAccess the port is closed again.
        Assert.True(E2ENet.WaitUntilClosed(host, port, budget),
            $"SQL port {host}:{port} was still reachable after RevokeSqlAccess.");
    }

    // ListContainers returns a webClient URL (https://<appName>.<region>.cloudapp.azure.com/BC/) per
    // container; GetContainerDetails is hidden from the CLI catalog so it can't be used here.
    private string ContainerHost()
    {
        var listed = FkhCli.RunJson(Op, "ListContainers");
        foreach (var c in listed.GetProperty("containers").EnumerateArray())
        {
            if (!c.TryGetProperty("appLabel", out var al) || !string.Equals(al.GetString(), Name, StringComparison.OrdinalIgnoreCase))
                continue;
            var url = c.TryGetProperty("webClient", out var wc) ? wc.GetString() : null;
            Assert.False(string.IsNullOrWhiteSpace(url), $"ListContainers returned no webClient for '{Name}'.");
            return new Uri(url!).Host;
        }
        throw new Xunit.Sdk.XunitException($"Container '{Name}' not found in ListContainers output.");
    }

    // AllowSqlAccess returns sqlEndpoint as "<host>,1433" (SQL Server syntax).
    private static string SqlHost(JsonElement granted)
    {
        var endpoint = granted.GetProperty("sqlEndpoint").GetString() ?? "";
        var host = endpoint.Split(',')[0].Trim();
        Assert.False(string.IsNullOrWhiteSpace(host) || host.StartsWith('('),
            $"AllowSqlAccess did not return a usable endpoint: '{endpoint}'.");
        return host;
    }

    // AllowWinRmAccess returns winRmEndpoint as "<host>:5986".
    private static string WinRmHost(JsonElement granted)
    {
        var endpoint = granted.GetProperty("winRmEndpoint").GetString() ?? "";
        var idx = endpoint.LastIndexOf(':');
        var host = (idx > 0 ? endpoint[..idx] : endpoint).Trim();
        Assert.False(string.IsNullOrWhiteSpace(host) || host.StartsWith('('),
            $"AllowWinRmAccess did not return a usable endpoint: '{endpoint}'.");
        return host;
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
