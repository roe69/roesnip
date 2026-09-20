using System.IO;
using RoeSnip.App.Recording;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Recording;
using RoeSnip.Core.Settings;
using Xunit;

namespace RoeSnip.App.Tests;

/// <summary>RecordingController.RoundDownToEven — the H.264 4:2:0 chroma-subsampling floor ported
/// verbatim from the WPF app's own RecordingController, exercised directly (pure geometry, no
/// capture backend/encoder involved).</summary>
public class RecordingControllerTests
{
    [Fact]
    public void EvenSelection_IsUnchanged()
    {
        var selection = RectPhysical.FromSize(10, 20, 400, 300);
        Assert.Equal(selection, RecordingController.RoundDownToEven(selection));
    }

    [Fact]
    public void OddWidthAndHeight_FloorToEven()
    {
        var selection = RectPhysical.FromSize(10, 20, 401, 301);
        var result = RecordingController.RoundDownToEven(selection);
        Assert.Equal(400, result.Width);
        Assert.Equal(300, result.Height);
        Assert.Equal(10, result.Left);
        Assert.Equal(20, result.Top);
    }

    [Fact]
    public void TinyOddSelection_FloorsToTheTwoByTwoMinimum_NeverZero()
    {
        var selection = RectPhysical.FromSize(0, 0, 1, 1);
        var result = RecordingController.RoundDownToEven(selection);
        Assert.Equal(2, result.Width);
        Assert.Equal(2, result.Height);
    }

    [Fact]
    public void InvertedSelection_IsNormalizedFirst()
    {
        // Right < Left / Bottom < Top (a drag that went backward) must be normalized before the
        // even-floor applies, same as RectPhysical.Normalized()'s own contract.
        var selection = new RectPhysical(401, 301, 10, 20);
        var result = RecordingController.RoundDownToEven(selection);
        Assert.Equal(10, result.Left);
        Assert.Equal(20, result.Top);
        Assert.Equal(390, result.Width); // (401-10)=391 -> floored even 390
        Assert.Equal(280, result.Height); // (301-20)=281 -> floored even 280
    }
}

/// <summary>Item 21's RecordingSession additions (Restart/BeginShareHandoff/Elapsed/PhaseChanged/
/// PausedChanged/Ended) - scoped to the Setup-phase guard clauses and the full Setup -> Cancel
/// teardown path, none of which touch a real capture backend/encoder (mirrors this file's own
/// existing precedent - RoundDownToEven above is the only slice of RecordingController.cs that was
/// unit-testable without one; BeginCapture itself needs a real WGC session and is exercised live via
/// --record-smoketest / the automation harness instead, per item 20's own PARITY.md note).</summary>
public class RecordingSessionSetupPhaseTests
{
    private static MonitorInfo TestMonitor() => new(
        Index: 0, DeviceName: "TEST", BackendKey: "0x0", BoundsPx: RectPhysical.FromSize(0, 0, 1920, 1080),
        DpiX: 96, DpiY: 96, Scale: 1.0, AdvancedColorActive: false, SdrWhiteNits: 80, MaxLuminanceNits: 80,
        IsPrimary: true);

    private static RecordingSession NewSetupSession(RecordingFormat format = RecordingFormat.Gif) =>
        RecordingController.StartNew(TestMonitor(), RectPhysical.FromSize(0, 0, 400, 300), format, RoeSnipSettings.Default);

    [Fact]
    public void FreshSession_IsSetup_WithZeroElapsed()
    {
        var session = NewSetupSession();
        try
        {
            Assert.True(session.IsSetup);
            Assert.False(session.IsCapturing);
            Assert.False(session.IsReviewing);
            Assert.Equal(TimeSpan.Zero, session.Elapsed);
        }
        finally
        {
            session.CancelAndDiscard();
        }
    }

