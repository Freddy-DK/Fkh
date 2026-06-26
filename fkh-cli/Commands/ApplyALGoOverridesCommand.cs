using System.Reflection;

sealed class ApplyALGoOverridesCommand : ClientCommand
{
    public override string Name => "applyALGoOverrides";
    public override string Description => "Copies bundled ALGoScripts override files to a local folder. Defaults to the current directory.";
    public override List<ClientCommandParameter> Parameters =>
    [
        new() { Name = "output", Type = "string", Description = "Destination folder for ALGoScripts files. Default: current directory", Required = false },
    ];

    public override Task<int> ExecuteAsync(string[] args, CliSettings settings, bool asJson)
    {
        Dictionary<string, string> parameters;
        try
        {
            parameters = ParseClientArgs(args);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"{Ansi.Red}{ex.Message}{Ansi.Reset}");
            return Task.FromResult(1);
        }

        var outputFolder = parameters.TryGetValue("output", out var output)
            ? output
            : Directory.GetCurrentDirectory();

        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            Console.Error.WriteLine($"{Ansi.Red}Output folder cannot be empty.{Ansi.Reset}");
            return Task.FromResult(1);
        }

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            const string scriptPrefix = "ALGoScripts/";
            var resourceNames = assembly
                .GetManifestResourceNames()
                .Where(name => name.StartsWith(scriptPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (resourceNames.Count == 0)
            {
                Console.Error.WriteLine($"{Ansi.Red}No ALGoScripts resources were found in the CLI package.{Ansi.Reset}");
                return Task.FromResult(1);
            }

            var destinationRoot = Path.GetFullPath(outputFolder);
            Directory.CreateDirectory(destinationRoot);

            var copied = 0;
            foreach (var resourceName in resourceNames)
            {
                var relativePath = resourceName[scriptPrefix.Length..]
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                var targetPath = Path.Combine(destinationRoot, relativePath);
                var targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(targetDir))
                    Directory.CreateDirectory(targetDir);

                using var resourceStream = assembly.GetManifestResourceStream(resourceName);
                if (resourceStream is null)
                    continue;

                using var fileStream = File.Create(targetPath);
                resourceStream.CopyTo(fileStream);
                copied++;

                if (!asJson)
                    Console.WriteLine($"Copied {relativePath}");
            }

            if (asJson)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    output = destinationRoot,
                    filesCopied = copied
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                Console.WriteLine($"{Ansi.Cyan}Copied {copied} ALGoScripts file(s) to {destinationRoot}{Ansi.Reset}");
            }

            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{Ansi.Red}Failed to download ALGoScripts: {ex.Message}{Ansi.Reset}");
            return Task.FromResult(1);
        }
    }
}