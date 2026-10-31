using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Yomori.Models;
using Yomori.Services;

var tests = new (string Name, Action Run)[]
{
    ("decode_backend defaults, normalization and clone", TestDecodeBackendConfig),
    ("decode policy priority and frozen numeric contract", TestDecodePolicy),
    ("old RDP params ABI has no appended decode field", TestLegacyAbiShape),
    ("version/capability gate falls back without AVC export", TestDecodeApiGate),
    ("connect info passes auto decode defaults", TestConnectInfoDefaults),
    ("decode status reports requested entry, not hardware success", TestDecodeStatus),
    ("frame phase stats aggregate windows and stay allocation-free", TestFramePhaseStats),
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

Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} DecodeChecks passed");
if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine(failure);
    }

    return 1;
}

return 0;

static void TestDecodeBackendConfig()
{
    var missing = JsonSerializer.Deserialize<RdpConfig>("{}")!;
    Equal(DecodeBackends.Auto, missing.DecodeBackend, "missing key defaults to auto");

    var invalid = JsonSerializer.Deserialize<RdpConfig>("""{"decode_backend":"bogus"}""")!;
    Equal(DecodeBackends.Auto, invalid.DecodeBackend, "invalid value normalizes to auto");

    var software = JsonSerializer.Deserialize<RdpConfig>("""{"decode_backend":" SOFTWARE "}""")!;
    Equal(DecodeBackends.Software, software.DecodeBackend, "software is trimmed and normalized");

    var clone = (RdpConfig)software.Clone();
    Equal(DecodeBackends.Software, clone.DecodeBackend, "clone keeps decode_backend");
    clone.DecodeBackend = "unexpected";
    Equal(DecodeBackends.Auto, clone.DecodeBackend, "setter normalizes invalid values");
}

static void TestDecodePolicy()
{
    Equal(0u, RdpDecodePolicy.LegacyNonAvc, "legacy policy value");
    Equal(1u, RdpDecodePolicy.Auto, "auto policy value");
    Equal(2u, RdpDecodePolicy.Software, "software policy value");

    Equal(RdpDecodePolicy.Auto, RdpDecodePolicy.Resolve(null, null, false), "null config is auto");
    Equal(RdpDecodePolicy.Auto, RdpDecodePolicy.Resolve("bogus", "bogus", false), "invalid config is auto");
    Equal(RdpDecodePolicy.Software, RdpDecodePolicy.Resolve("software", "auto", false), "decode software");
    Equal(RdpDecodePolicy.Software, RdpDecodePolicy.Resolve("auto", "software", false), "render software wins");
    Equal(RdpDecodePolicy.Software, RdpDecodePolicy.Resolve("auto", "auto", true), "nod3d wins");
    Equal(RdpDecodePolicy.Software, RdpDecodePolicy.Resolve("software", "auto", true), "both software");
}

static void TestLegacyAbiShape()
{
    var fields = typeof(YcnRdpParams).GetFields(BindingFlags.Instance | BindingFlags.Public);
    var names = string.Join(",", fields.Select(f => f.Name));
    Equal("Host,Port,Username,Domain,Password,DesktopWidth,DesktopHeight,ColorDepth,UseNla,AllowSelfsigned,EnableAudio,UseGfx",
        names, "legacy params field order");
    Assert(!fields.Any(f => f.Name.Contains("Decode", StringComparison.Ordinal)),
        "decode_policy must not be appended to the legacy params struct");

    var lastOffset = Marshal.OffsetOf<YcnRdpParams>(nameof(YcnRdpParams.UseGfx)).ToInt32();
    Assert(Marshal.SizeOf<YcnRdpParams>() >= lastOffset + sizeof(int),
        "legacy params size covers the original final field");
}

static void TestDecodeApiGate()
{
    Equal(0u, YcnDecodePolicies.LegacyNonAvc, "legacy policy");
    Equal(1u, YcnDecodePolicies.AutoHardwarePreferred, "auto policy");
    Equal(2u, YcnDecodePolicies.SoftwareAvc, "software policy");
    Equal(1u, (uint)YcnDecodeCapabilities.AvcDecode, "AVC capability bit");
    Equal(2u, (uint)YcnDecodeCapabilities.HardwareDiagnostics, "hardware diagnostics bit");
    Equal(4u, (uint)YcnDecodeCapabilities.HardwareCompiled, "hardware compiled bit");

    Assert(!RdpNativeInterop.ShouldUseVersionedConnect(0, 0x7), "old API uses legacy connect");
    Assert(!RdpNativeInterop.ShouldUseVersionedConnect(2, 0x7), "unknown API version uses legacy connect");
    Assert(!RdpNativeInterop.ShouldUseVersionedConnect(1, 0x1), "AVC without HW diagnostics uses legacy connect");
    Assert(!RdpNativeInterop.ShouldUseVersionedConnect(1, 0x2), "HW diagnostics without AVC uses legacy connect");
    Assert(RdpNativeInterop.ShouldUseVersionedConnect(1, 0x3), "AVC + HW diagnostics uses versioned connect");
    Assert(RdpNativeInterop.ShouldUseVersionedConnect(1, 0x7), "compiled HW bit is optional for software AVC fallback");
}

