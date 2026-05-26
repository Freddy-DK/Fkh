using System.Text.Json;
using Fkh.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddSingleton(jsonOptions);
        // Register GitHubAuthService with a named HttpClient
        services.AddHttpClient<GitHubAuthService>();

        // Register GitHub App token service for triggering workflows
        services.AddHttpClient<GitHubAppTokenService>();

        // Register AKS operation services
        services.AddSingleton<FkhListContainers>();
        services.AddSingleton<FkhCreateContainer>();
        services.AddSingleton<FkhRemoveContainer>();
        services.AddSingleton<FkhScaleContainer>();
        services.AddSingleton<FkhGetContainerLog>();
        services.AddSingleton<FkhGetContainerEventLog>();
        services.AddSingleton<FkhAutoStop>();
        services.AddSingleton<FkhAllowSqlAccess>();
        services.AddSingleton<FkhListImages>();
        services.AddSingleton<FkhCreateImage>();
        services.AddSingleton<FkhRemoveImage>();
        services.AddSingleton<FkhListVMs>();
        services.AddSingleton<FkhInvokeSqlCmd>();
        services.AddSingleton<FkhInvokeScript>();
        services.AddSingleton<FkhWaitForContainer>();
        services.AddSingleton<FkhGetDatabaseUploadSas>();
        services.AddSingleton<FkhGetDatabaseDownloadSas>();
        services.AddSingleton<FkhBackupTenantDatabase>();
        services.AddSingleton<FkhRestoreTenantDatabase>();
        services.AddSingleton<FkhDismountTenant>();
        services.AddSingleton<FkhMountTenant>();
        services.AddSingleton<FkhConvertToSingleTenant>();
        services.AddSingleton<FkhBackupDatabase>();
        services.AddSingleton<FkhStatus>();
        services.AddSingleton<FkhPrepull>();
        services.AddSingleton<FkhUserSettings>();
        services.AddSingleton<FkhClusterControl>();
        services.AddSingleton<FkhCopyFileFromContainer>();
        services.AddSingleton<FkhCopyFileToContainer>();
        services.AddSingleton<FkhGetAppInfo>();
        services.AddSingleton<FkhGetUser>();
        services.AddSingleton<FkhNewUser>();
        services.AddSingleton<FkhGetContainerDetails>();
    })
    .Build();

host.Run();
