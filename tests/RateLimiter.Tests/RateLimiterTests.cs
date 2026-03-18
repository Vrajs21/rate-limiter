using RateLimiter;
using Xunit;

namespace RateLimiter.Tests;

// ════════════════════════════════════════════════════════════════════════════
// VALUE OBJECT TESTS
// ════════════════════════════════════════════════════════════════════════════

public class RateLimitRuleTests
{
    [Fact]
    public void Two_identical_rules_are_equal()
    {
        var r1 = new RateLimitRule(10, TimeSpan.FromMinutes(1));
        var r2 = new RateLimitRule(10, TimeSpan.FromMinutes(1));
        Assert.Equal(r1, r2);
    }

    [Fact]
    public void Different_rules_are_not_equal()
    {
        var r1 = new RateLimitRule(10, TimeSpan.FromMinutes(1));
        var r2 = new RateLimitRule(20, TimeSpan.FromMinutes(1));
        Assert.NotEqual(r1, r2);
    }
}

public class RateLimitResultTests
{
    [Fact]
    public void Allow_returns_IsAllowed_true()
        => Assert.True(RateLimitResult.Allow().IsAllowed);

    [Fact]
    public void Deny_returns_IsAllowed_false_with_reason()
    {
        var result = RateLimitResult.Deny("limit exceeded");
        Assert.False(result.IsAllowed);
        Assert.Equal("limit exceeded", result.RejectionReason);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// STRATEGY TESTS
// ════════════════════════════════════════════════════════════════════════════

public class SlidingWindowStrategyTests
{
    [Fact]
    public void Allows_up_to_max_requests()
    {
        var strategy = new SlidingWindowStrategy();
        var rule     = new RateLimitRule(3, TimeSpan.FromSeconds(10));

        Assert.True(strategy.TryAcquire(rule));
        Assert.True(strategy.TryAcquire(rule));
        Assert.True(strategy.TryAcquire(rule));
        Assert.False(strategy.TryAcquire(rule)); // 4th is denied
    }

    [Fact]
    public void Has_correct_algorithm_name()
        => Assert.Equal("SlidingWindow", new SlidingWindowStrategy().AlgorithmName);

    [Fact]
    public void Independent_rules_tracked_separately()
    {
        var strategy = new SlidingWindowStrategy();
        var rule1    = new RateLimitRule(2, TimeSpan.FromSeconds(10));
        var rule2    = new RateLimitRule(5, TimeSpan.FromSeconds(10));

        // Exhaust rule1
        strategy.TryAcquire(rule1);
        strategy.TryAcquire(rule1);
        Assert.False(strategy.TryAcquire(rule1)); // rule1 exhausted

        // rule2 still has capacity
        Assert.True(strategy.TryAcquire(rule2));
    }
}

public class TokenBucketStrategyTests
{
    [Fact]
    public void Allows_burst_up_to_capacity()
    {
        var strategy = new TokenBucketStrategy();
        var rule     = new RateLimitRule(5, TimeSpan.FromSeconds(10));

        // All 5 should pass immediately (bucket starts full)
        for (int i = 0; i < 5; i++)
            Assert.True(strategy.TryAcquire(rule));

        Assert.False(strategy.TryAcquire(rule)); // bucket empty
    }

    [Fact]
    public void Has_correct_algorithm_name()
        => Assert.Equal("TokenBucket", new TokenBucketStrategy().AlgorithmName);

    [Fact]
    public void Bucket_refills_over_time()
    {
        var strategy = new TokenBucketStrategy();
        // Very fast refill: 10 tokens per 100ms
        var rule = new RateLimitRule(10, TimeSpan.FromMilliseconds(100));

        // Drain the bucket
        for (int i = 0; i < 10; i++) strategy.TryAcquire(rule);
        Assert.False(strategy.TryAcquire(rule)); // empty

        // Wait for refill
        Thread.Sleep(150);
        Assert.True(strategy.TryAcquire(rule)); // should have refilled
    }
}

// ════════════════════════════════════════════════════════════════════════════
// STRATEGY FACTORY TESTS
// ════════════════════════════════════════════════════════════════════════════

public class StrategyFactoryTests
{
    [Theory]
    [InlineData("/login")]
    [InlineData("/register")]
    [InlineData("/otp")]
    public void Security_sensitive_endpoints_get_SlidingWindow(string endpoint)
    {
        var factory = new StrategyFactory();
        Assert.Equal(StrategyType.SlidingWindow, factory.Resolve(endpoint));
    }

    [Fact]
    public void Unknown_endpoint_gets_TokenBucket_by_default()
    {
        var factory = new StrategyFactory();
        Assert.Equal(StrategyType.TokenBucket, factory.Resolve("/search"));
    }

    [Fact]
    public void Explicit_override_wins_over_heuristic()
    {
        var factory = new StrategyFactory();
        factory.SetStrategy("/login", StrategyType.TokenBucket); // override security rule
        Assert.Equal(StrategyType.TokenBucket, factory.Resolve("/login"));
    }

    [Fact]
    public void Clear_override_reverts_to_heuristic()
    {
        var factory = new StrategyFactory();
        factory.SetStrategy("/login", StrategyType.TokenBucket);
        factory.ClearOverride("/login");
        Assert.Equal(StrategyType.SlidingWindow, factory.Resolve("/login")); // back to security default
    }

    [Fact]
    public void Create_returns_correct_concrete_type()
    {
        var factory = new StrategyFactory();
        factory.SetStrategy("/test", StrategyType.SlidingWindow);
        Assert.IsType<SlidingWindowStrategy>(factory.Create("/test"));

        factory.SetStrategy("/test", StrategyType.TokenBucket);
        Assert.IsType<TokenBucketStrategy>(factory.Create("/test"));
    }
}

// ════════════════════════════════════════════════════════════════════════════
// RATE LIMITER SERVICE INTEGRATION TESTS
// ════════════════════════════════════════════════════════════════════════════

public class RateLimiterServiceTests
{
    private static RateLimiterService BuildService(StrategyFactory? factory = null)
    {
        return new RateLimiterService(
            apiConfigs: new[]
            {
                new ApiConfig("/login",
                    new RateLimitRule(3, TimeSpan.FromSeconds(30))),
                new ApiConfig("/search",
                    new RateLimitRule(5, TimeSpan.FromSeconds(30))),
                new ApiConfig("/upload",
                    new RateLimitRule(4, TimeSpan.FromMinutes(1))),
                new ApiConfig("/download",
                    new RateLimitRule(4, TimeSpan.FromMinutes(1))),
            },
            groupConfigs: new[]
            {
                new CombinedGroupConfig(
                    "transfer",
                    new[] { "/upload", "/download" },
                    new RateLimitRule(5, TimeSpan.FromMinutes(1)))
            },
            factory: factory
        );
    }

    // ── Granular limits ───────────────────────────────────────────────────

    [Fact]
    public void Endpoint_allows_up_to_its_limit()
    {
        var svc = BuildService();
        Assert.True(svc.IsAllowed("user1", "/login").IsAllowed);
        Assert.True(svc.IsAllowed("user1", "/login").IsAllowed);
        Assert.True(svc.IsAllowed("user1", "/login").IsAllowed);
        Assert.False(svc.IsAllowed("user1", "/login").IsAllowed); // 4th denied
    }

    [Fact]
    public void Denial_includes_descriptive_reason()
    {
        var svc = BuildService();
        for (int i = 0; i < 3; i++) svc.IsAllowed("user1", "/login");
        var denied = svc.IsAllowed("user1", "/login");
        Assert.False(denied.IsAllowed);
        Assert.Contains("/login", denied.RejectionReason);
    }

    // ── Client isolation ──────────────────────────────────────────────────

    [Fact]
    public void Different_clients_have_independent_counters()
    {
        var svc = BuildService();
        // Exhaust user1
        for (int i = 0; i < 3; i++) svc.IsAllowed("user1", "/login");
        Assert.False(svc.IsAllowed("user1", "/login").IsAllowed);

        // user2 still fresh
        Assert.True(svc.IsAllowed("user2", "/login").IsAllowed);
    }

    // ── Combined group ────────────────────────────────────────────────────

    [Fact]
    public void Combined_group_is_enforced_across_endpoints()
    {
        var svc = BuildService();
        // 3 uploads + 2 downloads = 5 total (group limit)
        for (int i = 0; i < 3; i++) Assert.True(svc.IsAllowed("charlie", "/upload").IsAllowed);
        for (int i = 0; i < 2; i++) Assert.True(svc.IsAllowed("charlie", "/download").IsAllowed);

        // 6th request to either endpoint should be denied by group
        Assert.False(svc.IsAllowed("charlie", "/upload").IsAllowed);
        Assert.False(svc.IsAllowed("charlie", "/download").IsAllowed);
    }

    // ── Unknown endpoint ──────────────────────────────────────────────────

    [Fact]
    public void Unknown_endpoint_is_allowed_by_default()
        => Assert.True(BuildService().IsAllowed("user1", "/unknown").IsAllowed);

    // ── Runtime strategy swap ─────────────────────────────────────────────

    [Fact]
    public void Runtime_strategy_swap_reflected_in_diagnostics()
    {
        var factory = new StrategyFactory();
        var svc     = BuildService(factory);

        Assert.Equal("TokenBucket", svc.GetAlgorithmName("/search")); // default
        factory.SetStrategy("/search", StrategyType.SlidingWindow);
        Assert.Equal("SlidingWindow", svc.GetAlgorithmName("/search")); // swapped
    }

    // ── Thread safety ─────────────────────────────────────────────────────

    [Fact]
    public void Concurrent_requests_never_exceed_limit()
    {
        var svc     = BuildService();
        int allowed = 0;
        var tasks   = Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() =>
            {
                if (svc.IsAllowed("stress", "/login").IsAllowed)
                    Interlocked.Increment(ref allowed);
            }))
            .ToArray();
        Task.WaitAll(tasks);
        Assert.True(allowed <= 3, $"Expected ≤3, got {allowed}");
    }

    // ── Multi-period ──────────────────────────────────────────────────────

    [Fact]
    public void Multi_period_all_rules_must_pass()
    {
        // /login has 3/30s AND 5/min — first rule is the tighter one
        var svc = BuildService();
        for (int i = 0; i < 3; i++) svc.IsAllowed("mp", "/login");
        // Even though 5/min allows more, 3/30s blocks it
        Assert.False(svc.IsAllowed("mp", "/login").IsAllowed);
    }
}
