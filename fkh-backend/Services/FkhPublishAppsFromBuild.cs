using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

/// <summary>
/// Resolves a GitHub Actions (AL-Go) build and publishes its app artifacts into a running
/// Business Central container. GitHub is accessed with a caller-supplied token that has access
/// to the source repository (the CLI resolves it from GH_TOKEN / 'gh auth token'); the actual
/// download, dependency sorting and publish/sync/install happen inside the container to
/// minimize roundtrips.
/// </summary>
public class FkhPublishAppsFromBuild : FkhServiceBase
{
    private readonly GitHubAuthService _gitHub;
    private readonly FkhInvokeScript _invokeScript;

    public FkhPublishAppsFromBuild(
        ILogger<FkhPublishAppsFromBuild> logger,
        GitHubAuthService gitHub,
        FkhInvokeScript invokeScript) : base(logger)
    {
        _gitHub = gitHub;
        _invokeScript = invokeScript;
    }

    // AL-Go artifact naming: {Project}-{Branch}-{Type}-{Version}. Version is at the end and the
    // type is a fixed keyword, so a greedy prefix reliably captures "{Project}-{Branch}".
    private static readonly Regex ArtifactNameRegex = new(
        @"^(?<prefix>.+)-(?<type>Apps|TestApps|Dependencies)-(?<version>\d+(?:\.\d+){1,3})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<object> PublishAppsFromBuildAsync(Dictionary<string, string> parameters)
    {
        var githubUsername = parameters["_githubUsername"];
        var appName = ResolveAppName(parameters);

        // The build is accessed with a GitHub token that has access to the source repository.
        // Under --useOIDC the caller must pass --buildToken (the login is an OIDC JWT that can't
        // call the GitHub API). Otherwise it defaults to the caller's own login token.
        var token = parameters.TryGetValue("buildToken", out var bt) && !string.IsNullOrWhiteSpace(bt)
            ? bt.Trim()
            : null;
        if (token is null)
        {
            if (string.Equals(parameters.GetValueOrDefault("_isOidc"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "--buildToken is required when authenticating with OIDC. Provide a GitHub token that can " +
                    "read the source repository's builds (e.g. the workflow's GITHUB_TOKEN with 'actions: read', " +
                    "or a token generated from a GitHub App).");
            token = parameters.GetValueOrDefault("_githubToken");
        }
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("No GitHub token was available to access the build.");

        var (owner, repo, branch) = ParseRepo(parameters);
        var buildId = parameters.TryGetValue("buildId", out var b) && !string.IsNullOrWhiteSpace(b) ? b.Trim() : null;
        var project = parameters.TryGetValue("project", out var pr) && !string.IsNullOrWhiteSpace(pr) ? pr.Trim() : ".";
        var includeTestApps = HasFlag(parameters, "includeTestApps");
        var includeDependencies = HasFlag(parameters, "includeDependencies");
        var excludeAppIds = ParseExcludeAppIds(parameters);

        Logger.LogInformation(
            "User '{User}' publishing apps from build {Owner}/{Repo}@{Branch} (build={Build}, project={Project}) into container '{Container}'.",
            githubUsername, owner, repo, branch, buildId ?? "latest", project, appName);

        var runId = await _gitHub.ResolveCicdRunIdAsync(token, owner, repo, branch, buildId);
        var artifacts = await _gitHub.ListRunArtifactsAsync(token, owner, repo, runId);

        var parsed = ParseArtifacts(artifacts, branch);
        var resolvedProject = ResolveProject(parsed, project);

        var selected = SelectArtifacts(parsed, resolvedProject, includeTestApps, includeDependencies);
        if (selected.Count == 0)
        {
            throw new InvalidOperationException(
                $"No app artifacts were found for project '{resolvedProject}' in build {runId} of {owner}/{repo}.");
        }

        Logger.LogInformation(
            "Selected {Count} artifact(s) from run {RunId} for project '{Project}': {Names}",
            selected.Count, runId, resolvedProject, string.Join(", ", selected.Select(a => a.Artifact.Name)));

        var script = BuildScript(token, selected, excludeAppIds);

        var invokeParameters = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase)
        {
            ["command"] = script,
        };
        invokeParameters.Remove("scriptParams");

        // Launches the download/publish script detached and returns { jobId, container }. The
        // caller polls ScriptStatus/ScriptResult; the backend never blocks on the long publish.
        return await _invokeScript.InvokeScriptAsync(invokeParameters);
    }

    private static (string Owner, string Repo, string Branch) ParseRepo(Dictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("buildRepo", out var repoSpec) || string.IsNullOrWhiteSpace(repoSpec))
            throw new InvalidOperationException("Parameter 'buildRepo' is required (format: org/repo or org/repo@branch).");

        repoSpec = repoSpec.Trim();

        var branch = "main";
        var atIndex = repoSpec.IndexOf('@');
        if (atIndex >= 0)
        {
            var branchFromSpec = repoSpec[(atIndex + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(branchFromSpec))
                branch = branchFromSpec;
            repoSpec = repoSpec[..atIndex].Trim();
        }

        var slashParts = repoSpec.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (slashParts.Length != 2)
            throw new InvalidOperationException($"Invalid buildRepo '{repoSpec}'. Expected format: org/repo or org/repo@branch.");

        return (slashParts[0], slashParts[1], branch);
    }

    private sealed record ParsedArtifact(GitHubAuthService.BuildArtifact Artifact, string Project, string Type, string Version);

    private static List<ParsedArtifact> ParseArtifacts(List<GitHubAuthService.BuildArtifact> artifacts, string branch)
    {
        // AL-Go replaces '/' in branch names with '_' when composing artifact names.
        var branchCandidates = new[] { branch, branch.Replace('/', '_') }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var result = new List<ParsedArtifact>();
        foreach (var artifact in artifacts)
        {
            if (artifact.Expired) continue;

            var match = ArtifactNameRegex.Match(artifact.Name);
            if (!match.Success) continue;

            var prefix = match.Groups["prefix"].Value;
            var type = match.Groups["type"].Value;
            var version = match.Groups["version"].Value;

            var project = branchCandidates
                .Where(bc => prefix.EndsWith($"-{bc}", StringComparison.OrdinalIgnoreCase))
                .Select(bc => prefix[..^(bc.Length + 1)])
                .FirstOrDefault();

            if (string.IsNullOrEmpty(project)) continue;

            result.Add(new ParsedArtifact(artifact, project, type, version));
        }

        return result;
    }

    private static string ResolveProject(List<ParsedArtifact> parsed, string project)
    {
        var appProjects = parsed
            .Where(p => string.Equals(p.Type, "Apps", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Project)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (appProjects.Count == 0)
            throw new InvalidOperationException("The build produced no Apps artifacts.");

        if (project == ".")
        {
            if (appProjects.Count == 1)
                return appProjects[0];

            throw new InvalidOperationException(
                "This is a multi-project repository. Specify --project with one of: " +
                string.Join(", ", appProjects.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) + ".");
        }

        var resolved = appProjects.FirstOrDefault(p => string.Equals(p, project, StringComparison.OrdinalIgnoreCase));
        if (resolved is null)
            throw new InvalidOperationException(
                $"No apps were found for project '{project}'. Available projects: " +
                string.Join(", ", appProjects.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) + ".");

        return resolved;
    }

    private static List<ParsedArtifact> SelectArtifacts(
        List<ParsedArtifact> parsed, string project, bool includeTestApps, bool includeDependencies)
    {
        var wantedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Apps" };
        if (includeTestApps) wantedTypes.Add("TestApps");
        if (includeDependencies) wantedTypes.Add("Dependencies");

        return parsed
            .Where(p => string.Equals(p.Project, project, StringComparison.OrdinalIgnoreCase)
                        && wantedTypes.Contains(p.Type))
            .ToList();
    }

    private static List<string> ParseExcludeAppIds(Dictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("excludeAppIds", out var raw) || string.IsNullOrWhiteSpace(raw))
            return new List<string>();

        return raw
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => id.Trim('{', '}').ToLowerInvariant())
            .Distinct()
            .ToList();
    }

    private static bool HasFlag(Dictionary<string, string> parameters, string name)
        => parameters.TryGetValue(name, out var value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static string BuildScript(string token, List<ParsedArtifact> selected, List<string> excludeAppIds)
    {
        var downloads = selected
            .Select(a => new { url = a.Artifact.ArchiveDownloadUrl, type = a.Type, name = a.Artifact.Name })
            .ToArray();

        var downloadsB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(downloads)));
        var excludeB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(excludeAppIds)));

        return ScriptTemplate
            .Replace("__TOKEN__", token)
            .Replace("__DOWNLOADS_B64__", downloadsB64)
            .Replace("__EXCLUDE_B64__", excludeB64);
    }

    // Runs inside the BC container. The detached wrapper dot-sources C:\run\prompt.ps1 first, so
    // the BC PowerShell cmdlets are already available. The single-quoted GitHub token is never
    // written to the log or the output streams; the whole script file is removed after the job.
    private const string ScriptTemplate = @"
