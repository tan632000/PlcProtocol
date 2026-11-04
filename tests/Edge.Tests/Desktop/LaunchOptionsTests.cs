using IndustrialIot.EdgeDesktop.Core;

namespace IndustrialIot.Edge.Tests.Desktop;

public class LaunchOptionsTests
{
    [Fact]
    public void NoArguments_UsePort5080()
    {
        Assert.True(LaunchOptions.TryParse([], out var options, out var error));
        Assert.Equal(5080, options!.Port);
        Assert.Null(error);
    }

    [Fact]
    public void ValidPort_IsUsed()
    {
        Assert.True(LaunchOptions.TryParse(["--port", "5081"], out var options, out _));
        Assert.Equal(5081, options!.Port);
    }

    [Theory]
    [InlineData("--port", "0")]
    [InlineData("--port", "65536")]
    [InlineData("--port", "x")]
    [InlineData("--port")]
    [InlineData("--verbose")]
    [InlineData("--port", "5081", "extra")]
    public void InvalidArguments_AreRefused(params string[] args)
    {
        Assert.False(LaunchOptions.TryParse(args, out var options, out var error));
        Assert.Null(options);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void SettingsPage_IsOnLocalhost() =>
        Assert.Equal("http://localhost:5081/settings", new LaunchOptions(5081).SettingsUri.ToString());
}
