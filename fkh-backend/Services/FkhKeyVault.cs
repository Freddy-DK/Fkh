using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhKeyVault : FkhServiceBase
{
    private readonly string _keyVaultUri;
    private readonly string _orgName;

    public FkhKeyVault(ILogger<FkhKeyVault> logger) : this((ILogger)logger) { }

    private FkhKeyVault(ILogger logger) : base(logger)
    {
        _keyVaultUri = Environment.GetEnvironmentVariable("KEYVAULT_URI")
            ?? throw new InvalidOperationException("KEYVAULT_URI is not configured.");
        _orgName = Environment.GetEnvironmentVariable("GITHUB_REPO_OWNER")
            ?? throw new InvalidOperationException("GITHUB_REPO_OWNER is not configured.");
    }

    /// <summary>Creates an instance outside of DI (e.g. for parameter secret substitution).</summary>
    public static FkhKeyVault Create(ILogger logger) => new(logger);

    private SecretClient CreateClient()
    {
#pragma warning disable CS0618
        var credential = new ManagedIdentityCredential(ClientId);
#pragma warning restore CS0618
        return new SecretClient(new Uri(_keyVaultUri), credential);
    }

    public async Task<object> GetSecretAsync(Dictionary<string, string> parameters)
    {
        var name = ValidateName(parameters);
        var githubUsername = parameters.GetValueOrDefault("_githubUsername", "unknown");
        var isOidc = IsTrue(parameters, "_isOidc");

        var value = await TryGetSecretValueAsync(name, githubUsername, isOidc);
        return new { Name = name, Secret = value ?? "" };
    }

    /// <summary>
    /// Resolves a secret value using the personal→organization fallback: the caller's
    /// personal secret (username-name) is preferred, then the organization secret
    /// (orgname-name). OIDC has no personal user and reads the organization secret only.
    /// Returns null if no matching secret exists.
    /// </summary>
    public async Task<string?> TryGetSecretValueAsync(string name, string githubUsername, bool isOidc)
    {
        var client = CreateClient();

        if (!isOidc)
        {
            var personalSecretName = $"{NormalizePrefix(githubUsername)}-{name}";
            try
            {
                var secret = await client.GetSecretAsync(personalSecretName);
                Logger.LogInformation("User '{User}' read personal secret '{Secret}' from Key Vault.", githubUsername, personalSecretName);
                return secret.Value.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // No personal secret; fall through to the organization secret.
            }
        }

        var orgSecretName = $"{NormalizePrefix(_orgName)}-{name}";
        try
        {
            var secret = await client.GetSecretAsync(orgSecretName);
            Logger.LogInformation("User '{User}' read organization secret '{Secret}' from Key Vault.", githubUsername, orgSecretName);
            return secret.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<object> SetSecretAsync(Dictionary<string, string> parameters)
    {
        var name = ValidateName(parameters);
        var githubUsername = parameters.GetValueOrDefault("_githubUsername", "unknown");
        var secret = parameters["secret"];
        var isAllUsers = IsTrue(parameters, "allusers");
        var isAdmin = IsTrue(parameters, "_isAdmin");
        var isOidc = IsTrue(parameters, "_isOidc");

        if (isAllUsers && !isAdmin)
            throw new UnauthorizedAccessException("Organization-wide secrets are admin only. Omit --allusers to set your own personal secret.");

        if (!isAllUsers && isOidc)
            throw new ArgumentException("Personal secrets are not available with OIDC authentication (there is no personal user). Use --allusers to set an organization-wide secret.");

        var prefix = NormalizePrefix(isAllUsers ? _orgName : githubUsername);
        var secretName = $"{prefix}-{name}";
        var scope = isAllUsers ? "organization" : "personal";
        Logger.LogInformation("User '{User}' setting {Scope} secret '{Secret}' in Key Vault.", githubUsername, scope, secretName);

        var client = CreateClient();
        await client.SetSecretAsync(secretName, secret);
        return new { Name = name, Message = $"Secret '{name}' set." };
    }

    /// <summary>
    /// Validates the secret name. The name must not contain a dash so that a
    /// personal secret can never collide with the org-prefixed naming scheme.
    /// </summary>
    private static string ValidateName(Dictionary<string, string> parameters)
    {
        var name = parameters["name"].Trim();
        if (name.Length == 0 || name.Any(c => !char.IsLetterOrDigit(c)))
            throw new ArgumentException("Parameter 'name' must contain only letters and digits.");
        return name;
    }

    private static bool IsTrue(Dictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var val) && string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePrefix(string value)
        => new(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
}
