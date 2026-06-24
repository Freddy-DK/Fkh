using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.ResourceManager.ContainerService;
using Fkh.Models;
using Fkh.Services;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Fkh;

/// <summary>
/// Base class for all Fkh Azure Functions.
/// Handles GitHub token extraction, identity validation, team membership check,
/// and uniform HTTP response shaping so individual functions stay minimal.
/// </summary>
public abstract class FunctionBase
{
    private static readonly List<OrgTeamConfig> AllowedOrgTeams = LoadOrgTeamConfig("ALLOWED_ORG_TEAMS");
    private static readonly List<OrgTeamConfig> AdminOrgTeams = LoadOrgTeamConfig("ADMIN_ORG_TEAMS", required: false);
    private static readonly List<OrgTeamConfig> SupportOrgTeams = LoadOrgTeamConfig("SUPPORT_ORG_TEAMS", required: false);
    private static readonly List<AllowedUserConfig> AllowedUsers = LoadAllowedUsers();
    private static readonly GitHubOidcService OidcService = new();
    private static readonly AdoOidcService AdoOidcService = new();

    // ── Brute-force protection ───────────────────────────────────────────────────
    private const int MaxFailedAttempts = 3;
    private static readonly TimeSpan BlockWindow = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, FailedAttemptRecord> FailedAttempts = new();

    private sealed class FailedAttemptRecord
    {
        public int Count;
        public DateTime WindowStart = DateTime.UtcNow;
    }

