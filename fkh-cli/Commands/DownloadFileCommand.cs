sealed class DownloadFileCommand : VersionedBlobCommand
{
    public override string Name => "DownloadFile";
    public override string Description => "Downloads a versioned file from blob storage. Specify the file as 'blob/version' or just 'blob' to use latest.";
    public override List<ClientCommandParameter> Parameters =>
    [
        new() { Name = "file", Type = "string", Description = "File to download as 'blob/version' or just 'blob' to use latest.", Required = true },
        new() { Name = "output", Type = "string", Description = "File path to save the downloaded file. Defaults to 'blob-version' in the current directory.", Required = false }
    ];

    private static readonly DownloadSpec Spec = new()
    {
        ReferenceParameterName = "file",
        ReferenceFormat = "blob/version",
        ReferenceExample = "myfile/latest",
        OutputPathParameterName = "output",
        SasFunctionName = "GetFileDownloadSas",
        ItemKind = "File",
        BlobDescription = "File",
        GetBlobName = static (name, version) => $"{name}/{version}",
        GetDefaultOutputPath = static (name, version) => $"{name}-{version}"
    };

    public override Task<int> ExecuteAsync(string[] args, CliSettings settings, bool asJson)
        => DownloadVersionedBlobAsync(args, settings, asJson, Spec);
}