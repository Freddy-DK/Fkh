using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class AutoStopFunction
{
    private readonly ILogger<AutoStopFunction> _logger;
    private readonly FkhAutoStop _autoStop;
    private readonly FkhAllowSqlAccess _sqlAccess;
    private readonly FkhAllowWinRmAccess _winRmAccess;
    private readonly FkhClusterSchedule _clusterSchedule;

    public AutoStopFunction(ILogger<AutoStopFunction> logger, FkhAutoStop autoStop, FkhAllowSqlAccess sqlAccess, FkhAllowWinRmAccess winRmAccess, FkhClusterSchedule clusterSchedule)
    {
        _logger = logger;
        _autoStop = autoStop;
        _sqlAccess = sqlAccess;
        _winRmAccess = winRmAccess;
        _clusterSchedule = clusterSchedule;
    }

    [Function("AutoStop")]
    public async Task RunAsync([TimerTrigger("0 */1 * * * *")] TimerInfo timerInfo)
    {
        try
        {
            await _autoStop.CheckAndStopExpiredPodsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-stop check failed.");
        }

        try
        {
            await _sqlAccess.CheckAndRevokeExpiredAccessAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SQL access auto-revoke check failed.");
        }

        try
        {
            await _winRmAccess.CheckAndRevokeExpiredAccessAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WinRM access auto-revoke check failed.");
        }

        try
        {
            await _clusterSchedule.CheckAndApplyScheduleAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cluster schedule check failed.");
        }
    }
}
