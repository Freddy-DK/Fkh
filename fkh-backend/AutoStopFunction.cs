using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Fkh;

public class AutoStopFunction
{
    private readonly ILogger<AutoStopFunction> _logger;
    private readonly FkhAutoStop _autoStop;
    private readonly FkhAllowSqlAccess _sqlAccess;

    public AutoStopFunction(ILogger<AutoStopFunction> logger, FkhAutoStop autoStop, FkhAllowSqlAccess sqlAccess)
    {
        _logger = logger;
        _autoStop = autoStop;
        _sqlAccess = sqlAccess;
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
    }
}
