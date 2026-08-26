using BcArtifacts;
using Xunit;

namespace Fkh.Backend.UnitTests;

public class BcArtifactHelperTests
{
    [Theory]
    [InlineData("https://bcartifacts.azureedge.net/sandbox/26.0.12345.0/w1", "w1")]
    [InlineData("https://bcartifacts.azureedge.net/onprem/25.0.0.0/dk", "dk")]
    [InlineData("https://bcartifacts.azureedge.net/sandbox/26.0.12345.0/us?sv=2021&sig=abc", "us")]
    public void GetArtifactCountry_extracts_country_segment(string url, string expected)
    {
        Assert.Equal(expected, BcArtifactHelper.GetArtifactCountry(url));
    }
}
