using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhConvertToSingleTenant : FkhServiceBase
{
    public FkhConvertToSingleTenant(ILogger<FkhConvertToSingleTenant> logger) : base(logger) { }

    public async Task<object> ConvertToSingleTenantAsync(Dictionary<string, string> parameters)
    {
        var containerName = ResolveAppName(parameters);
        var tenant = parameters.TryGetValue("tenant", out var t) && !string.IsNullOrWhiteSpace(t) ? t : "default";
        var doNotRestart = parameters.TryGetValue("doNotRestart", out var flag)
            && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);

        var appDatabaseName = containerName;
        var tenantDatabaseName = $"{containerName}-{tenant}";
        var deploymentName = $"{containerName}-deployment";

        var client = await GetKubernetesClientAsync();

        // Find the BC pod for this container
        var pods = await client.ListNamespacedPodAsync(Namespace, labelSelector: $"app={containerName}");
        var pod = pods.Items.FirstOrDefault(p => p.Status?.Phase == "Running")
            ?? throw new InvalidOperationException($"No running container found for '{containerName}'. Make sure the container is started and ready.");

        var podName = pod.Metadata.Name;
        var bcContainerName = pod.Spec.Containers[0].Name;
        var mssqlPod = await FindMssqlPodAsync(client);

        // Generate temporary blob SAS URLs for transferring database backups between pods
#pragma warning disable CS0618
        var credential = new ManagedIdentityCredential(ClientId);
#pragma warning restore CS0618
        var blobServiceClient = new BlobServiceClient(
            new Uri($"https://{DbsStorageAccountName}.blob.core.windows.net"), credential);
        var blobContainerClient = blobServiceClient.GetBlobContainerClient("databases");
        await blobContainerClient.CreateIfNotExistsAsync();
        var delegationKey = await blobServiceClient.GetUserDelegationKeyAsync(
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

        var appBlobName = $"_convert/{containerName}/app.bak";
        var tenantBlobName = $"_convert/{containerName}/tenant.bak";
        var mergedBlobName = $"_convert/{containerName}/merged.bak";

        string GenerateSasUrl(string blobName, BlobSasPermissions permissions)
        {
            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = "databases",
                BlobName = blobName,
                Resource = "b",
                ExpiresOn = DateTimeOffset.UtcNow.AddHours(1)
            };
            sasBuilder.SetPermissions(permissions);
            var blobClient = blobContainerClient.GetBlobClient(blobName);
            var uriBuilder = new BlobUriBuilder(blobClient.Uri)
            {
                Sas = sasBuilder.ToSasQueryParameters(delegationKey, blobServiceClient.AccountName)
            };
            return uriBuilder.ToUri().ToString();
        }

        var appUploadSas = GenerateSasUrl(appBlobName, BlobSasPermissions.Write);
        var appDownloadSas = GenerateSasUrl(appBlobName, BlobSasPermissions.Read);
        var tenantUploadSas = GenerateSasUrl(tenantBlobName, BlobSasPermissions.Write);
        var tenantDownloadSas = GenerateSasUrl(tenantBlobName, BlobSasPermissions.Read);
        var mergedUploadSas = GenerateSasUrl(mergedBlobName, BlobSasPermissions.Write);
        var mergedDownloadSas = GenerateSasUrl(mergedBlobName, BlobSasPermissions.Read);

        try
        {
            // Step 1: Stop BC service tier
            Logger.LogInformation("Step 1: Stopping BC service tier for '{Container}'...", containerName);
            await ExecInBcPodPwshAsync(client, podName, bcContainerName,
                "$ErrorActionPreference = 'Stop'; . 'C:\\run\\prompt.ps1' -silent; Stop-NAVServerInstance -ServerInstance $ServerInstance -Force");

            // Step 2: Start SQL Server Express in the Windows BC pod
            Logger.LogInformation("Step 2: Starting SQL Server Express in BC pod...");
            var startExpressResult = await ExecInBcPodPwshAsync(client, podName, bcContainerName,
                "$ErrorActionPreference = 'Stop'; Start-Service 'MSSQL$SQLEXPRESS'; Write-Output 'EXPRESS_STARTED'");
            if (!startExpressResult.Stdout.Contains("EXPRESS_STARTED"))
                throw new InvalidOperationException($"Failed to start SQL Server Express. {startExpressResult}");

            // Step 3: Backup app and tenant databases on the Linux MSSQL pod
            Logger.LogInformation("Step 3: Backing up databases '{AppDb}' and '{TenantDb}' on MSSQL pod...", appDatabaseName, tenantDatabaseName);

            var appBakPath = $"/var/opt/mssql/data/{appDatabaseName}-convert.bak";
            var backupAppSql = $"BACKUP DATABASE [{appDatabaseName}] TO DISK = N'{appBakPath}' WITH FORMAT, INIT, COMPRESSION; PRINT N'APP_BACKUP_OK'";
            var backupAppResult = await ExecInMssqlPodAsync(client, mssqlPod,
                $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -b -Q \"{backupAppSql}\"");
            if (!backupAppResult.Stdout.Contains("APP_BACKUP_OK"))
                throw new InvalidOperationException($"Failed to backup app database '{appDatabaseName}'. {backupAppResult}");

            var tenantBakPath = $"/var/opt/mssql/data/{tenantDatabaseName}-convert.bak";
            var backupTenantSql = $"BACKUP DATABASE [{tenantDatabaseName}] TO DISK = N'{tenantBakPath}' WITH FORMAT, INIT, COMPRESSION; PRINT N'TENANT_BACKUP_OK'";
            var backupTenantResult = await ExecInMssqlPodAsync(client, mssqlPod,
                $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -b -Q \"{backupTenantSql}\"");
            if (!backupTenantResult.Stdout.Contains("TENANT_BACKUP_OK"))
                throw new InvalidOperationException($"Failed to backup tenant database '{tenantDatabaseName}'. {backupTenantResult}");

            // Step 4: Upload backups from MSSQL pod to blob storage
            Logger.LogInformation("Step 4: Uploading database backups to blob storage...");

            var uploadAppResult = await ExecInMssqlPodAsync(client, mssqlPod,
                $"wget --method=PUT --header='x-ms-blob-type: BlockBlob' --body-file='{appBakPath}' -O - -S '{appUploadSas}' 2>&1 && echo 'APP_UPLOAD_OK'");
            if (!uploadAppResult.Stdout.Contains("APP_UPLOAD_OK"))
                throw new InvalidOperationException($"Failed to upload app database backup. {uploadAppResult}");

            var uploadTenantResult = await ExecInMssqlPodAsync(client, mssqlPod,
                $"wget --method=PUT --header='x-ms-blob-type: BlockBlob' --body-file='{tenantBakPath}' -O - -S '{tenantUploadSas}' 2>&1 && echo 'TENANT_UPLOAD_OK'");
            if (!uploadTenantResult.Stdout.Contains("TENANT_UPLOAD_OK"))
                throw new InvalidOperationException($"Failed to upload tenant database backup. {uploadTenantResult}");

            // Clean up backup files on MSSQL pod
            await ExecInMssqlPodAsync(client, mssqlPod, $"rm -f '{appBakPath}' '{tenantBakPath}'");

            // Create a temp directory accessible by both PowerShell and SQL Server Express
            var tempDir = "C:\\fkh-convert";
            await ExecInBcPodPwshAsync(client, podName, bcContainerName,
                $"$ErrorActionPreference = 'Stop'; " +
                $"New-Item -Path '{tempDir}' -ItemType Directory -Force | Out-Null; " +
                $"$acl = Get-Acl '{tempDir}'; " +
                "$rule = New-Object System.Security.AccessControl.FileSystemAccessRule('Everyone','FullControl','ContainerInherit,ObjectInherit','None','Allow'); " +
                $"$acl.SetAccessRule($rule); Set-Acl '{tempDir}' $acl");

            // Step 5: Download backups into the BC pod
            Logger.LogInformation("Step 5: Downloading database backups into BC pod...");

            var dlAppResult = await ExecInBcPodPwshAsync(client, podName, bcContainerName,
                $"$ErrorActionPreference = 'Stop'; Invoke-WebRequest -Uri '{appDownloadSas}' -OutFile '{tempDir}\\app.bak' -UseBasicParsing; Write-Output 'APP_DL_OK'");
            if (!dlAppResult.Stdout.Contains("APP_DL_OK"))
                throw new InvalidOperationException($"Failed to download app database backup to BC pod. {dlAppResult}");

            var dlTenantResult = await ExecInBcPodPwshAsync(client, podName, bcContainerName,
                $"$ErrorActionPreference = 'Stop'; Invoke-WebRequest -Uri '{tenantDownloadSas}' -OutFile '{tempDir}\\tenant.bak' -UseBasicParsing; Write-Output 'TENANT_DL_OK'");
            if (!dlTenantResult.Stdout.Contains("TENANT_DL_OK"))
                throw new InvalidOperationException($"Failed to download tenant database backup to BC pod. {dlTenantResult}");

            // Step 6: Restore both databases into local SQL Server Express
            Logger.LogInformation("Step 6: Restoring databases into local SQL Server Express...");

            await RestoreOnLocalExpressAsync(client, podName, bcContainerName, appDatabaseName, $"{tempDir}\\app.bak", tempDir);
            await RestoreOnLocalExpressAsync(client, podName, bcContainerName, tenantDatabaseName, $"{tempDir}\\tenant.bak", tempDir);

            // Step 7: Run Export-NAVApplication to merge app database into tenant database
            Logger.LogInformation("Step 7: Running Export-NAVApplication to merge '{AppDb}' into '{TenantDb}'...", appDatabaseName, tenantDatabaseName);
            var exportResult = await ExecInBcPodPwshAsync(client, podName, bcContainerName,
                "$ErrorActionPreference = 'Stop'; . 'C:\\run\\prompt.ps1' -silent; " +
                $"Export-NAVApplication -DatabaseServer '.\\SQLEXPRESS' -DatabaseName '{appDatabaseName}' -DestinationDatabase '{tenantDatabaseName}' -Force; " +
                "Write-Output 'EXPORT_OK'");
            if (!exportResult.Stdout.Contains("EXPORT_OK"))
                throw new InvalidOperationException($"Export-NAVApplication failed. {exportResult}");

            // Step 8: Backup the merged tenant database from local SQL Server Express
            Logger.LogInformation("Step 8: Backing up merged database from SQL Server Express...");
            var backupMergedResult = await ExecInBcPodPwshAsync(client, podName, bcContainerName,
                $"$ErrorActionPreference = 'Stop'; " +
                $"sqlcmd -S '.\\SQLEXPRESS' -b -Q \"BACKUP DATABASE [{tenantDatabaseName}] TO DISK = N'{tempDir}\\merged.bak' WITH FORMAT, INIT\"; " +
                "if ($LASTEXITCODE -ne 0) { throw 'sqlcmd backup failed' }; " +
                "Write-Output 'MERGED_BACKUP_OK'");
            if (!backupMergedResult.Stdout.Contains("MERGED_BACKUP_OK"))
                throw new InvalidOperationException($"Failed to backup merged database from SQL Express. {backupMergedResult}");

            // Step 9: Upload merged backup from BC pod to blob storage
            Logger.LogInformation("Step 9: Uploading merged database to blob storage...");
            var uploadMergedResult = await ExecInBcPodPwshAsync(client, podName, bcContainerName,
                "$ErrorActionPreference = 'Stop'; " +
                $"Invoke-WebRequest -Uri '{mergedUploadSas}' -Method PUT -InFile '{tempDir}\\merged.bak' " +
                "-Headers @{ 'x-ms-blob-type' = 'BlockBlob' } -UseBasicParsing; " +
                "Write-Output 'MERGED_UPLOAD_OK'");
            if (!uploadMergedResult.Stdout.Contains("MERGED_UPLOAD_OK"))
                throw new InvalidOperationException($"Failed to upload merged database to blob storage. {uploadMergedResult}");

            // Step 10: Drop old app and tenant databases on Linux MSSQL pod
            Logger.LogInformation("Step 10: Dropping old databases on MSSQL pod...");

            var dropAppSql = $"ALTER DATABASE [{appDatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{appDatabaseName}]";
            var dropAppResult = await ExecInMssqlPodAsync(client, mssqlPod,
                $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -b -Q \"{dropAppSql}\" && echo 'DROP_APP_OK'");
            if (!dropAppResult.Stdout.Contains("DROP_APP_OK"))
                throw new InvalidOperationException($"Failed to drop app database '{appDatabaseName}'. {dropAppResult}");

            var dropTenantSql = $"ALTER DATABASE [{tenantDatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{tenantDatabaseName}]";
            var dropTenantResult = await ExecInMssqlPodAsync(client, mssqlPod,
                $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -b -Q \"{dropTenantSql}\" && echo 'DROP_TENANT_OK'");
            if (!dropTenantResult.Stdout.Contains("DROP_TENANT_OK"))
                throw new InvalidOperationException($"Failed to drop tenant database '{tenantDatabaseName}'. {dropTenantResult}");

            // Step 11: Download merged backup on MSSQL pod and restore as the app database name
            Logger.LogInformation("Step 11: Restoring merged database on MSSQL pod as '{AppDb}'...", appDatabaseName);

            var dlMergedResult = await ExecInMssqlPodAsync(client, mssqlPod,
                $"wget -O '/var/opt/mssql/data/{appDatabaseName}-merged.bak' '{mergedDownloadSas}' 2>&1 && echo 'MERGED_DL_OK'");
            if (!dlMergedResult.Stdout.Contains("MERGED_DL_OK"))
                throw new InvalidOperationException($"Failed to download merged database to MSSQL pod. {dlMergedResult}");

            // Get logical file names from the merged backup
            var fileListSql = $"SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK=N'/var/opt/mssql/data/{appDatabaseName}-merged.bak'";
            var fileListResult = await ExecInMssqlPodAsync(client, mssqlPod,
                $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -b -h -1 -W -s \"|\" -Q \"{fileListSql}\"");

            string? dataLogicalName = null;
            string? logLogicalName = null;
            foreach (var line in fileListResult.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var cols = line.Split('|');
                if (cols.Length < 3) continue;
                var logicalName = cols[0].Trim();
                var fileType = cols[2].Trim();
                if (fileType == "D" && dataLogicalName == null)
                    dataLogicalName = logicalName;
                else if (fileType == "L" && logLogicalName == null)
                    logLogicalName = logicalName;
            }

            if (string.IsNullOrEmpty(dataLogicalName) || string.IsNullOrEmpty(logLogicalName))
                throw new InvalidOperationException($"Failed to determine logical file names from merged backup. {fileListResult}");

            // Restore the merged database with the app database name
            var restoreSql = $"RESTORE DATABASE [{appDatabaseName}] FROM DISK=N'/var/opt/mssql/data/{appDatabaseName}-merged.bak'" +
                $" WITH MOVE N'{dataLogicalName}' TO N'/var/opt/mssql/data/{appDatabaseName}.mdf'" +
                $", MOVE N'{logLogicalName}' TO N'/var/opt/mssql/log/{appDatabaseName}_log.ldf'" +
                ", REPLACE";
            var restoreResult = await ExecInMssqlPodAsync(client, mssqlPod,
                $"{SqlcmdPath} -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -b -Q \"{restoreSql}\" && echo 'RESTORE_OK'");
            if (!restoreResult.Stdout.Contains("RESTORE_OK"))
                throw new InvalidOperationException($"Failed to restore merged database as '{appDatabaseName}'. {restoreResult}");

            // Clean up backup file on MSSQL pod
            await ExecInMssqlPodAsync(client, mssqlPod, $"rm -f '/var/opt/mssql/data/{appDatabaseName}-merged.bak'");

            // Step 12: Clean up SQL Express and temp files in BC pod
            Logger.LogInformation("Step 12: Stopping SQL Server Express and cleaning up...");
            await ExecInBcPodPwshAsync(client, podName, bcContainerName,
                "Stop-Service 'MSSQL$SQLEXPRESS' -Force -ErrorAction SilentlyContinue; " +
                $"Remove-Item '{tempDir}' -Recurse -Force -ErrorAction SilentlyContinue");

            // Step 13: Update deployment — set multitenant env var to false and set customsetting
            Logger.LogInformation("Step 13: Updating deployment to set multitenant=false...");
            var deployment = await client.ReadNamespacedDeploymentAsync(deploymentName, Namespace);
            var container = deployment.Spec.Template.Spec.Containers[0];

            // Set multitenant env var to false
            var mtEnv = container.Env?.FirstOrDefault(e => e.Name == "multitenant");
            if (mtEnv != null)
            {
                mtEnv.Value = "false";
            }

            // Add/update customNavSettings to include Multitenant=false
            var csEnv = container.Env?.FirstOrDefault(e => e.Name == "customNavSettings");
            if (csEnv != null)
            {
                var settings = csEnv.Value?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() ?? [];
                settings.RemoveAll(s => s.StartsWith("Multitenant=", StringComparison.OrdinalIgnoreCase));
                settings.Add("Multitenant=false");
                csEnv.Value = string.Join(",", settings);
            }
            else
            {
                container.Env ??= new List<V1EnvVar>();
                container.Env.Add(new V1EnvVar { Name = "customNavSettings", Value = "Multitenant=false" });
            }

            if (doNotRestart)
                deployment.Spec.Replicas = 0;
            await client.ReplaceNamespacedDeploymentAsync(deployment, deploymentName, Namespace);
            Logger.LogInformation("Deployment updated — container will restart with single-tenant configuration.");
        }
        finally
        {
            // Best-effort cleanup of temporary blobs
            try
            {
                await blobContainerClient.GetBlobClient(appBlobName).DeleteIfExistsAsync();
                await blobContainerClient.GetBlobClient(tenantBlobName).DeleteIfExistsAsync();
                await blobContainerClient.GetBlobClient(mergedBlobName).DeleteIfExistsAsync();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to clean up temporary blobs under '_convert/{Container}'.", containerName);
            }
        }

        Logger.LogInformation("Container '{Container}' converted to single-tenant successfully.", containerName);

        var restartMsg = doNotRestart
            ? " Container is stopped (--doNotRestart). Start it to apply the changes."
            : " Container is restarting with single-tenant configuration.";

        return new
        {
            message = $"Container '{containerName}' converted to single-tenant.{restartMsg}",
            containerName,
            tenant,
            databaseName = appDatabaseName
        };
    }

    /// <summary>
    /// Restores a .bak file into the local SQL Server Express instance inside the BC pod.
    /// Handles FILELISTONLY to discover logical names and uses MOVE clauses for correct file placement.
    /// </summary>
    private async Task RestoreOnLocalExpressAsync(
        Kubernetes client, string podName, string bcContainerName,
        string databaseName, string bakFilePath, string tempDir)
    {
        // Use a PowerShell script that discovers logical file names and restores with MOVE clauses
        var script =
            "$ErrorActionPreference = 'Stop'; " +
            $"$fileList = @(sqlcmd -S '.\\SQLEXPRESS' -b -h -1 -W -s '|' -Q \"SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK = N'{bakFilePath}'\");" +
            "$dataName = $null; $logName = $null;" +
            "foreach ($line in $fileList) {" +
            "  $cols = $line -split '\\|';" +
            "  if ($cols.Count -ge 3) {" +
            "    if ($cols[2].Trim() -eq 'D' -and -not $dataName) { $dataName = $cols[0].Trim() }" +
            "    if ($cols[2].Trim() -eq 'L' -and -not $logName) { $logName = $cols[0].Trim() }" +
            "  }" +
            "};" +
            "if (-not $dataName -or -not $logName) { throw 'Could not determine logical file names from backup' };" +
            $"$sql = \"RESTORE DATABASE [{databaseName}] FROM DISK = N'{bakFilePath}'" +
            $" WITH MOVE N'\" + $dataName + \"' TO N'{tempDir}\\{databaseName}.mdf'" +
            $", MOVE N'\" + $logName + \"' TO N'{tempDir}\\{databaseName}_log.ldf'" +
            ", REPLACE\";" +
            "sqlcmd -S '.\\SQLEXPRESS' -b -Q $sql;" +
            "if ($LASTEXITCODE -ne 0) { throw 'Restore failed' };" +
            "Write-Output 'LOCAL_RESTORE_OK'";

        var result = await ExecInBcPodPwshAsync(client, podName, bcContainerName, script);
        if (!result.Stdout.Contains("LOCAL_RESTORE_OK"))
            throw new InvalidOperationException($"Failed to restore '{databaseName}' on local SQL Express. {result}");
    }
}
