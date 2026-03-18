namespace RateLimiter;

// ─────────────────────────────────────────────────────────────────────────────
// VALUE OBJECTS  — pure data, immutable, no behaviour
// Think of them as the "language" the system speaks internally.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One constraint: allow at most <see cref="MaxRequests"/> in a rolling
/// <see cref="Window"/>. Value object — immutable record.
/// </summary>
public record RateLimitRule(int MaxRequests, TimeSpan Window)
{
    public override string ToString() =>
        $"{MaxRequests} req / {Window.TotalSeconds}s";
}

/// <summary>
/// The answer the system gives back for every request.
/// IsAllowed=true  → green light.
/// IsAllowed=false → red light, RejectionReason explains why.
/// </summary>
public record RateLimitResult(bool IsAllowed, string? RejectionReason = null)
{
    public static RateLimitResult Allow()               => new(true);
    public static RateLimitResult Deny(string reason)   => new(false, reason);

    public override string ToString() =>
        IsAllowed ? "ALLOWED" : $"DENIED — {RejectionReason}";
}

/// <summary>
/// Carries everything needed to make a rate-limit decision:
/// who is calling (ClientId) and what are they calling (Endpoint).
/// </summary>
public record RateLimitRequest(string ClientId, string Endpoint);
