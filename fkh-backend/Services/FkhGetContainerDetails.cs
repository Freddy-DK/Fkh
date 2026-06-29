using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhGetContainerDetails : FkhServiceBase
{
    public FkhGetContainerDetails(ILogger<FkhGetContainerDetails> logger) : base(logger) { }

    public async Task<object> GetContainerDetailsAsync(Dictionary<string, string> parameters)
    {
        var name = parameters["name"];

        var client = await GetKubernetesClientAsync();

        // Find the deployment matching this container name
        var appName = ResolveAppName(parameters);
        var deploymentName = $"{appName}-deployment";

        V1Deployment deployment;
        try
        {
            deployment = await client.ReadNamespacedDeploymentAsync(deploymentName, Namespace);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"Container '{name}' not found.");
        }

        // Verify ownership unless admin
        var appLabel = deployment.Spec.Template.Metadata.Labels.TryGetValue("app", out var app) ? app : "";
        if (!CanAccessContainer(parameters, appLabel))
        {
            throw new UnauthorizedAccessException($"You do not have permission to access container '{name}'.");
        }

        // DevScope from annotation
        var devScope = deployment.Metadata.Annotations?.TryGetValue("fkh/dev-scope", out var dv) == true
            && string.Equals(dv, "true", StringComparison.OrdinalIgnoreCase);

        // Read the admin username from the deployment env vars
        var container = deployment.Spec.Template.Spec.Containers.FirstOrDefault();
        var envVars = container?.Env;
        var adminUsername = envVars?.FirstOrDefault(e => e.Name == "username")?.Value
            ?? throw new InvalidOperationException("Could not determine admin username from container configuration.");

        var isMultitenant = string.Equals(envVars?.FirstOrDefault(e => e.Name == "multitenant")?.Value, "Y", StringComparison.OrdinalIgnoreCase);

        // Web client URL from the service FQDN
        string? webClientUrl = null;
        try
        {
            var serviceName = $"{appName}-service";
            var svc = await client.ReadNamespacedServiceAsync(serviceName, Namespace);
            var dnsLabel = svc.Metadata.Annotations?.TryGetValue("service.beta.kubernetes.io/azure-dns-label-name", out var label) == true ? label : null;
            if (dnsLabel != null)
            {
                var fqdn = $"{dnsLabel}.{AksLocation}.cloudapp.azure.com";
                webClientUrl = isMultitenant
                    ? $"https://{fqdn}/BC/?tenant=default"
                    : $"https://{fqdn}/BC/";
            }
        }
        catch { /* service lookup failure is non-fatal */ }

        // Read the password from the Kubernetes secret
        var secretName = $"{appName}-secret";
        V1Secret secret;
        try
        {
            secret = await client.ReadNamespacedSecretAsync(secretName, Namespace);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"Credentials secret for container '{name}' not found.");
        }

        if (secret.Data == null || !secret.Data.TryGetValue("password", out var passwordBytes))
        {
            throw new InvalidOperationException($"Password not found in secret for container '{name}'.");
        }

        var adminPassword = System.Text.Encoding.UTF8.GetString(passwordBytes);

        return new
        {
            AdminUsername = adminUsername,
            AdminPassword = adminPassword,
            DevScope = devScope,
            WebClientUrl = webClientUrl
        };
    }
}
