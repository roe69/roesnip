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

    // Same process on purpose: Windows sharing modes and the Unix flock behind FileShare.None both
    // conflict per open handle, not per process (checked on Linux .NET 8: IOException HResult 11).
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
    public void HeldLock_ThrowsTheContentionErrorThisOsReportsAsBusy()
    {
        using UpdateLock? first = UpdateLock.TryAcquire(LockPath);
        Assert.NotNull(first);

        IOException ex = Assert.ThrowsAny<IOException>(() =>
            new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None).Dispose());
        Assert.True(UpdateLock.IsHeldElsewhere(ex, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS()),
            $"{ex.GetType().Name} HResult {ex.HResult}: {ex.Message}");
    }

    [Fact]
    public void UnusableFolder_Throws_NotBusy()
    {
        Directory.CreateDirectory(_dir);
        string notADir = Path.Combine(_dir, "file");
        File.WriteAllText(notADir, "x");

        Assert.ThrowsAny<IOException>(() => UpdateLock.TryAcquire(Path.Combine(notADir, "RoeSnip.exe.update.lock")));
    }

    // A root-owned lock left by a sudo run: modelled with mode 000 (root opens it anyway, so the
    // test then only checks the acquire still works).
    [Fact]
    public void UnopenableLockFile_InWritableFolder_IsReplaced()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_dir);
        File.WriteAllText(LockPath, "");
        File.SetUnixFileMode(LockPath, UnixFileMode.None);

        using UpdateLock? held = UpdateLock.TryAcquire(LockPath);
        Assert.NotNull(held);
        Assert.Null(UpdateLock.TryAcquire(LockPath)); // the new file is a real lock
    }

    [Fact]
    public void UnopenableLockFile_InReadOnlyFolder_Throws()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        Directory.CreateDirectory(_dir);
        File.WriteAllText(LockPath, "");
        File.SetUnixFileMode(LockPath, UnixFileMode.None);
        UnixFileMode dirMode = File.GetUnixFileMode(_dir);
        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => UpdateLock.TryAcquire(LockPath));
            Assert.True(File.Exists(LockPath));
        }
        finally
        {
            File.SetUnixFileMode(_dir, dirMode);
        }
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020), true, false, true)]  // ERROR_SHARING_VIOLATION
    [InlineData(unchecked((int)0x80070021), true, false, true)]  // ERROR_LOCK_VIOLATION
    [InlineData(unchecked((int)0x80070070), true, false, false)] // ERROR_DISK_FULL
    [InlineData(11, false, false, true)]                          // Linux EAGAIN
    [InlineData(28, false, false, false)]                         // Linux ENOSPC
    [InlineData(35, false, true, true)]                           // macOS EAGAIN
    [InlineData(11, false, true, false)]                          // macOS EDEADLK
    public void IsHeldElsewhere_OnlyForContention(int hresult, bool windows, bool mac, bool expected) =>
        Assert.Equal(expected, UpdateLock.IsHeldElsewhere(new IOException("x", hresult), windows, mac));

    [Fact]
    public void IsHeldElsewhere_NeverForOtherExceptionTypes()
    {
        Assert.False(UpdateLock.IsHeldElsewhere(new DirectoryNotFoundException("x") { HResult = 11 }, false, false));
        Assert.False(UpdateLock.IsHeldElsewhere(new UnauthorizedAccessException("x"), false, false));
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
