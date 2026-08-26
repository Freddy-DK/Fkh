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
            // --allusers sets an org-wide secret (personal secrets are unavailable under OIDC).
            FkhCli.RunJson(Op, "SetSecret", "--name", name, "--secret", value, "--allusers");
            FkhCli.RunJson(Op, "GetSecret", "--name", name);
            FkhCli.RunJson(Op, "ListSecrets");
        }
        finally
        {
            // Empty value removes the secret.
            FkhCli.Run(Op, "SetSecret", "--name", name, "--secret", "", "--allusers");
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

    [Fact]
    public void File_can_be_uploaded_listed_downloaded_and_removed()
    {
        RequireExtensive();

        var name = Unique("e2efile");
        const string version = "1.0";
        const string content = "fkh e2e file round-trip";
        var local = Path.Combine(Path.GetTempPath(), $"{name}.txt");
        var download = Path.Combine(Path.GetTempPath(), $"{name}-dl.txt");
        File.WriteAllText(local, content);

        var removed = false;
        try
        {
            FkhCli.Run(Op, "UploadFile", "--localPath", local, "--FileName", name, "--FileVersion", version);
            var listed = FkhCli.RunJson(Op, "ListFiles", "--file", $"{name}/*");
            Assert.True(JsonContainsText(listed, name), $"Uploaded file '{name}' not found in ListFiles.");

            FkhCli.Run(Op, "DownloadFile", "--file", $"{name}/{version}", "--output", download);
            Assert.True(File.Exists(download), "Downloaded file was not written.");
            Assert.Equal(content, File.ReadAllText(download));

            var removal = FkhCli.Run(Op, "RemoveFile", "--file", $"{name}/{version}", "--confirm");
            removed = removal.ExitCode == 0;
            Assert.True(removed, $"RemoveFile failed: {removal.StdErr}");
        }
        finally
        {
            if (!removed) FkhCli.Run(Op, "RemoveFile", "--file", $"{name}/{version}", "--confirm");
            File.Delete(local);
            if (File.Exists(download)) File.Delete(download);
        }
    }

    [Fact]
    public void Database_can_be_uploaded_listed_downloaded_and_removed()
    {
        RequireExtensive();

        var name = Unique("e2edb");
        const string version = "1.0";
        // Dummy .bak content — exercises the blob upload/download/remove flow, not a real restore.
        var content = $"dummy-bak-{name}";
        var local = Path.Combine(Path.GetTempPath(), $"{name}.bak");
        var download = Path.Combine(Path.GetTempPath(), $"{name}-dl.bak");
        File.WriteAllText(local, content);

        var removed = false;
        try
        {
            FkhCli.Run(Op, "UploadDatabase", "--bakFile", local, "--backupName", name, "--backupVersion", version);
            var listed = FkhCli.RunJson(Op, "ListDatabases", "--database", $"{name}/*");
            Assert.True(JsonContainsText(listed, name), $"Uploaded database '{name}' not found in ListDatabases.");

            FkhCli.Run(Op, "DownloadDatabase", "--database", $"{name}/{version}", "--output", download);
            Assert.True(File.Exists(download), "Downloaded database was not written.");
            Assert.Equal(content, File.ReadAllText(download));

            var removal = FkhCli.Run(Op, "RemoveDatabase", "--database", $"{name}/{version}", "--confirm");
            removed = removal.ExitCode == 0;
            Assert.True(removed, $"RemoveDatabase failed: {removal.StdErr}");
        }
        finally
        {
            if (!removed) FkhCli.Run(Op, "RemoveDatabase", "--database", $"{name}/{version}", "--confirm");
            File.Delete(local);
            if (File.Exists(download)) File.Delete(download);
        }
    }

    [Fact]
    public void Large_file_round_trips_and_matches_hash()
    {
        RequireExtensive();

        // ~2.5 GiB by default; override with FKH_E2E_LARGE_FILE_BYTES to run smaller on constrained disks.
        var sizeBytes = long.TryParse(Environment.GetEnvironmentVariable("FKH_E2E_LARGE_FILE_BYTES"), out var s)
            ? s
            : 2_684_354_560L;

        var name = Unique("e2ebig");
        const string version = "1.0";
        var local = Path.Combine(Path.GetTempPath(), $"{name}.bin");
        var download = Path.Combine(Path.GetTempPath(), $"{name}-dl.bin");
        var big = TimeSpan.FromMinutes(60);

        var removed = false;
        try
        {
            E2ELog.Line($"Generating {sizeBytes:N0}-byte random file for large-file round-trip.");
            var expectedHash = E2EFiles.WriteRandomFile(local, sizeBytes);
            E2ELog.Line($"Generated (SHA256 {expectedHash}); uploading.");

            FkhCli.Run(big, "UploadFile", "--localPath", local, "--FileName", name, "--FileVersion", version);
            FkhCli.Run(big, "DownloadFile", "--file", $"{name}/{version}", "--output", download);

            Assert.True(File.Exists(download), "Downloaded file was not written.");
            Assert.Equal(sizeBytes, new FileInfo(download).Length);
            Assert.Equal(expectedHash, E2EFiles.ComputeSha256(download));
            E2ELog.Line("Large file downloaded and hash matches.");

            var removal = FkhCli.Run(Op, "RemoveFile", "--file", $"{name}/{version}", "--confirm");
            removed = removal.ExitCode == 0;
            Assert.True(removed, $"RemoveFile failed: {removal.StdErr}");
        }
        finally
        {
            if (!removed) FkhCli.Run(Op, "RemoveFile", "--file", $"{name}/{version}", "--confirm");
            if (File.Exists(local)) File.Delete(local);
            if (File.Exists(download)) File.Delete(download);
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

    private static bool JsonContainsText(JsonElement element, string text)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString()?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Name.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
                    if (JsonContainsText(prop.Value, text)) return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (JsonContainsText(item, text)) return true;
                return false;
            default:
                return false;
        }
    }
}
