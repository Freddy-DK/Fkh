sealed class UploadDatabaseCommand : VersionedBlobCommand
{
    public override string Name => "UploadDatabase";
    public override string Description => "Uploads a .bak database file to blob storage. Updates a version manifest (all.json) with all versions and the latest. Admin only.";
    public override List<ClientCommandParameter> Parameters =>
    [
        new() { Name = "bakFile", Type = "file", Description = "Path to the .bak database backup file.", Required = true },
        new() { Name = "backupName", Type = "string", Description = "Backup name (used as the folder name in blob storage).", Required = true },
        new() { Name = "backupVersion", Type = "string", Description = "Version label for this backup (used as the blob name).", Required = true }
    ];

    private static readonly UploadSpec Spec = new()
    {
        LocalPathParameterName = "bakFile",
        NameParameterName = "backupName",
        VersionParameterName = "backupVersion",
        SasFunctionName = "GetDatabaseUploadSas",
        ItemKind = "Database",
        BlobDescription = "Database backup",
        GetBlobName = static (name, version) => $"{name}/{version}.bak",
        SasParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["containerName"] = "databases"
        }
    };

    public override Task<int> ExecuteAsync(string[] args, CliSettings settings, bool asJson)
        => UploadVersionedBlobAsync(args, settings, asJson, Spec);
}
