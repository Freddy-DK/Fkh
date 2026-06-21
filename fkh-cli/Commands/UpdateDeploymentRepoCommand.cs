sealed class UpdateDeploymentRepoCommand : ClientCommand
{
    public override string Name => "UpdateDeploymentRepo";
    public override string Description => "Updates an existing deployment repo with the latest workflow templates from your Fkh fork. Merges deployment.tfvars preserving existing values.";
    public override List<ClientCommandParameter> Parameters =>
    [
        new() { Name = "deploymentRepo", Type = "string", Description = "Owner/name of the deployment repo to update (e.g. myorg/fkh-deploy)", Required = true },
        new() { Name = "fkhRepo",        Type = "string", Description = "Owner/name of the Fkh fork, optionally with @branch (e.g. myorg/Fkh@dev). Default: Freddy-DK/Fkh@latest", Required = false },
        new() { Name = "confirm",        Type = "boolean", Description = "Skip the interactive confirmation prompt (for CI/automation)", Required = false },
    ];

    public override async Task<int> ExecuteAsync(string[] args, CliSettings settings, bool asJson)
    {
        var parameters = ParseClientArgs(args);

        // If --ghUser was specified, resolve that user's token so all gh CLI calls use the correct account
        if (!string.IsNullOrWhiteSpace(settings.User))
            Environment.SetEnvironmentVariable("GH_TOKEN", CreateTokenProvider(parameters, settings).GetToken());

        if (!parameters.TryGetValue("deploymentRepo", out var deployFullRepo) || string.IsNullOrWhiteSpace(deployFullRepo))
        {
            Console.Error.WriteLine($"{Ansi.Red}--deploymentRepo is required (e.g. myorg/fkh-deploy).{Ansi.Reset}");
            return 1;
        }

        var fkhFullRepo = parameters.TryGetValue("fkhRepo", out var fr) && !string.IsNullOrWhiteSpace(fr) ? fr : "Freddy-DK/Fkh";
        var (fkhRepo, fkhBranch) = ParseFkhRepo(fkhFullRepo);

        // Verify gh is authenticated
        var (ghExit, _, ghErr) = RunProcess("gh", ["auth", "status"]);
        if (ghExit != 0)
        {
            Console.Error.WriteLine($"{Ansi.Red}GitHub CLI is not authenticated. Run 'gh auth login' first.{Ansi.Reset}");
            Console.Error.WriteLine(ghErr);
            return 1;
        }

        var skipConfirm = parameters.TryGetValue("confirm", out var confirmVal) && string.Equals(confirmVal, "true", StringComparison.OrdinalIgnoreCase);

        // Resolve GitHub user account (only used for display; non-fatal in CI mode)
        var (userExit, ghUser, _) = RunProcess("gh", ["api", "user", "--jq", ".login"]);
        ghUser = ghUser?.Trim();
        if (userExit != 0 || string.IsNullOrWhiteSpace(ghUser))
        {
            if (!skipConfirm)
            {
                Console.Error.WriteLine($"{Ansi.Red}Failed to determine GitHub user. Ensure 'gh auth login' is complete.{Ansi.Reset}");
                return 1;
            }
            ghUser = "automation";
        }

        // Resolve "latest" / "preview" to actual release tags
        fkhBranch = ResolveFkhBranch(fkhRepo, fkhBranch);
        Console.WriteLine();
        Console.WriteLine($"  Action:          Update deployment repo");
        Console.WriteLine($"  Deployment repo: {deployFullRepo}");
        Console.WriteLine($"  Fkh fork:        {fkhRepo}");
        Console.WriteLine($"  Fkh version:     {fkhBranch}");
        Console.WriteLine($"  GitHub account:  {ghUser}");
        Console.WriteLine();
        if (!skipConfirm)
        {
            Console.Write("Do you want to proceed? [y/N] ");
            var answer = Console.ReadLine()?.Trim();
            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Aborted.");
                return 1;
            }
            Console.WriteLine();
        }

        return await UpdateDeploymentRepoAsync(deployFullRepo, fkhRepo, fkhBranch, "Update deployment repo from Fkh template");
    }

    /// <summary>
    /// Clones the deployment repo, fetches template files from the Fkh fork,
    /// writes them (skipping deployment.tfvars), commits and pushes.
    /// </summary>
    internal static async Task<int> UpdateDeploymentRepoAsync(string deployFullRepo, string fkhRepo, string fkhBranch, string commitMessage, bool quiet = false)
    {
        // 1. Verify gh is authenticated
        var (ghExit, _, ghErr) = RunProcess("gh", ["auth", "status"]);
        if (ghExit != 0)
        {
            Console.Error.WriteLine($"{Ansi.Red}GitHub CLI is not authenticated. Run 'gh auth login' first.{Ansi.Reset}");
            Console.Error.WriteLine(ghErr);
            return 1;
        }

        // 2. Configure git credential helper to use gh for push authentication
        RunProcess("gh", ["auth", "setup-git"]);

        // 3. Clone to temp directory
        var tempDir = Path.Combine(Path.GetTempPath(), $"fkh-deploy-{Guid.NewGuid():N}");
        Console.WriteLine($"Cloning {deployFullRepo}...");
        var (cloneExit, _, cloneErr) = RunProcess("gh", ["repo", "clone", deployFullRepo, tempDir]);
        if (cloneExit != 0)
        {
            Console.Error.WriteLine($"{Ansi.Red}Failed to clone repo: {cloneErr}{Ansi.Reset}");
            return 1;
        }

        try
        {
            // 3. Enumerate and fetch all files from deployment-repo/ in the Fkh fork
            Console.WriteLine($"Fetching template files from {fkhRepo}@{fkhBranch}...");
            var templateFiles = EnumerateGitHubDirectory(fkhRepo, "deployment-repo", fkhBranch);
            if (templateFiles.Count == 0)
            {
                Console.Error.WriteLine($"{Ansi.Red}No template files found in {fkhRepo}/deployment-repo.{Ansi.Reset}");
                return 1;
            }

            foreach (var templatePath in templateFiles)
            {
                // Strip the "deployment-repo/" prefix to get the target path
                var relativePath = templatePath["deployment-repo/".Length..];

                // Merge deployment.tfvars: preserve existing values in the new template
                if (relativePath.Equals("config/deployment.tfvars", StringComparison.OrdinalIgnoreCase))
                {
                    var existingPath = Path.Combine(tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(existingPath))
                    {
                        var newContent = await FetchFileFromGitHubAsync(fkhRepo, templatePath, fkhBranch);
                        if (newContent is null)
                        {
                            Console.Error.WriteLine($"{Ansi.Yellow}  Warning: Could not fetch {templatePath} — skipping.{Ansi.Reset}");
                            continue;
                        }

                        var oldContent = await File.ReadAllTextAsync(existingPath);
                        File.Move(existingPath, existingPath + ".old", overwrite: true);

                        var mergedContent = MergeTfvars(oldContent, newContent);
                        await File.WriteAllTextAsync(existingPath, mergedContent);
                        Console.WriteLine($"  {relativePath} (merged — old values preserved, old file saved as deployment.tfvars.old)");
                        continue;
                    }
                }

                var content = await FetchFileFromGitHubAsync(fkhRepo, templatePath, fkhBranch);
                if (content is null)
                {
                    Console.Error.WriteLine($"{Ansi.Yellow}  Warning: Could not fetch {templatePath} — skipping.{Ansi.Reset}");
                    continue;
                }

                // Replace Freddy-DK/Fkh with actual fkhRepo and @main with actual branch in .yml files
                if (templatePath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                {
                    content = content.Replace("Freddy-DK/Fkh", fkhRepo);
                    content = content.Replace("@main", $"@{fkhBranch}");
                    content = content.Replace("fkh-ref: main", $"fkh-ref: {fkhBranch}");
                }

                var targetPath = Path.Combine(tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var targetDir = Path.GetDirectoryName(targetPath)!;
                Directory.CreateDirectory(targetDir);
                await File.WriteAllTextAsync(targetPath, content);
                Console.WriteLine($"  {relativePath}");
            }

            // 4. Configure git identity, commit and push
            var (nameExit, ghName, _) = RunProcess("gh", ["api", "user", "--jq", ".login"]);
            ghName = nameExit == 0 ? ghName?.Trim() : null;
            if (string.IsNullOrEmpty(ghName))
                ghName = "github-actions[bot]";
            var ghEmail = nameExit == 0 && !string.IsNullOrWhiteSpace(ghName) && ghName != "github-actions[bot]"
                ? $"{ghName}@users.noreply.github.com"
                : "41898282+github-actions[bot]@users.noreply.github.com";
            RunProcess("git", ["config", "user.name", ghName], tempDir);
            RunProcess("git", ["config", "user.email", ghEmail], tempDir);

            RunProcess("git", ["add", "-A"], tempDir);

            // Check if there are changes to commit
            var (diffExit, _, _) = RunProcess("git", ["diff", "--cached", "--quiet"], tempDir);
            if (diffExit == 0)
            {
                if (!quiet)
                    Console.WriteLine("No changes detected — deployment repo is already up to date.");
                return 0;
            }

            Console.WriteLine("Committing and pushing...");
            var (commitExit, _, commitErr) = RunProcess("git", ["commit", "-m", commitMessage], tempDir);
            if (commitExit != 0)
            {
                Console.Error.WriteLine($"{Ansi.Red}Failed to commit: {commitErr}{Ansi.Reset}");
                return 1;
            }
            var (pushExit, _, pushErr) = RunProcess("git", ["push"], tempDir);
            if (pushExit != 0)
            {
                Console.Error.WriteLine($"{Ansi.Red}Failed to push: {pushErr}{Ansi.Reset}");
                return 1;
            }

            if (!quiet)
                Console.WriteLine($"{Ansi.Cyan}Deployment repo updated successfully!{Ansi.Reset}");
        }
        finally
        {
            // Clean up temp directory
            try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
        }

        return 0;
    }

    internal static Task<string?> FetchFileFromGitHubAsync(string repo, string path, string branch = "main")
    {
        // Use gh api to fetch file content (base64 encoded)
        var (exit, stdout, _) = RunProcess("gh", ["api", $"repos/{repo}/contents/{path}?ref={branch}", "--jq", ".content"]);
        if (exit != 0 || string.IsNullOrWhiteSpace(stdout))
            return Task.FromResult<string?>(null);

        try
        {
            // GitHub API returns base64 with newlines
            var base64 = stdout.Trim().Replace("\n", "").Replace("\r", "");
            var bytes = Convert.FromBase64String(base64);
            return Task.FromResult<string?>(System.Text.Encoding.UTF8.GetString(bytes));
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    internal static (string repo, string branch) ParseFkhRepo(string fkhFullRepo)
    {
        var atIndex = fkhFullRepo.IndexOf('@');
        if (atIndex >= 0)
            return (fkhFullRepo[..atIndex], fkhFullRepo[(atIndex + 1)..]);
        return (fkhFullRepo, "latest");
    }

    /// <summary>
    /// Resolves "latest" and "preview" branch aliases to actual release tags from the given repo.
    /// - "latest" → tag of the latest stable (non-prerelease) release.
    /// - "preview" → the most recent release tag (beta or stable), whichever is newer.
    /// Falls back to "main" if no releases are found.
    /// </summary>
    internal static string ResolveFkhBranch(string fkhRepo, string fkhBranch)
    {
        if (!fkhBranch.Equals("latest", StringComparison.OrdinalIgnoreCase) &&
            !fkhBranch.Equals("preview", StringComparison.OrdinalIgnoreCase))
            return fkhBranch;

        if (fkhBranch.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            // Get the latest stable (non-prerelease) release
            var (exit, tag, _) = RunProcess("gh", ["api", $"repos/{fkhRepo}/releases/latest", "--jq", ".tag_name"]);
            tag = tag?.Trim();
            if (exit == 0 && !string.IsNullOrWhiteSpace(tag))
                return tag;

            Console.Error.WriteLine($"{Ansi.Yellow}Warning: No stable release found in {fkhRepo}. Falling back to 'main'.{Ansi.Reset}");
            return "main";
        }

        // "preview" — use the newest release (beta or stable)
        // Get the most recent release overall (GitHub sorts by created_at desc)
        var (exitAll, newestTag, _) = RunProcess("gh", ["api", $"repos/{fkhRepo}/releases?per_page=1", "--jq", ".[0].tag_name"]);
        newestTag = newestTag?.Trim();
        if (exitAll == 0 && !string.IsNullOrWhiteSpace(newestTag))
            return newestTag;

        Console.Error.WriteLine($"{Ansi.Yellow}Warning: No releases found in {fkhRepo}. Falling back to 'main'.{Ansi.Reset}");
        return "main";
    }

    internal static List<string> EnumerateGitHubDirectory(string repo, string dirPath, string branch = "latest")
    {
        var files = new List<string>();
        var (exit, stdout, _) = RunProcess("gh", ["api", $"repos/{repo}/contents/{dirPath}?ref={branch}", "--jq", ".[] | .type + \"\\t\" + .path"]);
        if (exit != 0 || string.IsNullOrWhiteSpace(stdout))
            return files;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t', 2);
            if (parts.Length != 2) continue;
            var (type, path) = (parts[0], parts[1]);
            if (type == "file")
                files.Add(path);
            else if (type == "dir")
                files.AddRange(EnumerateGitHubDirectory(repo, path, branch));
        }
        return files;
    }

    /// <summary>
    /// Merges two tfvars files: uses the new template's structure but preserves values from the old file.
    /// </summary>
    internal static string MergeTfvars(string oldContent, string newContent)
    {
        var oldValues = ParseTfvarsValues(oldContent);
        var newLines = newContent.Split('\n');
        var result = new List<string>();

        for (int i = 0; i < newLines.Length; i++)
        {
            var line = newLines[i];
            var key = ExtractTfvarsKey(line);

            if (key is null || !oldValues.TryGetValue(key, out var oldValue))
            {
                // New key not in old file — emit ALL lines of the value (including multi-line body)
                if (key is not null)
                {
                    int endLine = SkipMultiLineValue(newLines, i);
                    for (int j = i; j <= endLine; j++)
                        result.Add(newLines[j]);
                    i = endLine;
                }
                else
                {
                    result.Add(line);
                }
                continue;
            }

            // We have an old value for this key — emit the key with old value
            result.AddRange(oldValue.Split('\n'));
            // Skip past the new template's value (which may be multi-line)
            i = SkipMultiLineValue(newLines, i);
        }

        return string.Join('\n', result);
    }

    /// <summary>
    /// Parses a tfvars file and returns a dictionary of key → full assignment line(s) (key = value, possibly multi-line).
    /// </summary>
    static Dictionary<string, string> ParseTfvarsValues(string content)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = content.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var key = ExtractTfvarsKey(lines[i]);
            if (key is null) continue;

            int startLine = i;
            i = SkipMultiLineValue(lines, i);

            // Collect all lines for this assignment
            var assignmentLines = new List<string>();
            for (int j = startLine; j <= i; j++)
                assignmentLines.Add(lines[j]);

            values[key] = string.Join('\n', assignmentLines);
        }

        return values;
    }

    /// <summary>
    /// Extracts the variable name from a tfvars assignment line, or null if the line is not an assignment.
    /// </summary>
    static string? ExtractTfvarsKey(string line)
    {
        var trimmed = line.TrimStart();
        // Skip comments and blank lines
        if (trimmed.Length == 0 || trimmed[0] == '#') return null;

        var eqIndex = trimmed.IndexOf('=');
        if (eqIndex <= 0) return null;

        var key = trimmed[..eqIndex].TrimEnd();
        // Key must be a valid identifier (letters, digits, underscores)
        if (key.Length == 0 || !key.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'))
            return null;

        return key;
    }

    /// <summary>
    /// Given lines and the index of an assignment line, returns the index of the last line of the value
    /// (handling multi-line lists, objects, and heredocs).
    /// </summary>
    static int SkipMultiLineValue(string[] lines, int assignmentLine)
    {
        var line = lines[assignmentLine];
        var eqIndex = line.IndexOf('=');
        if (eqIndex < 0) return assignmentLine;

        var valueStart = line[(eqIndex + 1)..].TrimStart();

        // Heredoc: <<-DELIMITER or <<DELIMITER
        if (valueStart.StartsWith("<<"))
        {
            var delimiter = valueStart.TrimStart('<').TrimStart('-').Trim();
            for (int i = assignmentLine + 1; i < lines.Length; i++)
            {
                if (lines[i].TrimStart() == delimiter || lines[i].Trim() == delimiter)
                    return i;
            }
            return lines.Length - 1;
        }

        // Multi-line list [...] or object {...}
        char open, close;
        if (valueStart.StartsWith('[')) { open = '['; close = ']'; }
        else if (valueStart.StartsWith('{')) { open = '{'; close = '}'; }
        else return assignmentLine; // simple single-line value

        int depth = 0;
        for (int i = assignmentLine; i < lines.Length; i++)
        {
            var scanLine = (i == assignmentLine) ? line[(eqIndex + 1)..] : lines[i];
            foreach (var ch in scanLine)
            {
                if (ch == open) depth++;
                else if (ch == close) depth--;
            }
            if (depth <= 0) return i;
        }
        return lines.Length - 1;
    }
}
