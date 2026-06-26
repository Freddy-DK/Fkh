using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhImportTestToolkit
{
    private const string ImportTestToolkitScript = @"Param(
    [switch] $includeTestFrameworkOnly,
    [switch] $includeTestLibrariesOnly,
    [switch] $includeTestRunnerOnly,
    [switch] $includePerformanceToolkit
)

. C:\Run\HelperFunctions.ps1
$installApps = GetTestToolkitApps -includeTestFrameworkOnly:$includeTestFrameworkOnly -includeTestLibrariesOnly:$includeTestLibrariesOnly -includeTestRunnerOnly:$includeTestRunnerOnly -includePerformanceToolkit:$includePerformanceToolkit
$installApps | ForEach-Object {
    $appFile = $_
    $navAppInfo = Get-NAVAppInfo -Path $appFile
    $appPublisher = $navAppInfo.Publisher
    $appName = $navAppInfo.Name
    $appVersion = $navAppInfo.Version
    $syncAndInstall = $true
    $tenantAppInfo = Get-NAVAppInfo -ServerInstance $serverInstance -Name $appName -Publisher $appPublisher -Version $appVersion -tenant default -tenantSpecificProperties
    if ($tenantAppInfo) {
        if ($tenantAppInfo.IsInstalled) {
            Write-Host ""Skipping $appName as it is already installed""
            $syncAndInstall = $false
        }
        else {
            Write-Host ""$appName is already published""
        }
    }
    else {
        Write-Host ""Publishing $appFile""
        Publish-NavApp -ServerInstance $ServerInstance -Path $appFile -SkipVerification
    }
    if ($syncAndInstall) {
        Write-Host ""Synchronizing $appName""
        Sync-NavTenant -ServerInstance $ServerInstance -Tenant default -Force
        Sync-NavApp -ServerInstance $ServerInstance -Publisher $appPublisher -Name $appName -Version $appVersion -Tenant default -Mode ForceSync -force -WarningAction Ignore
        Write-Host ""Installing $appName""
        Install-NavApp -ServerInstance $ServerInstance -Publisher $appPublisher -Name $appName -Version $appVersion -Tenant default
    }
}";

    private readonly ILogger<FkhImportTestToolkit> _logger;
    private readonly FkhInvokeScript _invokeScript;

    public FkhImportTestToolkit(ILogger<FkhImportTestToolkit> logger, FkhInvokeScript invokeScript)
    {
        _logger = logger;
        _invokeScript = invokeScript;
    }

    public Task<object> ImportTestToolkitAsync(Dictionary<string, string> parameters)
    {
        parameters.TryGetValue("name", out var containerName);
        _logger.LogInformation("Importing test toolkit into container '{Container}'.", containerName);

        var scriptParams = string.Empty;
        if (HasFlag(parameters, "includeTestFrameworkOnly"))  scriptParams += " -includeTestFrameworkOnly";
        if (HasFlag(parameters, "includeTestLibrariesOnly"))  scriptParams += " -includeTestLibrariesOnly";
        if (HasFlag(parameters, "includeTestRunnerOnly"))     scriptParams += " -includeTestRunnerOnly";
        if (HasFlag(parameters, "includePerformanceToolkit")) scriptParams += " -includePerformanceToolkit";

        var invokeScriptParameters = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase)
        {
            ["command"] = ImportTestToolkitScript
        };

        if (scriptParams.Length > 0)
            invokeScriptParameters["scriptParams"] = scriptParams;
        else
            invokeScriptParameters.Remove("scriptParams");

        return _invokeScript.InvokeScriptAsync(invokeScriptParameters);
    }

    private static bool HasFlag(Dictionary<string, string> parameters, string parameterName)
    {
        return parameters.TryGetValue(parameterName, out var value)
            && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}