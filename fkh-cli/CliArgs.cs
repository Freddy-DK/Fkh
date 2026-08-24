namespace Fkh.Cli;

// Pure argument-parsing helpers extracted from Program.cs so they can be unit tested.
internal static class CliArgs
{
    public static ParsedArgs ParseArgs(string[] args, FunctionCatalogResponse catalog)
    {
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
        {
            return new ParsedArgs { ShowHelp = true };
        }

        var command = args[0].ToLowerInvariant();
        var function = catalog.Functions.FirstOrDefault(f =>
            string.Equals(f.Name, command, StringComparison.OrdinalIgnoreCase));
        if (function is null)
        {
            throw new InvalidOperationException($"Unsupported command: {command}");
        }

        var parsed = new ParsedArgs { Command = function.Name };

        // Build a set of boolean parameter names for this function (flags, no value needed)
        var booleanParams = new HashSet<string>(
            function.Parameters.Where(p => string.Equals(p.Type, "boolean", StringComparison.OrdinalIgnoreCase)).Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);

        // 'confirm' is a reserved boolean flag that skips the interactive prompt for confirmation-required functions.
        if (function.RequiresConfirmation)
            booleanParams.Add("confirm");

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unknown argument: {arg}");
            }

            var key = arg[2..];
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException("Parameter name cannot be empty after '--'.");
            }

            if (string.Equals(key, "nowait", StringComparison.OrdinalIgnoreCase))
            {
                parsed.NoWait = true;
                continue;
            }

            if (string.Equals(key, "asJson", StringComparison.OrdinalIgnoreCase))
            {
                parsed.AsJson = true;
                continue;
            }

            if (string.Equals(key, "open", StringComparison.OrdinalIgnoreCase))
            {
                parsed.Open = true;
                continue;
            }

            if (string.Equals(key, "useOIDC", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(key, "ghUser", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "backendUrl", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                if (i >= args.Length)
                {
                    throw new InvalidOperationException($"Missing value for --{key}");
                }
                continue;
            }

            if (string.Equals(key, "output", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                if (i >= args.Length)
                {
                    throw new InvalidOperationException("Missing value for --output");
                }
                parsed.Output = args[i];
                continue;
            }

            if (booleanParams.Contains(key))
            {
                // Boolean flag — presence means true, no value expected
                parsed.Parameters[key] = "true";
            }
            else if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                // No following value — treat as boolean flag
                parsed.Parameters[key] = "true";
            }
            else
            {
                i++;
                parsed.Parameters[key] = args[i];
            }
        }

        return parsed;
    }

    // detectPublicIp is injected so callers can supply public-IP resolution; unit tests pass null.
    public static void EnsureRequiredParameters(FunctionDefinition function, Dictionary<string, string> parameters, Func<string?>? detectPublicIp = null)
    {
        var knownNames = function.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 'confirm' is a reserved flag that skips the interactive prompt for confirmation-required functions.
        if (function.RequiresConfirmation)
            knownNames.Add("confirm");

        var unknown = parameters.Keys.Where(k => !knownNames.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"Unknown parameters for {function.Name}: {string.Join(", ", unknown)}");
        }

        // Check for missing required parameters
        var missing = function.Parameters
            .Where(p => p.Required
                && !string.Equals(p.Type, "file", StringComparison.OrdinalIgnoreCase)
                && (!parameters.TryGetValue(p.Name, out var v) || string.IsNullOrWhiteSpace(v)))
            .Select(p => p.Name)
            .ToList();

        // Also check file-type required params (they won't be in parameters dict yet)
        var missingFiles = function.Parameters
            .Where(p => p.Required
                && string.Equals(p.Type, "file", StringComparison.OrdinalIgnoreCase)
                && (!parameters.TryGetValue(p.Name, out var v) || string.IsNullOrWhiteSpace(v)))
            .Select(p => p.Name)
            .ToList();
        missing.AddRange(missingFiles);

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Missing required parameters for {function.Name}: {string.Join(", ", missing.Select(m => $"--{m}"))}");
        }

        // Auto-detect IP if not provided
        foreach (var parameter in function.Parameters.Where(p =>
            string.Equals(p.Name, "ip", StringComparison.OrdinalIgnoreCase)
            && (!parameters.TryGetValue(p.Name, out var v) || string.IsNullOrWhiteSpace(v))))
        {
            var detectedIp = detectPublicIp?.Invoke();
            if (detectedIp is not null)
                parameters[parameter.Name] = detectedIp;
        }

        // Apply defaults for optional parameters
        foreach (var parameter in function.Parameters)
        {
            if (!parameters.TryGetValue(parameter.Name, out var value) || string.IsNullOrWhiteSpace(value))
            {
                if (!string.IsNullOrWhiteSpace(parameter.DefaultValue))
                {
                    parameters[parameter.Name] = parameter.DefaultValue;
                }
            }
        }
    }
}
