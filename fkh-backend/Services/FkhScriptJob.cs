using Microsoft.Extensions.Logging;

namespace Fkh.Services;

/// <summary>
/// Queries and collects the result of detached script jobs launched by InvokeScript,
/// PublishAppsFromBuild and MountTenant. A job is located by container name + job id, so both
/// must be supplied by the caller.
/// </summary>
public class FkhScriptJob : FkhServiceBase
{
    public FkhScriptJob(ILogger<FkhScriptJob> logger) : base(logger) { }

    /// <summary>
    /// Returns { jobId, container, status, error } for a job. Status is Running, Complete or
    /// Failure. Read-only — never removes the job files. A missing job is reported as Failure.
    /// </summary>
    public async Task<object> GetStatusAsync(Dictionary<string, string> parameters)
    {
        var appName = ResolveAppName(parameters);
        var jobId = GetJobId(parameters);

        var client = await GetKubernetesClientAsync();
        var (podName, containerName) = await FindBcPodAsync(client, appName);

        var (state, error) = await GetDetachedJobStatusAsync(client, podName, containerName, jobId);
        return state switch
        {
            DetachedJobState.Running => new { JobId = jobId, Container = appName, Status = "Running", Error = (string?)null },
            DetachedJobState.Complete => new { JobId = jobId, Container = appName, Status = "Complete", Error = (string?)null },
            DetachedJobState.Failed => new { JobId = jobId, Container = appName, Status = "Failure", Error = error },
            _ => new { JobId = jobId, Container = appName, Status = "Failure", Error = (string?)"job not found" },
        };
    }

    /// <summary>
    /// Returns the finished job's output and removes its files (one-shot). If the job is still
    /// running, returns status Running without consuming anything.
    /// </summary>
    public async Task<object> GetResultAsync(Dictionary<string, string> parameters)
    {
        var appName = ResolveAppName(parameters);
        var jobId = GetJobId(parameters);

        var client = await GetKubernetesClientAsync();
        var (podName, containerName) = await FindBcPodAsync(client, appName);

        var (state, _) = await GetDetachedJobStatusAsync(client, podName, containerName, jobId);
        if (state == DetachedJobState.Running)
            return new { JobId = jobId, Container = appName, Status = "Running", Error = (string?)null, Output = "" };

        var result = await CollectDetachedJobResultAsync(client, podName, containerName, jobId);
        if (result is null)
            return new { JobId = jobId, Container = appName, Status = "Failure", Error = (string?)"job not found", Output = "" };

        return string.IsNullOrWhiteSpace(result.Stderr)
            ? new { JobId = jobId, Container = appName, Status = "Complete", Error = (string?)null, Output = result.Stdout }
            : new { JobId = jobId, Container = appName, Status = "Failure", Error = (string?)result.Stderr, Output = result.Stdout };
    }

    private static string GetJobId(Dictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("jobId", out var jobId) || string.IsNullOrWhiteSpace(jobId))
            throw new InvalidOperationException("Parameter 'jobId' is required.");
        return jobId.Trim();
    }
}
