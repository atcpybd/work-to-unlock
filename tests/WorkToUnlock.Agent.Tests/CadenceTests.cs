using WorkToUnlock.Agent.Services;

namespace WorkToUnlock.Agent.Tests;

public sealed class CadenceTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(15_000L)]
    [InlineData(5_000_000_000L)] // about 58 days of uptime
    public void First_check_is_due_at_any_uptime(long uptimeMs)
    {
        var cadence = new Cadence(TimeSpan.FromSeconds(1));

        Assert.True(cadence.IsDue(uptimeMs));
    }

    [Fact]
    public void Due_once_per_interval()
    {
        var cadence = new Cadence(TimeSpan.FromSeconds(1));
        const long start = 15_000;

        Assert.True(cadence.IsDue(start));
        Assert.False(cadence.IsDue(start + 500));
        Assert.False(cadence.IsDue(start + 999));
        Assert.True(cadence.IsDue(start + 1000));
        Assert.False(cadence.IsDue(start + 1500));
        Assert.True(cadence.IsDue(start + 2100));
    }

    [Fact]
    public void Long_pauses_do_not_cause_catch_up_bursts()
    {
        var cadence = new Cadence(TimeSpan.FromSeconds(1));
        cadence.IsDue(1_000);

        Assert.True(cadence.IsDue(60_000));
        Assert.False(cadence.IsDue(60_500));
    }
}
