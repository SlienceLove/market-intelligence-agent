using System.Diagnostics;
using MarketIntelligence.Agent.Infrastructure.Bidding;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Tests;

public sealed class BiddingRateLimiterTests
{
    private static BiddingRateLimiter CreateLimiter(int minimumIntervalSeconds = 2, int globalQpsLimit = 5)
    {
        var options = Options.Create(new BiddingOptions
        {
            Collector = new CollectorSettings
            {
                MinimumIntervalSeconds = minimumIntervalSeconds,
                GlobalQpsLimit = globalQpsLimit
            }
        });

        return new BiddingRateLimiter(options);
    }

    [Fact]
    public async Task Platform_serialization_enforces_sequential_execution_for_same_platform()
    {
        // Arrange
        var limiter = CreateLimiter();
        var platformId = "example.com";
        var task1Started = new TaskCompletionSource<bool>();
        var task1CanComplete = new TaskCompletionSource<bool>();
        var task2Started = new TaskCompletionSource<bool>();

        // Act
        var task1 = Task.Run(async () =>
        {
            await limiter.WaitAsync(platformId, CancellationToken.None);
            task1Started.SetResult(true);
            await task1CanComplete.Task; // Hold the platform semaphore
        });

        // Wait for task1 to acquire the semaphore
        await task1Started.Task;

        var task2 = Task.Run(async () =>
        {
            task2Started.SetResult(true);
            await limiter.WaitAsync(platformId, CancellationToken.None);
        });

        // Wait briefly to ensure task2 has attempted to wait
        await task2Started.Task;
        await Task.Delay(100);

        // Assert: task2 should still be waiting
        Assert.False(task2.IsCompleted);

        // Release task1
        task1CanComplete.SetResult(true);
        await task1;

        // Now task2 should complete
        await task2.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(task2.IsCompleted);
    }

    [Fact]
    public async Task Minimum_interval_enforces_delay_between_consecutive_requests()
    {
        // Arrange
        var limiter = CreateLimiter(minimumIntervalSeconds: 2);
        var platformId = "example.com";
        var stopwatch = Stopwatch.StartNew();

        // Act: Make two consecutive requests
        await limiter.WaitAsync(platformId, CancellationToken.None);
        var firstRequestTime = stopwatch.Elapsed;

        await limiter.WaitAsync(platformId, CancellationToken.None);
        var secondRequestTime = stopwatch.Elapsed;

        // Assert: Second request should wait at least 2 seconds after first completes
        var elapsedBetweenRequests = secondRequestTime - firstRequestTime;
        Assert.True(elapsedBetweenRequests.TotalSeconds >= 1.9,
            $"Expected at least 1.9s between requests, got {elapsedBetweenRequests.TotalSeconds:F2}s");
    }

    [Fact]
    public async Task Minimum_interval_respects_request_duration()
    {
        // Arrange
        var limiter = CreateLimiter(minimumIntervalSeconds: 2);
        var platformId = "example.com";
        var stopwatch = Stopwatch.StartNew();

        // Act: First request
        await limiter.WaitAsync(platformId, CancellationToken.None);

        // Simulate a request that takes 3 seconds (longer than minimum interval)
        await Task.Delay(TimeSpan.FromSeconds(3));
        var firstRequestEnd = stopwatch.Elapsed;

        // Second request should proceed immediately (no additional wait needed)
        await limiter.WaitAsync(platformId, CancellationToken.None);
        var secondRequestStart = stopwatch.Elapsed;

        // Assert: Very little delay between first request end and second request start
        var additionalWait = secondRequestStart - firstRequestEnd;
        Assert.True(additionalWait.TotalSeconds < 0.5,
            $"Expected minimal additional wait, got {additionalWait.TotalSeconds:F2}s");
    }

    [Fact]
    public async Task Global_qps_limit_blocks_excess_concurrent_requests()
    {
        // Arrange
        var limiter = CreateLimiter(globalQpsLimit: 5);
        var platforms = Enumerable.Range(1, 6).Select(i => $"platform{i}.com").ToArray();
        var completedCount = 0;
        var completedLock = new object();

        // Act: Launch 6 concurrent requests (limit is 5)
        var tasks = platforms.Select(async platform =>
        {
            await limiter.WaitAsync(platform, CancellationToken.None);
            lock (completedLock)
            {
                completedCount++;
            }
        }).ToArray();

        // Wait briefly for requests to queue
        await Task.Delay(100);

        // Assert: Only 5 should have completed initially
        lock (completedLock)
        {
            Assert.True(completedCount <= 5, $"Expected at most 5 completed, got {completedCount}");
        }

        // Wait for all to complete
        await Task.WhenAll(tasks);

        lock (completedLock)
        {
            Assert.Equal(6, completedCount);
        }
    }

