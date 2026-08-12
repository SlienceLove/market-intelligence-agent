using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Infrastructure.Bidding;

/// <summary>
/// Rate limiter for bidding notice collection operations.
/// Enforces three layers: platform serialization, minimum interval, and global QPS.
/// </summary>
public interface IBiddingRateLimiter
{
    /// <summary>
    /// Waits until rate limit constraints allow the request to proceed.
    /// Enforces: platform serialization, minimum interval, and global QPS.
    /// </summary>
    /// <param name="platformId">The platform identifier (e.g., domain).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WaitAsync(string platformId, CancellationToken cancellationToken);
}

/// <summary>
/// Three-layer rate limiter: platform serial execution, minimum interval, and global QPS.
/// Thread-safe for concurrent calls across multiple platforms.
/// </summary>
public sealed class BiddingRateLimiter : IBiddingRateLimiter
{
    private readonly int _minimumIntervalSeconds;
    private readonly int _globalQpsLimit;

    // Layer 1: Platform serialization - one request at a time per platform
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _platformSemaphores = new();

    // Layer 2: Minimum interval tracking - last request completion time per platform
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRequestTime = new();

    // Layer 3: Global QPS limiting - sliding window of recent request timestamps
    private readonly SemaphoreSlim _globalLock = new(1, 1);
    private readonly Queue<DateTimeOffset> _globalRequestWindow = new();

    public BiddingRateLimiter(IOptions<BiddingOptions> options)
    {
        if (options?.Value is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _minimumIntervalSeconds = options.Value.Collector.MinimumIntervalSeconds;
        _globalQpsLimit = options.Value.Collector.GlobalQpsLimit;

        if (_minimumIntervalSeconds < 0)
        {
            throw new ArgumentException("MinimumIntervalSeconds cannot be negative.", nameof(options));
        }

        if (_globalQpsLimit <= 0)
        {
            throw new ArgumentException("GlobalQpsLimit must be positive.", nameof(options));
        }
    }

    public async Task WaitAsync(string platformId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(platformId))
        {
            throw new ArgumentException("Platform ID cannot be null or empty.", nameof(platformId));
        }

        // Layer 1: Acquire platform-specific semaphore (ensures serialization per platform)
        var platformSemaphore = _platformSemaphores.GetOrAdd(platformId, _ => new SemaphoreSlim(1, 1));
        await platformSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Layer 2: Enforce minimum interval since last request to this platform
            await EnforceMinimumIntervalAsync(platformId, cancellationToken).ConfigureAwait(false);

            // Layer 3: Enforce global QPS limit
            await EnforceGlobalQpsAsync(cancellationToken).ConfigureAwait(false);

            // Record this request time for future minimum interval enforcement
            _lastRequestTime[platformId] = DateTimeOffset.UtcNow;
        }
        finally
        {
            // Release platform semaphore so next request to this platform can proceed
            platformSemaphore.Release();
        }
    }

    private async Task EnforceMinimumIntervalAsync(string platformId, CancellationToken cancellationToken)
    {
        if (_minimumIntervalSeconds == 0)
        {
            return; // No minimum interval configured
        }

        if (_lastRequestTime.TryGetValue(platformId, out var lastRequest))
        {
            var elapsed = DateTimeOffset.UtcNow - lastRequest;
            var minimumInterval = TimeSpan.FromSeconds(_minimumIntervalSeconds);

            if (elapsed < minimumInterval)
            {
                var waitTime = minimumInterval - elapsed;
                await Task.Delay(waitTime, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task EnforceGlobalQpsAsync(CancellationToken cancellationToken)
    {
        await _globalLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var oneSecondAgo = now.AddSeconds(-1);

            // Remove requests older than 1 second (sliding window)
            while (_globalRequestWindow.Count > 0 && _globalRequestWindow.Peek() <= oneSecondAgo)
            {
                _globalRequestWindow.Dequeue();
            }

            // If we're at the limit, wait until the oldest request ages out
            while (_globalRequestWindow.Count >= _globalQpsLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var oldestRequest = _globalRequestWindow.Peek();
                var waitUntil = oldestRequest.AddSeconds(1);
                var waitTime = waitUntil - DateTimeOffset.UtcNow;

                if (waitTime > TimeSpan.Zero)
                {
                    // Release the lock while waiting to allow other operations
                    _globalLock.Release();
                    try
                    {
                        await Task.Delay(waitTime, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        await _globalLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                // Re-check and clean up after waiting
                now = DateTimeOffset.UtcNow;
                oneSecondAgo = now.AddSeconds(-1);
                while (_globalRequestWindow.Count > 0 && _globalRequestWindow.Peek() <= oneSecondAgo)
                {
                    _globalRequestWindow.Dequeue();
                }
            }

            // Record this request in the global window
            _globalRequestWindow.Enqueue(now);
        }
        finally
        {
            _globalLock.Release();
        }
    }
}
