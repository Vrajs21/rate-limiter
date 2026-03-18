namespace RateLimiter;

// ─────────────────────────────────────────────────────────────────────────────
// STRATEGY FACTORY
//
// This is where runtime selection happens.
//
// The factory answers ONE question: "Given this endpoint, which algorithm
// should enforce its rules?"
//
// Two ways to assign:
//   1. Explicit override  — caller says "use TokenBucket for /download"
//   2. Rule-based default — factory decides based on endpoint characteristics
//
// In an interview you'd say: "I keep a dictionary of overrides. If the
// endpoint has an override I return that. Otherwise I apply heuristics —
// security-sensitive endpoints (login, otp) get SlidingWindow because bursting
// is dangerous there; data endpoints get TokenBucket because batch usage is
// normal and we want to reward idle clients."
// ─────────────────────────────────────────────────────────────────────────────

public enum StrategyType
{
    SlidingWindow,
    TokenBucket
}

public sealed class StrategyFactory
{
    // Explicit per-endpoint overrides set by the caller.
    private readonly Dictionary<string, StrategyType> _overrides = new(StringComparer.OrdinalIgnoreCase);

    // Endpoints that are security-sensitive → always SlidingWindow, never allow bursts.
    private static readonly HashSet<string> SecuritySensitive = new(StringComparer.OrdinalIgnoreCase)
    {
        "/login", "/logout", "/register", "/otp", "/reset-password", "/verify"
    };

    /// <summary>
    /// Assign a specific algorithm to a specific endpoint at runtime.
    /// Call this before the first request arrives for that endpoint.
    /// </summary>
    public void SetStrategy(string endpoint, StrategyType type)
        => _overrides[endpoint] = type;

    /// <summary>
    /// Remove an override, reverting the endpoint to rule-based selection.
    /// </summary>
    public void ClearOverride(string endpoint)
        => _overrides.Remove(endpoint);

    /// <summary>
    /// Create a fresh strategy instance for the given endpoint.
    ///
    /// Selection order:
    ///   1. Explicit override  (highest priority)
    ///   2. Security heuristic (always SlidingWindow)
    ///   3. Default            (TokenBucket — allows bursting for normal APIs)
    /// </summary>
    public IRateLimitStrategy Create(string endpoint)
    {
        var type = Resolve(endpoint);
        return type switch
        {
            StrategyType.SlidingWindow => new SlidingWindowStrategy(),
            StrategyType.TokenBucket   => new TokenBucketStrategy(),
            _                          => new SlidingWindowStrategy()
        };
    }

    /// <summary>
    /// Returns which StrategyType would be used for an endpoint.
    /// Useful for diagnostics / logging without creating an instance.
    /// </summary>
    public StrategyType Resolve(string endpoint)
    {
        if (_overrides.TryGetValue(endpoint, out var explicitType))
            return explicitType;

        if (SecuritySensitive.Contains(endpoint))
            return StrategyType.SlidingWindow;

        return StrategyType.TokenBucket;   // default
    }
}
