using System.Diagnostics;
using System.Runtime.InteropServices;

sealed class ClientCommandParameter
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string Description { get; init; }
    public bool Required { get; init; }
}

abstract class ClientCommand
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract List<ClientCommandParameter> Parameters { get; }
    public abstract Task<int> ExecuteAsync(string[] args, CliSettings settings, bool asJson);

    protected static Dictionary<string, string> ParseClientArgs(string[] args)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                throw new InvalidOperationException($"Unknown argument: {arg}");

            var key = arg[2..];
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("Parameter name cannot be empty after '--'.");

            if (string.Equals(key, "asJson", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "nowait", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "wait", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "useOIDC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "poormansterminal", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(key, "ghUser", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "backendUrl", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                if (i >= args.Length)
                    throw new InvalidOperationException($"Missing value for --{key}");
                continue;
            }

            // If no following value or next arg is another flag, treat as boolean
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                parameters[key] = "true";
                continue;
            }

            i++;
            parameters[key] = args[i];
        }
        return parameters;
    }

    /// <summary>
    /// Creates a TokenProvider for client commands, respecting the --useOIDC setting.
    /// </summary>
    protected static TokenProvider CreateTokenProvider(Dictionary<string, string> parameters, CliSettings settings)
    {
        return new TokenProvider(useOidc: settings.UseOidc, ghUser: settings.User);
    }

    /// <summary>
    /// Validates and returns the trimmed backend URL, or writes an error and returns null.
    /// </summary>
    protected static string? ValidateBackendUrl(string? backendUrl)
    {
        var url = backendUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.Error.WriteLine($"{Ansi.Red}No backend URL configured. Set FKH_BACKEND_URL, create ~/.fkh/settings.json, or pass --backendUrl.{Ansi.Reset}");
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            Console.Error.WriteLine($"{Ansi.Red}Invalid backend URL: '{url}'. Must be an absolute http/https URL (e.g. https://fkh-myorg-backend.azurewebsites.net/api).{Ansi.Reset}");
            return null;
        }

        return url;
    }

    internal const int ProtocolVersion = 1;
    internal const string ClientApp = "CLI";

    /// <summary>
    /// Adds the standard Fkh protocol headers (protocol version + client identifier) to a request.
    /// </summary>
    internal static void AddProtocolHeaders(System.Net.Http.HttpRequestMessage request)
    {
        request.Headers.Add("X-Fkh-Protocol-Version", ProtocolVersion.ToString());
        request.Headers.Add("X-Fkh-Client", ClientApp);
    }

    // ── Shared K8s / process helpers ─────────────────────────────────────────

    protected static (int ExitCode, string Stdout, string Stderr) RunProcess(string fileName, string[] args, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process is null)
            return (1, "", "Could not start process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    protected static string? RunKubectl(params string[] args)
    {
        var result = RunProcess("kubectl", args);
        return result.ExitCode == 0 ? result.Stdout?.Trim() : null;
    }

    protected static string GetGitHubUsername()
    {
        var result = RunProcess("gh", ["api", "user", "--jq", ".login"]);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
            throw new InvalidOperationException("Could not determine GitHub username. Run 'gh auth login' first.");
        return result.Stdout.Trim();
    }

    protected static string SanitizeAppName(string name)
    {
        var appName = name.Replace('.', '-').Replace('_', '-').ToLowerInvariant();
        if (appName.Length > 63) appName = appName[..63];
        return appName.TrimEnd('-');
    }

    protected static bool DownloadFileFromPod(string podName, string remoteFile, string localFile)
    {
        // Chunked base64 download: pod reads file in 3MB chunks, base64-encodes each, writes one line per chunk
        var script = "$f = [IO.File]::OpenRead('" + remoteFile + "'); try { $buf = New-Object byte[] 3145728; while (($n = $f.Read($buf, 0, $buf.Length)) -gt 0) { if ($n -lt $buf.Length) { $chunk = New-Object byte[] $n; [Array]::Copy($buf, $chunk, $n); [Console]::WriteLine([Convert]::ToBase64String($chunk)) } else { [Console]::WriteLine([Convert]::ToBase64String($buf)) } } } finally { $f.Close() }";
        var psi = new ProcessStartInfo
        {
            FileName = "kubectl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "exec", podName, "-n", "app", "--", "pwsh", "-c", script })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process is null) return false;

        using (var fs = File.Create(localFile))
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;
                var bytes = Convert.FromBase64String(line);
                fs.Write(bytes, 0, bytes.Length);
            }
        }

        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine($"{Ansi.Red}{stderr}{Ansi.Reset}");
            return false;
        }
        return true;
    }

    protected static bool UploadFileToPod(string podName, string remoteFile, string localFile)
    {
        // Chunked base64 upload: C# reads file in 3MB chunks, base64-encodes each line to stdin.
        // Pod reads lines, decodes each, appends to file.
        var script = "$f = [IO.File]::Create('" + remoteFile + "'); try { while (($line = [Console]::ReadLine()) -ne $null) { if ($line.Length -gt 0) { $bytes = [Convert]::FromBase64String($line); $f.Write($bytes, 0, $bytes.Length) } } } finally { $f.Close() }";
        var psi = new ProcessStartInfo
        {
            FileName = "kubectl",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "exec", "-i", podName, "-n", "app", "--", "pwsh", "-c", script })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process is null) return false;

        using (var fs = File.OpenRead(localFile))
        {
            var buf = new byte[3 * 1024 * 1024]; // 3 MB chunks → ~4 MB base64 lines
            int bytesRead;
            while ((bytesRead = fs.Read(buf, 0, buf.Length)) > 0)
            {
                var b64 = Convert.ToBase64String(buf, 0, bytesRead);
                process.StandardInput.WriteLine(b64);
            }
        }
        process.StandardInput.Close();

        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine($"{Ansi.Red}{stderr}{Ansi.Reset}");
            return false;
        }
        return true;
    }

    // ── Cross-platform helpers ───────────────────────────────────────────────

    /// <summary>
    /// Launch a command in a new terminal window. Returns true if launched successfully.
    /// Falls back to inline execution if no supported terminal emulator is found.
    /// </summary>
    protected static bool LaunchInNewTerminal(string fileName, IEnumerable<string> arguments)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Build a cmd-safe command line with double-quote escaping
            static string CmdEscape(string arg) =>
                arg.Any(c => c is ' ' or '\t' or '&' or '|' or '<' or '>' or '^' or '"')
                    ? "\"" + arg.Replace("\"", "\\\"") + "\""
                    : arg;

            var parts = new List<string> { CmdEscape(fileName) };
            foreach (var arg in arguments)
                parts.Add(CmdEscape(arg));
            var cmdLine = string.Join(' ', parts);

            // Close on success, pause on failure so the user can read the error
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{cmdLine} || (echo. & echo Press any key to close... & pause >nul)\"",
                UseShellExecute = true,
            };
            return Process.Start(psi) is not null;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Write a temp script and open it in Terminal.app
            var script = BuildShellScript(fileName, arguments);
            var psi = new ProcessStartInfo
            {
                FileName = "open",
                ArgumentList = { "-a", "Terminal", script },
                UseShellExecute = false,
            };
            return Process.Start(psi) is not null;
        }

        // Linux: try common terminal emulators
        var commandLine = BuildCommandLine(fileName, arguments);
        string[][] terminals =
        [
            ["x-terminal-emulator", "-e", commandLine],
            ["gnome-terminal", "--", "sh", "-c", commandLine],
            ["konsole", "-e", "sh", "-c", commandLine],
            ["xfce4-terminal", "-e", commandLine],
            ["xterm", "-e", commandLine],
        ];

        foreach (var termArgs in terminals)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = termArgs[0],
                    UseShellExecute = false,
                };
                for (var i = 1; i < termArgs.Length; i++)
                    psi.ArgumentList.Add(termArgs[i]);
                if (Process.Start(psi) is not null)
                    return true;
            }
            catch (System.ComponentModel.Win32Exception) { /* not found, try next */ }
        }

        return false;
    }

    private static string BuildShellScript(string fileName, IEnumerable<string> arguments)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"fkh-{Guid.NewGuid():N}.sh");
        var cmdLine = BuildCommandLine(fileName, arguments);
        File.WriteAllText(scriptPath, $"#!/bin/sh\n{cmdLine}\nrm -f \"{scriptPath}\"\n");
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return scriptPath;
    }

    private static string BuildCommandLine(string fileName, IEnumerable<string> arguments)
    {
        var parts = new List<string> { ShellEscape(fileName) };
        foreach (var arg in arguments)
            parts.Add(ShellEscape(arg));
        return string.Join(' ', parts);
    }

    private static string ShellEscape(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '"' or '\'' or '\\' or '$' or '`' or '!' or '(' or ')' or '{' or '}' or '[' or ']' or '|' or '&' or ';' or '<' or '>' or '*' or '?' or '#' or '~'))
            return arg;
        return "'" + arg.Replace("'", "'\\''") + "'";
    }

    /// <summary>Returns the user's preferred editor command.</summary>
    protected static string GetEditorCommand()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "notepad.exe";

        var editor = Environment.GetEnvironmentVariable("VISUAL")
            ?? Environment.GetEnvironmentVariable("EDITOR");
        if (!string.IsNullOrWhiteSpace(editor))
            return editor;

        // Fallback: try common editors
        foreach (var candidate in new[] { "nano", "vi" })
        {
            try
            {
                var result = RunProcess("which", [candidate]);
                if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Stdout))
                    return candidate;
            }
            catch { /* not found */ }
        }

        return "vi";
    }
}

static class ClientCommands
{
    public static List<ClientCommand> All { get; } =
    [
        new ApplyALGoOverridesCommand(),
        new PublishAppCommand(),
        new UploadDatabaseCommand(),
        new DownloadDatabaseCommand(),
        new StatusCommand(),
        new OpenCommand(),
        new EditCommand(),
        new CopyFromContainerCommand(),
        new CopyToContainerCommand(),
        new CreateDeploymentRepoCommand(),
        new UpdateDeploymentRepoCommand()
    ];
}
