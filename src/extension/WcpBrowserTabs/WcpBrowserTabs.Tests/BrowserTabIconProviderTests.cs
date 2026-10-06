using Xunit;

namespace WcpBrowserTabs.Tests;

public sealed class BrowserTabIconProviderTests
{
    [Fact]
    public void GetTitleCandidatesStripsTrailingStatusSegments()
    {
        var candidates = BrowserTabIconProvider.GetTitleCandidates("Pull Request #1 - GitHub - Memory usage - 515 MB");

        Assert.Equal(
            ["Pull Request #1 - GitHub - Memory usage - 515 MB", "Pull Request #1 - GitHub - Memory usage", "Pull Request #1 - GitHub", "Pull Request #1"],
            candidates);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetTitleCandidatesReturnsEmptyForBlankTitles(string? title)
    {
        Assert.Empty(BrowserTabIconProvider.GetTitleCandidates(title));
    }

    [Fact]
    public void GetTitleCandidatesKeepsTitlesWithoutSeparators()
    {
        Assert.Equal(["- leading"], BrowserTabIconProvider.GetTitleCandidates(" - leading"));
        Assert.Equal(["Inbox"], BrowserTabIconProvider.GetTitleCandidates("Inbox"));
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A }, true)]
    [InlineData(new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01 }, true)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, true)]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9' }, true)]
    [InlineData(new byte[] { (byte)'<', (byte)'s', (byte)'v', (byte)'g' }, false)]
    [InlineData(new byte[] { }, false)]
    public void IsSupportedRasterImageDetectsFormats(byte[] data, bool expected)
    {
        Assert.Equal(expected, BrowserTabIconProvider.IsSupportedRasterImage(data));
    }
}
