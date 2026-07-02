sealed class DownloadDatabaseCommand : VersionedBlobCommand
{
    public override string Name => "DownloadDatabase";
    public override string Description => "Downloads a database backup (.bak) from blob storage. Specify the database as 'name/version' or just 'name' to use latest.";
    public override List<ClientCommandParameter> Parameters =>
    [
        new() { Name = "database", Type = "string", Description = "Database to download as 'name/version' or just 'name' to use latest.", Required = true },
        new() { Name = "output", Type = "string", Description = "File path to save the downloaded .bak file. Defaults to 'name-version.bak' in the current directory.", Required = false }
    ];

    private static readonly DownloadSpec Spec = new()
    {
        ReferenceParameterName = "database",
        ReferenceExample = "grmwithtests/latest",
        OutputPathParameterName = "output",
        SasFunctionName = "GetDatabaseDownloadSas",
        ItemKind = "Database",
        BlobDescription = "Database backup",
        GetBlobName = static (name, version) => $"{name}/{version}.bak",
        GetDefaultOutputPath = static (name, version) => $"{name}-{version}.bak",
        CreateResult = static (name, version, fileName, sizeBytes) => new
        {
            Database = name,
            Version = version,
            FileName = fileName,
            SizeBytes = sizeBytes
        }
    };

    public override Task<int> ExecuteAsync(string[] args, CliSettings settings, bool asJson)
        => DownloadVersionedBlobAsync(args, settings, asJson, Spec);
}
