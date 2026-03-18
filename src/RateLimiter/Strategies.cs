using System.Collections.Concurrent;

namespace RateLimiter;

// ─────────────────────────────────────────────────────────────────────────────
// STRATEGY PATTERN
//
// IRateLimitStrategy  →  the contract every algorithm must fulfil
// SlidingWindowStrategy  →  uses a timestamp queue, no bursting
// TokenBucketStrategy    →  uses tokens + lazy refill, allows bursting
//
// EndpointLimiter holds ONE IRateLimitStrategy.
// It never knows which concrete type it has — only calls TryAcquire().
// Swapping the algorithm = swapping the strategy object. Nothing else changes.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The Strategy interface. Any algorithm that can enforce a RateLimitRule
/// must implement this single method.
///
/// TryAcquire returns true  → request is within limit (slot consumed).
/// TryAcquire returns false → request is over limit  (nothing consumed).
/// </summary>
public interface IRateLimitStrategy
{
    bool TryAcquire(RateLimitRule rule);

    /// <summary>Human-readable name — useful for logging and diagnostics.</summary>
    string AlgorithmName { get; }
}

// ─────────────────────────────────────────────────────────────────────────────
// CONCRETE STRATEGY 1 — Sliding Window
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Sliding Window algorithm.
///
/// State per rule:
///   • Queue&lt;long&gt; _timestamps — ticks of every request in the current window.
///
/// On TryAcquire:
///   1. Evict timestamps older than (now − window).
///   2. If count &lt; maxRequests → enqueue now-tick, return true.
///   3. Otherwise → return false (queue unchanged).
///
/// Memory: O(maxRequests) per rule.
/// Latency: O(evicted items) amortized → O(1) in steady state.
/// Bursting: NOT allowed. Every window is evaluated strictly.
/// </summary>
public sealed class SlidingWindowStrategy : IRateLimitStrategy
{
    // Pair each rule with its queue and lock object in a single dictionary
    private readonly ConcurrentDictionary<RateLimitRule, (Queue<long> queue, object lockObj)> _buckets = new();

    public string AlgorithmName => "SlidingWindow";

    public bool TryAcquire(RateLimitRule rule)
    {
        var bucket = _buckets.GetOrAdd(rule, _ => (new Queue<long>(), new object()));
        lock (bucket.lockObj)
        {
            long now     = DateTime.UtcNow.Ticks;
            long cutoff  = now - rule.Window.Ticks;

            // Evict stale timestamps
            while (bucket.queue.Count > 0 && bucket.queue.Peek() <= cutoff)
                bucket.queue.Dequeue();

            if (bucket.queue.Count < rule.MaxRequests)
            {
                bucket.queue.Enqueue(now);
                return true;          // ✅ allowed
            }

            return false;             // ❌ denied
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CONCRETE STRATEGY 2 — Token Bucket
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Token Bucket algorithm.
///
/// State per rule:
///   • double _tokens      — current token count (can be fractional).
///   • long   _lastTick    — when we last calculated a refill.
///
/// On TryAcquire (lazy refill — no background thread needed):
///   1. elapsed   = now − lastTick
///   2. newTokens = elapsed × (maxRequests / window)
///   3. tokens    = min(tokens + newTokens, capacity)
///   4. If tokens ≥ 1 → tokens−=1, return true.
///   5. Otherwise → return false.
///
/// Memory: O(1) per rule — just two numbers, always.
/// Bursting: ALLOWED up to capacity. Quiet periods accumulate tokens.
/// </summary>
public sealed class TokenBucketStrategy : IRateLimitStrategy
{
    // Pair each rule with its state and lock object in a single dictionary
    private readonly ConcurrentDictionary<RateLimitRule, (BucketState state, object lockObj)> _buckets = new();

    public string AlgorithmName => "TokenBucket";

    public bool TryAcquire(RateLimitRule rule)
    {
        var bucket = _buckets.AddOrUpdate(
            rule,
            _ => (new BucketState(rule.MaxRequests, DateTime.UtcNow.Ticks), new object()),
            (key, old) => (old.state, old.lockObj)
        );
        lock (bucket.lockObj)
        {
            var state = bucket.state;
            long   now             = DateTime.UtcNow.Ticks;
            double elapsedSeconds  = TimeSpan.FromTicks(now - state.LastTick).TotalSeconds;
            double refillRate      = rule.MaxRequests / rule.Window.TotalSeconds;
            double newTokens       = elapsedSeconds * refillRate;

            double tokens = Math.Min(state.Tokens + newTokens, rule.MaxRequests);

            if (tokens >= 1.0)
            {
                _buckets[rule] = (new BucketState(tokens - 1.0, now), bucket.lockObj);
                return true;          // ✅ allowed
            }

            // Update lastTick even on deny so refill continues correctly.
            _buckets[rule] = (new BucketState(tokens, now), bucket.lockObj);
            return false;             // ❌ denied
        }
    }

    private record BucketState(double Tokens, long LastTick);
}
