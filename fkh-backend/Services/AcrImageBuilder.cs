using Azure.Containers.ContainerRegistry;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerRegistry;
using Azure.ResourceManager.ContainerRegistry.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Fkh.Models;
using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Text;

namespace Fkh.Services;

/// <summary>
/// Builds container images via ACR Tasks and provides deduplication
/// so multiple callers requesting the same image share a single build.
/// </summary>
public class AcrImageBuilder
{
    private readonly ILogger<AcrImageBuilder> _logger;

    private readonly string _subscriptionId;
    private readonly string _resourceGroup;
    private readonly string _acrName;
    private readonly string _clientId;
    private readonly string _storageAccountName;

    public AcrImageBuilder(ILogger<AcrImageBuilder> logger)
    {
        _logger = logger;
        _subscriptionId = Environment.GetEnvironmentVariable("AKS_SUBSCRIPTION_ID")
            ?? throw new InvalidOperationException("AKS_SUBSCRIPTION_ID is not configured.");
        _resourceGroup = Environment.GetEnvironmentVariable("AKS_RESOURCE_GROUP")
            ?? throw new InvalidOperationException("AKS_RESOURCE_GROUP is not configured.");
        _acrName = Environment.GetEnvironmentVariable("ACR_NAME")
            ?? throw new InvalidOperationException("ACR_NAME is not configured.");
        _clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID")
            ?? throw new InvalidOperationException("AZURE_CLIENT_ID is not configured.");
        _storageAccountName = Environment.GetEnvironmentVariable("DBS_STORAGE_ACCOUNT_NAME")
            ?? throw new InvalidOperationException("DBS_STORAGE_ACCOUNT_NAME is not configured.");
    }

    private string AcrLoginServer => $"{_acrName}.azurecr.io";

