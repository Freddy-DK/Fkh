sealed class UploadFileCommand : VersionedBlobCommand
{
    public override string Name => "UploadFile";
    public override string Description => "Uploads a local file to blob storage. Updates a version manifest (all.json) with all versions and the latest. Admin only.";
    public override List<ClientCommandParameter> Parameters =>
    [
        new() { Name = "localPath", Type = "file", Description = "Path to the local file to upload.", Required = true },
        new() { Name = "FileName", Type = "string", Description = "File name (used as the folder name in blob storage).", Required = true },
        new() { Name = "FileVersion", Type = "string", Description = "Version label for this file (used as the blob name).", Required = true }
    ];

    private static readonly UploadSpec Spec = new()
    {
        LocalPathParameterName = "localPath",
        NameParameterName = "FileName",
        VersionParameterName = "FileVersion",
        SasFunctionName = "GetFileUploadSas",
        ItemKind = "File",
        BlobDescription = "File",
        GetBlobName = static (name, version) => $"{name}/{version}"
    };

    public override Task<int> ExecuteAsync(string[] args, CliSettings settings, bool asJson)
        => UploadVersionedBlobAsync(args, settings, asJson, Spec);
}