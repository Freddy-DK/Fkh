using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Fkh.Services;

public abstract class FkhBackupDatabaseBase : FkhServiceBase
{
    protected FkhBackupDatabaseBase(ILogger logger) : base(logger) { }

    protected async Task<object> BackupDatabaseToStorageAsync(
        string githubUsername, string databaseName, string backupName, string backupVersion)
    {
        Logger.LogInformation(
            "User '{User}' backing up database '{Database}' as '{BackupName}/{BackupVersion}'.",
            githubUsername, databaseName, backupName, backupVersion);

        var client = await GetKubernetesClientAsync();
        var podName = await FindMssqlPodAsync(client);

        // Step 1: Verify the database exists
        var checkScript = $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -h -1 -W " +
            $"-Q \"SET NOCOUNT ON; IF DB_ID(N'{databaseName}') IS NOT NULL PRINT N'EXISTS' ELSE PRINT N'NOTEXISTS'\"";
        var checkResult = await ExecInMssqlPodAsync(client, podName, checkScript);
        if (!checkResult.Stdout.Contains("EXISTS") || checkResult.Stdout.Contains("NOTEXISTS"))
        {
            throw new InvalidOperationException(
                $"Database '{databaseName}' not found. Make sure the container name and tenant are correct.");
        }

        // Step 2: Back up the database to a .bak file on the MSSQL pod
        var bakFileName = $"{databaseName}-backup.bak";
        var bakFilePath = $"/var/opt/mssql/data/{bakFileName}";

        Logger.LogInformation("Backing up database '{Database}' to '{BakFile}'...", databaseName, bakFilePath);
        var backupSql = $"BACKUP DATABASE [{databaseName}] TO DISK = N'{bakFilePath}' WITH FORMAT, INIT, COMPRESSION; PRINT N'BACKUP_COMPLETE'";
        var backupScript = $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -b -Q \"{backupSql}\"";
        var backupResult = await ExecInMssqlPodAsync(client, podName, backupScript);
        if (!backupResult.Stdout.Contains("BACKUP_COMPLETE"))
        {
            throw new InvalidOperationException($"Failed to backup database '{databaseName}'. {backupResult}");
        }

        try
        {
            // Step 3: Generate an upload SAS URL for the databases container
#pragma warning disable CS0618
            var credential = new ManagedIdentityCredential(ClientId);
#pragma warning restore CS0618
            var blobServiceClient = new BlobServiceClient(
                new Uri($"https://{DbsStorageAccountName}.blob.core.windows.net"), credential);

            var blobContainerClient = blobServiceClient.GetBlobContainerClient("databases");
            await blobContainerClient.CreateIfNotExistsAsync();

            // Reuse the existing version's casing so a re-upload overwrites the same blob instead of creating a case-variant duplicate.
            try
            {
                var existingManifestResponse = await blobContainerClient
                    .GetBlobClient($"{backupName}/all.json").DownloadContentAsync();
                var existingManifest = JsonSerializer.Deserialize<DatabaseManifest>(
                    existingManifestResponse.Value.Content.ToString(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                var existingVersion = existingManifest?.Versions.FirstOrDefault(
                    v => string.Equals(v, backupVersion, StringComparison.OrdinalIgnoreCase));
                if (existingVersion is not null)
                    backupVersion = existingVersion;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                // No manifest yet; nothing to normalize.
            }

            var delegationKey = await blobServiceClient.GetUserDelegationKeyAsync(
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(60));

            var blobName = $"{backupName}/{backupVersion}.bak";
            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = "databases",
                BlobName = blobName,
                Resource = "b",
                ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(60)
            };
            sasBuilder.SetPermissions(BlobSasPermissions.Write);

            var blobClient = blobContainerClient.GetBlobClient(blobName);
            var blobUriBuilder = new BlobUriBuilder(blobClient.Uri)
            {
                Sas = sasBuilder.ToSasQueryParameters(delegationKey, blobServiceClient.AccountName)
            };
            var uploadSasUrl = blobUriBuilder.ToUri().ToString();

            // Step 4: Upload the .bak file from the MSSQL pod to blob storage using wget
            Logger.LogInformation("Uploading backup to blob storage as '{BlobName}'...", blobName);
            var uploadScript =
                $"wget --method=PUT --header='x-ms-blob-type: BlockBlob' --body-file='{bakFilePath}' -O - -S '{uploadSasUrl}' 2>&1 && echo 'UPLOAD_OK'";
            var uploadResult = await ExecInMssqlPodAsync(client, podName, uploadScript);
            if (!uploadResult.Stdout.Contains("UPLOAD_OK"))
            {
                throw new InvalidOperationException(
                    $"Failed to upload backup to blob storage. HTTP response: {uploadResult}");
            }

            // Step 5: Update the all.json manifest
            Logger.LogInformation("Updating manifest for '{BackupName}'...", backupName);

            // Need a container-level SAS for reading/writing the manifest
            var containerSasBuilder = new BlobSasBuilder
            {
                BlobContainerName = "databases",
                Resource = "c",
                ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(10)
            };
            containerSasBuilder.SetPermissions(BlobSasPermissions.Read | BlobSasPermissions.Write | BlobSasPermissions.List);
            var containerSasToken = containerSasBuilder.ToSasQueryParameters(delegationKey, blobServiceClient.AccountName);
            var containerSasUri = new Uri($"https://{DbsStorageAccountName}.blob.core.windows.net/databases?{containerSasToken}");
            var sasContainerClient = new BlobContainerClient(containerSasUri);

            var manifestBlobName = $"{backupName}/all.json";
            var manifestClient = sasContainerClient.GetBlobClient(manifestBlobName);

            DatabaseManifest manifest;
            try
            {
                var downloadResponse = await manifestClient.DownloadContentAsync();
                var existingJson = downloadResponse.Value.Content.ToString();
                manifest = JsonSerializer.Deserialize<DatabaseManifest>(existingJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new DatabaseManifest();
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                manifest = new DatabaseManifest();
            }

            if (!manifest.Versions.Contains(backupVersion, StringComparer.OrdinalIgnoreCase))
            {
                manifest.Versions.Add(backupVersion);
            }
            manifest.Versions.Sort(StringComparer.OrdinalIgnoreCase);
            manifest.Latest = backupVersion;

            var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            using (var manifestStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifestJson)))
            {
                await manifestClient.UploadAsync(manifestStream, overwrite: true);
            }

            Logger.LogInformation(
                "Backup complete: database '{Database}' uploaded as '{BackupName}/{BackupVersion}'.",
                databaseName, backupName, backupVersion);

            return new
            {
                Database = databaseName,
                BackupName = backupName,
                BackupVersion = backupVersion,
                BlobName = blobName,
                Manifest = manifestBlobName,
                Versions = manifest.Versions,
                Latest = manifest.Latest
            };
        }
        finally
        {
            // Step 6: Clean up the backup file from the pod
            await ExecInMssqlPodAsync(client, podName, $"rm -f '{bakFilePath}'");
        }
    }

    private sealed class DatabaseManifest
    {
        public List<string> Versions { get; set; } = new();
        public string? Latest { get; set; }
    }
}
