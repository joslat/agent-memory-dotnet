using FluentAssertions;
using AgentMemory.Nams.Identity.Internal;

namespace AgentMemory.Tests.Unit.Nams;

public sealed class KeyedAsyncLockTests
{
    [Fact]
    public async Task DifferentKeys_DoNotBlockEachOther()
    {
        var keyedLock = new KeyedAsyncLock();
        using var releaseA = await keyedLock.AcquireAsync("a", CancellationToken.None);

        var acquireB = keyedLock.AcquireAsync("b", CancellationToken.None);
        var completed = await Task.WhenAny(acquireB, Task.Delay(TimeSpan.FromSeconds(2)));

        completed.Should().Be(acquireB);
        (await acquireB).Dispose();
    }

    [Fact]
    public async Task SameKey_SerializesAccess()
    {
        var keyedLock = new KeyedAsyncLock();
        var order = new List<int>();
        var releaser = await keyedLock.AcquireAsync("x", CancellationToken.None);

        var secondAcquire = Task.Run(async () =>
        {
            using var release = await keyedLock.AcquireAsync("x", CancellationToken.None);
            order.Add(2);
        });

        await Task.Delay(50); // give the second acquire a chance to (incorrectly) proceed if not serialized
        order.Add(1);
        releaser.Dispose();
        await secondAcquire;

        order.Should().Equal(1, 2);
    }

    [Fact]
    public async Task Dispose_ReleasesLock_AllowsNextAcquire()
    {
        var keyedLock = new KeyedAsyncLock();
        var first = await keyedLock.AcquireAsync("y", CancellationToken.None);
        first.Dispose();

        var act = () => keyedLock.AcquireAsync("y", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Dispose_CalledTwice_DoesNotDoubleRelease()
    {
        var keyedLock = new KeyedAsyncLock();
        var releaser = await keyedLock.AcquireAsync("z", CancellationToken.None);
        releaser.Dispose();
        releaser.Dispose(); // must not throw or over-release the semaphore

        var act = () => keyedLock.AcquireAsync("z", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        await act.Should().NotThrowAsync();
    }
}
