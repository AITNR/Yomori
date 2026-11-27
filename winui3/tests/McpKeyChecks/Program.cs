using Yomori.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

foreach (var mode in new[] { "success", "down-false", "down-throw", "delay-throw", "up-false", "validate-throw", "both-fail" })
{
    var events = new List<string>();
    Exception? failure = null;
    try
    {
        await McpKeyPress.ExecuteAsync(down =>
        {
            events.Add(down ? "down" : "up");
            if (down && mode == "down-throw") throw new InvalidOperationException();
            return down ? mode != "down-false" && mode != "both-fail" : mode != "up-false" && mode != "both-fail";
        }, () =>
        {
            events.Add("validate");
            if (mode == "validate-throw") throw new InvalidOperationException();
            return Task.CompletedTask;
        }, milliseconds =>
        {
            Check(milliseconds == 650, "Queue hold must remain explicit");
            events.Add("hold");
            if (mode == "delay-throw") throw new OperationCanceledException();
            return Task.CompletedTask;
        });
    }
    catch (Exception error) { failure = error; }
    var expected = mode == "validate-throw" ? "validate"
        : mode is "down-false" or "down-throw" or "both-fail" ? "validate,down,up"
        : "validate,down,hold,up";
    Check(string.Join(',', events) == expected, mode + " sequence");
    Check((failure is null) == (mode == "success"), mode + " outcome");
    if (mode == "both-fail") Check(failure is AggregateException, "Preserve both failures");
}

var released = false;
var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var operation = McpKeyPress.ExecuteAsync(down => { if (!down) released = true; return true; },
    () => Task.CompletedTask, _ => hold.Task);
Check(!released && !operation.IsCompleted, "Must await the actual delay before releasing");
hold.SetResult(true);
await operation;
Check(released, "Release after delay");
Console.WriteLine("PASS 8 keyboard sequencing checks (no real input; queue admission only)");
