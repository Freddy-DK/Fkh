using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Fkh.Services;

public class FkhPrepull : FkhServiceBase
{
    private const string DaemonSetName = "fkh-image-prepull";
    private const string PurposeLabel = "fkh/purpose";
    private const string PurposeValue = "image-prepull";

    public FkhPrepull(ILogger<FkhPrepull> logger) : base(logger) { }

    public async Task<object> ListPrepulledAsync(Dictionary<string, string> parameters)
    {
        var client = await GetKubernetesClientAsync();

        try
        {
            var ds = await client.ReadNamespacedDaemonSetAsync(DaemonSetName, Namespace);
            var images = ds.Spec.Template.Spec.InitContainers?
                .Select(c => new { Name = c.Name, Image = c.Image })
                .OrderBy(c => c.Name)
                .ToList() ?? [];

            return new { Images = images, Count = images.Count };
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new { Images = Array.Empty<object>(), Count = 0, Message = "Pre-pull DaemonSet not found. No images are being pre-pulled." };
        }
    }

    public async Task<object> AddPrepullAsync(Dictionary<string, string> parameters)
    {
        var image = parameters["image"];
        var client = await GetKubernetesClientAsync();

        V1DaemonSet ds;
        try
        {
            ds = await client.ReadNamespacedDaemonSetAsync(DaemonSetName, Namespace);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // DaemonSet doesn't exist — create it
            ds = BuildNewDaemonSet(image);
            await client.CreateNamespacedDaemonSetAsync(ds, Namespace);
            Logger.LogInformation("Created pre-pull DaemonSet with image {Image}", image);
            return new { Action = "Created", Image = image, Message = $"Created pre-pull DaemonSet with image '{image}'." };
        }

        var initContainers = ds.Spec.Template.Spec.InitContainers ?? new List<V1Container>();

        // Check if image is already configured
        if (initContainers.Any(c => string.Equals(c.Image, image, StringComparison.OrdinalIgnoreCase)))
        {
            return new { Action = "AlreadyExists", Image = image, Message = $"Image '{image}' is already in the pre-pull list." };
        }

        // Add new init container
        var index = initContainers.Count;
        initContainers.Add(new V1Container
        {
            Name = $"prepull-{index}",
            Image = image,
            Command = new List<string> { "cmd", "/c", "echo Image pulled successfully" },
            Resources = new V1ResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity>
                {
                    ["cpu"] = new ResourceQuantity("500m"),
                    ["memory"] = new ResourceQuantity("1Gi")
                },
                Limits = new Dictionary<string, ResourceQuantity>
                {
                    ["cpu"] = new ResourceQuantity("500m"),
                    ["memory"] = new ResourceQuantity("1Gi")
                }
            }
        });

        ds.Spec.Template.Spec.InitContainers = initContainers;
        await client.ReplaceNamespacedDaemonSetAsync(ds, DaemonSetName, Namespace);

        Logger.LogInformation("Added pre-pull image {Image}", image);
        return new { Action = "Added", Image = image, Message = $"Added image '{image}' to the pre-pull list." };
    }

    public async Task<object> RemovePrepullAsync(Dictionary<string, string> parameters)
    {
        var image = parameters["image"];
        var client = await GetKubernetesClientAsync();

        V1DaemonSet ds;
        try
        {
            ds = await client.ReadNamespacedDaemonSetAsync(DaemonSetName, Namespace);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new { Action = "NotFound", Image = image, Message = "Pre-pull DaemonSet not found. Nothing to remove." };
        }

        var initContainers = ds.Spec.Template.Spec.InitContainers ?? new List<V1Container>();

        var toRemove = initContainers.FirstOrDefault(c => string.Equals(c.Image, image, StringComparison.OrdinalIgnoreCase));
        if (toRemove == null)
        {
            return new { Action = "NotFound", Image = image, Message = $"Image '{image}' is not in the pre-pull list." };
        }

        initContainers.Remove(toRemove);

        // Re-index container names
        for (int i = 0; i < initContainers.Count; i++)
        {
            initContainers[i].Name = $"prepull-{i}";
        }

        if (initContainers.Count == 0)
        {
            // No more images to pre-pull — delete the DaemonSet
            await client.DeleteNamespacedDaemonSetAsync(DaemonSetName, Namespace);
            Logger.LogInformation("Removed last pre-pull image {Image}, deleted DaemonSet", image);
            return new { Action = "RemovedAndDeleted", Image = image, Message = $"Removed image '{image}' and deleted the pre-pull DaemonSet (no images remaining)." };
        }

        ds.Spec.Template.Spec.InitContainers = initContainers;
        await client.ReplaceNamespacedDaemonSetAsync(ds, DaemonSetName, Namespace);

        Logger.LogInformation("Removed pre-pull image {Image}", image);
        return new { Action = "Removed", Image = image, Message = $"Removed image '{image}' from the pre-pull list." };
    }

    private V1DaemonSet BuildNewDaemonSet(string image)
    {
        return new V1DaemonSet
        {
            Metadata = new V1ObjectMeta
            {
                Name = DaemonSetName,
                NamespaceProperty = Namespace,
                Labels = new Dictionary<string, string> { [PurposeLabel] = PurposeValue }
            },
            Spec = new V1DaemonSetSpec
            {
                Selector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string> { [PurposeLabel] = PurposeValue }
                },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = new Dictionary<string, string> { [PurposeLabel] = PurposeValue }
                    },
                    Spec = new V1PodSpec
                    {
                        NodeSelector = new Dictionary<string, string> { ["kubernetes.io/os"] = "windows" },
                        Tolerations = new List<V1Toleration>
                        {
                            new()
                            {
                                Key = "kubernetes.azure.com/scalesetpriority",
                                OperatorProperty = "Equal",
                                Value = "spot",
                                Effect = "NoSchedule"
                            }
                        },
                        PriorityClassName = "system-node-critical",
                        InitContainers = new List<V1Container>
                        {
                            new()
                            {
                                Name = "prepull-0",
                                Image = image,
                                Command = new List<string> { "cmd", "/c", "echo Image pulled successfully" },
                                Resources = new V1ResourceRequirements
                                {
                                    Requests = new Dictionary<string, ResourceQuantity>
                                    {
                                        ["cpu"] = new ResourceQuantity("500m"),
                                        ["memory"] = new ResourceQuantity("1Gi")
                                    },
                                    Limits = new Dictionary<string, ResourceQuantity>
                                    {
                                        ["cpu"] = new ResourceQuantity("500m"),
                                        ["memory"] = new ResourceQuantity("1Gi")
                                    }
                                }
                            }
                        },
                        Containers = new List<V1Container>
                        {
                            new()
                            {
                                Name = "pause",
                                Image = "mcr.microsoft.com/oss/kubernetes/pause:3.10",
                                Command = new List<string> { "cmd", "/c", "ping -n 2147483647 127.0.0.1 > nul" },
                                Resources = new V1ResourceRequirements
                                {
                                    Requests = new Dictionary<string, ResourceQuantity>
                                    {
                                        ["cpu"] = new ResourceQuantity("10m"),
                                        ["memory"] = new ResourceQuantity("32Mi")
                                    },
                                    Limits = new Dictionary<string, ResourceQuantity>
                                    {
                                        ["cpu"] = new ResourceQuantity("10m"),
                                        ["memory"] = new ResourceQuantity("32Mi")
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
    }
}
