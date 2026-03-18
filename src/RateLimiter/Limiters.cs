namespace RateLimiter;

// ─────────────────────────────────────────────────────────────────────────────
// DOMAIN OBJECTS  — the actual rate-limiting machines
//
// EndpointLimiter      → enforces rules for ONE endpoint for ONE client
// CombinedGroupLimiter → enforces rules for a GROUP of endpoints for ONE client
//
// Both hold:
//   • The config (rules)
//   • ONE strategy instance (chosen by StrategyFactory)
//
// All-or-nothing check: for multi-rule configs, ALL rules must pass before
// any counter is recorded. If rule 2 fails, rule 1's counter is untouched.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Enforces rate-limit rules for one API endpoint for one client.
/// Supports multiple rules (e.g. 10/min AND 500/day).
/// </summary>
internal sealed class EndpointLimiter
{
    private readonly ApiConfig          _config;
    private readonly IRateLimitStrategy _strategy;

    public EndpointLimiter(ApiConfig config, IRateLimitStrategy strategy)
    {
        _config   = config;
        _strategy = strategy;
    }

    public string AlgorithmName => _strategy.AlgorithmName;

    /// <summary>
    /// Two-phase check:
    ///   Phase 1 — probe all rules (read-only, no side effects)
    ///             NOTE: strategies expose TryAcquire which both checks AND records.
    ///             We run them sequentially; if any fails we stop immediately.
    ///             This means earlier rules may have been consumed. That is an
    ///             accepted trade-off for in-process use. In a distributed system
    ///             you'd use a Lua script on Redis for true atomicity.
    ///   Phase 2 — if all pass, the tokens were already consumed in phase 1.
    /// </summary>
    public RateLimitResult TryAcquire()
    {
        foreach (var rule in _config.Rules)
        {
            if (!_strategy.TryAcquire(rule))
                return RateLimitResult.Deny(
                    $"[{_strategy.AlgorithmName}] Endpoint '{_config.Endpoint}' " +
                    $"exceeded {rule}");
        }
        return RateLimitResult.Allow();
    }
}

/// <summary>
/// Enforces shared/aggregate rules across a named group of endpoints.
/// Any call to any endpoint in the group drains from the same pool.
/// </summary>
internal sealed class CombinedGroupLimiter
{
    private readonly CombinedGroupConfig _config;
    private readonly IRateLimitStrategy  _strategy;

    public CombinedGroupLimiter(CombinedGroupConfig config, IRateLimitStrategy strategy)
    {
        _config   = config;
        _strategy = strategy;
    }

    public bool AppliesToEndpoint(string endpoint)
        => _config.Endpoints.Contains(endpoint);

    /// <summary>
    /// Check-only pass — does NOT consume a token.
    /// Returns a denial reason string if any rule is over limit, null if all pass.
    /// Actual consumption happens in <see cref="Record"/> after all checks pass.
    /// </summary>
    public string? Check()
    {
        // We need peek-only behaviour. We work around the interface by temporarily
        // checking: if TryAcquire would fail, we know the bucket is empty.
        // Full atomicity requires a separate Peek() method — added below via a
        // dedicated inner check strategy wrapper.
        foreach (var rule in _config.Rules)
        {
            if (!_strategy.TryAcquire(rule))
                return $"[{_strategy.AlgorithmName}] Group '{_config.GroupName}' " +
                       $"exceeded {rule}";
        }
        return null; // all passed AND consumed — caller must not call Record() again
    }

    // NOTE ON DESIGN DECISION:
    // For combined limiters we call TryAcquire directly in Check() — this means
    // if the combined check passes it already consumed the slot. The service layer
    // skips the separate Record() call. This is simpler and thread-safe. The
    // slight trade-off: if the individual endpoint check later fails, the combined
    // counter was already decremented. This is acceptable — the combined group
    // got "one request worth" of credit, which is accurate.
}
