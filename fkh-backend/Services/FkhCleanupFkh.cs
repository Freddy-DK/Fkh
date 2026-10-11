using k8s;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhCleanupFkh : FkhServiceBase
{
    private const int GracePeriodHours = 24;

    public FkhCleanupFkh(ILogger<FkhCleanupFkh> logger) : base(logger) { }

    public async Task<object> CleanupFkhAsync(Dictionary<string, string> parameters)
    {
        var forceProvided = parameters.TryGetValue("force", out var forceValue)
            && bool.TryParse(forceValue, out var force)
            && force;

        var client = await GetKubernetesClientAsync();
        var deployments = await client.ListNamespacedDeploymentAsync(Namespace);
        var appNames = deployments.Items
            .Where(d => d.Spec.Template.Metadata.Labels != null
                && d.Spec.Template.Metadata.Labels.TryGetValue("app-type", out var appType)
                && appType == "windows-servicetier"
                && d.Spec.Template.Metadata.Labels.ContainsKey("app"))
            .Select(d => d.Spec.Template.Metadata.Labels["app"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var podName = await FindMssqlPodAsync(client);
        var databaseRows = await QuerySqlRowsAsync(client, podName,
            "SET NOCOUNT ON; " +
            $"SELECT name, CASE WHEN create_date <= DATEADD(HOUR, -{GracePeriodHours}, GETDATE()) THEN 1 ELSE 0 END " +
            "FROM sys.databases WHERE database_id > 4 ORDER BY name; " +
            "PRINT N'CLEANUP_DATABASE_LIST_COMPLETE'",
            "CLEANUP_DATABASE_LIST_COMPLETE");

        var databaseStates = databaseRows
            .Where(row => row.Length >= 2 && (row[1] == "0" || row[1] == "1"))
            .Select(row => (Name: row[0], IsStale: row[1] == "1"))
            .ToList();

        var orphanedDatabases = FindOrphanDatabaseNames(
            databaseStates.Where(database => database.IsStale).Select(database => database.Name), appNames);
        var recentOrphanedDatabases = FindOrphanDatabaseNames(
            databaseStates.Where(database => !database.IsStale).Select(database => database.Name), appNames);

        var deletedDatabases = new List<string>();
        var databaseFailures = new List<string>();
        if (forceProvided)
        {
            foreach (var databaseName in orphanedDatabases)
            {
                try
                {
                    var identifier = QuoteSqlIdentifier(databaseName);
                    var literal = QuoteSqlLiteral(databaseName);
                    var dropSql = $"IF DB_ID(N'{literal}') IS NOT NULL BEGIN " +
                        $"ALTER DATABASE {identifier} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                        $"DROP DATABASE {identifier}; PRINT N'DATABASE_DROPPED'; END " +
                        "ELSE PRINT N'DATABASE_DROPPED'";
                    var result = await RunSqlAsync(client, podName, dropSql);
                    if (result.Stdout.Contains("DATABASE_DROPPED", StringComparison.Ordinal))
                        deletedDatabases.Add(databaseName);
                    else
                        databaseFailures.Add($"{databaseName}: {result}");
                }
                catch (Exception ex)
                {
                    databaseFailures.Add($"{databaseName}: {ex.Message}");
                }
            }
        }

        var bakFiles = await CleanupBakFilesAsync(client, podName, !forceProvided);
        var logRows = await QuerySqlRowsAsync(client, podName,
            "SET NOCOUNT ON; " +
            "SELECT d.name, mf.name FROM sys.databases d " +
            "JOIN sys.master_files mf ON d.database_id = mf.database_id " +
            "WHERE d.database_id > 4 AND d.state = 0 AND mf.type = 1 ORDER BY d.name, mf.name; " +
            "PRINT N'CLEANUP_LOG_LIST_COMPLETE'",
            "CLEANUP_LOG_LIST_COMPLETE");

        var logsForDeployedApps = logRows
            .Where(row => row.Length >= 2 && IsManagedDatabaseName(row[0])
                && IsDatabaseForAnyApp(row[0], appNames))
            .ToList();

        var logShrinkAttempts = new List<string>();
        var shrinkFailures = new List<string>();
        if (forceProvided)
        {
            foreach (var row in logsForDeployedApps)
            {
                var databaseName = row[0];
                var logicalName = row[1];
                try
                {
                    var (shrunk, result) = await ShrinkDatabaseLogAsync(client, podName, databaseName, logicalName);
                    if (shrunk)
                        logShrinkAttempts.Add($"{databaseName}/{logicalName}");
                    else
                        shrinkFailures.Add($"{databaseName}/{logicalName}: {result}");
                }
                catch (Exception ex)
                {
                    shrinkFailures.Add($"{databaseName}/{logicalName}: {ex.Message}");
                }
            }
        }

        return new
        {
            ForceProvided = forceProvided,
            Message = forceProvided
                ? "Force was provided; cleanup actions were applied where possible."
                : "Force was not provided; preview only. Add --force to perform cleanup.",
            GracePeriodHours,
            OrphanedDatabases = forceProvided ? deletedDatabases : orphanedDatabases,
            RecentOrphanedDatabases = recentOrphanedDatabases,
            DatabaseFailures = databaseFailures,
            BakFiles = bakFiles.Files,
            BakFileFailure = bakFiles.Failure,
            LogFiles = forceProvided
                ? logShrinkAttempts
                : logsForDeployedApps.Select(row => $"{row[0]}/{row[1]}").ToList(),
            LogShrinkFailures = shrinkFailures,
        };
    }

    internal static IReadOnlyList<string> FindOrphanDatabaseNames(
        IEnumerable<string> databaseNames, IEnumerable<string> deployedAppNames)
    {
        var appNames = deployedAppNames.ToList();
        return databaseNames
            .Where(IsManagedDatabaseName)
            .Where(databaseName => !IsDatabaseForAnyApp(databaseName, appNames))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(databaseName => databaseName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsManagedDatabaseName(string databaseName) =>
        databaseName.Contains('-', StringComparison.Ordinal)
        && databaseName.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsDatabaseForAnyApp(string databaseName, IEnumerable<string> appNames) =>
        appNames.Any(appName =>
            databaseName.Equals(appName, StringComparison.OrdinalIgnoreCase)
            || databaseName.StartsWith(appName + "-", StringComparison.OrdinalIgnoreCase));

    private async Task<List<string[]>> QuerySqlRowsAsync(
        Kubernetes client, string podName, string sql, string completionMarker)
    {
        var result = await RunSqlAsync(client, podName, sql, includeTableOptions: true);
        if (!result.Stdout.Contains(completionMarker, StringComparison.Ordinal))
            throw new InvalidOperationException($"Failed to query SQL cleanup metadata. {result}");

        return result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Contains('|'))
            .Select(line => line.Split('|', StringSplitOptions.TrimEntries))
            .ToList();
    }

    private async Task<ExecResult> RunSqlAsync(
        Kubernetes client, string podName, string sql, bool includeTableOptions = false)
    {
        var tableOptions = includeTableOptions ? "-h -1 -W -s \"|\" " : "";
        var script = $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -b {tableOptions}-Q {QuoteForBash(sql)}";
        return await ExecInMssqlPodAsync(client, podName, script);
    }

    private async Task<(List<string> Files, string? Failure)> CleanupBakFilesAsync(
        Kubernetes client, string podName, bool dryRun)
    {
        var action = dryRun ? "-print" : "-print -delete";
        var script = $"find /var/opt/mssql/data -maxdepth 1 -type f -name '*.bak' -mmin +{GracePeriodHours * 60} {action} " +
            "&& echo 'BAK_CLEANUP_COMPLETE'";
        var result = await ExecInMssqlPodAsync(client, podName, script);
        if (!result.Stdout.Contains("BAK_CLEANUP_COMPLETE", StringComparison.Ordinal))
            return ([], result.ToString());

        var files = result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line != "BAK_CLEANUP_COMPLETE")
            .ToList();
        return (files, null);
    }

    private static string QuoteSqlIdentifier(string identifier) =>
        $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string QuoteSqlLiteral(string literal) =>
        literal.Replace("'", "''", StringComparison.Ordinal);

    private static string QuoteForBash(string value) =>
        $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}