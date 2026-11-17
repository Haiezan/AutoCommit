using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AutoCommit.Core;

public static class ClipboardRetry
{
    public static async Task<bool> TryWriteAsync(Action write, CancellationToken token, int delayMilliseconds = 100)
    {
        // Keep the caller's UI/STA synchronization context across asynchronous delays.
        for (var attempt = 0; attempt < 6; attempt++) {
            token.ThrowIfCancellationRequested();
            try { write(); return true; }
            catch (ExternalException ex) when (ex.HResult == unchecked((int)0x800401D0)) {
                if (attempt == 5) return false;
                await Task.Delay(delayMilliseconds * (attempt + 1), token);
            }
        }
        return false;
    }
}
