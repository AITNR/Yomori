using Yomori.Services;

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
static async Task MustFail(Func<Task> action)
{
    try { await action(); } catch (InvalidOperationException) { return; }
    catch (OperationCanceledException) { return; }
    catch (AggregateException) { return; }
    throw new Exception("Expected failure was reported as success");
}
int passed = 0;
var events = new List<uint>();
var delays = new List<int>();
var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
Task<bool> Send(uint flags, ushort x, ushort y)
{
    Check(x == 960 && y == 890, "Coordinates changed");
    events.Add(flags);
    return flags == 0x9000 ? ack.Task : Task.FromResult(true);
}
var click = McpMouseClick.ExecuteAsync(Send, () => Task.CompletedTask, 960, 890, ms => { delays.Add(ms); return Task.CompletedTask; });
Check(!click.IsCompleted && events.SequenceEqual(new uint[] {0x0800,0x9000}), "Did not wait for down dispatch");
Check(delays.SequenceEqual(new[] {50}), "Hold started before down receipt");
ack.SetResult(true); await click;
Check(events.SequenceEqual(new uint[] {0x0800,0x9000,0x1000}), "Move/down/up must be separate and ordered");
Check(delays.SequenceEqual(new[] {50,120}), "Hold duration is not 120ms after acknowledgement");
passed++;
foreach (var failure in new[] {"move","down","down-throws","hold","release","both","validate"})
{
    events.Clear(); int validations = 0;
    Task<bool> FaultySend(uint flags, ushort x, ushort y)
    {
        events.Add(flags);
        if (flags==0x9000 && (failure=="down-throws" || failure=="both")) throw new InvalidOperationException("down failed");
        return Task.FromResult(!((flags==0x0800 && failure=="move") || (flags==0x9000 && failure=="down") || (flags==0x1000 && (failure=="release" || failure=="both"))));
    }
    await MustFail(() => McpMouseClick.ExecuteAsync(FaultySend, () => {
        if (++validations==2 && failure=="validate") throw new InvalidOperationException("lease revoked");
        return Task.CompletedTask;
    }, 960,890,ms => {
        if (ms==120 && failure=="hold") throw new OperationCanceledException();
        return Task.CompletedTask;
    }));
    if (failure is "move" or "validate") Check(events.SequenceEqual(new uint[] {0x0800}), "Pressed after rejection");
    else Check(events.Count(f=>f==0x1000)==1, "Release was omitted or repeated");
    passed++;
}
var dragEvents = new List<uint>();
var dragDelays = new List<int>();
Task<bool> DragSend(uint flags, ushort x, ushort y)
{
    dragEvents.Add(flags);
    Check((flags == 0x0800 && x == 100 && y == 100)
        || (flags == 0x9000 && x == 100 && y == 100)
        || (flags == 0x1800 && y == 100 && x >= 100 && x <= 300)
        || (flags == 0x1000 && x == 300 && y == 100), "Drag event changed");
    return Task.FromResult(true);
}
await McpMouseDrag.ExecuteAsync(DragSend, () => Task.CompletedTask, 100, 100, 300, 100,
    milliseconds => { dragDelays.Add(milliseconds); return Task.CompletedTask; });
Check(dragEvents[0] == 0x0800 && dragEvents[1] == 0x9000
    && dragEvents.Count(f => f == 0x1800) == 5 && dragEvents[^1] == 0x1000, "Drag order changed");
Check(dragDelays.SequenceEqual(new[] {50, 120, 40, 40, 40, 40, 40}), "Drag timing changed");
passed++;
Console.WriteLine($"PASS {passed} mouse sequencing checks (no real input)");
