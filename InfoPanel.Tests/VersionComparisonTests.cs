using InfoPanel.Utils;

namespace InfoPanel.Tests;

public class VersionComparisonTests
{
    [Theory]
    [InlineData("1.4.0-preview.4", "1.4.0-preview.3")]
    [InlineData("v1.4.0-preview.4", "1.4.0-preview.3")]
    [InlineData("1.4.0", "1.4.0-preview.9")]
    [InlineData("1.4.1", "1.4.0")]
    [InlineData("1.4.0-preview.1+abc", "1.4.0-preview.0")]
    [InlineData("1.5.0-preview.1", "1.4.9")]
    public void Newer_version_compares_greater(string latest, string current)
    {
        Assert.True(VersionComparison.IsNewer(latest, current));
        Assert.True(VersionComparison.Compare(latest, current) > 0);
    }

    [Theory]
    [InlineData("1.4.0", "1.4.0")]
    [InlineData("v1.4.0", "1.4.0")]
    [InlineData("1.4.0+deadbeef", "1.4.0")]
    [InlineData("1.4.0-preview.2", "v1.4.0-preview.2")]
    public void Equal_versions(string a, string b)
    {
        Assert.Equal(0, VersionComparison.Compare(a, b));
        Assert.False(VersionComparison.IsNewer(a, b));
    }

    [Fact]
    public void Stable_install_is_not_offered_a_prerelease()
    {
        Assert.False(VersionComparison.IsOfferedUpdate("1.5.0-preview.1", "1.4.0"));
        Assert.False(VersionComparison.IsEligibleRelease("1.5.0-preview.1", "1.4.0"));
        Assert.True(VersionComparison.IsOfferedUpdate("1.4.1", "1.4.0"));
    }

    [Fact]
    public void Prerelease_install_is_offered_newer_prereleases_and_the_stable_release()
    {
        Assert.True(VersionComparison.IsOfferedUpdate("1.5.0-preview.2", "1.5.0-preview.1"));
        Assert.True(VersionComparison.IsOfferedUpdate("1.5.0", "1.5.0-preview.4"));
        Assert.False(VersionComparison.IsOfferedUpdate("1.5.0-preview.1", "1.5.0-preview.1"));
    }

    [Fact]
    public void Numeric_prerelease_identifiers_compare_as_numbers()
    {
        Assert.True(VersionComparison.Compare("1.0.0-preview.10", "1.0.0-preview.9") > 0);
    }
}
