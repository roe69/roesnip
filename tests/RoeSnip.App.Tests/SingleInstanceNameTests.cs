using RoeSnip.App.AppShell;
using Xunit;

namespace RoeSnip.App.Tests;

/// <summary>Off Windows the single-instance pipe is a Unix socket whose path must fit sun_path.</summary>
public class SingleInstanceNameTests
{
    [Fact]
    public void UnixPipeName_IsShortAndPerUid()
    {
        Assert.Equal("rsnip-501", SingleInstance.UnixPipeName(501));
        Assert.NotEqual(SingleInstance.UnixPipeName(1000), SingleInstance.UnixPipeName(1001));
    }

    [Fact]
    public void UnixSocketPath_FitsSunPathOnMacTmpdir()
    {
        // A macOS per-user TMPDIR is ~49 chars; sun_path holds 103 plus the NUL.
        const string macTmp = "/var/folders/zz/zyxvpxvq6csfxvn_n0000000000000/T/";
        string socket = macTmp + "CoreFxPipe_" + SingleInstance.UnixPipeName(uint.MaxValue);
        Assert.True(socket.Length <= 103, $"{socket.Length} chars");
    }
}
