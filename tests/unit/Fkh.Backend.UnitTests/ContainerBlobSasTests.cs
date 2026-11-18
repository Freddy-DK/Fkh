using Fkh.Services;
using k8s.Models;
using Xunit;

namespace Fkh.Backend.UnitTests;

public class ContainerBlobSasTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_secret_without_an_expiry_needs_renewal()
    {
        Assert.True(FkhServiceBase.ContainerBlobSasNeedsRenewal(null, Now));
    }

    [Fact]
    public void A_freshly_renewed_sas_does_not_need_renewal()
    {
        Assert.False(FkhServiceBase.ContainerBlobSasNeedsRenewal(Now + FkhServiceBase.ContainerBlobSasLifetime, Now));
    }

    [Fact]
    public void A_sas_is_renewed_once_the_renewal_interval_has_passed()
    {
        var renewedAt = Now - FkhServiceBase.ContainerBlobSasRenewalInterval - TimeSpan.FromMinutes(1);
        Assert.True(FkhServiceBase.ContainerBlobSasNeedsRenewal(renewedAt + FkhServiceBase.ContainerBlobSasLifetime, Now));
    }

    [Fact]
    public void An_expired_sas_needs_renewal()
    {
        Assert.True(FkhServiceBase.ContainerBlobSasNeedsRenewal(Now.AddDays(-1), Now));
    }

    [Fact]
    public void The_sas_lifetime_stays_within_the_user_delegation_key_limit()
    {
        Assert.True(FkhServiceBase.ContainerBlobSasLifetime < TimeSpan.FromDays(7));
    }

    [Fact]
    public void The_expiry_is_read_from_the_secret_annotation()
    {
        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Annotations = new Dictionary<string, string> { ["fkh/blob-sas-expires-at"] = "2026-11-16T11:00:00.0000000+00:00" }
            }
        };

        Assert.Equal(new DateTimeOffset(2026, 11, 16, 11, 0, 0, TimeSpan.Zero), FkhServiceBase.GetContainerBlobSasExpiry(secret));
    }

    [Fact]
    public void A_secret_without_annotations_has_no_expiry()
    {
        Assert.Null(FkhServiceBase.GetContainerBlobSasExpiry(new V1Secret { Metadata = new V1ObjectMeta() }));
    }

    [Fact]
    public void Detects_the_expired_sas_startup_failure()
    {
        const string log = """
            Checking for encryption key in blob storage...
            ContainerBlobContainer is set. Attempting to download encryption key from blob storage...
            The remote server returned an error: (403) Forbidden.
            CONTAINER STARTUP FAILED — container will stay alive for troubleshooting.
            """;

        Assert.True(FkhContainerBlobSas.IsExpiredSasStartupFailure(log));
    }

    [Theory]
    [InlineData("Encryption key downloaded to c:\\run\\my\\DynamicsNAV.key\nReady for connections!")]
    [InlineData("Downloading license file\nThe remote server returned an error: (403) Forbidden.\nCONTAINER STARTUP FAILED")]
    [InlineData("Attempting to download encryption key from blob storage...\nThe remote server returned an error: (403) Forbidden.")]
    public void Ignores_other_logs(string log)
    {
        Assert.False(FkhContainerBlobSas.IsExpiredSasStartupFailure(log));
    }
}
