using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public abstract class FkhBlobContainerSasService : FkhServiceBase
{
    protected FkhBlobContainerSasService(ILogger logger) : base(logger) { }

    protected async Task<object> GetContainerSasAsync(
        string containerName,
        BlobSasPermissions permissions,
        bool createIfNotExists,
        string accessDescription,
        Dictionary<string, string> parameters)
    {
#pragma warning disable CS0618
        var credential = new ManagedIdentityCredential(ClientId);
#pragma warning restore CS0618
        var blobServiceClient = new BlobServiceClient(
            new Uri($"https://{DbsStorageAccountName}.blob.core.windows.net"), credential);

        var blobContainerClient = blobServiceClient.GetBlobContainerClient(containerName);
        if (createIfNotExists)
            await blobContainerClient.CreateIfNotExistsAsync();

        var expiresOn = DateTimeOffset.UtcNow.AddMinutes(60);
        var delegationKey = await blobServiceClient.GetUserDelegationKeyAsync(DateTimeOffset.UtcNow, expiresOn);

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = containerName,
            Resource = "c",
            ExpiresOn = expiresOn
        };
        sasBuilder.SetPermissions(permissions);

        var sasToken = sasBuilder.ToSasQueryParameters(delegationKey, blobServiceClient.AccountName);
        var sasUrl = $"https://{DbsStorageAccountName}.blob.core.windows.net/{containerName}?{sasToken}";

        Logger.LogInformation("Generated 60-minute {AccessDescription} SAS for container '{ContainerName}' (user: {Username})",
            accessDescription, containerName, parameters.GetValueOrDefault("_githubUsername", "unknown"));

        return new
        {
            SasUrl = sasUrl,
            ContainerName = containerName,
            ExpiresInMinutes = 60,
            StorageAccountName = DbsStorageAccountName
        };
    }
}