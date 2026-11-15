using VideoWallRW.App.Remote;
using Xunit;

namespace VideoWallRW.Core.Tests.Remote;

public class PinAttemptLimiterTests
{
    private DateTime _now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    private PinAttemptLimiter Create() => new(3, TimeSpan.FromMinutes(2), () => _now);

    [Fact]
    public void LocksAfterMaxFailures()
    {
        var limiter = Create();
        Assert.False(limiter.RegisterFailure("10.0.0.5"));
        Assert.False(limiter.RegisterFailure("10.0.0.5"));
        Assert.True(limiter.RegisterFailure("10.0.0.5"));
        Assert.True(limiter.IsLocked("10.0.0.5"));
        Assert.False(limiter.IsLocked("10.0.0.6")); // outro cliente não é afetado
    }

    [Fact]
    public void LockExpiresAndCountRestarts()
    {
        var limiter = Create();
        for (int i = 0; i < 3; i++) limiter.RegisterFailure("a");
        _now += TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1);
        Assert.False(limiter.IsLocked("a"));
        Assert.False(limiter.RegisterFailure("a")); // contagem recomeçou do zero
    }

    [Fact]
    public void SuccessResetsFailures()
    {
        var limiter = Create();
        limiter.RegisterFailure("a");
        limiter.RegisterFailure("a");
        limiter.Reset("a");
        Assert.False(limiter.RegisterFailure("a"));
        Assert.False(limiter.RegisterFailure("a"));
    }
}
