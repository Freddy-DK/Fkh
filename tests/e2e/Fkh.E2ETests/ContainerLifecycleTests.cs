using System.Text.Json;
using Xunit;

namespace Fkh.E2ETests;

// Full provisioning lifecycle driven through the CLI. Destructive and slow, so it only runs
// under FKH_E2E_EXTENSIVE=true and requires an artifact URL + admin password to be supplied.
[Trait("Category", "Extensive")]
public class ContainerLifecycleTests : E2ETest
{
    // Container creation may pull a Business Central artifact/image that does not yet exist in the
    // registry, which can take up to ~an hour; allow plenty of headroom.
    private static readonly TimeSpan CreateTimeout = TimeSpan.FromMinutes(90);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromMinutes(90);
    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromMinutes(30);

    [Fact]
    public void Create_wait_inspect_and_remove_container()
    {
        Assert.SkipUnless(E2EConfig.IsConfigured, "FKH_E2E_BACKEND_URL is not set.");
        Assert.SkipUnless(E2EConfig.Extensive, "Set FKH_E2E_EXTENSIVE=true to run extensive tests.");

        var artifactUrl = Environment.GetEnvironmentVariable("FKH_E2E_ARTIFACT_URL");
        var adminPassword = Environment.GetEnvironmentVariable("FKH_E2E_ADMIN_PASSWORD");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(artifactUrl), "FKH_E2E_ARTIFACT_URL is not set.");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(adminPassword), "FKH_E2E_ADMIN_PASSWORD is not set.");

        var name = E2EConfig.ResourcePrefix;
        var created = false;

        E2ELog.Line($"Lifecycle test starting for container '{name}' (artifact {artifactUrl}).");

        try
        {
            E2ELog.Line($"Creating container '{name}' — this can take up to an hour if the image must be built/pulled.");
            FkhCli.RunJson(CreateTimeout, "CreateContainer",
                "--name", name,
                "--artifactUrl", artifactUrl!,
                "--adminUsername", "admin",
                "--adminPassword", adminPassword!);
            created = true;
            E2ELog.Line($"Container '{name}' created.");

            E2ELog.Line($"Waiting for container '{name}' to be ready.");
            FkhCli.RunJson(WaitTimeout, "WaitForContainer", "--name", name);
            E2ELog.Line($"Container '{name}' is ready.");

            E2ELog.Line($"Fetching details for '{name}'.");
            var details = FkhCli.RunJson("GetContainerDetails", "--name", name);
            Assert.Equal(JsonValueKind.Object, details.ValueKind);

            var listed = FkhCli.RunJson("ListContainers");
            var found = JsonContainsName(listed, name);
            Assert.True(found, $"Created container '{name}' not found in ListContainers output.");
            E2ELog.Line($"Verified '{name}' appears in ListContainers.");
        }
        finally
        {
            if (created)
            {
                E2ELog.Line($"Cleaning up: removing container '{name}'.");
                var removal = FkhCli.Run(RemoveTimeout, "RemoveContainer", "--name", name, "--confirm");
                Assert.True(removal.ExitCode == 0,
                    $"Cleanup failed to remove container '{name}': {removal.StdErr}");
                E2ELog.Line($"Container '{name}' removed.");
            }
        }
    }

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
