sealed class CreateDeploymentRepoCommand : ClientCommand
{
    public override string Name => "CreateDeploymentRepo";
    public override string Description => "Creates a private GitHub repo with deployment workflow templates for your Fkh fork.";
    public override List<ClientCommandParameter> Parameters =>
    [
        new() { Name = "deploymentRepo", Type = "string", Description = "Owner/name of the deployment repo to create (e.g. myorg/fkh-deploy)", Required = true },
        new() { Name = "fkhRepo",        Type = "string", Description = "Owner/name of the Fkh fork, optionally with @branch (e.g. myorg/Fkh@dev). Default: Freddy-DK/Fkh@latest", Required = false },
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
        var (fkhRepo, fkhBranch) = UpdateDeploymentRepoCommand.ParseFkhRepo(fkhFullRepo);

        // 1. Verify gh is authenticated
        var (ghExit, _, ghErr) = RunProcess("gh", ["auth", "status"]);
        if (ghExit != 0)
        {
            Console.Error.WriteLine($"{Ansi.Red}GitHub CLI is not authenticated. Run 'gh auth login' first.{Ansi.Reset}");
            Console.Error.WriteLine(ghErr);
            return 1;
        }

        // 2. Resolve GitHub user account
        var (userExit, ghUser, _) = RunProcess("gh", ["api", "user", "--jq", ".login"]);
        ghUser = ghUser?.Trim();
        if (userExit != 0 || string.IsNullOrWhiteSpace(ghUser))
        {
            Console.Error.WriteLine($"{Ansi.Red}Failed to determine GitHub user. Ensure 'gh auth login' is complete.{Ansi.Reset}");
            return 1;
        }

        // 3. Confirm before proceeding
        Console.WriteLine();
        Console.WriteLine($"  Action:          Create private deployment repo");
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

        // 4. Create private repo
        Console.WriteLine($"Creating private repo {deployFullRepo}...");
        var (createExit, createOut, createErr) = RunProcess("gh", ["repo", "create", deployFullRepo, "--private", "--confirm"]);
        if (createExit != 0)
        {
            Console.Error.WriteLine($"{Ansi.Red}Failed to create repo: {createErr}{Ansi.Reset}");
            return 1;
        }
        Console.WriteLine($"  Created: {createOut.Trim()}");

        // 5. Populate the repo with template files
        var result = await UpdateDeploymentRepoCommand.UpdateDeploymentRepoAsync(deployFullRepo, fkhRepo, fkhBranch, "Initial deployment repo from Fkh template", quiet: true);
        if (result != 0)
            return result;

        // 6. Print next steps
        Console.WriteLine();
        Console.WriteLine($"{Ansi.Cyan}Deployment repo created successfully!{Ansi.Reset}");
        Console.WriteLine($"  https://github.com/{deployFullRepo}");
        Console.WriteLine();
        Console.WriteLine("Next steps:");
        Console.WriteLine($"  1. Setup Azure Identity: https://github.com/{fkhRepo}/blob/{fkhBranch}/Installation/Step2-AzureIdentity.md");
        Console.WriteLine($"  2. Setup GitHub App: https://github.com/{fkhRepo}/blob/{fkhBranch}/Installation/Step3-GitHubApp.md");
        Console.WriteLine($"  3. Setup GitHub Teams: https://github.com/{fkhRepo}/blob/{fkhBranch}/Installation/Step4-GitHubTeams.md");
        Console.WriteLine($"  4. Edit config/deployment.tfvars in {deployFullRepo}: https://github.com/{fkhRepo}/blob/{fkhBranch}/Installation/Step5-ConfigureEnvironment.md");
        Console.WriteLine("  5. Add these GitHub Secrets in the deployment repo:");
        Console.WriteLine("     - SQL_SA_PASSWORD");
        Console.WriteLine("     - GH_APP_PRIVATE_KEY");
        Console.WriteLine("  6. Run the 'Deploy Full Stack' workflow from the Actions tab");

        return 0;
    }
}
