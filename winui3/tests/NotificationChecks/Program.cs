using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Yomori.Models;
using Yomori.Services;

if (args.Length == 2 && args[0] == "--exit-probe")
{
    var center = new NotificationCenter(args[1], TimeSpan.FromMinutes(1));
    center.Add("exit-probe", "pending at process exit", AppNotificationKinds.Info);
    return 0;
}

var tests = new (string Name, Action Run)[]
{
    ("rapid mutations coalesce into one background write", TestRapidMutationsCoalesce),
    ("explicit flush, 50-item bound, clear and reset semantics", TestExplicitFlushAndLifecycle),
    ("corrupt file remains tolerant and next flush repairs it", TestCorruptFileTolerance),
    ("process exit flushes a still-pending snapshot", TestProcessExitFlush),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.Message}");
        Console.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} notification checks passed");
foreach (var failure in failures)
{
    Console.WriteLine(failure);
}

return failures.Count == 0 ? 0 : 1;

static void TestRapidMutationsCoalesce()
{
    using var temp = new TempDirectory();
    var path = Path.Combine(temp.Path, "notifications.json");
    var center = new NotificationCenter(path, TimeSpan.FromSeconds(1));

    for (var i = 0; i < 20; i++)
    {
        center.Add($"n{i}", $"body {i}", AppNotificationKinds.Info);
    }

    center.MarkRead(center.Items[0]);
    center.MarkRead(center.Items[4]);
    center.Remove(center.Items[10]);
    center.Add("latest", "last body", AppNotificationKinds.Download);

    Equal(20, center.Items.Count, "in-memory collection updates immediately");
    Equal(18, center.UnreadCount, "unread count updates immediately");
    Assert(!File.Exists(path), "rapid mutations do not synchronously create the JSON file");

    WaitUntil(
        () => center.PersistenceWriteCount == 1
              && TryLoad(path, out var rows)
              && rows.Count == 20
              && rows[0].Title == "latest",
        TimeSpan.FromSeconds(6),
        "latest snapshot is eventually written");

    Equal(1, center.PersistenceWriteCount, "all rapid mutations share one bounded background write");

    var loaded = new NotificationCenter(path, TimeSpan.FromMinutes(1));
    loaded.Load();
    Equal(20, loaded.Items.Count, "reopened collection has the merged final size");
    Equal(18, loaded.UnreadCount, "reopened collection preserves merged read state");
    Equal("latest", loaded.Items[0].Title, "newest notification remains first");
    Assert(loaded.Items.All(item => item.Title != "n9"), "removed notification stays removed in the merged snapshot");

    center.ResetOnStartup();
    loaded.ResetOnStartup();
}

static void TestExplicitFlushAndLifecycle()
{
    using var temp = new TempDirectory();
    var path = Path.Combine(temp.Path, "notifications.json");
    var center = new NotificationCenter(path, TimeSpan.FromMinutes(1));

    for (var i = 0; i < 60; i++)
    {
        center.Add($"n{i}", $"body {i}", AppNotificationKinds.Info);
    }

    Equal(NotificationCenter.MaxItems, center.Items.Count, "in-memory collection enforces the 50-item bound");
    center.MarkRead(center.Items[0]);
    center.Remove(center.Items[1]);
    Assert(!File.Exists(path), "long coalescing window leaves pending state only in memory");

    center.Flush();
    Equal(1, center.PersistenceWriteCount, "explicit flush performs the first persistence immediately");
    Assert(File.Exists(path), "explicit flush creates the JSON file synchronously");

    var loaded = new NotificationCenter(path, TimeSpan.FromMinutes(1));
    loaded.Load();
    Equal(49, loaded.Items.Count, "flushed snapshot file contains the bounded final state");
    Equal(48, loaded.UnreadCount, "flushed snapshot preserves unread state");
    Equal("n59", loaded.Items[0].Title, "50-item bound keeps the newest notifications");
    Assert(loaded.Items[0].IsRead, "flushed snapshot preserves per-item read state");

    center.Clear();
    Equal(0, center.Items.Count, "clear updates the live collection immediately");
    Assert(TryLoad(path, out var beforeFlush) && beforeFlush.Count == 49,
        "clear does not synchronously rewrite the previous JSON file");

    center.Flush();
    Assert(TryLoad(path, out var cleared) && cleared.Count == 0,
        "explicit flush persists an empty notification list");

    center.ResetOnStartup();
    Assert(!File.Exists(path), "ResetOnStartup deletes the JSON file immediately");
    center.Flush();
    Assert(!File.Exists(path), "flush after reset does not resurrect the deleted file");

    loaded.ResetOnStartup();
}

static void TestCorruptFileTolerance()
{
    using var temp = new TempDirectory();
    var path = Path.Combine(temp.Path, "notifications.json");
    File.WriteAllText(path, "{not-json", new UTF8Encoding(false));

    var center = new NotificationCenter(path, TimeSpan.FromMinutes(1));
    center.Load();
    Equal(0, center.Items.Count, "corrupt JSON loads as an empty collection");

    center.Add("repaired", "new body", AppNotificationKinds.Warning);
    center.Flush();

    WaitUntil(
        () => TryLoad(path, out var rows) && rows.Count == 1 && rows[0].Title == "repaired",
        TimeSpan.FromSeconds(2),
        "next explicit flush replaces corrupt content");
    center.ResetOnStartup();
}

static void TestProcessExitFlush()
{
    using var temp = new TempDirectory();
    var path = Path.Combine(temp.Path, "notifications.json");
    var executable = Environment.ProcessPath
        ?? throw new InvalidOperationException("test process path is unavailable");
    var startInfo = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add("--exit-probe");
    startInfo.ArgumentList.Add(path);

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("exit probe did not start");
    Assert(process.WaitForExit(10_000), "exit probe finishes promptly");
    Equal(0, process.ExitCode, "exit probe exits cleanly");
    Assert(TryLoad(path, out var rows) && rows.Count == 1 && rows[0].Title == "exit-probe",
        "process exit flushes the latest pending snapshot");
}

static bool TryLoad(string path, out List<AppNotification> rows)
{
    try
    {
        if (!File.Exists(path))
        {
            rows = new List<AppNotification>();
            return false;
        }

        rows = JsonSerializer.Deserialize<List<AppNotification>>(File.ReadAllText(path, Encoding.UTF8),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? new List<AppNotification>();
        return true;
    }
    catch
    {
        rows = new List<AppNotification>();
        return false;
    }
}

static void WaitUntil(Func<bool> predicate, TimeSpan timeout, string message)
{
    var stopwatch = Stopwatch.StartNew();
    while (stopwatch.Elapsed < timeout)
    {
        if (predicate())
        {
            return;
        }

        Thread.Sleep(20);
    }

    Assert(predicate(), message);
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
        throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
    }
}

sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Yomori.NotificationChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; assertion failures remain the signal.
        }
    }
}
