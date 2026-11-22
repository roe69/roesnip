using System;
using System.IO;
using System.Threading.Tasks;
using RoeSnip.Core.Updates;
using Xunit;

namespace RoeSnip.Core.Tests.Updates;

public sealed class UpdateLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roesnip_updatelock_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string LockPath => UpdateLock.PathFor(Path.Combine(_dir, "RoeSnip.AppImage"));

    [Fact]
    public void PathFor_SitsNextToTheTarget()
    {
        string target = Path.Combine(_dir, "RoeSnip.app");
        Assert.Equal(target + ".update.lock", UpdateLock.PathFor(target + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void SecondAcquire_FailsWhileHeld_AndSucceedsAfterRelease()
    {
        using (UpdateLock? first = UpdateLock.TryAcquire(LockPath))
        {
            Assert.NotNull(first);
            Assert.Null(UpdateLock.TryAcquire(LockPath));
        }

        using UpdateLock? again = UpdateLock.TryAcquire(LockPath);
        Assert.NotNull(again);
        Assert.True(File.Exists(LockPath));
    }

    [Fact]
    public async Task AcquireAsync_Uncontended_IsNotContended()
    {
        (UpdateLock? held, bool contended) = await UpdateLock.AcquireAsync(LockPath, TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
        using (held)
        {
            Assert.NotNull(held);
            Assert.False(contended);
        }
    }

    [Fact]
    public async Task AcquireAsync_ZeroWait_ReturnsBusyAtOnce()
    {
        using UpdateLock? holder = UpdateLock.TryAcquire(LockPath);
        Assert.NotNull(holder);

        (UpdateLock? held, bool contended) = await UpdateLock.AcquireAsync(LockPath, TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
        Assert.Null(held);
        Assert.True(contended);
    }

    [Fact]
    public async Task AcquireAsync_WaitsForTheHolder_AndReportsContended()
    {
        UpdateLock? holder = UpdateLock.TryAcquire(LockPath);
        Assert.NotNull(holder);
        _ = Task.Delay(150).ContinueWith(_ => holder!.Dispose());

        (UpdateLock? held, bool contended) = await UpdateLock.AcquireAsync(LockPath, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(20));
        using (held)
        {
            Assert.NotNull(held);
            Assert.True(contended);
        }
    }

    [Fact]
    public async Task AcquireAsync_TimesOut_WhenNeverReleased()
    {
        using UpdateLock? holder = UpdateLock.TryAcquire(LockPath);
        (UpdateLock? held, bool contended) = await UpdateLock.AcquireAsync(LockPath, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(20));
        Assert.Null(held);
        Assert.True(contended);
    }
}
