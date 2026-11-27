using System;
using System.Threading.Tasks;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>One bounded drag. Every event waits for the Native dispatch receipt.</summary>
internal static class McpMouseDrag
{
    internal const int PreDragMilliseconds = 50;
    internal const int HoldMilliseconds = 120;
    internal const int DragSteps = 5;
    internal const int DragStepMilliseconds = 40;

    internal static async Task ExecuteAsync(
        Func<uint, ushort, ushort, Task<bool>> send,
        Func<Task> validate,
        ushort startX, ushort startY, ushort endX, ushort endY,
        Func<int, Task>? delay = null)
    {
        delay ??= milliseconds => Task.Delay(milliseconds);
        await validate().ConfigureAwait(false);
        if (!await send(YcnPointerFlags.Move, startX, startY).ConfigureAwait(false))
            throw new InvalidOperationException("Drag start movement was not dispatched.");
        await delay(PreDragMilliseconds).ConfigureAwait(false);
        await validate().ConfigureAwait(false);

        Exception? failure = null;
        var pressed = false;
        try
        {
            if (!await send(YcnPointerFlags.Button1 | YcnPointerFlags.Down, startX, startY).ConfigureAwait(false))
                throw new InvalidOperationException("Drag button-down was not dispatched.");
            pressed = true;
            await delay(HoldMilliseconds).ConfigureAwait(false);
            for (var step = 1; step <= DragSteps; step++)
            {
                var currentX = (ushort)(startX + ((int)endX - startX) * step / DragSteps);
                var currentY = (ushort)(startY + ((int)endY - startY) * step / DragSteps);
                if (!await send(YcnPointerFlags.Move | YcnPointerFlags.Button1, currentX, currentY).ConfigureAwait(false))
                    throw new InvalidOperationException("Drag movement was not dispatched.");
                await delay(DragStepMilliseconds).ConfigureAwait(false);
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (pressed)
            {
                try
                {
                    if (!await send(YcnPointerFlags.Button1, endX, endY).ConfigureAwait(false))
                        throw new InvalidOperationException("Drag button-up was not confirmed; stop input.");
                }
                catch (Exception releaseError)
                {
                    failure = failure is null ? releaseError : new AggregateException(failure, releaseError);
                }
            }
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
