using System.Diagnostics;
using System.Runtime.InteropServices;

sealed class OpenCommand : ClientCommand
{
    public override string Name => "Open";
    public override string Description => "Opens an interactive pwsh prompt inside a Kubernetes pod.";
    public override List<ClientCommandParameter> Parameters =>
    [
        new() { Name = "name", Type = "string", Description = "Name of the pod to connect to.", Required = true },
        new() { Name = "wait", Type = "flag", Description = "Block the process until the session ends (default opens a new window).", Required = false },
        new() { Name = "poormansterminal", Type = "flag", Description = "Use the backend-based terminal instead of kubectl exec, even if kubectl is available.", Required = false }
    ];

    public override async Task<int> ExecuteAsync(string[] args, CliSettings settings, bool asJson)
    {
        Dictionary<string, string> parameters;
        try
        {
            parameters = ParseClientArgs(args);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"{Ansi.Red}{ex.Message}{Ansi.Reset}");
            return 1;
        }

        if (!parameters.TryGetValue("name", out var containerName) || string.IsNullOrWhiteSpace(containerName))
        {
            Console.Error.WriteLine($"{Ansi.Red}Missing required parameter --name{Ansi.Reset}");
            return 1;
        }

        var poorMansTerminal = args.Any(a => string.Equals(a, "--poormansterminal", StringComparison.OrdinalIgnoreCase));
        var wait = args.Any(a => string.Equals(a, "--wait", StringComparison.OrdinalIgnoreCase));

        if (poorMansTerminal || !IsKubectlAvailable())
        {
            if (!wait)
            {
                var exePath = Environment.ProcessPath ?? "fkh";
                var relaunchArgs = args.ToList();
                if (!relaunchArgs.Any(a => string.Equals(a, "--poormansterminal", StringComparison.OrdinalIgnoreCase)))
                    relaunchArgs.Add("--poormansterminal");
                relaunchArgs.Add("--wait");

                IEnumerable<string> reArgs = relaunchArgs;
                if (exePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
                    exePath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase))
                    reArgs = new[] { "run", "--" }.Concat(relaunchArgs);

                Console.WriteLine($"{Ansi.Cyan}Launching backend terminal in a new window...{Ansi.Reset}");
                if (LaunchInNewTerminal(exePath, reArgs))
                    return 0;

                Console.Error.WriteLine($"{Ansi.Yellow}Could not open a new terminal window — running inline.{Ansi.Reset}");
            }

            var backendUrl = ValidateBackendUrl(settings.BackendUrl);
            if (backendUrl is null)
            {
                var reason = poorMansTerminal ? "--poormansterminal requested" : "kubectl not found";
                Console.Error.WriteLine($"{Ansi.Red}{reason} — cannot fall back to backend.{Ansi.Reset}");
                return 1;
            }

            TokenProvider tokenProvider;
            try { tokenProvider = CreateTokenProvider(parameters, settings); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{Ansi.Red}{ex.Message}{Ansi.Reset}");
                return 1;
            }

            int width;
            try { width = Console.WindowWidth; } catch { width = 220; }

