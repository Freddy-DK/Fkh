using System.Text.Json;
using Xunit;

[assembly: AssemblyFixture(typeof(Fkh.E2ETests.E2EEnvironment))]

namespace Fkh.E2ETests;

// Runs once before any E2E test. When the backend is configured, it ensures the AKS cluster
// is running (auto-stop may have stopped it), starting it via `fkh startfkh` if needed.
public sealed class E2EEnvironment
{
    public E2EEnvironment()
    {
        if (!E2EConfig.IsConfigured)
            return;

        EnsureClusterRunning();
    }

    private static void EnsureClusterRunning()
    {
        var state = GetPowerState();
        E2ELog.Line($"E2E setup: cluster power state = {state ?? "unknown"}.");
        if (state is "Running")
            return;

        // `fkh startfkh` is idempotent and blocks (via the CLI 202/Retry-After loop) until the
        // start operation completes; allow generous time for AKS node pools to spin up.
        E2ELog.Line("E2E setup: cluster not running — invoking StartFkh.");
        var start = FkhCli.Run(TimeSpan.FromMinutes(20), "StartFkh");
        if (start.ExitCode != 0)
            throw new InvalidOperationException(
                $"Failed to start the Fkh cluster for E2E tests.\nSTDOUT:\n{start.StdOut}\nSTDERR:\n{start.StdErr}");

        WaitUntilRunning(TimeSpan.FromMinutes(15));
        E2ELog.Line("E2E setup: cluster is running.");
    }

    private static void WaitUntilRunning(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var state = GetPowerState();
            E2ELog.Line($"E2E setup: waiting for cluster to run (state = {state ?? "unknown"}).");
            if (state is "Running")
                return;
            Thread.Sleep(TimeSpan.FromSeconds(30));
        }
        throw new TimeoutException($"Cluster did not reach 'Running' within {timeout} after StartFkh.");
    }

    // Returns the cluster power state ("Running"/"Stopped"/...) or null if it can't be determined.
    private static string? GetPowerState()
    {
        var result = FkhCli.Run("Status");
        if (result.ExitCode != 0)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            return doc.RootElement.TryGetProperty("clusterPowerState", out var state)
                ? state.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