    [Fact]
    public async Task Independent_platforms_do_not_block_each_other_within_global_limit()
    {
        // Arrange
        var limiter = CreateLimiter(minimumIntervalSeconds: 0, globalQpsLimit: 10);
        var platform1 = "platform1.com";
        var platform2 = "platform2.com";
        var stopwatch = Stopwatch.StartNew();

        // Act: Make concurrent requests to different platforms
        var task1 = limiter.WaitAsync(platform1, CancellationToken.None);
        var task2 = limiter.WaitAsync(platform2, CancellationToken.None);

        await Task.WhenAll(task1, task2);
        var elapsed = stopwatch.Elapsed;

        // Assert: Should complete quickly (no serialization between different platforms)
        Assert.True(elapsed.TotalSeconds < 1.0,
            $"Expected concurrent execution, but took {elapsed.TotalSeconds:F2}s");
    }

    [Fact]
    public async Task Cancellation_during_wait_throws_operation_cancelled()
    {
        // Arrange
        var limiter = CreateLimiter(minimumIntervalSeconds: 5);
        var platformId = "example.com";
        var cts = new CancellationTokenSource();

        // Make first request to set up minimum interval
        await limiter.WaitAsync(platformId, CancellationToken.None);

        // Act & Assert: Second request should be waiting, cancel it
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await limiter.WaitAsync(platformId, cts.Token);
        });
    }

    [Fact]
    public async Task Global_qps_sliding_window_allows_new_requests_after_aging_out()
    {
        // Arrange
        var limiter = CreateLimiter(minimumIntervalSeconds: 0, globalQpsLimit: 3);
        var platforms = Enumerable.Range(1, 5).Select(i => $"platform{i}.com").ToArray();

        // Act: Make 3 requests (fills the limit)
        await Task.WhenAll(
            limiter.WaitAsync(platforms[0], CancellationToken.None),
            limiter.WaitAsync(platforms[1], CancellationToken.None),
            limiter.WaitAsync(platforms[2], CancellationToken.None)
        );

        // Wait for 1+ seconds for the sliding window to advance
        await Task.Delay(TimeSpan.FromSeconds(1.1));

        var stopwatch = Stopwatch.StartNew();

        // New requests should proceed without much delay
        await limiter.WaitAsync(platforms[3], CancellationToken.None);
        await limiter.WaitAsync(platforms[4], CancellationToken.None);

        var elapsed = stopwatch.Elapsed;

        // Assert: Should complete quickly after window slides
        Assert.True(elapsed.TotalSeconds < 0.5,
            $"Expected quick completion after window slide, but took {elapsed.TotalSeconds:F2}s");
    }

    [Fact]
    public async Task Configuration_override_applies_custom_minimum_interval()
    {
        // Arrange
        var limiter = CreateLimiter(minimumIntervalSeconds: 1, globalQpsLimit: 10);
        var platformId = "example.com";
        var stopwatch = Stopwatch.StartNew();

        // Act
        await limiter.WaitAsync(platformId, CancellationToken.None);
        await limiter.WaitAsync(platformId, CancellationToken.None);
        var elapsed = stopwatch.Elapsed;

        // Assert: Should wait ~1 second (not default 2)
        Assert.True(elapsed.TotalSeconds >= 0.9 && elapsed.TotalSeconds < 1.5,
            $"Expected ~1s wait with custom config, got {elapsed.TotalSeconds:F2}s");
    }

    [Fact]
    public async Task Configuration_override_applies_custom_global_qps()
    {
        // Arrange
        var limiter = CreateLimiter(minimumIntervalSeconds: 0, globalQpsLimit: 2);
        var platforms = Enumerable.Range(1, 3).Select(i => $"platform{i}.com").ToArray();
        var completedCount = 0;
        var completedLock = new object();

        // Act: Launch 3 concurrent requests (limit is 2)
        var tasks = platforms.Select(async platform =>
        {
            await limiter.WaitAsync(platform, CancellationToken.None);
            lock (completedLock)
            {
                completedCount++;
            }
        }).ToArray();

        // Wait briefly
        await Task.Delay(100);

        // Assert: Only 2 should have completed initially with custom limit
        lock (completedLock)
        {
            Assert.True(completedCount <= 2, $"Expected at most 2 completed with limit=2, got {completedCount}");
        }

        await Task.WhenAll(tasks);
    }

    [Fact]
    public void Constructor_throws_on_null_options()
    {
        Assert.Throws<ArgumentNullException>(() => new BiddingRateLimiter(null!));
    }

    [Fact]
    public void Constructor_throws_on_negative_minimum_interval()
    {
        var options = Options.Create(new BiddingOptions
        {
            Collector = new CollectorSettings
            {
                MinimumIntervalSeconds = -1,
                GlobalQpsLimit = 5
            }
        });

        Assert.Throws<ArgumentException>(() => new BiddingRateLimiter(options));
    }

    [Fact]
    public void Constructor_throws_on_zero_or_negative_global_qps()
    {
        var options = Options.Create(new BiddingOptions
        {
            Collector = new CollectorSettings
            {
                MinimumIntervalSeconds = 2,
                GlobalQpsLimit = 0
            }
        });

        Assert.Throws<ArgumentException>(() => new BiddingRateLimiter(options));
    }

    [Fact]
    public async Task WaitAsync_throws_on_null_or_empty_platform_id()
    {
        var limiter = CreateLimiter();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await limiter.WaitAsync(null!, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await limiter.WaitAsync("", CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await limiter.WaitAsync("   ", CancellationToken.None));
    }
}