    private static string GetClientIp(HttpRequestData req)
    {
        // Azure Functions behind a load balancer forwards the real IP in X-Forwarded-For
        if (req.Headers.TryGetValues("X-Forwarded-For", out var xff))
        {
            var first = xff.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                // X-Forwarded-For can be "client, proxy1, proxy2" — take the first
                var ip = first.Split(',')[0].Trim();
                // Strip port if present (e.g. "1.2.3.4:12345")
                var colonIdx = ip.LastIndexOf(':');
                if (colonIdx > 0 && !ip.Contains(']')) // avoid stripping IPv6
                    ip = ip[..colonIdx];
                return ip;
            }
        }
        return req.Url.Host;
    }

    private static bool IsIpBlocked(string ip)
    {
        if (!FailedAttempts.TryGetValue(ip, out var record))
            return false;

        // If the window has expired, reset
        if (DateTime.UtcNow - record.WindowStart > BlockWindow)
        {
            FailedAttempts.TryRemove(ip, out _);
            return false;
        }

        return record.Count >= MaxFailedAttempts;
    }

    private static void RecordFailedAttempt(string ip)
    {
        FailedAttempts.AddOrUpdate(ip,
            _ => new FailedAttemptRecord { Count = 1 },
            (_, existing) =>
            {
                if (DateTime.UtcNow - existing.WindowStart > BlockWindow)
                {
                    // Window expired — start fresh
                    existing.Count = 1;
                    existing.WindowStart = DateTime.UtcNow;
                }
                else
                {
                    Interlocked.Increment(ref existing.Count);
                }
                return existing;
            });
    }

    private static void ClearFailedAttempts(string ip)
    {
        FailedAttempts.TryRemove(ip, out _);
    }

    /// <summary>Returns IPs currently blocked due to too many failed attempts.</summary>
    public static List<string> GetBlockedIps()
    {
        var now = DateTime.UtcNow;
        return FailedAttempts
            .Where(kv => now - kv.Value.WindowStart <= BlockWindow && kv.Value.Count >= MaxFailedAttempts)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>Returns IPs with recent failed auth attempts (within the block window).</summary>
    public static List<object> GetRecentFailedAttempts()
    {
        var now = DateTime.UtcNow;
        return FailedAttempts
            .Where(kv => now - kv.Value.WindowStart <= BlockWindow)
            .Select(kv => (object)new
            {
                Ip = kv.Key,
                FailedCount = kv.Value.Count,
                Blocked = kv.Value.Count >= MaxFailedAttempts,
                WindowStart = kv.Value.WindowStart,
            })
            .ToList();
    }

    /// <summary>
    /// Like <see cref="ExecuteAsync"/> but expects a multipart/form-data request.
    /// The "parameters" JSON part is parsed the same way.  File parts are passed
    /// separately so the service can stream them without buffering the full blob
    /// in the parameter dictionary.
    /// </summary>
    protected async Task<HttpResponseData> ExecuteWithFileAsync(
        HttpRequestData req,
        ILogger logger,
        GitHubAuthService gitHub,
        string operationName,
        Func<Dictionary<string, string>, Dictionary<string, byte[]>, Task<object>> aksOperation)
    {
        var (auth, errorResponse) = await AuthenticateAndAuthorizeAsync(req, logger, gitHub, operationName);
        if (errorResponse is not null) return errorResponse;

        {
            var clusterError = await CheckClusterRunningAsync(req, logger);
            if (clusterError is not null) return clusterError;
        }

        // ── Parse multipart/form-data ────────────────────────────────────────────
        var contentType = req.Headers.GetValues("Content-Type").FirstOrDefault() ?? "";
        if (!contentType.Contains("multipart/form-data"))
            return Respond(req, HttpStatusCode.BadRequest, "Expected multipart/form-data request.");

        var boundary = GetMultipartBoundary(contentType);
        if (boundary is null)
            return Respond(req, HttpStatusCode.BadRequest, "Missing multipart boundary.");

        // Try to seek body stream to start in case it was already read
        if (req.Body.CanSeek)
        {
            req.Body.Position = 0;
        }

        // Read body into buffer
        using var bodyMs = new MemoryStream();
        await req.Body.CopyToAsync(bodyMs);
        var bodyBytes = bodyMs.ToArray();

        var (formFields, formFiles) = await ParseMultipartAsync(new MemoryStream(bodyBytes), boundary);

        // Build parameter dictionary from the "parameters" JSON field or individual form fields
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (formFields.TryGetValue("parameters", out var parametersJson))
        {
            try
            {
                var invokeRequest = JsonSerializer.Deserialize<FunctionInvokeRequest>(parametersJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (invokeRequest?.Parameters != null)
                {
                    foreach (var kv in invokeRequest.Parameters)
                        parameters[kv.Key] = kv.Value;
                }
            }
            catch
            {
                return Respond(req, HttpStatusCode.BadRequest, "Invalid JSON in 'parameters' form field.");
            }
        }

        // Also accept individual form fields as parameters (non-file fields)
        foreach (var kv in formFields)
        {
            if (!string.Equals(kv.Key, "parameters", StringComparison.OrdinalIgnoreCase))
                parameters[kv.Key] = kv.Value;
        }

        // Extract _prefixed internal parameters before validation
        var internalParams = parameters.Where(kv => kv.Key.StartsWith('_')).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var key in internalParams.Keys)
            parameters.Remove(key);

        // Remove file-type parameters from validation (they come from formFiles)
        var fileParamNames = auth!.Function.Parameters
            .Where(p => string.Equals(p.Type, "file", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Validate non-file parameters
        var allowedNames = auth.Function.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = parameters.Keys.Where(k => !allowedNames.Contains(k)).ToList();
        if (unknown.Count > 0)
            return Respond(req, HttpStatusCode.BadRequest, $"Unknown parameters for {auth.Function.Name}: {string.Join(", ", unknown)}.");

        foreach (var parameter in auth.Function.Parameters)
        {
            if (fileParamNames.Contains(parameter.Name))
            {
                // File parameters are validated by checking formFiles
                if (parameter.Required && !formFiles.ContainsKey(parameter.Name))
                    return Respond(req, HttpStatusCode.BadRequest, $"Missing required file parameter '{parameter.Name}'.");
                continue;
            }

            parameters.TryGetValue(parameter.Name, out var value);
            value = string.IsNullOrWhiteSpace(value) ? parameter.DefaultValue : value;

            if (parameter.Required && string.IsNullOrWhiteSpace(value))
                return Respond(req, HttpStatusCode.BadRequest, $"Missing required parameter '{parameter.Name}' for {auth.Function.Name}.");

            if (string.Equals(parameter.Name, "name", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(value))
            {
                if (!value.All(c => char.IsLetterOrDigit(c) || c == '-'))
                    return Respond(req, HttpStatusCode.BadRequest, "Parameter 'name' may only contain alphanumeric characters and hyphens.");
            }

            if (!string.IsNullOrWhiteSpace(value))
                parameters[parameter.Name] = value;
        }

        ClearFailedAttempts(auth.ClientIp);
        parameters["_githubUsername"] = auth.Username;
        parameters["_isAdmin"] = auth.IsAdmin.ToString();
        parameters["_isSupport"] = auth.IsSupport.ToString();
        foreach (var kv in internalParams)
            parameters[kv.Key] = kv.Value;

        // ── Execute operation ─────────────────────────────────────────────────────
        return await RunOperationAsync(req, logger, operationName, auth.Username,
            () => aksOperation(parameters, formFiles));
    }

    private static string? GetMultipartBoundary(string contentType)
    {
        // Content-Type: multipart/form-data; boundary=----WebKitFormBoundary...
        var parts = contentType.Split(';');
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
            {
                var boundary = trimmed["boundary=".Length..].Trim('"');
                return string.IsNullOrWhiteSpace(boundary) ? null : boundary;
            }
        }
        return null;
    }

    private static async Task<(Dictionary<string, string> Fields, Dictionary<string, byte[]> Files)> ParseMultipartAsync(
        Stream body, string boundary)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        // Read the entire body (Azure Functions buffers it anyway)
        using var ms = new MemoryStream();
        await body.CopyToAsync(ms);
        var data = ms.ToArray();

        var boundaryBytes = Encoding.UTF8.GetBytes("--" + boundary);
        var endBoundaryBytes = Encoding.UTF8.GetBytes("--" + boundary + "--");

        var positions = new List<int>();
        for (var i = 0; i <= data.Length - boundaryBytes.Length; i++)
        {
            if (MatchesAt(data, i, boundaryBytes))
                positions.Add(i);
        }

        // DEBUG: log boundary match count
        Console.Error.WriteLine($"[DEBUG ParseMultipart] boundary positions found: {positions.Count}");

        for (var p = 0; p < positions.Count - 1; p++)
        {
            var start = positions[p] + boundaryBytes.Length;
            var end = positions[p + 1];

            // Skip \r\n after boundary
            if (start < data.Length && data[start] == '\r') start++;
            if (start < data.Length && data[start] == '\n') start++;

            // Find end of headers (double CRLF)
            var headerEnd = -1;
            for (var i = start; i <= end - 4; i++)
            {
                if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                {
                    headerEnd = i;
                    break;
                }
            }
            if (headerEnd < 0) continue;

            var headerText = Encoding.UTF8.GetString(data, start, headerEnd - start);
            var contentStart = headerEnd + 4;
            var contentEnd = end;

            // Strip trailing \r\n before next boundary
            if (contentEnd >= 2 && data[contentEnd - 2] == '\r' && data[contentEnd - 1] == '\n')
                contentEnd -= 2;

            var name = ExtractHeaderValue(headerText, "name");
            if (name is null) continue;

            var filename = ExtractHeaderValue(headerText, "filename");
            if (filename is not null)
            {
                // File part
                var fileData = new byte[contentEnd - contentStart];
                Array.Copy(data, contentStart, fileData, 0, fileData.Length);
                files[name] = fileData;
            }
            else
            {
                // Text field
                fields[name] = Encoding.UTF8.GetString(data, contentStart, contentEnd - contentStart);
            }
        }

        return (fields, files);
    }

    private static bool MatchesAt(byte[] data, int offset, byte[] pattern)
    {
        if (offset + pattern.Length > data.Length) return false;
        for (var i = 0; i < pattern.Length; i++)
        {
            if (data[offset + i] != pattern[i]) return false;
        }
        return true;
    }

    private static string? ExtractHeaderValue(string headers, string key)
    {
        // Look for: key="value" (quoted)
        var searchKeyQuoted = key + "=\"";
        var idx = 0;
        while ((idx = headers.IndexOf(searchKeyQuoted, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            // Ensure we matched the full key, not a suffix (e.g. "filename=" when searching for "name=")
            if (idx == 0 || headers[idx - 1] == ' ' || headers[idx - 1] == ';')
            {
                var start = idx + searchKeyQuoted.Length;
                var end = headers.IndexOf('"', start);
                if (end >= 0) return headers[start..end];
            }
            idx += searchKeyQuoted.Length;
        }

        // Try unquoted: key=value (terminated by ; or end of line)
        var searchKeyUnquoted = key + "=";
        idx = 0;
        while ((idx = headers.IndexOf(searchKeyUnquoted, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (idx == 0 || headers[idx - 1] == ' ' || headers[idx - 1] == ';')
            {
                var valStart = idx + searchKeyUnquoted.Length;
                var valEnd = headers.IndexOfAny([';', '\r', '\n'], valStart);
                var result = valEnd < 0 ? headers[valStart..] : headers[valStart..valEnd];
                return result.Trim();
            }
            idx += searchKeyUnquoted.Length;
        }

        return null;
    }

    /// <summary>
    /// Authenticates the caller, authorises via GitHub team membership, then
    /// delegates to <paramref name="aksOperation"/> and returns its result as JSON.
    /// </summary>
    protected async Task<HttpResponseData> ExecuteAsync(
        HttpRequestData req,
        ILogger logger,
        GitHubAuthService gitHub,
        string operationName,
        Func<Dictionary<string, string>, Task<object>> aksOperation,
        bool skipClusterCheck = false)
    {
        var (auth, errorResponse) = await AuthenticateAndAuthorizeAsync(req, logger, gitHub, operationName);
        if (errorResponse is not null) return errorResponse;

        if (!skipClusterCheck)
        {
            var clusterError = await CheckClusterRunningAsync(req, logger);
            if (clusterError is not null) return clusterError;
        }

        // ── Parse and validate request parameters ───────────────────────────────
        var parametersResult = await ParseAndValidateParametersAsync(req, auth!.Function, auth.IsAdmin);
        if (!parametersResult.Success)
            return Respond(req, HttpStatusCode.BadRequest, parametersResult.ErrorMessage!);

        ClearFailedAttempts(auth.ClientIp);

        // Inject the authenticated GitHub username so services can use it
        parametersResult.Parameters!["_githubUsername"] = auth.Username;
        parametersResult.Parameters!["_isAdmin"] = auth.IsAdmin.ToString();
        parametersResult.Parameters!["_isSupport"] = auth.IsSupport.ToString();

        // Resolve artifact shorthand (e.g. "///us/latest") to a full URL
        var artifactError = await ResolveArtifactAsync(req, logger, parametersResult.Parameters);
        if (artifactError is not null) return artifactError;

        // ── Execute operation ─────────────────────────────────────────────────────
        return await RunOperationAsync(req, logger, operationName, auth.Username,
            () => aksOperation(parametersResult.Parameters!));
    }

    /// <summary>
    /// Like <see cref="ExecuteAsync"/> but fires the operation on a background thread
    /// and returns HTTP 202 immediately. The operation continues running in-process
    /// up to the Azure Functions timeout. Errors are logged but not returned to the caller.
    /// </summary>
    protected async Task<HttpResponseData> ExecuteFireAndForgetAsync(
        HttpRequestData req,
        ILogger logger,
        GitHubAuthService gitHub,
        string operationName,
        Func<Dictionary<string, string>, Task<object>> aksOperation)
    {
        var (auth, errorResponse) = await AuthenticateAndAuthorizeAsync(req, logger, gitHub, operationName);
        if (errorResponse is not null) return errorResponse;

        var clusterError = await CheckClusterRunningAsync(req, logger);
        if (clusterError is not null) return clusterError;

        var parametersResult = await ParseAndValidateParametersAsync(req, auth!.Function, auth.IsAdmin);
        if (!parametersResult.Success)
            return Respond(req, HttpStatusCode.BadRequest, parametersResult.ErrorMessage!);

        ClearFailedAttempts(auth.ClientIp);

        parametersResult.Parameters!["_githubUsername"] = auth.Username;
        parametersResult.Parameters!["_isAdmin"] = auth.IsAdmin.ToString();
        parametersResult.Parameters!["_isSupport"] = auth.IsSupport.ToString();

        var artifactError = await ResolveArtifactAsync(req, logger, parametersResult.Parameters);
        if (artifactError is not null) return artifactError;

        // Fire the operation on a background thread — do not await
        var parameters = parametersResult.Parameters!;
        _ = Task.Run(async () =>
        {
            try
            {
                await aksOperation(parameters);
                logger.LogInformation("Background operation {Operation} completed for user {Username}.",
                    operationName, auth.Username);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background operation {Operation} failed for user {Username}.",
                    operationName, auth.Username);
            }
        });

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new
        {
            message = $"{operationName} started. The operation is running in the background."
        });
        return response;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // Shared pipeline helpers
    // ═══════════════════════════════════════════════════════════════════════════════

    private static async Task<HttpResponseData?> CheckClusterRunningAsync(HttpRequestData req, ILogger logger)
    {
        try
        {
            var subscriptionId = Environment.GetEnvironmentVariable("AKS_SUBSCRIPTION_ID");
            var resourceGroup = Environment.GetEnvironmentVariable("AKS_RESOURCE_GROUP");
            var clusterName = Environment.GetEnvironmentVariable("AKS_CLUSTER_NAME");
            var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
            if (subscriptionId is null || resourceGroup is null || clusterName is null || clientId is null)
                return null; // Can't check — let the operation proceed

#pragma warning disable CS0618
            var credential = new Azure.Identity.ManagedIdentityCredential(clientId);
#pragma warning restore CS0618
            var armClient = new Azure.ResourceManager.ArmClient(credential);
            var aksId = Azure.ResourceManager.ContainerService.ContainerServiceManagedClusterResource
                .CreateResourceIdentifier(subscriptionId, resourceGroup, clusterName);
            var cluster = armClient.GetContainerServiceManagedClusterResource(aksId);
            var data = (await cluster.GetAsync()).Value.Data;
            var powerState = data.PowerStateCode?.ToString();

            if (!string.Equals(powerState, "Running", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("AKS cluster {Cluster} power state is '{PowerState}'. Rejecting request.", clusterName, powerState);
                var message = string.Equals(powerState, "Stopped", StringComparison.OrdinalIgnoreCase)
                    ? "The cluster is currently stopped. Use 'fkh startfkh' to start it before running other commands."
                    : $"The cluster is currently {powerState?.ToLowerInvariant() ?? "unknown"}. Please wait for it to be fully running.";
                return Respond(req, HttpStatusCode.ServiceUnavailable, message);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to check AKS cluster power state. Proceeding anyway.");
        }
        return null;
    }

    private sealed class AuthResult
    {
        public required string Username { get; init; }
        public required bool IsAdmin { get; init; }
        public required bool IsSupport { get; init; }
        public required string ClientIp { get; init; }
        public required FunctionDefinition Function { get; init; }
    }

    /// <summary>
    /// Shared authentication and authorization pipeline used by both
    /// <see cref="ExecuteAsync"/> and <see cref="ExecuteWithFileAsync"/>.
    /// Returns (AuthResult, null) on success or (null, errorResponse) on failure.
    /// </summary>
    private async Task<(AuthResult?, HttpResponseData?)> AuthenticateAndAuthorizeAsync(
        HttpRequestData req,
        ILogger logger,
        GitHubAuthService gitHub,
        string operationName)
    {
        var clientIp = GetClientIp(req);

        // ── Step 0a: Protocol version check ───────────────────────────────────────
        var protocolVersionHeader = req.Headers.TryGetValues("X-Fkh-Protocol-Version", out var pvValues)
            ? pvValues.FirstOrDefault() : null;
        var clientAppHeader = req.Headers.TryGetValues("X-Fkh-Client", out var caValues)
            ? caValues.FirstOrDefault() : null;

        var protocolVersion = Models.ProtocolVersionConfig.ParseProtocolVersion(protocolVersionHeader);
        var clientName = Models.ProtocolVersionConfig.ParseClientName(clientAppHeader);
        var protocolError = await Models.ProtocolVersionConfig.ValidateAsync(protocolVersion, clientName);
        if (protocolError is not null)
        {
            logger.LogWarning("Protocol version mismatch from {ClientApp} (version {ProtocolVersion}): {Error}",
                clientName, protocolVersion, protocolError);
            return (null, Respond(req, HttpStatusCode.UpgradeRequired, protocolError));
        }

        // ── Step 0b: Check IP block list ──────────────────────────────────────────
        if (IsIpBlocked(clientIp))
        {
            logger.LogWarning("Blocked request from {IP} — too many failed auth attempts.", clientIp);
            return (null, Respond(req, HttpStatusCode.Forbidden, "Too many failed attempts. Try again later."));
        }

        // ── Step 1: Extract Bearer token ─────────────────────────────────────────
        if (!req.Headers.TryGetValues("Authorization", out var authValues))
        {
            RecordFailedAttempt(clientIp);
            return (null, Respond(req, HttpStatusCode.Unauthorized, "Missing Authorization header."));
        }

        var authHeader = authValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer "))
        {
            RecordFailedAttempt(clientIp);
            return (null, Respond(req, HttpStatusCode.Unauthorized, "Authorization header must be a Bearer token."));
        }

        var function = FunctionCatalog.GetRequired(operationName);
        var token = authHeader["Bearer ".Length..].Trim();

        // ── Step 2 & 3: Authenticate and authorize ───────────────────────────────
        string username;
        var isAdmin = false;
        var isSupport = false;

        if (AdoOidcService.IsAdoOidcToken(token))
        {
            var subject = await AdoOidcService.ValidateTokenAsync(token);
            if (subject is null)
            {
                logger.LogError("Azure DevOps OIDC token validation failed or connection not in allow-list.");
                RecordFailedAttempt(clientIp);
                return (null, Respond(req, HttpStatusCode.Forbidden,
                    "Azure DevOps OIDC token invalid or service connection not authorized. Check ALLOWED_ADO_CONNECTIONS configuration."));
            }

            username = subject.Replace("sc://", "").Replace('/', '-');
            isAdmin = true;
            logger.LogInformation("Received {Operation} request from ADO OIDC caller: {Subject} (username: {Username}, admin: true)", operationName, subject, username);
        }
        else if (GitHubOidcService.IsOidcToken(token))
        {
            var repository = await OidcService.ValidateTokenAsync(token);
            if (repository is null)
            {
                logger.LogError("OIDC token validation failed or repository not in allow-list.");
                RecordFailedAttempt(clientIp);
                return (null, Respond(req, HttpStatusCode.Forbidden,
                    "OIDC token invalid or repository not authorized. Check ALLOWED_OIDC_REPOS configuration."));
            }

            username = repository.Replace('/', '-');
            isAdmin = true;
            logger.LogInformation("Received {Operation} request from OIDC caller: {Repository} (username: {Username}, admin: true)", operationName, repository, username);
        }
        else
        {
            var ghUsername = await gitHub.GetAuthenticatedUsernameAsync(token);
            if (ghUsername is null)
            {
                logger.LogError("Invalid or expired GitHub token received.");
                RecordFailedAttempt(clientIp);
                return (null, Respond(req, HttpStatusCode.Unauthorized, "Invalid or expired GitHub token."));
            }

            username = ghUsername;
            logger.LogInformation("Received {Operation} request from GitHub user: {Username}", operationName, username);

            var (authorized, userIsAdmin, userIsSupport) = await AuthorizeGitHubUserAsync(gitHub, token, username, logger);
            isAdmin = userIsAdmin;
            isSupport = userIsSupport;
            if (!authorized)
            {
                logger.LogWarning("User {Username} is not authorized.", username);
                RecordFailedAttempt(clientIp);
                return (null, Respond(req, HttpStatusCode.Forbidden, "You are not authorized to use this Fkh deployment."));
            }
        }

        // ── Admin-only check ─────────────────────────────────────────────────────
        if (function.AdminOnly && !isAdmin)
        {
            logger.LogWarning("User {Username} attempted admin-only operation {Operation}.", username, operationName);
            return (null, Respond(req, HttpStatusCode.Forbidden, "This operation is restricted to administrators."));
        }

        return (new AuthResult
        {
            Username = username,
            IsAdmin = isAdmin,
            IsSupport = isSupport,
            ClientIp = clientIp,
            Function = function
        }, null);
    }

    private static async Task<(bool Authorized, bool IsAdmin, bool IsSupport)> AuthorizeGitHubUserAsync(
        GitHubAuthService gitHub,
        string token,
        string username,
        ILogger logger)
    {
        var isAdmin = false;
        var isSupport = false;
        var authorized = false;

        foreach (var entry in AllowedUsers)
        {
            if (!string.Equals(entry.User, username, StringComparison.OrdinalIgnoreCase))
                continue;

            authorized = true;
            switch (entry.Role.ToLowerInvariant())
            {
                case "admin":
                    isAdmin = true;
                    logger.LogInformation("User {Username} authorized as admin via allowed_users", username);
                    break;
                case "support":
                    isSupport = true;
                    logger.LogInformation("User {Username} authorized as support via allowed_users", username);
                    break;
                default:
                    logger.LogInformation("User {Username} authorized as member via allowed_users", username);
                    break;
            }
        }

        foreach (var orgTeam in AdminOrgTeams)
        {
            if (await gitHub.IsTeamMemberAsync(token, orgTeam.Org, orgTeam.Team, username))
            {
                logger.LogInformation("User {Username} authorized as admin via org={Org} team={Team}", username, orgTeam.Org, orgTeam.Team);
                authorized = true;
                isAdmin = true;
            }
        }

        if (!isAdmin)
        {
            foreach (var orgTeam in SupportOrgTeams)
            {
                if (await gitHub.IsTeamMemberAsync(token, orgTeam.Org, orgTeam.Team, username))
                {
                    logger.LogInformation("User {Username} authorized as support via org={Org} team={Team}", username, orgTeam.Org, orgTeam.Team);
                    authorized = true;
                    isSupport = true;
                }
            }
        }

        if (!isAdmin)
        {
            foreach (var orgTeam in AllowedOrgTeams)
            {
                if (await gitHub.IsTeamMemberAsync(token, orgTeam.Org, orgTeam.Team, username))
                {
                    logger.LogInformation("User {Username} authorized via org={Org} team={Team}", username, orgTeam.Org, orgTeam.Team);
                    authorized = true;
                }
            }
        }

        if (isAdmin)
            isSupport = false;

        return (authorized, isAdmin, isSupport);
    }



    /// <summary>
    /// Resolves artifact shorthand (e.g. "///us/latest") to a full URL if present.
    /// Returns null on success, or an error response on failure.
    /// </summary>
    private static async Task<HttpResponseData?> ResolveArtifactAsync(
        HttpRequestData req,
        ILogger logger,
        Dictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("artifactUrl", out var rawArtifact)
            || string.IsNullOrWhiteSpace(rawArtifact)
            || rawArtifact.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var segments = ($"{rawArtifact}/////").Split('/');
            var storageAccount = segments[0];
            var artifactType = string.IsNullOrEmpty(segments[1]) ? "Sandbox" : segments[1];
            var version = segments[2];
            var country = string.IsNullOrEmpty(segments[3]) ? "us" : segments[3];
            var selectStr = string.IsNullOrEmpty(segments[4]) ? "latest" : segments[4];

            var type = Enum.Parse<BcArtifacts.ArtifactType>(artifactType, ignoreCase: true);
            var select = Enum.Parse<BcArtifacts.ArtifactSelect>(selectStr, ignoreCase: true);

            var resolved = (await BcArtifacts.BcArtifactHelper.GetBcArtifactUrlAsync(
                type: type, country: country, version: version,
                select: select, storageAccount: storageAccount)).FirstOrDefault();

            if (string.IsNullOrEmpty(resolved))
                return Respond(req, HttpStatusCode.BadRequest, $"No artifacts found for '{rawArtifact}'.");

            logger.LogInformation("Resolved artifact shorthand '{Raw}' to '{Resolved}'", rawArtifact, resolved);
            parameters["artifactUrl"] = resolved;
            return null;
        }
        catch (Exception ex)
        {
            return Respond(req, HttpStatusCode.BadRequest, $"Failed to resolve artifact '{rawArtifact}': {ex.Message}");
        }
    }

    /// <summary>
    /// Executes the operation, serializes the result as JSON, and handles
    /// <see cref="RetryAfterException"/> and unexpected errors uniformly.
    /// </summary>
    private static async Task<HttpResponseData> RunOperationAsync(
        HttpRequestData req,
        ILogger logger,
        string operationName,
        string username,
        Func<Task<object>> operation)
    {
        try
        {
            var result = await operation();
            var response = req.CreateResponse(HttpStatusCode.OK);
            var jsonString = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            response.WriteString(jsonString);
            return response;
        }
        catch (RetryAfterException retryEx)
        {
            logger.LogInformation("Operation {Operation} requested retry in {Seconds}s: {Message}",
                operationName, retryEx.RetryAfterSeconds, retryEx.Message);
            var response = req.CreateResponse(HttpStatusCode.Accepted);
            response.Headers.Add("Retry-After", retryEx.RetryAfterSeconds.ToString());
            await response.WriteAsJsonAsync(new { message = retryEx.Message, retryAfterSeconds = retryEx.RetryAfterSeconds });
            return response;
        }
        catch (HttpRequestException ex) when (IsConnectivityError(ex))
        {
            logger.LogWarning(ex, "Connectivity error during {Operation} for user {Username}. Checking cluster power state.", operationName, username);
            var powerState = await GetClusterPowerStateForErrorAsync(logger);
            if (powerState != null && !string.Equals(powerState, "Running", StringComparison.OrdinalIgnoreCase))
            {
                var message = string.Equals(powerState, "Stopped", StringComparison.OrdinalIgnoreCase)
                    ? "The cluster is currently stopped. Use 'fkh startfkh' to start it before running other commands."
                    : $"The cluster is currently {powerState.ToLowerInvariant()}. Please wait for it to be fully running.";
                return Respond(req, HttpStatusCode.ServiceUnavailable, message);
            }
            // Cluster reports Running but API unreachable — transient issue
            return Respond(req, HttpStatusCode.ServiceUnavailable,
                "The cluster API is not reachable yet. It may still be starting up — please try again in a minute.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Operation {Operation} failed for user {Username}.", operationName, username);
            return Respond(req, HttpStatusCode.BadRequest, ex.Message);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Operation {Operation} failed for user {Username}.", operationName, username);
            return Respond(req, HttpStatusCode.BadRequest, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Operation {Operation} denied for user {Username}.", operationName, username);
            return Respond(req, HttpStatusCode.Forbidden, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute {Operation} for user {Username}.", operationName, username);
            var detail = $"[{ex.GetType().Name}] {ex.Message}";
            if (ex.InnerException is { } inner)
                detail += $"\n  Inner: [{inner.GetType().Name}] {inner.Message}";
            var frames = ex.StackTrace?.Split('\n').Take(5);
            if (frames != null)
                detail += "\n" + string.Join("\n", frames);
            return Respond(req, HttpStatusCode.InternalServerError, detail);
        }
    }

    private static bool IsConnectivityError(HttpRequestException ex)
    {
        // DNS resolution failure, connection refused, or similar network errors
        if (ex.InnerException is System.Net.Sockets.SocketException)
            return true;
        // Some HttpRequestException variants don't wrap a SocketException
        var msg = ex.Message;
        return msg.Contains("No such host", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("actively refused", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> GetClusterPowerStateForErrorAsync(ILogger logger)
    {
        try
        {
            var subscriptionId = Environment.GetEnvironmentVariable("AKS_SUBSCRIPTION_ID");
            var resourceGroup = Environment.GetEnvironmentVariable("AKS_RESOURCE_GROUP");
            var clusterName = Environment.GetEnvironmentVariable("AKS_CLUSTER_NAME");
            var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
            if (subscriptionId is null || resourceGroup is null || clusterName is null || clientId is null)
                return null;

#pragma warning disable CS0618
            var credential = new Azure.Identity.ManagedIdentityCredential(clientId);
#pragma warning restore CS0618
            var armClient = new Azure.ResourceManager.ArmClient(credential);
            var aksId = Azure.ResourceManager.ContainerService.ContainerServiceManagedClusterResource
                .CreateResourceIdentifier(subscriptionId, resourceGroup, clusterName);
            var cluster = armClient.GetContainerServiceManagedClusterResource(aksId);
            var data = (await cluster.GetAsync()).Value.Data;
            return data.PowerStateCode?.ToString();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to check AKS cluster power state during error recovery.");
            return null;
        }
    }

    private static async Task<ParameterValidationResult> ParseAndValidateParametersAsync(
        HttpRequestData req,
        FunctionDefinition function,
        bool isAdmin)
    {
        string raw;
        using (var reader = new StreamReader(req.Body))
        {
            raw = await reader.ReadToEndAsync();
        }

        FunctionInvokeRequest invokeRequest;
        if (string.IsNullOrWhiteSpace(raw))
        {
            invokeRequest = new FunctionInvokeRequest
            {
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };
        }
        else
        {
            try
            {
                invokeRequest = JsonSerializer.Deserialize<FunctionInvokeRequest>(raw,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new FunctionInvokeRequest();
            }
            catch
            {
                return ParameterValidationResult.Fail(
                    "Invalid request body. Expected JSON object: { \"parameters\": { \"key\": \"value\" } }");
            }
        }

        var incoming = invokeRequest.Parameters ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (incoming.Comparer != StringComparer.OrdinalIgnoreCase)
        {
            incoming = new Dictionary<string, string>(incoming, StringComparer.OrdinalIgnoreCase);
        }
        var allowedNames = function.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Extract _prefixed internal parameters before validation (e.g. _timezone)
        var internalParams = incoming.Where(kv => kv.Key.StartsWith('_')).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var key in internalParams.Keys)
            incoming.Remove(key);

        var unknown = incoming.Keys.Where(k => !allowedNames.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            return ParameterValidationResult.Fail(
                $"Unknown parameters for {function.Name}: {string.Join(", ", unknown)}.");
        }

        var validated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in function.Parameters)
        {
            incoming.TryGetValue(parameter.Name, out var value);
            value = string.IsNullOrWhiteSpace(value) ? parameter.DefaultValue : value;

            if (parameter.Required && string.IsNullOrWhiteSpace(value))
            {
                return ParameterValidationResult.Fail(
                    $"Missing required parameter '{parameter.Name}' for {function.Name}.");
            }

            // Validate 'name' parameters
            if (string.Equals(parameter.Name, "name", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(value))
            {
                if (!value.All(c => char.IsLetterOrDigit(c) || c == '-'))
                {
                    return ParameterValidationResult.Fail(
                        "Parameter 'name' may only contain alphanumeric characters and hyphens.");
                }
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                validated[parameter.Name] = value;
            }
        }

        // Re-add internal parameters (e.g. _timezone) so services can access them
        foreach (var kv in internalParams)
            validated[kv.Key] = kv.Value;

        return ParameterValidationResult.Ok(validated);
    }

    private static HttpResponseData Respond(HttpRequestData req, HttpStatusCode status, string message)
    {
        var response = req.CreateResponse(status);
        response.WriteString(message);
        return response;
    }

    private static List<OrgTeamConfig> LoadOrgTeamConfig(string envVarName, bool required = true)
    {
        var raw = Environment.GetEnvironmentVariable(envVarName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (required)
                throw new InvalidOperationException(
                    $"{envVarName} app setting is missing. " +
                    "Expected JSON array, e.g. [{\"Org\":\"my-org\",\"Team\":\"Fkh-members\"}]");
            return [];
        }

        return JsonSerializer.Deserialize<List<OrgTeamConfig>>(raw,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"Failed to parse {envVarName}.");
    }

    private static List<AllowedUserConfig> LoadAllowedUsers()
    {
        var raw = Environment.GetEnvironmentVariable("ALLOWED_USERS");
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var users = JsonSerializer.Deserialize<List<AllowedUserConfig>>(raw,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Failed to parse ALLOWED_USERS.");

        foreach (var user in users)
        {
            var role = user.Role.ToLowerInvariant();
            if (role is not ("admin" or "member" or "support"))
            {
                throw new InvalidOperationException(
                    $"Invalid role '{user.Role}' for user '{user.User}' in ALLOWED_USERS. " +
                    "Expected admin, member, or support.");
            }
        }

        return users;
    }

    private sealed class ParameterValidationResult
    {
        public bool Success { get; init; }
        public Dictionary<string, string>? Parameters { get; init; }
        public string? ErrorMessage { get; init; }

        public static ParameterValidationResult Ok(Dictionary<string, string> parameters)
            => new() { Success = true, Parameters = parameters };

        public static ParameterValidationResult Fail(string message)
            => new() { Success = false, ErrorMessage = message };
    }
}
