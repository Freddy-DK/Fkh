using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Fkh.Services;

/// <summary>
/// Validates Azure DevOps OIDC tokens and checks the subject claim against the allowed connections list.
/// </summary>
public class AdoOidcService
{
    private static readonly List<AdoConnection> AllowedConnections = LoadAllowedConnections();

    private static readonly Dictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> ConfigManagers = CreateConfigManagers();

    /// <summary>
    /// Returns true if the token's issuer matches a known Azure DevOps OIDC issuer.
    /// </summary>
    public static bool IsAdoOidcToken(string token)
    {
        if (!token.StartsWith("eyJ", StringComparison.Ordinal))
            return false;

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(token);
            return jwt.Issuer.StartsWith("https://vstoken.dev.azure.com/", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validates an Azure DevOps OIDC token and returns the subject claim (e.g. "sc://org/project/connection").
    /// Returns null if validation fails or the connection is not in the allow-list.
    /// </summary>
    public async Task<string?> ValidateTokenAsync(string token)
    {
        if (AllowedConnections.Count == 0)
            return null;

        var handler = new JwtSecurityTokenHandler();
        JwtSecurityToken unvalidatedJwt;
        try
        {
            unvalidatedJwt = handler.ReadJwtToken(token);
        }
        catch
        {
            return null;
        }

        var issuer = unvalidatedJwt.Issuer;
        if (!ConfigManagers.TryGetValue(issuer.ToLowerInvariant(), out var configManager))
            return null;

        var config = await configManager.GetConfigurationAsync(CancellationToken.None);

        var validationParameters = new TokenValidationParameters
        {
            ValidIssuer = issuer,
            IssuerSigningKeys = config.SigningKeys,
            ValidAudiences = ["api://AzureADTokenExchange"],
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        };

        try
        {
            handler.ValidateToken(token, validationParameters, out var validatedToken);
            var jwt = (JwtSecurityToken)validatedToken;

            var subject = jwt.Subject;
            if (string.IsNullOrEmpty(subject))
                return null;

            // Check if the subject matches any allowed connection
            var isAllowed = AllowedConnections.Any(c =>
                string.Equals($"sc://{c.DevopsOrg}/{c.DevopsProject}/{c.DevopsConnectionName}", subject, StringComparison.OrdinalIgnoreCase));

            return isAllowed ? subject : null;
        }
        catch (SecurityTokenException)
        {
            return null;
        }
    }

    private static List<AdoConnection> LoadAllowedConnections()
    {
        var raw = Environment.GetEnvironmentVariable("ALLOWED_ADO_CONNECTIONS");
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return JsonSerializer.Deserialize<List<AdoConnection>>(raw, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        }) ?? [];
    }

    private static Dictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> CreateConfigManagers()
    {
        var managers = new Dictionary<string, ConfigurationManager<OpenIdConnectConfiguration>>(StringComparer.OrdinalIgnoreCase);
        foreach (var conn in AllowedConnections)
        {
            var issuer = $"https://vstoken.dev.azure.com/{conn.DevopsOrgId}";
            var key = issuer.ToLowerInvariant();
            if (!managers.ContainsKey(key))
            {
                managers[key] = new ConfigurationManager<OpenIdConnectConfiguration>(
                    $"{issuer}/.well-known/openid-configuration",
                    new OpenIdConnectConfigurationRetriever(),
                    new HttpDocumentRetriever());
            }
        }
        return managers;
    }

    private sealed class AdoConnection
    {
        public string DevopsOrgId { get; set; } = "";
        public string DevopsOrg { get; set; } = "";
        public string DevopsProject { get; set; } = "";
        public string DevopsConnectionName { get; set; } = "";
    }
}
