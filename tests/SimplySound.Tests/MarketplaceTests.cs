using VRSoundboard;
using Xunit;

namespace VRSoundboard.Tests;

public sealed class MarketplaceTests
{
    [Theory]
    [InlineData("https://www.myinstants.com/media/sounds/vine-boom.mp3", true)]
    [InlineData("http://www.myinstants.com/media/sounds/vine-boom.mp3", false)]
    [InlineData("https://www.myinstants.com:8443/media/sounds/vine-boom.mp3", false)]
    [InlineData("https://myinstants.com/media/sounds/vine-boom.mp3", false)]
    [InlineData("https://www.myinstants.com.evil.example/media/sounds/vine-boom.mp3", false)]
    [InlineData("https://www.myinstants.com@evil.example/media/sounds/vine-boom.mp3", false)]
    [InlineData("https://www.myinstants.com/media/private/vine-boom.mp3", false)]
    [InlineData("https://www.myinstants.com/media/sounds/vine-boom.html", false)]
    public void MarketplaceMp3UrlMustUseTheOfficialMediaOrigin(string url, bool expected)
        => Assert.Equal(expected, MarketplaceService.IsAllowedMp3Url(url));

    [Theory]
    [InlineData("https://www.myinstants.com/en/instant/vine-boom-sound-70972/", true)]
    [InlineData("https://www.myinstants.com/instant/vine-boom/", false)]
    [InlineData("https://evil.example/en/instant/vine-boom/", false)]
    [InlineData("http://www.myinstants.com/en/instant/vine-boom/", false)]
    public void MarketplaceCreditsLinkMustUseAnOfficialSoundPage(string url, bool expected)
        => Assert.Equal(expected, MarketplaceService.IsAllowedPageUrl(url));
}
