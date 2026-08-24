using System.Text.Json;
using Xunit;

namespace Fkh.E2ETests;

// Full provisioning lifecycle driven through the CLI. Destructive and slow, so it only runs
// under FKH_E2E_EXTENSIVE=true and requires an artifact URL + admin password to be supplied.
[Trait("Category", "Extensive")]
public class ContainerLifecycleTests
{
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

        try
        {
            FkhCli.RunJson("CreateContainer",
                "--name", name,
                "--artifactUrl", artifactUrl!,
                "--adminUsername", "admin",
                "--adminPassword", adminPassword!);
            created = true;

            // Wait for readiness (the CLI polls through the 202/Retry-After loop).
            FkhCli.RunJson("WaitForContainer", "--name", name);

            var details = FkhCli.RunJson("GetContainerDetails", "--name", name);
            Assert.Equal(JsonValueKind.Object, details.ValueKind);

            var listed = FkhCli.RunJson("ListContainers");
            var found = JsonContainsName(listed, name);
            Assert.True(found, $"Created container '{name}' not found in ListContainers output.");
        }
        finally
        {
            if (created)
            {
                var removal = FkhCli.Run("RemoveContainer", "--name", name, "--confirm");
                Assert.True(removal.ExitCode == 0,
                    $"Cleanup failed to remove container '{name}': {removal.StdErr}");
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
