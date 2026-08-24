using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Fkh.E2ETests;

internal sealed record CliResult(int ExitCode, string StdOut, string StdErr);

// Drives the built `fkh` CLI against the configured backend. Resolution order for the CLI:
//   1. FKH_CLI_PATH env var (path to `fkh` executable or `fkh.dll`)
//   2. `dotnet run --project <repo>/fkh-cli/fkh-cli.csproj` (developer fallback)
internal static class FkhCli
{
    private static readonly (string Exe, string[] Prefix) Invocation = ResolveInvocation();

    public static CliResult Run(params string[] args) => Run(TimeSpan.FromMinutes(5), args);

    public static CliResult Run(TimeSpan timeout, params string[] args)
    {
        var backendUrl = E2EConfig.BackendUrl
            ?? throw new InvalidOperationException("FKH_E2E_BACKEND_URL is not set.");

        var fullArgs = new List<string>(Invocation.Prefix);
        fullArgs.AddRange(args);
        fullArgs.Add("--asJson");
        fullArgs.Add("--backendUrl");
        fullArgs.Add(backendUrl);
        if (E2EConfig.UseOidc)
            fullArgs.Add("--useOIDC");

        var psi = new ProcessStartInfo
        {
            FileName = Invocation.Exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in fullArgs)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"fkh {string.Join(' ', args)} timed out after {timeout}.");
        }
        process.WaitForExit(); // flush async buffers

        return new CliResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    // Runs the command, asserts success, and returns the parsed JSON stdout.
    public static JsonElement RunJson(params string[] args)
    {
        var result = Run(args);
        if (result.ExitCode != 0)
            throw new Xunit.Sdk.XunitException(
                $"fkh {string.Join(' ', args)} exited with {result.ExitCode}.\nSTDOUT:\n{result.StdOut}\nSTDERR:\n{result.StdErr}");

        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"fkh {string.Join(' ', args)} did not return JSON: {ex.Message}\nSTDOUT:\n{result.StdOut}");
        }
    }

    private static (string, string[]) ResolveInvocation()
    {
        var cliPath = Environment.GetEnvironmentVariable("FKH_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(cliPath))
        {
            return cliPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? ("dotnet", [cliPath])
                : (cliPath, []);
        }

        var csproj = FindCliProject();
        return ("dotnet", ["run", "--project", csproj, "-f", "net10.0", "--"]);
    }

    private static string FindCliProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "fkh-cli", "fkh-cli.csproj");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate fkh-cli.csproj. Set FKH_CLI_PATH to the published fkh executable or fkh.dll.");
    }
}
