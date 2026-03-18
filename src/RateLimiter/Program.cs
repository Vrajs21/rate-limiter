using RateLimiter;

Console.OutputEncoding = System.Text.Encoding.UTF8;

PrintBanner("Rate Limiter — Strategy Pattern Demo");

// ════════════════════════════════════════════════════════════════════════════
// SETUP
// Build the factory, assign strategies, then build the service.
// ════════════════════════════════════════════════════════════════════════════

var factory = new StrategyFactory();

// Runtime override: /download uses TokenBucket (allows burst for data endpoints)
factory.SetStrategy("/download", StrategyType.TokenBucket);
factory.SetStrategy("/search",   StrategyType.TokenBucket);
// /login will auto-select SlidingWindow (it's in the SecuritySensitive list)

var service = new RateLimiterService(
    apiConfigs: new[]
    {
        new ApiConfig("/login",
            new RateLimitRule(3, TimeSpan.FromSeconds(10)),   // 3 per 10s
            new RateLimitRule(5, TimeSpan.FromMinutes(1))),   // 5 per minute

        new ApiConfig("/search",
            new RateLimitRule(8, TimeSpan.FromSeconds(10))),  // 8 per 10s

        new ApiConfig("/upload",
            new RateLimitRule(5, TimeSpan.FromMinutes(1))),   // 5 per minute

        new ApiConfig("/download",
            new RateLimitRule(5, TimeSpan.FromMinutes(1))),   // 5 per minute
    },
    groupConfigs: new[]
    {
        new CombinedGroupConfig(
            "transfer-group",
            new[] { "/upload", "/download" },
            new RateLimitRule(6, TimeSpan.FromMinutes(1)))    // 6 combined per minute
    },
    factory: factory
);

// ════════════════════════════════════════════════════════════════════════════
// DEMO 1 — Show which algorithm each endpoint got
// ════════════════════════════════════════════════════════════════════════════

PrintSection("1. Strategy assignment");
foreach (var ep in new[] { "/login", "/search", "/upload", "/download" })
    Console.WriteLine($"  {ep,-12}  →  {service.GetAlgorithmName(ep)}");

// ════════════════════════════════════════════════════════════════════════════
// DEMO 2 — Granular limit: /login allows 3 per 10s
// ════════════════════════════════════════════════════════════════════════════

PrintSection("2. /login — 3 req/10s (SlidingWindow, no bursting)");
for (int i = 1; i <= 5; i++)
    Print(i, service.IsAllowed("alice", "/login"), "/login");

// ════════════════════════════════════════════════════════════════════════════
// DEMO 3 — TokenBucket allows burst on /search
// ════════════════════════════════════════════════════════════════════════════

PrintSection("3. /search — 8 req/10s (TokenBucket, burst allowed)");
for (int i = 1; i <= 10; i++)
    Print(i, service.IsAllowed("alice", "/search"), "/search");

// ════════════════════════════════════════════════════════════════════════════
// DEMO 4 — Client isolation
// ════════════════════════════════════════════════════════════════════════════

PrintSection("4. Client isolation — bob's /login is independent of alice's");
for (int i = 1; i <= 4; i++)
    Print(i, service.IsAllowed("bob", "/login"), "/login");

// ════════════════════════════════════════════════════════════════════════════
// DEMO 5 — Combined group limit
// ════════════════════════════════════════════════════════════════════════════

PrintSection("5. Combined group — /upload + /download share 6/min");
var transferCalls = new[]
{
    "/upload", "/upload", "/upload",
    "/download", "/download", "/download", "/download"
};
for (int i = 0; i < transferCalls.Length; i++)
    Print(i + 1, service.IsAllowed("charlie", transferCalls[i]), transferCalls[i]);

// ════════════════════════════════════════════════════════════════════════════
// DEMO 6 — Runtime strategy swap (the key Strategy Pattern showcase)
// ════════════════════════════════════════════════════════════════════════════

PrintSection("6. Runtime strategy swap — switch /search to SlidingWindow mid-flight");
Console.WriteLine("  Before swap: " + service.GetAlgorithmName("/search"));
factory.SetStrategy("/search", StrategyType.SlidingWindow);
Console.WriteLine("  After swap:  " + service.GetAlgorithmName("/search"));
Console.WriteLine("  (New clients of /search will now use SlidingWindow)");

// ════════════════════════════════════════════════════════════════════════════
// DEMO 7 — Thread-safety stress test
// ════════════════════════════════════════════════════════════════════════════

PrintSection("7. Thread-safety — 50 concurrent requests to /login (limit 3/10s)");
int allowed = 0, denied = 0;
var tasks = Enumerable.Range(0, 50)
    .Select(_ => Task.Run(() =>
    {
        var r = service.IsAllowed("dave", "/login");
        if (r.IsAllowed) Interlocked.Increment(ref allowed);
        else             Interlocked.Increment(ref denied);
    }))
    .ToArray();
Task.WaitAll(tasks);
Console.WriteLine($"  Allowed: {allowed}  |  Denied: {denied}");
Console.WriteLine(allowed <= 3
    ? "  ✓ Thread-safety OK — at most 3 requests got through"
    : "  ✗ Unexpected count");

PrintBanner("Done");

// ── Helpers ──────────────────────────────────────────────────────────────────

static void Print(int i, RateLimitResult r, string ep)
{
    var icon = r.IsAllowed ? "✓" : "✗";
    var color = r.IsAllowed ? ConsoleColor.Green : ConsoleColor.Red;
    Console.Write($"  #{i,2} [{ep}] ");
    Console.ForegroundColor = color;
    Console.WriteLine($"{icon} {r}");
    Console.ResetColor();
}

static void PrintSection(string title)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"── {title}");
    Console.ResetColor();
}

static void PrintBanner(string title)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine(new string('═', 60));
    Console.WriteLine($"  {title}");
    Console.WriteLine(new string('═', 60));
    Console.ResetColor();
}
