using Fkh.Models;

namespace Fkh;

public static class FunctionCatalog
{
    public static List<FunctionDefinition> Functions { get; } = new()
    {
        new FunctionDefinition
        {
            Name = "CreateContainer",
            Description = "Creates a container using the provided artifact and admin credentials.",
            Route = "CreateContainer",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name for the container. Combined with the GitHub username to form the container name.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "artifactUrl",
                    Type = "string",
                    Description = "Artifact URL used by the container provisioning workflow.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "adminUsername",
                    Type = "string",
                    Description = "Administrator username for the container.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "adminPassword",
                    Type = "string",
                    Description = "Administrator password for the container.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "useDatabase",
                    Type = "string",
                    Description = "Database to restore. Can be a SAS URL to a .bak file, or 'name/version' referencing an uploaded database (use 'latest' for the most recent version).",
                    Required = false,
                    DefaultValue = null
                },
                new()
                {
                    Name = "tenantDatabase",
                    Type = "string",
                    Description = "Tenant database to restore for multitenant containers. Can be a SAS URL to a .bak file, or 'name/version' referencing an uploaded database (use 'latest' for the most recent version). When specified, the container is automatically created as multitenant and this database is restored as containername-default.",
                    Required = false,
                    DefaultValue = null
                },
                new()
                {
                    Name = "autostop",
                    Type = "string",
                    Description = "When to auto-stop the container. Use '<n>h' for hours from now (e.g. '4h') or a time of day in UTC (e.g. '18:00' or '6PM'). Leave empty for no auto-stop.",
                    Required = false,
                    DefaultValue = null
                },
                new()
                {
                    Name = "cpu",
                    Type = "string",
                    Description = "CPU cores to request for the container (e.g. '250m', '500m', '1', '2').",
                    Required = false,
                    DefaultValue = Environment.GetEnvironmentVariable("CONTAINER_DEFAULT_CPU") ?? "250m"
                },
                new()
                {
                    Name = "memory",
                    Type = "string",
                    Description = "Memory to request for the container (e.g. '3Gi', '4Gi', '8Gi').",
                    Required = false,
                    DefaultValue = Environment.GetEnvironmentVariable("CONTAINER_DEFAULT_MEMORY") ?? "3Gi"
                },
                new()
                {
                    Name = "repo",
                    Type = "string",
                    Description = "Source repository (e.g. 'org/repo'). Stored as metadata on the container.",
                    Required = false,
                    DefaultValue = null
                },
                new()
                {
                    Name = "project",
                    Type = "string",
                    Description = "AL-Go project name. Stored as metadata on the container.",
                    Required = false,
                    DefaultValue = null
                },
                new()
                {
                    Name = "moveAllAppsToDevScope",
                    Type = "boolean",
                    Description = "Move all published apps to dev scope after database restore. Sets Published As to Dev and clears uninstalled app records.",
                    Required = false,
                    DefaultValue = "false"
                },
                new()
                {
                    Name = "multitenant",
                    Type = "boolean",
                    Description = "Create the container as multitenant. Restores the -app and -tenant database backups separately and mounts the tenant database as the default tenant.",
                    Required = false,
                    DefaultValue = "false"
                },
                new()
                {
                    Name = "spot",
                    Type = "boolean",
                    Description = "Place the container on a Spot (preemptible) VM for lower cost. The container may be evicted if Azure reclaims capacity.",
                    Required = false,
                    DefaultValue = "false"
                },
                new()
                {
                    Name = "licenseFileUrl",
                    Type = "string",
                    Description = "URL to a license file (.bclicense or .flf) to use in the container.",
                    Required = false,
                    DefaultValue = null
                },
                new()
                {
                    Name = "authenticationEmail",
                    Type = "string",
                    Description = "Email address for Azure AD authentication. When set, the container uses AAD auth instead of NavUserPassword. Requires AAD App Registration setup — see docs/AadAuthentication.md.",
                    Required = false,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "RemoveContainer",
            Description = "Removes a container and its database. The full resource name is derived from your GitHub username and the name you provide.",
            Route = "RemoveContainer",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container to remove (same name used when creating it).",
                    Required = true,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "StopContainer",
            Description = "Stops a container by scaling its deployment to 0 replicas.",
            Route = "StopContainer",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container to stop (same name used when creating it).",
                    Required = true,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "StopAllContainers",
            Description = "Stops all running containers in the cluster by scaling their deployments to 0 replicas (admin only).",
            Route = "StopAllContainers",
            AdminOnly = true,
            Parameters = new List<FunctionParameterDefinition>()
        },
        new FunctionDefinition
        {
            Name = "StartContainer",
            Description = "Starts a previously stopped container by scaling its deployment to 1 replica.",
            Route = "StartContainer",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container to start (same name used when creating it).",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "autostop",
                    Type = "string",
                    Description = "When to auto-stop the container. Use '<n>h' for hours from now (e.g. '4h') or a time of day in UTC (e.g. '18:00' or '6PM'). Leave empty for no auto-stop.",
                    Required = false,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "ExtendAutoStop",
            Description = "Extends the auto-stop time of a running container by 2 hours.",
            Route = "ExtendAutoStop",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container to extend auto-stop for.",
                    Required = true,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "SetAutoStop",
            Description = "Sets the auto-stop time of a running container. Use '<n>h' for hours from now (e.g. '2h') or a time of day (e.g. '18:00' or '6PM').",
            Route = "SetAutoStop",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container to set auto-stop for.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "autostop",
                    Type = "string",
                    Description = "When to auto-stop the container. Use '<n>h' for hours from now (e.g. '2h') or a time of day (e.g. '18:00' or '6PM').",
                    Required = true,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "ClearAutoStop",
            Description = "Clears the auto-stop time of a container (admin only).",
            Route = "ClearAutoStop",
            AdminOnly = true,
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container to clear auto-stop for.",
                    Required = true,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "ListContainers",
            Description = "Lists containers. By default lists only your own containers. Set 'all' to 'true' to include common containers for members, or all containers for admins.",
            Route = "ListContainers",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "all",
                    Type = "boolean",
                    Description = "Include common containers for members, or all containers for admins.",
                    Required = false,
                    DefaultValue = null
                },
                new()
                {
                    Name = "runningOnly",
                    Type = "boolean",
                    Description = "Only list running containers (excludes stopped, pending, and failed containers).",
                    Required = false,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "AllowSqlAccess",
            Description = "Opens external SQL Server access for your IP address. Creates a temporary LoadBalancer service and network policy.",
            Route = "AllowSqlAccess",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "ip",
                    Type = "string",
                    Description = "Your public IP address (e.g. 203.0.113.10). VSIX and CLI auto-detect this.",
                    Required = false,
                    DefaultValue = null
                },
                new()
                {
                    Name = "hours",
                    Type = "string",
                    Description = "Hours to keep SQL access open (e.g. '2'). Access is auto-revoked after this period.",
                    Required = false,
                    DefaultValue = "2"
                },
                new()
                {
                    Name = "mySqlPassword",
                    Type = "string",
                    Description = "If set, creates a SQL login for your GitHub username with this password and grants db_owner on all your databases.",
                    Required = false,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "RevokeSqlAccess",
            Description = "Revokes your external SQL Server access immediately, removing the LoadBalancer service and network policy.",
            Route = "RevokeSqlAccess",
            Parameters = new List<FunctionParameterDefinition>()
        },
        new FunctionDefinition
        {
            Name = "CreateImage",
            Description = "Creates (builds) an image in the Azure Container Registry from a BC artifact URL. By default returns immediately after triggering the build workflow.",
            Route = "CreateImage",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "artifactUrl",
                    Type = "string",
                    Description = "Artifact URL used to build the image.",
                    Required = true,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "RemoveImage",
            Description = "Removes an image (tag or entire repository) from the Azure Container Registry and its associated database backup.",
            Route = "RemoveImage",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "repository",
                    Type = "string",
                    Description = "Repository name in the container registry (e.g. 'businesscentral').",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "tag",
                    Type = "string",
                    Description = "Specific tag to remove. If omitted, the entire repository is removed.",
                    Required = false,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "ListImages",
            Description = "Lists available images in the Azure Container Registry.",
            Route = "ListImages",
            Parameters = new List<FunctionParameterDefinition>()
        },
        new FunctionDefinition
        {
            Name = "ListVMs",
            Description = "Lists Windows VMs in the Kubernetes cluster. Admin only.",
            Route = "ListVMs",
            AdminOnly = true,
            Parameters = new List<FunctionParameterDefinition>()
        },
        new FunctionDefinition
        {
            Name = "WaitForContainer",
            Description = "Waits for a container to be ready. Polls until the container is running and ready for connections, or fails if the container enters an error state.",
            Route = "WaitForContainer",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container to wait for (same name used when creating it).",
                    Required = true,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "GetContainerLog",
            Description = "Gets logs from a container.",
            Route = "GetContainerLog",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container to get logs from.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "tail",
                    Type = "string",
                    Description = "Number of lines to retrieve from the end of the log.",
                    Required = false,
                    DefaultValue = "500"
                }
            }
        },
        new FunctionDefinition
        {
            Name = "GetContainerEventLog",
            Description = "Downloads the Windows Application event log from a container as an .evtx file.",
            Route = "GetContainerEventLog",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container to download the event log from.",
                    Required = true,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "InvokeSqlCmd",
            Description = "Executes a SQL statement against a container's database.",
            Route = "InvokeSqlCmd",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container whose database to run the SQL against.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "query",
                    Type = "string",
                    Description = "The SQL statement to execute.",
                    Required = true,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "InvokeScript",
            Description = "Invokes a PowerShell 7 (pwsh) script inside a running Business Central container.",
            Route = "InvokeScript",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container to run the script in.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "command",
                    Type = "string",
                    Description = "PowerShell script to execute in the container. Use this or --file, not both.",
                    Required = false,
                    DefaultValue = null
                },
                new()
                {
                    Name = "scriptFile",
                    Type = "file",
                    Description = "Path to a PowerShell script file (.ps1) to execute in the container. Use this or --command, not both.",
                    Required = false,
                    DefaultValue = null
                },
                new()
                {
                    Name = "scriptParams",
                    Type = "string",
                    Description = "Parameters to pass to the script (PowerShell syntax, e.g. -Name 'value' -Count 5).",
                    Required = false,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "ImportTestToolkit",
            Description = "Imports the Business Central test toolkit apps into a running container.",
            Route = "ImportTestToolkit",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container to import the test toolkit into.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "includeTestFrameworkOnly",
                    Type = "boolean",
                    Description = "Import only the test framework apps.",
                    Required = false,
                    DefaultValue = "false"
                },
                new()
                {
                    Name = "includeTestLibrariesOnly",
                    Type = "boolean",
                    Description = "Import only the test libraries apps.",
                    Required = false,
                    DefaultValue = "false"
                },
                new()
                {
                    Name = "includeTestRunnerOnly",
                    Type = "boolean",
                    Description = "Import only the test runner app.",
                    Required = false,
                    DefaultValue = "false"
                },
                new()
                {
                    Name = "includePerformanceToolkit",
                    Type = "boolean",
                    Description = "Include the performance toolkit apps in the import.",
                    Required = false,
                    DefaultValue = "false"
                }
            }
        },
        new FunctionDefinition
        {
            Name = "GetDatabaseUploadSas",
            Description = "Returns a SAS URL for uploading database backups to blob storage. Admin only.",
            Route = "GetDatabaseUploadSas",
            Hidden = true,
            AdminOnly = true,
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "containerName",
                    Type = "string",
                    Description = "Blob container name to get upload access to.",
                    Required = false,
                    DefaultValue = "databases"
                }
            }
        },
        new FunctionDefinition
        {
            Name = "GetDatabaseDownloadSas",
            Description = "Returns a read-only SAS URL for downloading database backups from blob storage.",
            Route = "GetDatabaseDownloadSas",
            Hidden = true,
            Parameters = new List<FunctionParameterDefinition>()
        },
        new FunctionDefinition
        {
            Name = "ListPrepulled",
            Description = "Lists images currently configured for pre-pulling on Windows nodes. Admin only.",
            Route = "ListPrepulled",
            AdminOnly = true,
            Parameters = new List<FunctionParameterDefinition>()
        },
        new FunctionDefinition
        {
            Name = "AddPrepull",
            Description = "Adds an image to the pre-pull list so it is cached on all Windows nodes. Admin only. Note: terraform apply will reset to tfvars values.",
            Route = "AddPrepull",
            AdminOnly = true,
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "image",
                    Type = "string",
                    Description = "Full image reference to pre-pull (e.g. 'myacr.azurecr.io/businesscentral:latest').",
                    Required = true,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "RemovePrepull",
            Description = "Removes an image from the pre-pull list. Admin only. Note: terraform apply will reset to tfvars values.",
            Route = "RemovePrepull",
            AdminOnly = true,
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "image",
                    Type = "string",
                    Description = "Full image reference to remove from pre-pulling.",
                    Required = true,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "GetVersion",
            Description = "Returns backend and cluster version information.",
            Route = "GetVersion",
            Parameters = new List<FunctionParameterDefinition>()
        },
        new FunctionDefinition
        {
            Name = "GetCurrentUser",
            Description = "Returns the authenticated Fkh username and authorization flags.",
            Route = "GetCurrentUser",
            Parameters = new List<FunctionParameterDefinition>()
        },
        new FunctionDefinition
        {
            Name = "Status",
            Description = "Returns system status including Kubernetes nodes, BC containers, SQL, storage, quotas, and security. Admin only.",
            Route = "Status",
            Hidden = true,
            AdminOnly = true,
            Parameters = new List<FunctionParameterDefinition>()
        },
        new FunctionDefinition
        {
            Name = "GetContainerDetails",
            Description = "Returns admin credentials, dev scope flag, and web client URL for a container. Only the container owner (or an admin) can retrieve details.",
            Route = "GetContainerDetails",
            Hidden = true,
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container.",
                    Required = true,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "GetSettings",
            Description = "Gets user settings. Admins can view settings for any user or all users. Users can only view their own settings.",
            Route = "GetSettings",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "username",
                    Type = "string",
                    Description = "Username to get settings for. Omit to get your own settings (or all settings if admin).",
                    Required = false,
                    DefaultValue = null
                },
                new()
                {
                    Name = "property",
                    Type = "string",
                    Description = "Specific setting to retrieve. Omit to get all settings.",
                    Required = false,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "SetSettings",
            Description = "Sets a user setting. Admins can set settings for any user, _members, and _admins. Users can only set their own non-admin settings.",
            Route = "SetSettings",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "username",
                    Type = "string",
                    Description = "Username to set settings for. Omit to set your own settings. Admins can use '_members' or '_admins' for defaults.",
                    Required = false,
                    DefaultValue = null
                },
                new()
                {
                    Name = "property",
                    Type = "string",
                    Description = "Setting name to set (e.g. 'MaxContainers').",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "value",
                    Type = "string",
                    Description = "Value to set the setting to.",
                    Required = true,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "ClearSettings",
            Description = "Clears user settings. If a property is specified, only that setting is removed. If omitted, all settings for the user are removed. Admin only.",
            Route = "ClearSettings",
            AdminOnly = true,
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "username",
                    Type = "string",
                    Description = "Username to clear settings for. Use '_members' or '_admins' to clear default settings.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "property",
                    Type = "string",
                    Description = "Specific setting to remove. Omit to remove all settings for the user.",
                    Required = false,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "StopFkh",
            Description = "Stops the AKS cluster to save costs. All nodes are deallocated. Use 'startfkh' to restart. Admin only.",
            Route = "StopFkh",
            AdminOnly = true,
            RequiresConfirmation = true,
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "confirm",
                    Type = "boolean",
                    Description = "Confirm that you want to stop the cluster.",
                    Required = false,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "StartFkh",
            Description = "Starts a previously stopped AKS cluster. Restores all nodes and workloads. Admin only.",
            Route = "StartFkh",
            AdminOnly = true,
            Parameters = new List<FunctionParameterDefinition>()
        },
        new FunctionDefinition
        {
            Name = "BackupDatabase",
            Description = "Backs up the app database (or single tenant database) from a running container and uploads it to blob storage. Updates the version manifest (all.json).",
            Route = "BackupDatabase",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container in which the database is running.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "backupName",
                    Type = "string",
                    Description = "Backup name in the storage account (used as the folder name in blob storage).",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "backupVersion",
                    Type = "string",
                    Description = "Backup version in the storage account (used as the blob name).",
                    Required = true,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "BackupTenantDatabase",
            Description = "Backs up a tenant database from a running container and uploads it to blob storage. Updates the version manifest (all.json).",
            Route = "BackupTenantDatabase",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container in which the database is running.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "tenant",
                    Type = "string",
                    Description = "Tenant to back up. The database name is derived as '<containername>-<tenant>'.",
                    Required = false,
                    DefaultValue = "default"
                },
                new()
                {
                    Name = "backupName",
                    Type = "string",
                    Description = "Backup name in the storage account (used as the folder name in blob storage).",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "backupVersion",
                    Type = "string",
                    Description = "Backup version in the storage account (used as the blob name).",
                    Required = true,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "RestoreTenantDatabase",
            Description = "Restores a tenant database on a running container from blob storage. The database reference can be a SAS URL to a .bak file, or 'name/version' referencing an uploaded database (use 'latest' for the most recent version).",
            Route = "RestoreTenantDatabase",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container in which the database should be restored.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "tenant",
                    Type = "string",
                    Description = "Tenant to restore. The database name is derived as '<containername>-<tenant>'.",
                    Required = false,
                    DefaultValue = "default"
                },
                new()
                {
                    Name = "database",
                    Type = "string",
                    Description = "Database to restore. Can be a SAS URL to a .bak file, or 'name/version' referencing an uploaded database (use 'latest' for the most recent version).",
                    Required = true,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "GetAppInfo",
            Description = "Returns installed apps from a running Business Central container. Optionally filters by app name, publisher, or app ID. Supports wildcards (*) in name and publisher filters.",
            Route = "GetAppInfo",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container to query.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "tenant",
                    Type = "string",
                    Description = "Tenant to query for app info.",
                    Required = false,
                    DefaultValue = "default"
                },
                new()
                {
                    Name = "appName",
                    Type = "string",
                    Description = "Filter by app name. Supports wildcards (*).",
                    Required = false,
                    DefaultValue = null
                },
                new()
                {
                    Name = "appPublisher",
                    Type = "string",
                    Description = "Filter by app publisher. Supports wildcards (*).",
                    Required = false,
                    DefaultValue = null
                },
                new()
                {
                    Name = "appId",
                    Type = "string",
                    Description = "Filter by app ID (exact match).",
                    Required = false,
                    DefaultValue = null
                },
                new()
                {
                    Name = "sort",
                    Type = "boolean",
                    Description = "Sort apps so dependencies are returned before apps that depend on them.",
                    Required = false,
                    DefaultValue = "false"
                }
            }
        },
        new FunctionDefinition
        {
            Name = "CopyFileFromContainer",
            Description = "Downloads a file from a running container. Supports wildcard paths. Returns the file content as base64.",
            Route = "CopyFileFromContainer",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container to download the file from.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "containerFilename",
                    Type = "string",
                    Description = "Path to the file inside the container (supports wildcards).",
                    Required = true,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "CopyFileToContainer",
            Description = "Uploads a file to a running container.",
            Route = "CopyFileToContainer",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container to upload the file to.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "containerFilename",
                    Type = "string",
                    Description = "Destination path inside the container.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "file",
                    Type = "file",
                    Description = "The file to upload to the container.",
                    Required = true,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "GetUser",
            Description = "Returns users and their permission sets from a running Business Central container. Optionally filters by username (exact match, case-insensitive).",
            Route = "GetUser",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container to query.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "tenant",
                    Type = "string",
                    Description = "Tenant to query for user info.",
                    Required = false,
                    DefaultValue = "default"
                },
                new()
                {
                    Name = "username",
                    Type = "string",
                    Description = "Username to look up (exact match, case-insensitive). If omitted, returns all users.",
                    Required = false,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "NewUser",
            Description = "Creates a new user in a running Business Central container and assigns permission sets.",
            Route = "NewUser",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container to create the user in.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "tenant",
                    Type = "string",
                    Description = "Tenant to create the user in.",
                    Required = false,
                    DefaultValue = "default"
                },
                new()
                {
                    Name = "username",
                    Type = "string",
                    Description = "Username for the new user.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "fullName",
                    Type = "string",
                    Description = "Full name for the new user.",
                    Required = false,
                    DefaultValue = null
                },
                new()
                {
                    Name = "licenseType",
                    Type = "string",
                    Description = "License type for the new user (Full, Limited, DeviceOnly, WindowsGroup, External, ExternalAdmin, ExternalAccountant, Application, AADGroup, Agent).",
                    Required = false,
                    DefaultValue = null
                },
                new()
                {
                    Name = "company",
                    Type = "string",
                    Description = "Default company for the new user.",
                    Required = false,
                    DefaultValue = null
                },
                new()
                {
                    Name = "profileID",
                    Type = "string",
                    Description = "Profile ID (role center) for the new user.",
                    Required = false,
                    DefaultValue = null
                },
                new()
                {
                    Name = "authenticationEmail",
                    Type = "string",
                    Description = "Authentication email for the new user.",
                    Required = false,
                    DefaultValue = null
                },
                new()
                {
                    Name = "permissions",
                    Type = "string",
                    Description = "Comma-separated list of permission set IDs to assign to the user (e.g. 'SUPER,D365 BUS FULL ACCESS').",
                    Required = true,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "DismountTenant",
            Description = "Dismounts a tenant from a running Business Central container and optionally removes the tenant database.",
            Route = "DismountTenant",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container to dismount the tenant from.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "tenant",
                    Type = "string",
                    Description = "Tenant to dismount.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "doNotRemoveDatabase",
                    Type = "boolean",
                    Description = "If set, the tenant database will not be removed after dismounting.",
                    Required = false,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "MountTenant",
            Description = "Mounts a tenant database in a running Business Central container. The database must already exist (e.g. from a previous dismount with --doNotRemoveDatabase, or restored via RestoreTenantDatabase). The database name is derived as '<containername>-<tenant>'.",
            Route = "MountTenant",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container to mount the tenant in.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "tenant",
                    Type = "string",
                    Description = "Tenant to mount. The database name is derived as '<containername>-<tenant>'.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "environmentType",
                    Type = "string",
                    Description = "Environment type for the tenant (e.g. 'Production', 'Sandbox'). If omitted, uses the server's default.",
                    Required = false,
                    DefaultValue = null
                }
            }
        },
        new FunctionDefinition
        {
            Name = "ConvertToSingleTenant",
            Description = "Converts a multitenant container to single-tenant by backing up both databases to a local SQL Server Express instance inside the BC pod, using Export-NAVApplication to merge the app database into the tenant database, then restoring the merged database back to the Linux SQL pod as the single database.",
            Route = "ConvertToSingleTenant",
            Parameters = new List<FunctionParameterDefinition>
            {
                new()
                {
                    Name = "name",
                    Type = "string",
                    Description = "Name of the container to convert to single-tenant.",
                    Required = true,
                    DefaultValue = null
                },
                new()
                {
                    Name = "tenant",
                    Type = "string",
                    Description = "Tenant whose database will become the single-tenant database.",
                    Required = false,
                    DefaultValue = "default"
                },
                new()
                {
                    Name = "doNotRestart",
                    Type = "boolean",
                    Description = "If set, the service tier will not be restarted after the conversion.",
                    Required = false,
                    DefaultValue = "false"
                }
            }
        }
    };

    public static FunctionDefinition GetRequired(string functionName)
    {
        var function = Functions.FirstOrDefault(f =>
            string.Equals(f.Name, functionName, StringComparison.OrdinalIgnoreCase));

        if (function is null)
        {
            throw new InvalidOperationException($"Function '{functionName}' is not registered in FunctionCatalog.");
        }

        return function;
    }
}
