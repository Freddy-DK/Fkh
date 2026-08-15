using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhKeyVault : FkhServiceBase
{
    private readonly string _keyVaultUri;
    private readonly string _orgName;

    public FkhKeyVault(ILogger<FkhKeyVault> logger) : base(logger)
    {
        _keyVaultUri = Environment.GetEnvironmentVariable("KEYVAULT_URI")
            ?? throw new InvalidOperationException("KEYVAULT_URI is not configured.");
        _orgName = Environment.GetEnvironmentVariable("GITHUB_REPO_OWNER")
            ?? throw new InvalidOperationException("GITHUB_REPO_OWNER is not configured.");
    }

    private SecretClient CreateClient()
    {
#pragma warning disable CS0618
        var credential = new ManagedIdentityCredential(ClientId);
#pragma warning restore CS0618
        return new SecretClient(new Uri(_keyVaultUri), credential);
    }

    public async Task<object> GetSecretAsync(Dictionary<string, string> parameters)
    {
        var (name, secretName, scope, githubUsername) = ResolveSecretName(parameters);
        Logger.LogInformation("User '{User}' reading {Scope} secret '{Secret}' from Key Vault.", githubUsername, scope, secretName);

        var client = CreateClient();
        try
        {
            var secret = await client.GetSecretAsync(secretName);
            return new { Name = name, Secret = secret.Value.Value };
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new { Name = name, Secret = "" };
        }
    }

    public async Task<object> SetSecretAsync(Dictionary<string, string> parameters)
    {
        var (name, secretName, scope, githubUsername) = ResolveSecretName(parameters);
        var secret = parameters["secret"];
        Logger.LogInformation("User '{User}' setting {Scope} secret '{Secret}' in Key Vault.", githubUsername, scope, secretName);

        var client = CreateClient();
        await client.SetSecretAsync(secretName, secret);
        return new { Name = name, Message = $"Secret '{name}' set." };
    }

    /// <summary>
    /// Resolves the prefixed Key Vault secret name and enforces access rules.
    /// Personal secrets are prefixed with the caller's GitHub username; organization
    /// secrets are prefixed with the org name and require admin. The name must not
    /// contain a dash so an admin's org-scoped read can never resolve to a personal secret.
    /// </summary>
    private (string Name, string SecretName, string Scope, string GithubUsername) ResolveSecretName(Dictionary<string, string> parameters)
    {
        var name = parameters["name"].Trim();
        var githubUsername = parameters.GetValueOrDefault("_githubUsername", "unknown");
        var isPersonal = parameters.TryGetValue("personal", out var personalVal)
            && string.Equals(personalVal, "true", StringComparison.OrdinalIgnoreCase);
        var isAdmin = parameters.TryGetValue("_isAdmin", out var adminVal)
            && string.Equals(adminVal, "true", StringComparison.OrdinalIgnoreCase);
        var isOidc = parameters.TryGetValue("_isOidc", out var oidcVal)
            && string.Equals(oidcVal, "true", StringComparison.OrdinalIgnoreCase);

        if (name.Length == 0 || name.Any(c => !char.IsLetterOrDigit(c)))
            throw new ArgumentException("Parameter 'name' must contain only letters and digits.");

        if (isPersonal && isOidc)
            throw new ArgumentException("Personal secrets are not available with OIDC authentication (there is no personal user). Omit --personal to use organization secrets.");

        if (!isPersonal && !isAdmin)
            throw new UnauthorizedAccessException("Organization secrets are admin only. Use --personal to manage your own secrets.");

        var prefix = NormalizePrefix(isPersonal ? githubUsername : _orgName);
        return (name, $"{prefix}-{name}", isPersonal ? "personal" : "organization", githubUsername);
    }

    private static string NormalizePrefix(string value)
        => new(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
}
