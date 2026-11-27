using System;
using System.Threading.Tasks;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>One bounded click. Send must await the native dispatch receipt, not queue admission.</summary>
internal static class McpMouseClick
{
    internal const int HoldMilliseconds = 120;

    internal static async Task ExecuteAsync(
        Func<uint, ushort, ushort, Task<bool>> send,
        Func<Task> validate,
        ushort x, ushort y,
        Func<int, Task>? delay = null)
    {
        delay ??= milliseconds => Task.Delay(milliseconds);
        await validate().ConfigureAwait(false);
        if (!await send(YcnPointerFlags.Move, x, y).ConfigureAwait(false))
            throw new InvalidOperationException("Mouse movement was not dispatched.");
        await delay(50).ConfigureAwait(false);
        await validate().ConfigureAwait(false);
        // A timed-out down may already have reached transport. Always attempt up.
        Exception? failure = null;
        try
        {
            if (!await send(YcnPointerFlags.Button1 | YcnPointerFlags.Down, x, y).ConfigureAwait(false))
                throw new InvalidOperationException("Mouse button-down was not dispatched.");
            await delay(HoldMilliseconds).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try
            {
                if (!await send(YcnPointerFlags.Button1, x, y).ConfigureAwait(false))
                    throw new InvalidOperationException("Mouse button-up was not confirmed; stop input.");
            }
            catch (Exception releaseError)
            {
                failure = failure is null ? releaseError : new AggregateException(failure, releaseError);
            }
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
