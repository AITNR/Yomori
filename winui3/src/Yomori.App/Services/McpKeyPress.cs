using System;
using System.Threading.Tasks;

namespace Yomori.Services;

/// <summary>Separate queue admission for down/up. This is not a native dispatch receipt.</summary>
internal static class McpKeyPress
{
    // The native event loop may wait 500ms before draining input. Leave a bounded margin.
    internal const int QueueHoldMilliseconds = 650;

    internal static async Task ExecuteAsync(Func<bool, bool> send, Func<Task> validate, Func<int, Task>? delay = null)
    {
        delay ??= milliseconds => Task.Delay(milliseconds);
        await validate().ConfigureAwait(false);
        Exception? failure = null;
        try
        {
            if (!send(true)) throw new InvalidOperationException("Key-down was not queued.");
            await delay(QueueHoldMilliseconds).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try
            {
                if (!send(false)) throw new InvalidOperationException("Key-up was not queued; stop input.");
            }
            catch (Exception error)
            {
                failure = failure is null ? error : new AggregateException(failure, error);
            }
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
