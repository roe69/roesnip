using RoeSnip.Core.Diagnostics;
using Xunit;

namespace RoeSnip.Core.Tests.Diagnostics;

public sealed class IdleGapLogTests
{
    [Fact]
    public void FormatSuffix_NegativeIdle_ReturnsEmptyString()
    {
        // The sentinel both apps' AppComposition.RunCaptureFlowAsync use for "no previous trigger
        // this process" — the first hotkey press after launch must not gain a nonsensical
        // "[idle -1 ms ...]" tag, and the log line it's appended to must read exactly as it did
        // before this suffix existed.
        Assert.Equal(string.Empty, IdleGapLog.FormatSuffix(-1));
    }

    [Fact]
    public void FormatSuffix_ZeroIdle_IncludesTheGap()
    {
        Assert.Equal(" [idle 0 ms since previous trigger]", IdleGapLog.FormatSuffix(0));
    }

    [Fact]
    public void FormatSuffix_PositiveIdle_RendersRawMilliseconds()
    {
        // Raw milliseconds, not a "3m45s" rendering (see the class doc comment) — this is
        // grep/awk/script fodder for correlating a slow trigger with the idle gap before it, so the
        // exact number must round-trip untouched.
        Assert.Equal(" [idle 3512345 ms since previous trigger]", IdleGapLog.FormatSuffix(3_512_345));
    }
}
