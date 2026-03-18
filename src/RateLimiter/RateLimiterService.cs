using System.Collections.Concurrent;

namespace RateLimiter;

// ─────────────────────────────────────────────────────────────────────────────
// RATE LIMITER SERVICE  — the single public entry point
//
// Responsibilities:
//   1. Accept ApiConfig and CombinedGroupConfig at startup
//   2. Use StrategyFactory to assign an algorithm per endpoint
//   3. Lazily create per-client limiters on first request
//   4. Orchestrate the three-step check on every IsAllowed() call:
//        Step A — check combined groups (fail fast on shared quota)
//        Step B — check individual endpoint rules
//        Step C — if both pass, the request is allowed
//
// Thread-safety: ConcurrentDictionary for the client→limiter maps.
//                Locking is inside each strategy, not at service level.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RateLimiterService
{
    private readonly Dictionary<string, ApiConfig>           _apiConfigs;
    private readonly List<CombinedGroupConfig>               _groupConfigs;
    private readonly StrategyFactory                         _factory;

    // clientId → endpoint → EndpointLimiter
    private readonly ConcurrentDictionary<string,
        ConcurrentDictionary<string, EndpointLimiter>> _endpointLimiters = new();

    // clientId → groupName → CombinedGroupLimiter
    private readonly ConcurrentDictionary<string,
        ConcurrentDictionary<string, CombinedGroupLimiter>> _groupLimiters = new();

    public RateLimiterService(
        IEnumerable<ApiConfig>          apiConfigs,
        IEnumerable<CombinedGroupConfig>? groupConfigs = null,
        StrategyFactory?                  factory       = null)
    {
        _apiConfigs   = apiConfigs.ToDictionary(c => c.Endpoint, StringComparer.OrdinalIgnoreCase);
        _groupConfigs = groupConfigs?.ToList() ?? [];
        _factory      = factory ?? new StrategyFactory();
    }

    /// <summary>
    /// Expose the factory so callers can override strategies at runtime.
    /// e.g. service.Factory.SetStrategy("/download", StrategyType.TokenBucket);
    /// </summary>
    public StrategyFactory Factory => _factory;

    // ── Main decision method ─────────────────────────────────────────────────

    public RateLimitResult IsAllowed(string clientId, string endpoint)
        => IsAllowed(new RateLimitRequest(clientId, endpoint));

    public RateLimitResult IsAllowed(RateLimitRequest request)
    {
        // ── Step A: Combined group checks ────────────────────────────────────
        var applicableGroups = _groupConfigs
            .Where(g => g.Endpoints.Contains(request.Endpoint))
            .Select(g => GetOrCreateGroupLimiter(request.ClientId, g))
            .ToList();

        foreach (var groupLimiter in applicableGroups)
        {
            string? denial = groupLimiter.Check();
            if (denial != null)
                return RateLimitResult.Deny(denial);
        }

        // ── Step B: Individual endpoint check ────────────────────────────────
        if (_apiConfigs.TryGetValue(request.Endpoint, out var config))
        {
            var limiter = GetOrCreateEndpointLimiter(request.ClientId, config);
            var result  = limiter.TryAcquire();
            if (!result.IsAllowed)
                return result;
        }

        return RateLimitResult.Allow();
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns which algorithm is assigned to an endpoint (without creating state).
    /// </summary>
    public string GetAlgorithmName(string endpoint)
        => _factory.Resolve(endpoint).ToString();

    // ── Private factory helpers ───────────────────────────────────────────────

    private EndpointLimiter GetOrCreateEndpointLimiter(string clientId, ApiConfig config)
    {
        var clientMap = _endpointLimiters.GetOrAdd(clientId, _ => new());
        return clientMap.GetOrAdd(config.Endpoint, _ =>
            new EndpointLimiter(config, _factory.Create(config.Endpoint)));
    }

    private CombinedGroupLimiter GetOrCreateGroupLimiter(string clientId, CombinedGroupConfig config)
    {
        var clientMap = _groupLimiters.GetOrAdd(clientId, _ => new());
        return clientMap.GetOrAdd(config.GroupName, _ =>
        {
            // Combined groups use SlidingWindow by default (shared quotas should be strict).
            // You can change this by adding the group name to StrategyFactory overrides.
            var strategy = _factory.Create(config.GroupName);
            return new CombinedGroupLimiter(config, strategy);
        });
    }
}
