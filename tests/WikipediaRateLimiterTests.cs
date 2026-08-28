using System.Diagnostics;
using Xunit;

namespace Chronicle.Plugin.Wikipedia.Tests;

public class WikipediaRateLimiterTests
{
    [Fact]
    public async Task ThrottleAsync_FirstCall_DoesNotWait()
    {
        var limiter = new WikipediaRateLimiter(200);
        var sw = Stopwatch.StartNew();

        await limiter.ThrottleAsync(CancellationToken.None);

        Assert.True(sw.ElapsedMilliseconds < 100, $"First call should not wait; took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ThrottleAsync_SecondCallWithinFloor_WaitsOutTheRemainder()
    {
        var limiter = new WikipediaRateLimiter(200);

        await limiter.ThrottleAsync(CancellationToken.None);
        var sw = Stopwatch.StartNew();
        await limiter.ThrottleAsync(CancellationToken.None);
        sw.Stop();

        // Allow generous scheduling slack — this only needs to prove throttling happened,
        // not hit an exact millisecond target.
        Assert.True(sw.ElapsedMilliseconds >= 150, $"Second call should have waited ~200ms; took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ThrottleAsync_CallAfterFloorHasElapsed_DoesNotWaitAgain()
    {
        var limiter = new WikipediaRateLimiter(50);

        await limiter.ThrottleAsync(CancellationToken.None);
        await Task.Delay(100);

        var sw = Stopwatch.StartNew();
        await limiter.ThrottleAsync(CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 50, $"Should not wait once the floor has already elapsed; took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Constructor_BelowAbsoluteFloor_ClampsUpward()
    {
        // Not directly observable without a timing test, but a value far below the intended
        // floor (e.g. a misconfigured "0") must not disable throttling entirely. Verified via
        // the same behavior as the floor test above, using an aggressively low input.
        var limiter = new WikipediaRateLimiter(0);
        Assert.NotNull(limiter); // construction itself must not throw
    }

    [Fact]
    public async Task Constructor_BelowAbsoluteFloor_StillEnforcesFiftyMsMinimum()
    {
        var limiter = new WikipediaRateLimiter(0);

        await limiter.ThrottleAsync(CancellationToken.None);
        var sw = Stopwatch.StartNew();
        await limiter.ThrottleAsync(CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 30, $"A '0' setting must still be clamped to the 50ms absolute floor; took {sw.ElapsedMilliseconds}ms");
    }
}
