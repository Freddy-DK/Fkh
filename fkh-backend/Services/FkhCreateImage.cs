using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhCreateImage : FkhServiceBase
{
    private readonly AcrImageBuilder _imageBuilder;

    public FkhCreateImage(ILogger<FkhCreateImage> logger, AcrImageBuilder imageBuilder) : base(logger)
    {
        _imageBuilder = imageBuilder;
    }

    public async Task<object> CreateImageAsync(Dictionary<string, string> parameters)
    {
        var artifactUrl = parameters["artifactUrl"];
        var forceRebuild = parameters.TryGetValue("forceRebuild", out var fr)
            && string.Equals(fr, "true", StringComparison.OrdinalIgnoreCase);

        var imageTag = GetImageTag(artifactUrl);
        var fullImage = $"{AcrLoginServer}/{AcrRepository}:{imageTag}";

        Logger.LogInformation("Checking ACR for image {Image} (forceRebuild={ForceRebuild})", fullImage, forceRebuild);

        // EnsureImageAsync returns normally if image exists, throws RetryAfterException otherwise
        await _imageBuilder.EnsureImageAsync(imageTag, artifactUrl, forceRebuild: forceRebuild);

        return new { Image = fullImage, Message = forceRebuild ? "Image rebuild complete." : "Image already exists." };
    }
}
