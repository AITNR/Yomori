using Yomori.Helpers;

var tests = new (string Name, Action Run)[]
{
    ("filters supported images and sorts by file name", TestFilteringAndSorting),
    ("reuses unchanged catalog and refreshes after add/remove", TestCatalogInvalidation),
    ("random selection keeps avoid-last contract", TestRandomSelection),
    ("missing directory behaves as an empty gallery", TestMissingDirectory),
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

Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} test groups passed.");
if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

return 0;

static void TestFilteringAndSorting()
{
    using var temp = new TempDirectory();
    File.WriteAllBytes(Path.Combine(temp.Path, "z.PNG"), [1]);
    File.WriteAllBytes(Path.Combine(temp.Path, "a.jpg"), [1]);
    File.WriteAllBytes(Path.Combine(temp.Path, "m.webp"), [1]);
    File.WriteAllBytes(Path.Combine(temp.Path, "ignored.txt"), [1]);
    File.WriteAllBytes(Path.Combine(temp.Path, "no-extension"), [1]);

    var images = HomeBannerPicker.EnumerateImages(temp.Path);
    AssertSequence(
        images.Select(Path.GetFileName),
        ["a.jpg", "m.webp", "z.PNG"]);
}

static void TestCatalogInvalidation()
{
    using var temp = new TempDirectory();
    var firstFile = Path.Combine(temp.Path, "first.png");
    File.WriteAllBytes(firstFile, [1]);
    var nextDirectoryTimestamp = Directory.GetLastWriteTimeUtc(temp.Path);

    var first = HomeBannerPicker.EnumerateImages(temp.Path);
    var second = HomeBannerPicker.EnumerateImages(temp.Path);
    Assert(first.Count == 1, "initial catalog should contain one image");
    Assert(ReferenceEquals(first, second), "unchanged directory should reuse the cached catalog");

    var addedFile = Path.Combine(temp.Path, "second.jpg");
    File.WriteAllBytes(addedFile, [1]);
    nextDirectoryTimestamp = nextDirectoryTimestamp.AddSeconds(2);
    Directory.SetLastWriteTimeUtc(temp.Path, nextDirectoryTimestamp);
    var afterAdd = HomeBannerPicker.EnumerateImages(temp.Path);
    Assert(afterAdd.Count == 2, "adding an image should refresh the catalog");
    Assert(!ReferenceEquals(first, afterAdd), "changed directory should not reuse the old catalog");

    File.Delete(firstFile);
    nextDirectoryTimestamp = nextDirectoryTimestamp.AddSeconds(2);
    Directory.SetLastWriteTimeUtc(temp.Path, nextDirectoryTimestamp);
    var afterDelete = HomeBannerPicker.EnumerateImages(temp.Path);
    Assert(afterDelete.Count == 1, "deleting an image should refresh the catalog");
    Assert(Path.GetFileName(afterDelete[0]) == "second.jpg", "remaining image should stay listed");
}

static void TestRandomSelection()
{
    using var temp = new TempDirectory();
    var a = Path.Combine(temp.Path, "a.png");
    var b = Path.Combine(temp.Path, "b.png");
    var c = Path.Combine(temp.Path, "c.png");
    File.WriteAllBytes(a, [1]);
    File.WriteAllBytes(b, [1]);
    File.WriteAllBytes(c, [1]);

    for (var seed = 0; seed < 100; seed++)
    {
        var picked = HomeBannerPicker.PickRandom(temp.Path, a, new Random(seed));
        Assert(picked is not null, "gallery with multiple images should return a pick");
        Assert(
            !string.Equals(picked, a, StringComparison.OrdinalIgnoreCase),
            "avoid path should be excluded while other images remain");
    }

    File.Delete(b);
    File.Delete(c);
    BumpDirectoryTimestamp(temp.Path);
    Assert(
        string.Equals(
            HomeBannerPicker.PickRandom(temp.Path, a, new Random(7)),
            a,
            StringComparison.OrdinalIgnoreCase),
        "single remaining image must be returned even when it equals avoid path");
}

static void TestMissingDirectory()
{
    var missing = Path.Combine(Path.GetTempPath(), $"home-banner-missing-{Guid.NewGuid():N}");
    Assert(HomeBannerPicker.EnumerateImages(missing).Count == 0, "missing directory should be empty");
    Assert(HomeBannerPicker.PickRandom(missing) is null, "missing directory should not produce a pick");
}

static void BumpDirectoryTimestamp(string path)
{
    var next = Directory.GetLastWriteTimeUtc(path).AddSeconds(2);
    Directory.SetLastWriteTimeUtc(path, next);
}

static void AssertSequence(IEnumerable<string?> actual, string[] expected)
{
    var values = actual.ToArray();
    Assert(values.Length == expected.Length, $"expected {expected.Length} entries, got {values.Length}");
    for (var i = 0; i < expected.Length; i++)
    {
        Assert(
            string.Equals(values[i], expected[i], StringComparison.OrdinalIgnoreCase),
            $"entry {i}: expected {expected[i]}, got {values[i] ?? "<null>"}");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"home-banner-checks-{Guid.NewGuid():N}");
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
            // 测试清理失败不覆盖实际断言结果。
        }
    }
}