static void TestConnectInfoDefaults()
{
    var info = new RdpConnectInfo
    {
        Host = "127.0.0.1",
        Username = "user",
        Password = "secret",
    };

    Equal(DecodeBackends.Auto, info.DecodeBackend, "default decode backend");
    Equal(RenderBackends.Auto, info.RenderBackend, "default render backend");
}

static void TestDecodeStatus()
{
    using var client = new RdpEmbeddedClient();
    var status = client.DecodeStatus();
    Assert(status.Contains("decode_backend=auto", StringComparison.Ordinal), "reports configured auto");
    Assert(status.Contains("entry=connect", StringComparison.Ordinal), "reports legacy entry before probe");
    Assert(status.Contains("hw_compiled=no", StringComparison.Ordinal), "does not mark hardware as compiled");
    Assert(!status.Contains("hardware active", StringComparison.OrdinalIgnoreCase),
        "does not claim successful hardware decode");
}

static void TestFramePhaseStats()
{
    var stats = new RdpFramePhaseStats();
    var ms = Stopwatch.Frequency / 1000;
    var firstBytes = 7680L * 1080;
    var secondBytes = 5120L * 720;

    stats.RecordGrab(ms * 2, 1920, 1080, 7680);
    stats.RecordGrab(ms * 4, 1280, 720, 5120);
    stats.RecordD3D(ms * 3, 1920, 1080, 7680, uploaded: true);
    stats.RecordD3D(ms * 9, 1920, 1080, 7680, uploaded: false);
    stats.RecordSoftCommit(ms * 5, 1280, 720, 5120);
    stats.RecordSoftAwait(ms * 7);
    stats.RecordTick(ms * 6);
    stats.RecordTick(ms * 8);

    var snap = stats.TakeSnapshot(5000);
    Equal(2L, snap.Grab.Count, "grab count");
    Equal(3.0, Math.Round(snap.Grab.AverageMs, 3), "grab average ms");
    Equal(4.0, Math.Round(snap.Grab.MaxMs, 3), "grab max ms");
    Equal(firstBytes + secondBytes, snap.Grab.Bytes, "grab bytes");
    Equal(Math.Round((firstBytes + secondBytes) * 1000.0 / 5000.0, 3),
        Math.Round(snap.Grab.BytesPerSecond(5000), 3), "grab bytes/sec");
    Equal(2L, snap.D3D.Count, "d3d attempt count includes failures");
    Equal(1L, snap.D3DFailures, "d3d failure count");
    Equal(6.0, Math.Round(snap.D3D.AverageMs, 3), "d3d average includes failed attempt");
    Equal(9.0, Math.Round(snap.D3D.MaxMs, 3), "d3d max includes failed attempt");
    Equal(firstBytes, snap.D3D.Bytes, "d3d bytes count only successful uploads");
    Equal(1L, snap.SoftCommit.Count, "soft count");
    Equal(5.0, Math.Round(snap.SoftCommit.AverageMs, 3), "soft average");
    Equal(secondBytes, snap.SoftCommit.Bytes, "soft bytes");
    Equal(1L, snap.SoftAwait.Count, "soft await count");
    Equal(7.0, Math.Round(snap.SoftAwait.AverageMs, 3), "soft await average");
    Equal(2L, snap.Tick.Count, "tick count");
    Equal(7.0, Math.Round(snap.Tick.AverageMs, 3), "tick average");
    Equal(8.0, Math.Round(snap.Tick.MaxMs, 3), "tick max");
    Equal(1280u, snap.Width, "last width");
    Equal(720u, snap.Height, "last height");
    Equal(5120u, snap.Stride, "last stride");
    Equal(1, snap.ResolutionChanges, "resolution change count");

    var reset = stats.TakeSnapshot(5000);
    Equal(0L, reset.Grab.Count, "snapshot resets grab count");
    Equal(0L, reset.D3D.Count, "snapshot resets d3d count");
    Equal(1280u, reset.Width, "last resolution is retained between windows");
    Equal(0, reset.ResolutionChanges, "resolution changes reset per window");

    // 热路径要求：Stopwatch 计时只读时间戳 + long 累加，不分帧分配。
    for (var i = 0; i < 32; i++)
    {
        stats.RecordGrab(ms, 1280, 720, 5120);
        stats.RecordD3D(ms, 1280, 720, 5120, uploaded: true);
        stats.RecordSoftCommit(ms, 1280, 720, 5120);
        stats.RecordSoftAwait(ms);
        stats.RecordTick(ms);
    }
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < 256; i++)
    {
        stats.RecordGrab(ms, 1280, 720, 5120);
        stats.RecordD3D(ms, 1280, 720, 5120, uploaded: true);
        stats.RecordSoftCommit(ms, 1280, 720, 5120);
        stats.RecordSoftAwait(ms);
        stats.RecordTick(ms);
    }
    var after = GC.GetAllocatedBytesForCurrentThread();
    Equal(0L, after - before, "hot-path records must not allocate");

    stats.Reset();
    var cleared = stats.TakeSnapshot(5000);
    Equal(0L, cleared.Grab.Count, "Reset clears counters");
    Equal(0u, cleared.Width, "Reset clears resolution");
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
