using PanelFleet.Application.Terminal;
using PanelFleet.Client.UnitTests.Overview;

namespace PanelFleet.Client.UnitTests.Terminal;

public sealed class TerminalInputLimiterTests
{
    private static TerminalLimits Limits(int framesPerSecond = 10, int bytesPerSecond = 1000) => new(
        TimeSpan.FromMinutes(30),
        TimeSpan.FromMinutes(240),
        65536,
        framesPerSecond,
        bytesPerSecond);

    [Fact]
    public void Burst_IsCappedAtConfiguredRate()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new TerminalInputLimiter(Limits(), time);

        for (var index = 0; index < 10; index++)
        {
            Assert.True(limiter.TryAcquire(10));
        }

        Assert.False(limiter.TryAcquire(10));
    }

    [Fact]
    public void Tokens_RefillOverTime()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new TerminalInputLimiter(Limits(framesPerSecond: 2), time);

        Assert.True(limiter.TryAcquire(10));
        Assert.True(limiter.TryAcquire(10));
        Assert.False(limiter.TryAcquire(10));

        time.Advance(TimeSpan.FromMilliseconds(600));

        Assert.True(limiter.TryAcquire(10));
    }

    [Fact]
    public void ByteRate_IsEnforced()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new TerminalInputLimiter(Limits(framesPerSecond: 100, bytesPerSecond: 100), time);

        Assert.True(limiter.TryAcquire(90));
        Assert.False(limiter.TryAcquire(20));

        time.Advance(TimeSpan.FromMilliseconds(250));

        Assert.True(limiter.TryAcquire(20));
    }

    [Fact]
    public void NegativeFrameSize_IsRejected()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new TerminalInputLimiter(Limits(), time);

        Assert.False(limiter.TryAcquire(-1));
    }
}
