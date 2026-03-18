# Rate Limiter — Strategy Pattern (C# / .NET 8)

## Project structure

```
RateLimiterFull/
├── RateLimiter.sln
├── src/
│   └── RateLimiter/
│       ├── RateLimiter.csproj
│       ├── ValueObjects.cs       ← RateLimitRule, RateLimitResult, RateLimitRequest
│       ├── Strategies.cs         ← IRateLimitStrategy, SlidingWindowStrategy, TokenBucketStrategy
│       ├── StrategyFactory.cs    ← Runtime algorithm selection
│       ├── Configuration.cs      ← ApiConfig, CombinedGroupConfig
│       ├── Limiters.cs           ← EndpointLimiter, CombinedGroupLimiter
│       ├── RateLimiterService.cs ← Public entry point
│       └── Program.cs            ← Runnable demo
└── tests/
    └── RateLimiter.Tests/
        ├── RateLimiter.Tests.csproj
        └── RateLimiterTests.cs   ← All xUnit tests
```

---

## Prerequisites

Install .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0

Verify:
```bash
dotnet --version
# should print 8.x.x
```

---

## Run the demo (VS Code terminal)

```bash
# 1. Open the folder in VS Code
cd RateLimiterFull

# 2. Restore packages
dotnet restore

# 3. Run the demo app
dotnet run --project src/RateLimiter/RateLimiter.csproj
```

---

## Run the tests

```bash
# Run all tests and see results
dotnet test

# Run with detailed output
dotnet test --logger "console;verbosity=detailed"

# Run a single test by name
dotnet test --filter "Concurrent_requests_never_exceed_limit"
```

---

## Open in VS Code with full IntelliSense

1. Open VS Code
2. File → Open Folder → select `RateLimiterFull`
3. Install the **C# Dev Kit** extension (Microsoft) if prompted
4. VS Code will auto-detect the solution and restore packages

### Recommended extensions
- `ms-dotnettools.csharp`        — C# language support
- `ms-dotnettools.csdevkit`      — Solution explorer, test runner
- `formulahendry.dotnet-test-explorer` — Visual test runner sidebar

---

## Key design decisions (interview talking points)

| Decision | Why |
|---|---|
| `IRateLimitStrategy` interface | Decouples algorithm from the objects that use it (Strategy Pattern) |
| `StrategyFactory` | Single place for runtime algorithm selection — open/closed principle |
| `record` for value objects | Value equality, immutable, no boilerplate |
| `ConcurrentDictionary` | Lock-free reads for existing keys, thread-safe writes |
| Lazy refill in TokenBucket | No background thread needed — compute at request time |
| `SecuritySensitive` heuristic | Default to safe (SlidingWindow) for auth endpoints |

---

## Switch algorithms at runtime

```csharp
var factory = new StrategyFactory();
var service = new RateLimiterService(configs, factory: factory);

// Switch /search from TokenBucket to SlidingWindow at runtime
service.Factory.SetStrategy("/search", StrategyType.SlidingWindow);

// Revert back
service.Factory.ClearOverride("/search");
```
