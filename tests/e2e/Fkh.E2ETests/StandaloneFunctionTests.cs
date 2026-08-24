using System.Text.Json;
using Xunit;

namespace Fkh.E2ETests;

// Backend functions that do not require a running container. Read-only checks plus self-contained
// lifecycles (secret, settings). Extensive-only because they assume an admin identity / mutate state.
// Cluster-wide or heavyweight functions are present but skipped with a reason so they stay visible.
[Trait("Category", "Extensive")]
public class StandaloneFunctionTests : E2ETest
{
    private static readonly TimeSpan Op = TimeSpan.FromMinutes(10);

    private static void RequireExtensive()
    {
        Assert.SkipUnless(E2EConfig.IsConfigured, "FKH_E2E_BACKEND_URL is not set.");
        Assert.SkipUnless(E2EConfig.Extensive, "Set FKH_E2E_EXTENSIVE=true to run extensive tests.");
    }

    private static string Unique(string prefix) => $"{prefix}{DateTime.UtcNow:HHmmss}{Random.Shared.Next(100, 999)}";

    public static TheoryData<string> AdminReadOnlyCommands =>
    [
        "GetVersion",
        "GetSettings",
        "Status",
        "ListVMs",
        "ListPrepulled",
        "ListSecrets",
    ];

    [Theory]
    [MemberData(nameof(AdminReadOnlyCommands))]
    public void Read_only_command_returns_json(string command)
    {
        RequireExtensive();
        var json = FkhCli.RunJson(Op, command);
        Assert.True(json.ValueKind is JsonValueKind.Object or JsonValueKind.Array,
            $"{command} returned unexpected JSON kind {json.ValueKind}.");
    }

    [Fact]
    public void Secret_can_be_set_read_and_removed()
    {
        RequireExtensive();
        var name = Unique("e2esecret"); // secret names may not contain a dash
        var value = Unique("val");
        try
        {
            FkhCli.RunJson(Op, "SetSecret", "--name", name, "--secret", value);
            FkhCli.RunJson(Op, "GetSecret", "--name", name);
            FkhCli.RunJson(Op, "ListSecrets");
        }
        finally
        {
            // Empty value removes the secret.
            FkhCli.Run(Op, "SetSecret", "--name", name, "--secret", "");
        }
    }

    [Fact]
    public void Setting_can_be_set_read_and_cleared()
    {
        RequireExtensive();

        var me = FkhCli.RunJson(Op, "GetCurrentUser");
        var username = me.TryGetProperty("username", out var u) ? u.GetString()
            : me.TryGetProperty("login", out var l) ? l.GetString()
            : null;
        Assert.SkipWhen(string.IsNullOrWhiteSpace(username), "Could not determine current username.");

        var property = Unique("E2ETest");
        try
        {
            FkhCli.RunJson(Op, "SetSettings", "--username", username!, "--property", property, "--value", "1");
            FkhCli.RunJson(Op, "GetSettings", "--username", username!, "--property", property);
        }
        finally
        {
            FkhCli.Run(Op, "ClearSettings", "--username", username!, "--property", property);
        }
    }

    // ── Cluster-wide or heavyweight functions: present for visibility, skipped to protect the
    // shared environment. Exercise these manually against a disposable deployment.

    [Fact]
    public void StopFkh_is_not_run_automatically()
        => Assert.Skip("StopFkh stops the whole cluster; exercise manually. (StartFkh is covered by the ensure-running fixture.)");

    [Fact]
    public void StartFkh_is_covered_by_the_ensure_running_fixture()
        => Assert.Skip("StartFkh is invoked by E2EEnvironment when the cluster is stopped.");

    [Fact]
    public void StopAllContainers_is_not_run_automatically()
        => Assert.Skip("StopAllContainers stops every user's containers; exercise manually.");

    [Fact]
    public void CreateImage_is_not_run_automatically()
        => Assert.Skip("CreateImage triggers a long-running image build workflow; exercise manually.");

    [Fact]
    public void RemoveImage_is_not_run_automatically()
        => Assert.Skip("RemoveImage deletes registry images/backups; exercise manually against a disposable image.");

    [Fact]
    public void AddPrepull_is_not_run_automatically()
        => Assert.Skip("AddPrepull/RemovePrepull change node pre-pull config cluster-wide; exercise manually.");

    [Fact]
    public void RemovePrepull_is_not_run_automatically()
        => Assert.Skip("AddPrepull/RemovePrepull change node pre-pull config cluster-wide; exercise manually.");

    [Fact]
    public void RemoveFile_is_not_run_automatically()
        => Assert.Skip("RemoveFile requires a pre-uploaded file blob; exercise manually after uploadfile.");
}
