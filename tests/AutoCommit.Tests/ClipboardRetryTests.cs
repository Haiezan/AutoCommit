using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AutoCommit.Core;
using Xunit;

public class ClipboardRetryTests
{
    private static ExternalException Busy() => new ExternalException("busy", unchecked((int)0x800401D0));
    [Fact]
    public async Task TransientContentionEventuallyCopies()
    {
        var calls = 0;
        Assert.True(await ClipboardRetry.TryWriteAsync(() => { if (++calls < 3) throw Busy(); }, default, 0));
        Assert.Equal(3, calls);
    }
    [Fact]
    public async Task PersistentContentionStopsAfterBoundedAttempts()
    {
        var calls = 0;
        Assert.False(await ClipboardRetry.TryWriteAsync(() => { calls++; throw Busy(); }, default, 0));
        Assert.Equal(6, calls);
    }
    [Fact]
    public async Task UnrelatedErrorsAreNotHidden()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ClipboardRetry.TryWriteAsync(() => throw new InvalidOperationException(), default, 0));
    }
    [Fact]
    public async Task CancellationDoesNotWrite()
    {
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClipboardRetry.TryWriteAsync(() => calls++, new CancellationToken(true), 0));
        Assert.Equal(0, calls);
    }
}
