using Xunit;

namespace Fkh.E2ETests;

// Creates a single Business Central container once, shared by all container-scoped function tests,
// and removes it on teardown. When the E2E environment is not fully configured for extensive tests
// the fixture is a no-op and exposes a SkipReason so the dependent tests skip.
public sealed class ContainerFixture : IAsyncLifetime
{
    public string ContainerName { get; } = E2EConfig.ResourcePrefix;
    // The backend resolves a short name to "<sanitized-user>-<name>"; that is the app label reported
    // by ListContainers and embedded in the web client host. Resolved once the user is known.
    public string FullContainerName { get; private set; } = E2EConfig.ResourcePrefix;
    public bool Ready { get; private set; }
    public string SkipReason { get; private set; } = "Container fixture not initialized.";

    private bool _created;

    public async ValueTask InitializeAsync()
    {
        await Task.CompletedTask;

        if (!E2EConfig.IsConfigured) { SkipReason = "FKH_E2E_BACKEND_URL is not set."; return; }
        if (!E2EConfig.Extensive) { SkipReason = "Set FKH_E2E_EXTENSIVE=true to run extensive tests."; return; }

        var artifactUrl = Environment.GetEnvironmentVariable("FKH_E2E_ARTIFACT_URL");
        var adminPassword = Environment.GetEnvironmentVariable("FKH_E2E_ADMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(artifactUrl)) { SkipReason = "FKH_E2E_ARTIFACT_URL is not set."; return; }
        if (string.IsNullOrWhiteSpace(adminPassword)) { SkipReason = "FKH_E2E_ADMIN_PASSWORD is not set."; return; }

        var whoami = FkhCli.RunJson(TimeSpan.FromMinutes(5), "GetCurrentUser");
        var username = whoami.GetProperty("username").GetString() ?? "";
        FullContainerName = SanitizeAppName($"{username}-{ContainerName}");

        E2ELog.Line($"[fixture] Creating shared container '{ContainerName}' (artifact {artifactUrl}) — up to ~an hour if the image must be built.");
        FkhCli.RunJson(TimeSpan.FromMinutes(90), "CreateContainer",
            "--name", ContainerName,
            "--artifactUrl", artifactUrl!,
            "--adminUsername", "admin",
            "--adminPassword", adminPassword!);
        _created = true;

        FkhCli.RunJson(TimeSpan.FromMinutes(90), "WaitForContainer", "--name", ContainerName);
        Ready = true;
        E2ELog.Line($"[fixture] Shared container '{ContainerName}' is ready.");
    }

    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask;

        if (!_created) return;
        E2ELog.Line($"[fixture] Removing shared container '{ContainerName}'.");
        var removal = FkhCli.Run(TimeSpan.FromMinutes(30), "RemoveContainer", "--name", ContainerName);
        Assert.True(removal.ExitCode == 0,
            $"Failed to remove E2E container '{ContainerName}'. STDOUT: {removal.StdOut} STDERR: {removal.StdErr}");
    }

    // Mirrors the backend's SanitizeAppName so the resolved app label can be reproduced client-side.
    private static string SanitizeAppName(string name)
    {
        var appName = name.Replace('.', '-').Replace('_', '-').ToLowerInvariant();
        if (appName.Length > 63) appName = appName[..63];
        return appName.TrimEnd('-');
    }
}