$ErrorActionPreference = 'Stop'
if (-not $ServerInstance) { $ServerInstance = 'BC' }
$tenant = 'default'

$token = '__TOKEN__'
$downloads = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__DOWNLOADS_B64__')) | ConvertFrom-Json
$excludeIds = @([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__EXCLUDE_B64__')) | ConvertFrom-Json) | ForEach-Object { ""$_"".ToLower() }

$work = Join-Path 'c:\run\my' ('fkh-pub-' + [guid]::NewGuid().ToString('N'))
$appDir = Join-Path $work 'apps'
New-Item -ItemType Directory -Path $appDir -Force | Out-Null

try {
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $hc = [System.Net.Http.HttpClient]::new($handler)
    $hc.Timeout = [TimeSpan]::FromMinutes(10)
    $hc.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token)
    $hc.DefaultRequestHeaders.UserAgent.ParseAdd('FKH')
    $hc.DefaultRequestHeaders.Accept.ParseAdd('application/vnd.github+json')

    foreach ($d in $downloads) {
        Write-Host ('Downloading artifact ' + $d.name + ' [' + $d.type + ']')
        $zipPath = Join-Path $work ($d.name + '.zip')
        $resp = $hc.GetAsync($d.url).GetAwaiter().GetResult()
        $status = [int]$resp.StatusCode
        if ($status -ge 300 -and $status -lt 400 -and $resp.Headers.Location) {
            # Follow the redirect to the signed blob URL without the Authorization header.
            $loc = $resp.Headers.Location.AbsoluteUri
            Invoke-WebRequest -Uri $loc -OutFile $zipPath -UseBasicParsing
        } elseif ($resp.IsSuccessStatusCode) {
            $bytes = $resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            [IO.File]::WriteAllBytes($zipPath, $bytes)
        } else {
            throw ('Failed to download artifact ' + $d.name + ': HTTP ' + $status)
        }
        $extractDir = Join-Path $work ('x-' + [guid]::NewGuid().ToString('N'))
        Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force
        Get-ChildItem -Path $extractDir -Recurse -Filter '*.app' | ForEach-Object {
            Copy-Item $_.FullName (Join-Path $appDir $_.Name) -Force
        }
    }
    $hc.Dispose()

    $apps = @()
    Get-ChildItem -Path $appDir -Filter '*.app' | ForEach-Object {
        $info = Get-NAVAppInfo -Path $_.FullName
        $appIdRaw = $info.AppId
        $appId = if ($null -ne $appIdRaw.Value) { $appIdRaw.Value.ToString() } else { $appIdRaw.ToString() }
        $deps = @()
        foreach ($dep in $info.Dependencies) {
            $depId = if ($dep.AppId) { $dep.AppId } elseif ($dep.Id) { $dep.Id } else { $null }
            if ($depId) {
                $depIdStr = if ($null -ne $depId.Value) { $depId.Value.ToString() } else { $depId.ToString() }
                $deps += $depIdStr.ToLower()
            }
        }
        $apps += [pscustomobject]@{
            Path = $_.FullName
            AppId = $appId.ToLower()
            Name = $info.Name
            Publisher = $info.Publisher
            Version = $info.Version.ToString()
            Dependencies = $deps
        }
    }

    if ($excludeIds.Count -gt 0) {
        $apps = @($apps | Where-Object { $excludeIds -notcontains $_.AppId })
    }

    # Deduplicate by AppId, keeping the highest version.
    $apps = @($apps | Group-Object AppId | ForEach-Object {
        $_.Group | Sort-Object { [Version]$_.Version } -Descending | Select-Object -First 1
    })

    if ($apps.Count -eq 0) {
        Write-Host 'No apps to publish after applying exclusions.'
        return
    }

    # Topological sort so dependencies are published before dependents.
    $byId = @{}
    foreach ($a in $apps) { $byId[$a.AppId] = $a }
    $sorted = New-Object System.Collections.Generic.List[object]
    $visited = @{}
    $visiting = @{}
    function Visit-App($app) {
        if ($visited.ContainsKey($app.AppId)) { return }
        if ($visiting.ContainsKey($app.AppId)) { return }
        $visiting[$app.AppId] = $true
        foreach ($depId in $app.Dependencies) {
            if ($byId.ContainsKey($depId)) { Visit-App $byId[$depId] }
        }
        $visiting.Remove($app.AppId)
        $visited[$app.AppId] = $true
        $sorted.Add($app)
    }
    foreach ($a in $apps) { Visit-App $a }

    $published = @()
    foreach ($a in $sorted) {
        Write-Host ('Publishing ' + $a.Name + ' v' + $a.Version + ' (' + $a.Publisher + ')')
        Publish-NAVApp -ServerInstance $ServerInstance -Path $a.Path -SkipVerification
        Sync-NAVApp -ServerInstance $ServerInstance -Name $a.Name -Publisher $a.Publisher -Version $a.Version -Tenant $tenant -Mode Add -Force -WarningAction SilentlyContinue

        $existing = @(Get-NAVAppInfo -ServerInstance $ServerInstance -Tenant $tenant -Id $a.AppId -TenantSpecificProperties | Where-Object { $_.IsInstalled }) | Select-Object -First 1
        if ($existing -and $existing.Version.ToString() -eq $a.Version) {
            Write-Host ('  already installed v' + $a.Version + ', skipping')
        } elseif ($existing) {
            Write-Host ('  upgrading from v' + $existing.Version.ToString())
            Start-NAVAppDataUpgrade -ServerInstance $ServerInstance -Name $a.Name -Publisher $a.Publisher -Version $a.Version -Tenant $tenant
        } else {
            Write-Host '  installing'
            Install-NAVApp -ServerInstance $ServerInstance -Name $a.Name -Publisher $a.Publisher -Version $a.Version -Tenant $tenant
        }
        $published += ($a.Name + ' v' + $a.Version)
    }

    Write-Host ''
    Write-Host ('Published ' + $published.Count + ' app(s):')
    $published | ForEach-Object { Write-Host ('  - ' + $_) }
}
finally {
    Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
}
";
}
