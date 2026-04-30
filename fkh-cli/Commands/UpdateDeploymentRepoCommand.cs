sealed class UpdateDeploymentRepoCommand : ClientCommand
{
    public override string Name => "UpdateDeploymentRepo";
    public override string Description => "Updates an existing deployment repo with the latest workflow templates from your Fkh fork. Never overwrites deployment.tfvars.";
    public override List<ClientCommandParameter> Parameters =>
    [
        new() { Name = "deploymentRepo", Type = "string", Description = "Owner/name of the deployment repo to update (e.g. myorg/fkh-deploy)", Required = true },
        new() { Name = "fkhRepo",        Type = "string", Description = "Owner/name of the Fkh fork, optionally with @branch (e.g. myorg/Fkh@dev). Default: Freddy-DK/Fkh@main", Required = false },
    ];

    public override async Task<int> ExecuteAsync(string[] args, CliSettings settings, bool asJson)
    {
        var parameters = ParseClientArgs(args);

        // If --ghUser was specified, resolve that user's token so all gh CLI calls use the correct account
        if (!string.IsNullOrWhiteSpace(settings.User))
            Environment.SetEnvironmentVariable("GH_TOKEN", GetToken(parameters, settings.User));

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

        // Resolve GitHub user account
        var (userExit, ghUser, _) = RunProcess("gh", ["api", "user", "--jq", ".login"]);
        ghUser = ghUser?.Trim();
        if (userExit != 0 || string.IsNullOrWhiteSpace(ghUser))
        {
            Console.Error.WriteLine($"{Ansi.Red}Failed to determine GitHub user. Ensure 'gh auth login' is complete.{Ansi.Reset}");
            return 1;
        }

        // Confirm before proceeding
        Console.WriteLine();
        Console.WriteLine($"  Action:          Update deployment repo");
        Console.WriteLine($"  Deployment repo: {deployFullRepo}");
        Console.WriteLine($"  Fkh fork:        {fkhRepo}");
        Console.WriteLine($"  Fkh branch:      {fkhBranch}");
        Console.WriteLine($"  GitHub account:  {ghUser}");
        Console.WriteLine();
        Console.Write("Do you want to proceed? [y/N] ");
        var answer = Console.ReadLine()?.Trim();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Aborted.");
            return 1;
        }
        Console.WriteLine();

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

        // 2. Clone to temp directory
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

                // Never overwrite deployment.tfvars
                // Never overwrite an existing deployment.tfvars, but do create it if missing
                if (relativePath.Equals("config/deployment.tfvars", StringComparison.OrdinalIgnoreCase))
                {
                    var existingPath = Path.Combine(tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(existingPath))
                    {
                        Console.WriteLine($"  {relativePath} (skipped — not overwritten)");
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
            var (_, ghName, _) = RunProcess("gh", ["api", "user", "--jq", ".login"]);
            var (_, ghEmail, _) = RunProcess("gh", ["api", "user", "--jq", ".email // (.login + \"@users.noreply.github.com\")"]);
            ghName = ghName?.Trim(); ghEmail = ghEmail?.Trim();
            if (!string.IsNullOrEmpty(ghName))
                RunProcess("git", ["config", "user.name", ghName], tempDir);
            if (!string.IsNullOrEmpty(ghEmail))
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
        return (fkhFullRepo, "main");
    }

    internal static List<string> EnumerateGitHubDirectory(string repo, string dirPath, string branch = "main")
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
}
