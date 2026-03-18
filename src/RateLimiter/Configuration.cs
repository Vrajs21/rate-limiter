namespace RateLimiter;

// ─────────────────────────────────────────────────────────────────────────────
// CONFIGURATION OBJECTS
//
// These are the "blueprints" you hand to the service at startup.
// They describe WHAT rules apply to WHICH endpoints.
// They do NOT hold any runtime state (no counters, no timestamps).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Blueprint for one API endpoint.
/// One endpoint can have multiple rules (multi-period constraints).
///
/// Example:
///   new ApiConfig("/login",
///       new RateLimitRule(5,  TimeSpan.FromMinutes(1)),   // 5/min
///       new RateLimitRule(20, TimeSpan.FromHours(1)))      // 20/hour
/// </summary>
public sealed class ApiConfig
{
    public string                   Endpoint { get; }
    public IReadOnlyList<RateLimitRule> Rules { get; }

    public ApiConfig(string endpoint, params RateLimitRule[] rules)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Endpoint cannot be blank.", nameof(endpoint));
        if (rules.Length == 0)
            throw new ArgumentException("At least one rule required.", nameof(rules));

        Endpoint = endpoint;
        Rules    = rules;
    }
}

/// <summary>
/// Blueprint for a combined/aggregate limit across multiple endpoints.
/// All endpoints in the group share the same quota pool.
///
/// Example:
///   new CombinedGroupConfig("transfer-group",
///       new[] { "/upload", "/download" },
///       new RateLimitRule(50, TimeSpan.FromHours(1)))   // 50 combined/hour
/// </summary>
public sealed class CombinedGroupConfig
{
    public string                      GroupName { get; }
    public IReadOnlySet<string>        Endpoints { get; }
    public IReadOnlyList<RateLimitRule> Rules    { get; }

    public CombinedGroupConfig(string groupName, IEnumerable<string> endpoints, params RateLimitRule[] rules)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            throw new ArgumentException("GroupName cannot be blank.", nameof(groupName));
        if (rules.Length == 0)
            throw new ArgumentException("At least one rule required.", nameof(rules));

        GroupName = groupName;
        Endpoints = new HashSet<string>(endpoints, StringComparer.OrdinalIgnoreCase);
        Rules     = rules;
    }
}