    /// <summary>
    /// Checks whether the image already exists in ACR.
    /// </summary>
    public async Task<bool> ImageExistsAsync(string imageTag)
    {
#pragma warning disable CS0618
        var credential = new ManagedIdentityCredential(_clientId);
#pragma warning restore CS0618
        var client = new ContainerRegistryClient(new Uri($"https://{AcrLoginServer}"), credential);

        try
        {
            var artifact = client.GetArtifact("businesscentral", imageTag);
            await artifact.GetManifestPropertiesAsync();
            return true;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether an ACR Task run is already building the given image tag.
    /// Returns true if a build is Running or Queued.
    /// </summary>
    public async Task<bool> IsBuildInProgressAsync(string imageTag)
    {
        var registry = await GetRegistryResourceAsync();
        var runs = registry.GetContainerRegistryRuns();

        await foreach (var run in runs.GetAllAsync())
        {
            var status = run.Data.Status;
            if (status != ContainerRegistryRunStatus.Running && status != ContainerRegistryRunStatus.Queued)
                continue;

            var images = run.Data.OutputImages;
            if (images != null && images.Any(img =>
                img.Tag != null && img.Tag.Equals(imageTag, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogInformation("Found in-progress build for image tag {ImageTag} (run {RunId})",
                    imageTag, run.Data.RunId);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Starts an ACR docker build for the given image tag.
    /// Uploads the build context (Dockerfile + files) to blob storage, then schedules the build.
    /// </summary>
    /// <param name="imageTag">The tag to apply to the built image.</param>
    /// <param name="dockerfileContent">The Dockerfile content.</param>
    /// <param name="contextFiles">Additional files for the build context (relative path → content bytes).</param>
    public async Task StartBuildAsync(string imageTag, string dockerfileContent, Dictionary<string, byte[]>? contextFiles = null)
    {
        _logger.LogInformation("Starting ACR build for image tag {ImageTag}...", imageTag);

        // Upload build context as tar.gz to blob storage and get a SAS URL
        var contextUrl = await UploadBuildContextAsync(imageTag, dockerfileContent, contextFiles);

        var registry = await GetRegistryResourceAsync();

        var dockerBuildRequest = new ContainerRegistryDockerBuildContent(
            dockerFilePath: "Dockerfile",
            platform: new ContainerRegistryPlatformProperties(ContainerRegistryOS.Windows))
        {
            SourceLocation = contextUrl,
            ImageNames = { $"businesscentral:{imageTag}" },
            IsPushEnabled = true,
            NoCache = false,
        };

        // Schedule the run — WaitUntil.Started returns once the run is queued
        await registry.ScheduleRunAsync(Azure.WaitUntil.Started, dockerBuildRequest);

        _logger.LogInformation("ACR build scheduled for image tag {ImageTag}.", imageTag);
    }

    /// <summary>
    /// Ensures an image exists in ACR. If not, checks for an in-progress build or starts a new one.
    /// Throws <see cref="RetryAfterException"/> if the image is not yet available.
    /// </summary>
    /// <param name="imageTag">The image tag to ensure.</param>
    /// <param name="artifactUrl">The BC artifact URL (used to generate the Dockerfile).</param>
    /// <param name="retryAfterSeconds">How many seconds the client should wait before retrying.</param>
    /// <param name="forceRebuild">When true, starts a new build even if the image already exists.</param>
    public async Task EnsureImageAsync(string imageTag, string artifactUrl, int retryAfterSeconds = 60, bool forceRebuild = false)
    {
        var fullImage = $"{AcrLoginServer}/businesscentral:{imageTag}";

        // 1. Image already exists and not forcing rebuild — done
        if (!forceRebuild && await ImageExistsAsync(imageTag))
        {
            _logger.LogInformation("Image {Image} already exists in ACR.", fullImage);
            return;
        }

        // 2. Build already in progress — wait
        if (await IsBuildInProgressAsync(imageTag))
        {
            throw new RetryAfterException(
                $"Image {fullImage} is being built. Waiting for completion...",
                retryAfterSeconds: retryAfterSeconds);
        }

        // 3. Force rebuild requested but image exists and no build running — start fresh build
        // 4. Image doesn't exist — start new build
        var (dockerfile, contextFiles) = GenerateBuildContext(artifactUrl);
        await StartBuildAsync(imageTag, dockerfile, contextFiles);

        throw new RetryAfterException(
            $"Image {fullImage} build has been started. Waiting for completion...",
            retryAfterSeconds: retryAfterSeconds);
    }

    /// <summary>
    /// Generates the Dockerfile and build context files for a BC image build.
    /// TODO: The actual Dockerfile content and attached files will be provided later.
    /// </summary>
    private (string Dockerfile, Dictionary<string, byte[]>? ContextFiles) GenerateBuildContext(string artifactUrl)
    {
        // Placeholder: Fill in the actual Dockerfile generation logic
        var dockerfile = $"""
            # TODO: Fill in Dockerfile for BC image build
            # Artifact URL: {artifactUrl}
            """;

        return (dockerfile, null);
    }

    /// <summary>
    /// Packages the Dockerfile and context files into a tar.gz, uploads to blob storage,
    /// and returns a SAS URL for ACR to pull from.
    /// </summary>
    private async Task<string> UploadBuildContextAsync(string imageTag, string dockerfileContent, Dictionary<string, byte[]>? contextFiles)
    {
#pragma warning disable CS0618
        var credential = new ManagedIdentityCredential(_clientId);
#pragma warning restore CS0618
        var blobServiceClient = new BlobServiceClient(
            new Uri($"https://{_storageAccountName}.blob.core.windows.net"), credential);
        var containerClient = blobServiceClient.GetBlobContainerClient("acr-build-context");
        await containerClient.CreateIfNotExistsAsync();

        var blobName = $"{imageTag}-{DateTime.UtcNow:yyyyMMddHHmmss}.tar.gz";
        var blobClient = containerClient.GetBlobClient(blobName);

        // Create tar.gz in memory
        using var memoryStream = new MemoryStream();
        using (var gzipStream = new GZipStream(memoryStream, CompressionLevel.Fastest, leaveOpen: true))
        {
            WriteTarEntry(gzipStream, "Dockerfile", Encoding.UTF8.GetBytes(dockerfileContent));
            if (contextFiles != null)
            {
                foreach (var (path, content) in contextFiles)
                {
                    WriteTarEntry(gzipStream, path, content);
                }
            }
            // Write two empty 512-byte blocks to signal end of tar archive
            var endBlock = new byte[1024];
            gzipStream.Write(endBlock, 0, endBlock.Length);
        }

        memoryStream.Position = 0;
        await blobClient.UploadAsync(memoryStream, overwrite: true);

        // Generate a short-lived SAS URL (valid for 2 hours — enough for a build)
        var userDelegationKey = await blobServiceClient.GetUserDelegationKeyAsync(
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(2));

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = "acr-build-context",
            BlobName = blobName,
            Resource = "b",
            StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5),
            ExpiresOn = DateTimeOffset.UtcNow.AddHours(2),
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        var sasUri = new BlobUriBuilder(blobClient.Uri)
        {
            Sas = sasBuilder.ToSasQueryParameters(userDelegationKey.Value, _storageAccountName)
        };

        return sasUri.ToUri().ToString();
    }

    /// <summary>
    /// Writes a single file entry to a tar stream (POSIX/ustar format).
    /// </summary>
    private static void WriteTarEntry(Stream stream, string fileName, byte[] content)
    {
        var header = new byte[512];
        var nameBytes = Encoding.ASCII.GetBytes(fileName);
        Array.Copy(nameBytes, 0, header, 0, Math.Min(nameBytes.Length, 100));

        // File mode: 0644
        Encoding.ASCII.GetBytes("0000644").CopyTo(header, 100);
        // Owner/group ID: 0
        Encoding.ASCII.GetBytes("0000000").CopyTo(header, 108);
        Encoding.ASCII.GetBytes("0000000").CopyTo(header, 116);
        // File size in octal
        Encoding.ASCII.GetBytes(Convert.ToString(content.Length, 8).PadLeft(11, '0')).CopyTo(header, 124);
        // Modification time
        var mtime = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        Encoding.ASCII.GetBytes(Convert.ToString(mtime, 8).PadLeft(11, '0')).CopyTo(header, 136);
        // Type flag: regular file
        header[156] = (byte)'0';
        // USTAR magic
        Encoding.ASCII.GetBytes("ustar\0").CopyTo(header, 257);
        Encoding.ASCII.GetBytes("00").CopyTo(header, 263);

        // Compute checksum (initialize checksum field with spaces first)
        for (int i = 148; i < 156; i++) header[i] = (byte)' ';
        var checksum = header.Sum(b => (int)b);
        Encoding.ASCII.GetBytes(Convert.ToString(checksum, 8).PadLeft(6, '0')).CopyTo(header, 148);
        header[154] = 0;
        header[155] = (byte)' ';

        stream.Write(header, 0, 512);
        stream.Write(content, 0, content.Length);

        // Pad to 512-byte boundary
        var remainder = content.Length % 512;
        if (remainder > 0)
        {
            var padding = new byte[512 - remainder];
            stream.Write(padding, 0, padding.Length);
        }
    }

    private async Task<ContainerRegistryResource> GetRegistryResourceAsync()
    {
#pragma warning disable CS0618
        var credential = new ManagedIdentityCredential(_clientId);
#pragma warning restore CS0618
        var armClient = new ArmClient(credential);

        var registryId = ContainerRegistryResource.CreateResourceIdentifier(
            _subscriptionId, _resourceGroup, _acrName);
        return armClient.GetContainerRegistryResource(registryId);
    }
}
