using PanelFleet.Application.Terminal;

namespace PanelFleet.Client.UnitTests.Terminal;

public sealed class TerminalOptionsTests
{
    [Fact]
    public void Defaults_AreSecureAndPractical()
    {
        var options = new TerminalOptions();

        Assert.True(options.Enabled);
        Assert.Equal(1800, options.IdleTimeoutSeconds);
        Assert.Equal(240, options.MaxSessionMinutes);
        Assert.Equal(65536, options.MaxFrameBytes);
        Assert.Equal(30, options.MaxInputFramesPerSecond);
        Assert.Equal(131072, options.MaxInputBytesPerSecond);
        Assert.Equal(20, options.MaxConcurrentSessions);
        Assert.Empty(options.AllowedOrigins);
        Assert.True(options.AllowMissingOrigin);
        Assert.Equal(TimeSpan.FromMinutes(30), options.IdleTimeout);
        Assert.Equal(TimeSpan.FromMinutes(240), options.MaxSessionDuration);
    }

    [Fact]
    public void Validate_AcceptsValidConfiguration()
    {
        var options = new TerminalOptions
        {
            Enabled = true,
            AllowedOrigins = ["https://panelfleet.example.com"]
        };

        options.Validate();
    }

    [Theory]
    [InlineData(nameof(TerminalOptions.IdleTimeoutSeconds))]
    [InlineData(nameof(TerminalOptions.MaxSessionMinutes))]
    [InlineData(nameof(TerminalOptions.MaxFrameBytes))]
    [InlineData(nameof(TerminalOptions.MaxInputFramesPerSecond))]
    [InlineData(nameof(TerminalOptions.MaxInputBytesPerSecond))]
    [InlineData(nameof(TerminalOptions.MaxConcurrentSessions))]
    public void Validate_RejectsOutOfRangeValues(string property)
    {
        var options = new TerminalOptions();
        switch (property)
        {
            case nameof(TerminalOptions.IdleTimeoutSeconds):
                options.IdleTimeoutSeconds = 1;
                break;
            case nameof(TerminalOptions.MaxSessionMinutes):
                options.MaxSessionMinutes = 0;
                break;
            case nameof(TerminalOptions.MaxFrameBytes):
                options.MaxFrameBytes = 10;
                break;
            case nameof(TerminalOptions.MaxInputFramesPerSecond):
                options.MaxInputFramesPerSecond = 0;
                break;
            case nameof(TerminalOptions.MaxInputBytesPerSecond):
                options.MaxInputBytesPerSecond = 10;
                break;
            case nameof(TerminalOptions.MaxConcurrentSessions):
                options.MaxConcurrentSessions = 0;
                break;
            default:
                throw new InvalidOperationException("未知配置项。" + property);
        }

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void Validate_RejectsInvalidOrigin()
    {
        var options = new TerminalOptions { AllowedOrigins = ["not-a-url"] };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_WhenDisabled_IsLenient()
    {
        var options = new TerminalOptions
        {
            Enabled = false,
            IdleTimeoutSeconds = 1,
            MaxFrameBytes = 1,
            MaxConcurrentSessions = 0,
            AllowedOrigins = ["not-a-url"]
        };

        options.Validate();
    }
}
