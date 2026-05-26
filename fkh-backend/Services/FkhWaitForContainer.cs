using Fkh.Models;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhWaitForContainer : FkhServiceBase
{
    public FkhWaitForContainer(ILogger<FkhWaitForContainer> logger) : base(logger) { }

    public async Task<object> WaitForContainerAsync(Dictionary<string, string> parameters)
    {
        var name = parameters.TryGetValue("name", out var n) ? n : null;
        var githubUsername = parameters["_githubUsername"];
        var appName = ResolveAppName(parameters);
        var deploymentName = $"{appName}-deployment";

        var client = await GetKubernetesClientAsync();

        // Read the deployment
        V1Deployment deployment;
        try
        {
            deployment = await client.ReadNamespacedDeploymentAsync(deploymentName, Namespace);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"Container '{name}' not found.");
        }

        var replicas = deployment.Spec.Replicas ?? 0;
        if (replicas == 0)
        {
            throw new InvalidOperationException($"Container '{name}' is stopped. Start it first.");
        }

        var readyReplicas = deployment.Status?.ReadyReplicas ?? 0;

        // Check pod status
        var pods = await client.ListNamespacedPodAsync(Namespace, labelSelector: $"app={appName}");
        var pod = pods.Items.FirstOrDefault();

        if (readyReplicas < replicas)
        {
            // Pod not ready yet — check if it's pending
            if (pod == null)
            {
                throw new RetryAfterException($"Container '{name}' is pending — no pod created yet.", 5);
            }

            if (pod.Status?.Phase == "Pending")
            {
                var condition = pod.Status.Conditions?.FirstOrDefault(c => c.Type == "PodScheduled" && c.Status == "False");
                var reason = condition?.Message ?? "Waiting for pod to be scheduled.";
                throw new RetryAfterException($"Container '{name}' is pending — {reason}", 5);
            }

            // Check for container failures (CrashLoopBackOff, ImagePullBackOff, etc.)
            var containerStatus = pod.Status?.ContainerStatuses?.FirstOrDefault();
            if (containerStatus?.State?.Waiting != null)
            {
                var waitingReason = containerStatus.State.Waiting.Reason;
                if (waitingReason is "CrashLoopBackOff" or "ImagePullBackOff" or "ErrImagePull" or "InvalidImageName")
                {
                    throw new InvalidOperationException(
                        $"Container '{name}' failed: {waitingReason} — {containerStatus.State.Waiting.Message}");
                }

                throw new RetryAfterException($"Container '{name}' is starting — {waitingReason}.", 5);
            }

            throw new RetryAfterException($"Container '{name}' is starting — waiting for pod to become ready.", 5);
        }

        // Pod is ready — check container logs for "Ready for connections!"
        if (pod != null)
        {
            try
            {
                var stream = await client.ReadNamespacedPodLogAsync(
                    pod.Metadata.Name,
                    Namespace,
                    container: pod.Spec.Containers[0].Name);
                using var reader = new StreamReader(stream);
                var logs = await reader.ReadToEndAsync();

                if (logs.Contains("CONTAINER STARTUP FAILED"))
                {
                    throw new InvalidOperationException($"Container '{name}' failed to start. Use GetContainerLog to see details. Container will remain alive for troubleshooting — remove it when done.");
                }

                if (!logs.Contains("Ready for connections!"))
                {
                    throw new RetryAfterException($"Container '{name}' is initializing — waiting for BC to be ready.", 5);
                }
            }
            catch (RetryAfterException)
            {
                throw; // Don't swallow our own retry
            }
            catch
            {
                // If log read fails, assume it's still initializing
                throw new RetryAfterException($"Container '{name}' is initializing — unable to read logs yet.", 5);
            }
        }

        // Pod is ready
        return new
        {
            Name = name,
            Status = "Running",
            Message = $"Container '{name}' is ready for connections."
        };
    }
}
