using RoeSnip.Core.Recording;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>Registry-plumbing coverage for <see cref="RecordingCapabilitiesRegistry"/> — the same
/// candidate-list select/skip mechanics as <c>RoeSnip.Core.Capture.CaptureBackendRegistry</c>, plus
/// the one behavior that registry does NOT have: a non-throwing fallback
/// (<see cref="RecordingCapabilities.None"/>) when nothing matches, since "no MP4/audio backend on
/// this OS" is a documented degrade here, not an error. Registration is a process-global, append-
/// only list (same caveat CaptureBackendRegistry's own doc comment carries) — every assertion below
/// is written to hold regardless of what any other candidate already registered in this process, by
/// only ever checking for ITS OWN uniquely-tagged candidate rather than assuming an empty list.</summary>
public class RecordingCapabilitiesTests
{
    [Fact]
    public void None_ReportsEveryCapabilityFalse()
    {
        Assert.False(RecordingCapabilities.None.SupportsMp4);
        Assert.False(RecordingCapabilities.None.SupportsMicrophone);
        Assert.False(RecordingCapabilities.None.SupportsLoopback);
    }

    [Fact]
    public void ForCurrentPlatform_SkipsUnsupportedCandidates_AndReturnsTheFirstMatch()
    {
        var expected = new RecordingCapabilities(SupportsMp4: true, SupportsMicrophone: true, SupportsLoopback: false);
        bool unsupportedCandidateInvoked = false;

        // An always-false candidate ahead of the real match must never have its factory invoked at
        // all (registered as a throwing factory so this test fails loudly if that contract slips).
        RecordingCapabilitiesRegistry.Register(
            () => false,
            () => { unsupportedCandidateInvoked = true; throw new System.InvalidOperationException("must not be called"); });
        RecordingCapabilitiesRegistry.Register(() => true, () => expected);

        var actual = RecordingCapabilitiesRegistry.ForCurrentPlatform();

        Assert.False(unsupportedCandidateInvoked);
        Assert.Same(expected, actual);
    }
}