    [Fact]
    public void Restart_InSetup_IsANoOp()
    {
        var session = NewSetupSession();
        try
        {
            bool phaseChangedFired = false;
            session.PhaseChanged += _ => phaseChangedFired = true;

            session.Restart();

            Assert.True(session.IsSetup);
            Assert.False(phaseChangedFired); // no real transition happened - nothing captured yet
        }
        finally
        {
            session.CancelAndDiscard();
        }
    }

    [Fact]
    public void BeginShareHandoff_InSetup_ReturnsNull_TakeUntouched()
    {
        var session = NewSetupSession();
        try
        {
            var handoff = session.BeginShareHandoff();
            Assert.Null(handoff);
            Assert.True(session.IsSetup); // fail-closed: nothing was hard-stopped or rearmed
        }
        finally
        {
            session.CancelAndDiscard();
        }
    }

    [Fact]
    public void CancelAndDiscard_FromSetup_RaisesEnded_AndClearsController_Active()
    {
        var session = NewSetupSession();
        Assert.True(RecordingController.IsActive);
        Assert.Same(session, RecordingController.Active);

        bool ended = false;
        session.Ended += () => ended = true;

        session.CancelAndDiscard();

        Assert.True(ended);
        Assert.False(RecordingController.IsActive);
        Assert.Null(RecordingController.Active);
    }

    [Fact]
    public void FreshSession_HasNoFinishedTakePath()
    {
        var session = NewSetupSession();
        try
        {
            Assert.False(session.AwaitingAnotherTakeChoice);
            Assert.Null(session.FinishedTakePath);
        }
        finally
        {
            session.CancelAndDiscard();
        }
    }

    /// <summary>Pins what the finished-take prompt's "Copy again" and "Save" act on: a copied take
    /// parks on the prompt carrying the STAGED path (the copy that outlives the session), not the
    /// temp path the session is about to forget.</summary>
    [Fact]
    public void CompleteClipboardHandoff_ParksOnThePrompt_CarryingTheStagedPath()
    {
        var session = NewSetupSession();
        try
        {
            string staged = Path.Combine(Path.GetTempPath(), "roesnip_staged_take.gif");
            string? prompt = null;
            session.TakeFinished += m => prompt = m;

            session.CompleteClipboardHandoff(staged);

            Assert.True(session.AwaitingAnotherTakeChoice);
            Assert.Equal(staged, session.FinishedTakePath);
            Assert.NotNull(prompt);
        }
        finally
        {
            session.CancelAndDiscard();
        }
    }

    /// <summary>The other half of that contract: re-arming drops the finished take, so Copy again /
    /// Save can never act on the PREVIOUS take once a new one is being set up.</summary>
    [Fact]
    public void RecordAnotherTake_ClearsTheFinishedTakePath()
    {
        var session = NewSetupSession();
        try
        {
            session.CompleteClipboardHandoff(Path.Combine(Path.GetTempPath(), "roesnip_staged_take.gif"));

            session.RecordAnotherTake();

            Assert.False(session.AwaitingAnotherTakeChoice);
            Assert.Null(session.FinishedTakePath);
            Assert.True(session.IsSetup);
        }
        finally
        {
            session.CancelAndDiscard();
        }
    }

    [Fact]
    public void FinishAfterTake_ClearsTheFinishedTakePath_AndEndsTheSession()
    {
        var session = NewSetupSession();
        session.CompleteClipboardHandoff(Path.Combine(Path.GetTempPath(), "roesnip_staged_take.gif"));
        bool ended = false;
        session.Ended += () => ended = true;

        session.FinishAfterTake();

        Assert.True(ended);
        Assert.False(session.AwaitingAnotherTakeChoice);
        Assert.Null(session.FinishedTakePath);
    }

    [Fact]
    public void StartNew_WhileAlreadyActive_Throws()
    {
        var session = NewSetupSession();
        try
        {
            Assert.Throws<InvalidOperationException>(
                () => RecordingController.StartNew(TestMonitor(), RectPhysical.FromSize(0, 0, 200, 200), RecordingFormat.Gif, RoeSnipSettings.Default));
        }
        finally
        {
            session.CancelAndDiscard();
        }
    }
}
