using Yomori.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("TTL and normalized keys", TestTtlAndNormalizedKeys),
    ("Concurrent lookup coalescing", TestConcurrentCoalescing),
    ("Query failure caching", TestFailureCaching),
    ("Bounded capacity eviction", TestCapacityEviction),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.Message}");
        Console.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} test groups passed.");
if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

return 0;

static Task TestTtlAndNormalizedKeys()
{
    var now = 1000L;
    var queries = 0;
    var cache = new CommandlineKeywordQueryCache(
        _ => { queries++; return true; },
        TimeSpan.FromMilliseconds(750),
        capacity: 4,
        clock: () => now);

    Assert(cache.Lookup("Game.exe"), "first lookup returns the query result");
    Equal(1, queries, "first lookup runs exactly one query");

    now += 749;
    Assert(cache.Lookup("  game.EXE  "), "a fresh case-insensitive entry is reused");
    Equal(1, queries, "fresh cache hit does not start another query");

    now += 1;
    Assert(cache.Lookup("GAME.exe"), "the boundary is refreshed after the TTL expires");
    Equal(2, queries, "expired entry runs a new query at the exact TTL boundary");
    return Task.CompletedTask;
}

static async Task TestConcurrentCoalescing()
{
    var queries = 0;
    var started = new ManualResetEventSlim(false);
    var release = new ManualResetEventSlim(false);
    var cache = new CommandlineKeywordQueryCache(
        _ =>
        {
            if (Interlocked.Increment(ref queries) == 1)
            {
                started.Set();
            }

            release.Wait(TimeSpan.FromSeconds(5));
            return true;
        },
        TimeSpan.FromSeconds(5),
        capacity: 4,
        clock: () => 0);

    var lookups = Enumerable.Range(0, 8)
        .Select(_ => Task.Run(() => cache.Lookup("same-keyword")))
        .ToArray();

    Assert(started.Wait(TimeSpan.FromSeconds(2)), "the first query starts");
    await Task.Delay(100);
    Equal(1, Volatile.Read(ref queries), "concurrent callers join one in-flight query");

    release.Set();
    var results = await Task.WhenAll(lookups).WaitAsync(TimeSpan.FromSeconds(5));
    Assert(results.All(result => result), "every joined caller receives the shared result");
    Equal(1, Volatile.Read(ref queries), "query count stays at one after all callers return");
}

static Task TestFailureCaching()
{
    var now = 0L;
    var queries = 0;
    var cache = new CommandlineKeywordQueryCache(
        _ =>
        {
            queries++;
            throw new InvalidOperationException("query failed");
        },
        TimeSpan.FromMilliseconds(500),
        capacity: 4,
        clock: () => now);

    Assert(!cache.Lookup("broken"), "an exception is exposed as a not-found result");
    Assert(!cache.Lookup("BROKEN"), "the failure result is reused within its TTL");
    Equal(1, queries, "exception result is cached briefly instead of retrying every poll");

    now = 500;
    Assert(!cache.Lookup("broken"), "expired failures are retried");
    Equal(2, queries, "failure retry starts exactly one new query");
    return Task.CompletedTask;
}

static Task TestCapacityEviction()
{
    var now = 0L;
    var queries = 0;
    var cache = new CommandlineKeywordQueryCache(
        _ => { queries++; return true; },
        TimeSpan.FromSeconds(1),
        capacity: 2,
        clock: () => now);

    cache.Lookup("a");
    now = 100;
    cache.Lookup("b");
    Equal(2, queries, "two distinct entries are cached");

    now = 200;
    cache.Lookup("c");
    Equal(3, queries, "a third entry evicts the oldest completed entry");

    now = 300;
    cache.Lookup("b");
    cache.Lookup("c");
    Equal(3, queries, "the two newest entries remain cached");

    cache.Lookup("a");
    Equal(4, queries, "the evicted entry must be queried again");
    return Task.CompletedTask;
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message}; expected '{expected}', actual '{actual}'");
    }
}