            return await new PoorMansTerminal(backendUrl, tokenProvider, containerName, width).RunAsync();
        }

        // Ensure kubectl is pointing at the correct AKS cluster
        if (!EnsureKubectlContext(settings.BackendUrl))
        {
            // Context check failed or user chose poor mans terminal — fall back
            var backendUrl = ValidateBackendUrl(settings.BackendUrl);
            if (backendUrl is null)
            {
                Console.Error.WriteLine($"{Ansi.Red}Cannot fall back to backend terminal — no backend URL configured.{Ansi.Reset}");
                return 1;
            }

            TokenProvider tokenProvider;
            try { tokenProvider = CreateTokenProvider(parameters, settings); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{Ansi.Red}{ex.Message}{Ansi.Reset}");
                return 1;
            }

            int width;
            try { width = Console.WindowWidth; } catch { width = 220; }

            Console.WriteLine($"{Ansi.Cyan}Falling back to backend terminal...{Ansi.Reset}");
            return await new PoorMansTerminal(backendUrl, tokenProvider, containerName, width).RunAsync();
        }

        // Resolve container name to app label (same as server-side ResolveAppName)
        // If name contains '-', treat as full name (admin); otherwise prefix with GitHub username
        var appName = containerName.Contains('-')
            ? SanitizeAppName(containerName)
            : SanitizeAppName($"{GetGitHubUsername()}-{containerName}");

        Console.WriteLine($"{Ansi.Dim}Looking up pod for container '{containerName}' (app={appName})...{Ansi.Reset}");

        // Find the pod name via label selector
        var getPodPsi = new ProcessStartInfo
        {
            FileName = "kubectl",
            ArgumentList = { "get", "pods", "-n", "app", "-l", $"app={appName}", "-o", "jsonpath={.items[*].metadata.name}" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var getPodProcess = Process.Start(getPodPsi);
        if (getPodProcess is null)
        {
            Console.Error.WriteLine($"{Ansi.Red}Could not start kubectl.{Ansi.Reset}");
            return 1;
        }

        var podName = getPodProcess.StandardOutput.ReadToEnd().Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var podErr = getPodProcess.StandardError.ReadToEnd().Trim();
        getPodProcess.WaitForExit();

        if (string.IsNullOrWhiteSpace(podName))
        {
            Console.Error.WriteLine($"{Ansi.Red}No pod found for container '{containerName}' (app={appName}).{Ansi.Reset}");
            if (!string.IsNullOrWhiteSpace(podErr))
                Console.Error.WriteLine($"{Ansi.Red}{podErr}{Ansi.Reset}");
            return 1;
        }

        Console.WriteLine($"{Ansi.Cyan}Opening pwsh prompt in pod {podName}...{Ansi.Reset}");

        // kubectl on Windows doesn't propagate terminal size to the remote pty,
        // so the remote pwsh defaults to 80x24. Work around by using PowerShell's
        // $Host.UI.RawUI to resize the terminal on startup (works in Windows containers).
        int cols, rows;
        try { cols = Console.WindowWidth; rows = Console.WindowHeight; }
        catch { cols = 120; rows = 50; }

        var sizeCmd = $"try {{ $r = $Host.UI.RawUI; $r.BufferSize = [System.Management.Automation.Host.Size]::new({cols}, 9999); $r.WindowSize = [System.Management.Automation.Host.Size]::new({cols}, {rows}) }} catch {{}}; . 'C:\\run\\prompt.ps1' -silent";

        if (wait)
        {
            var psiInline = new ProcessStartInfo
            {
                FileName = "kubectl",
                ArgumentList = { "exec", "-it", podName, "-n", "app", "--", "pwsh", "-NoExit", "-c", sizeCmd },
                UseShellExecute = false,
            };

            using var process = Process.Start(psiInline);
            if (process is null)
            {
                Console.Error.WriteLine($"{Ansi.Red}Could not start kubectl.{Ansi.Reset}");
                return 1;
            }

            process.WaitForExit();
            return process.ExitCode;
        }

        // Default: launch in a new terminal window so the current process returns immediately.
        var kubectlArgs = new[] { "exec", "-it", podName, "-n", "app", "--", "pwsh", "-NoExit", "-c", sizeCmd };
        if (!LaunchInNewTerminal("kubectl", kubectlArgs))
        {
            Console.Error.WriteLine($"{Ansi.Yellow}Could not open a new terminal window — running inline (use --wait to suppress this warning).{Ansi.Reset}");
            var psiFallback = new ProcessStartInfo
            {
                FileName = "kubectl",
                UseShellExecute = false,
            };
            foreach (var a in kubectlArgs)
                psiFallback.ArgumentList.Add(a);
            using var fallback = Process.Start(psiFallback);
            if (fallback is null)
            {
                Console.Error.WriteLine($"{Ansi.Red}Could not start kubectl.{Ansi.Reset}");
                return 1;
            }
            fallback.WaitForExit();
            return fallback.ExitCode;
        }
        return 0;
    }

    /// <summary>
    /// Checks whether kubectl is pointing at the AKS cluster that matches the backend URL.
    /// Returns true if kubectl context is correct and ready to use, false to fall back to poor mans terminal.
    /// Backend URL pattern: https://fkh-{name}-backend.azurewebsites.net/api
    /// → Resource group: fkh-{name}, Cluster: fkh-{name}-aks
    /// </summary>
    private static bool EnsureKubectlContext(string? backendUrl)
    {
        if (string.IsNullOrWhiteSpace(backendUrl))
            return true; // Can't derive cluster info — proceed optimistically

        // Extract deployment name from backend URL
        // Expected format: https://fkh-{deploymentName}-backend.azurewebsites.net/api
        if (!Uri.TryCreate(backendUrl.TrimEnd('/'), UriKind.Absolute, out var uri))
            return true;

        var host = uri.Host; // e.g. fkh-contoso-backend.azurewebsites.net
        // Strip staging suffix so both prod and staging URLs resolve to the same cluster
        host = host.Replace("-backend-staging.", "-backend.");
        const string prefix = "fkh-";
        const string suffix = "-backend.azurewebsites.net";
        if (!host.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return true; // Non-standard URL — proceed optimistically

        var deploymentName = host[prefix.Length..^suffix.Length];
        if (string.IsNullOrWhiteSpace(deploymentName))
            return true;

        var expectedCluster = $"fkh-{deploymentName}-aks";
        var resourceGroup = $"fkh-{deploymentName}";

        // Check current kubectl context
        var currentContext = RunKubectl("config", "current-context");
        if (currentContext is not null && currentContext.Contains(expectedCluster, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"{Ansi.Dim}kubectl context: {currentContext}{Ansi.Reset}");
            return true;
        }

        Console.WriteLine($"{Ansi.Yellow}kubectl context '{currentContext ?? "(none)"}' does not match expected cluster '{expectedCluster}'.{Ansi.Reset}");
        Console.Write($"Switch kubectl context to '{expectedCluster}'? [Y]es / [N]o (use backend terminal): ");

        var key = Console.ReadKey(intercept: false);
        Console.WriteLine();

        if (key.Key != ConsoleKey.Y)
            return false; // User chose poor mans terminal

        Console.WriteLine($"{Ansi.Cyan}Running: az aks get-credentials --resource-group {resourceGroup} --name {expectedCluster} --overwrite-existing{Ansi.Reset}");

        // az CLI on Windows is az.cmd — must invoke via cmd.exe
        var azFileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd" : "az";
        var azArgs = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "/c", "az", "aks", "get-credentials", "--resource-group", resourceGroup, "--name", expectedCluster, "--overwrite-existing" }
            : new[] { "aks", "get-credentials", "--resource-group", resourceGroup, "--name", expectedCluster, "--overwrite-existing" };

        while (true)
        {
            var result = RunProcess(azFileName, azArgs);
            if (result.ExitCode == 0)
            {
                Console.WriteLine($"{Ansi.Cyan}Switched kubectl context to '{expectedCluster}'.{Ansi.Reset}");
                return true;
            }

            Console.Error.WriteLine($"{Ansi.Red}Failed to get AKS credentials:{Ansi.Reset}");
            if (!string.IsNullOrWhiteSpace(result.Stderr))
                Console.Error.WriteLine($"{Ansi.Red}{result.Stderr}{Ansi.Reset}");

            Console.Write($"{Ansi.Yellow}Run 'az login' in another terminal, then press [R]etry / [N]o (use backend terminal): {Ansi.Reset}");
            var retryKey = Console.ReadKey(intercept: false);
            Console.WriteLine();

            if (retryKey.Key != ConsoleKey.R)
            {
                Console.Error.WriteLine($"{Ansi.Yellow}Reverting to backend terminal.{Ansi.Reset}");
                return false;
            }
        }
    }

    private static bool IsKubectlAvailable()
    {
        try
        {
            var result = RunProcess("kubectl", ["version", "--client"]);
            return result.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
