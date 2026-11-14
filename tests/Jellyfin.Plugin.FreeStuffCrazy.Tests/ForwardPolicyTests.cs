using Jellyfin.Plugin.FreeStuffCrazy.Relay;
using Xunit;

namespace Jellyfin.Plugin.FreeStuffCrazy.Tests;

public class ForwardPolicyTests
{
    private const string Item = "0123456789abcdef0123456789abcdef";
    private const string DashedItem = "01234567-89ab-cdef-0123-456789abcdef";

    [Theory]
    [InlineData("GET", "/System/Info/Public")]
    [InlineData("GET", "/QuickConnect/Enabled")]
    [InlineData("POST", "/QuickConnect/Initiate")]
    [InlineData("GET", "/QuickConnect/Connect?secret=abc")]
    [InlineData("POST", "/QuickConnect/Authorize?code=123456")]
    [InlineData("POST", "/Users/AuthenticateByName")]
    [InlineData("POST", "/Users/AuthenticateWithQuickConnect")]
    [InlineData("GET", "/Users/Me")]
    [InlineData("GET", "/UserViews?userId=" + Item)]
    [InlineData("GET", "/Items?ParentId=" + Item + "&Recursive=true")]
    [InlineData("GET", "/Items/" + Item)]
    [InlineData("GET", "/items/" + DashedItem)]
    [InlineData("POST", "/Items/" + Item + "/PlaybackInfo")]
    [InlineData("GET", "/Items/" + Item + "/Images/Primary?fillWidth=390")]
    [InlineData("GET", "/Items/" + Item + "/Images/Backdrop/0")]
    [InlineData("GET", "/Shows/NextUp")]
    [InlineData("GET", "/Shows/" + Item + "/Episodes?SeasonId=" + Item)]
    [InlineData("POST", "/UserPlayedItems/" + Item)]
    [InlineData("DELETE", "/UserPlayedItems/" + Item)]
    [InlineData("POST", "/Sessions/Playing/Progress")]
    [InlineData("POST", "/Sessions/Logout")]
    [InlineData("DELETE", "/Videos/ActiveEncodings?DeviceId=x&PlaySessionId=y")]
    [InlineData("GET", "/Videos/" + Item + "/master.m3u8?PlaySessionId=abc")]
    [InlineData("GET", "/Videos/" + DashedItem + "/hls1/main/12.ts")]
    [InlineData("GET", "/Videos/" + Item + "/hls1/main/0.mp4?runtimeTicks=0")]
    public void ForwardsWhatThePlayerUses(string method, string path) =>
        Assert.True(ForwardPolicy.IsAllowed(method, path));

    [Theory]
    [InlineData("GET", "/System/Configuration")] // admin endpoint
    [InlineData("GET", "/Startup/Configuration")] // setup wizard
    [InlineData("POST", "/Startup/User")]
    [InlineData("GET", "/Users")]
    [InlineData("GET", "/Users/Public")]
    [InlineData("POST", "/Library/Refresh")]
    [InlineData("DELETE", "/Items/" + Item)] // right path, wrong method
    [InlineData("GET", "/Sessions/Logout")]
    [InlineData("GET", "/Items/" + Item + "/Download")]
    [InlineData("GET", "/Items/not-an-id")]
    [InlineData("GET", "/Videos/" + Item + "/stream.mkv")]
    [InlineData("GET", "/Videos/" + Item + "/hls1/main/12.exe")]
    public void RefusesEverythingElse(string method, string path) =>
        Assert.False(ForwardPolicy.IsAllowed(method, path));

    [Theory]
    [InlineData("http://evil.example/System/Info/Public")] // absolute URL
    [InlineData("//evil.example/System/Info/Public")] // scheme-relative
    [InlineData("System/Info/Public")] // not rooted
    [InlineData("/System/Info/Public/../../System/Configuration")]
    [InlineData("/System%2FInfo/Public")] // encoded slash
    [InlineData("/System/Info/Public%2e%2e")]
    [InlineData("/System\\Info\\Public")]
    [InlineData("/System/Info/Public\r\nX-Injected: 1")]
    [InlineData("/System/Info/Public #frag")]
    [InlineData("/System/Info/Public#frag")]
    [InlineData("")]
    public void RefusesTricksInThePath(string path) =>
        Assert.False(ForwardPolicy.IsAllowed("GET", path));

    [Fact]
    public void RefusesOverlongPaths() =>
        Assert.False(ForwardPolicy.IsAllowed("GET", "/Items?x=" + new string('a', 7000)));

    [Theory]
    [InlineData("Authorization", true)]
    [InlineData("x-emby-authorization", true)]
    [InlineData("Range", true)]
    [InlineData("Host", false)]
    [InlineData("Cookie", false)]
    [InlineData("X-Forwarded-For", false)]
    [InlineData("Connection", false)]
    public void ForwardsOnlyKnownRequestHeaders(string name, bool forwarded) =>
        Assert.Equal(forwarded, ForwardPolicy.IsForwardedRequestHeader(name));

    [Theory]
    [InlineData("Content-Type", true)]
    [InlineData("Content-Range", true)]
    [InlineData("Set-Cookie", false)]
    [InlineData("Location", false)]
    [InlineData("Server", false)]
    public void ReturnsOnlyKnownResponseHeaders(string name, bool returned) =>
        Assert.Equal(returned, ForwardPolicy.IsReturnedResponseHeader(name));
}
