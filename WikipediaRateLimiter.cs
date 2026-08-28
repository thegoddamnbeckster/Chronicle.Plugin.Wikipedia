namespace Chronicle.Plugin.Wikipedia;

/// <summary>
/// Serialises all outbound HTTP requests to Wikipedia with a minimum inter-request delay.
/// Wikimedia publishes no hard numeric rate limit for a well-identified client (unlike
/// MusicBrainz's documented 1 req/sec) — their guidance is qualitative: serialize requests,
/// set a real User-Agent, don't hammer it. The default floor here (100ms / 10 req/s) is
/// deliberately far more conservative than that guidance requires, because this is a shared,
/// free, donation-funded service. The 50ms absolute floor prevents a misconfigured setting
/// from disabling throttling entirely.
/// </summary>
internal sealed class WikipediaRateLimiter
{
    private const int AbsoluteFloorMs = 50;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _floorMs;

    // DateTime.MinValue sentinel, not Stopwatch.StartNew() — a Stopwatch started at
    // construction time makes the very first ThrottleAsync call (which has no prior request
    // to space out from at all) measure "time since construction" instead of "time since the
    // last request", incorrectly waiting out up to the full floor for no reason. A caught-by-
    // its-own-test bug during code review: Chronicle.Plugin.FanEdit's rate limiter has this
    // same latent issue (Stopwatch.StartNew()), but Chronicle.Plugin.MusicBrainz's
    // DateTime.MinValue-sentinel approach is the more correct of the two house patterns, so
    // this follows that one rather than copying the weaker precedent.
    private DateTime _lastRequest = DateTime.MinValue;

    public WikipediaRateLimiter(int floorMs) => _floorMs = Math.Max(floorMs, AbsoluteFloorMs);

    public async Task ThrottleAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var elapsed = DateTime.UtcNow - _lastRequest;
            if (elapsed < TimeSpan.FromMilliseconds(_floorMs))
            {
                var wait = TimeSpan.FromMilliseconds(_floorMs) - elapsed;
                PluginLog.Debug($"ThrottleAsync: waiting {wait.TotalMilliseconds:F0}ms (floor={_floorMs}ms)");
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            _lastRequest = DateTime.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }
}
