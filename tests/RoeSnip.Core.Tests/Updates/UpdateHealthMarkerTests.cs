using System;
using System.IO;
using RoeSnip.Core.Updates;
using Xunit;

namespace RoeSnip.Core.Tests.Updates;

/// <summary>Exercises UpdateHealthMarker against isolated temp directories only - never a real
/// per-OS config directory.</summary>
public sealed class UpdateHealthMarkerTests : IDisposable
{
    private readonly string _tempDir;

    public UpdateHealthMarkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "roesnip_update_health_test_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Load_MissingFile_ReturnsHealthyDefault_AndDoesNotCreateAnything()
    {
        var state = UpdateHealthMarker.Load(_tempDir);

        Assert.Null(state.PendingVersion);
        Assert.Equal(0, state.AttemptCount);
        Assert.Null(state.QuarantinedVersion);
        Assert.False(Directory.Exists(_tempDir));
    }

    [Fact]
    public void Load_CorruptJson_ReturnsHealthyDefault_AndLeavesFileUntouched()
    {
        Directory.CreateDirectory(_tempDir);
        string path = Path.Combine(_tempDir, "update-health.json");
        File.WriteAllText(path, "{ not json");

        var state = UpdateHealthMarker.Load(_tempDir);

        Assert.Null(state.PendingVersion);
        Assert.Equal("{ not json", File.ReadAllText(path)); // untouched, not rewritten to defaults
    }

    [Fact]
    public void Load_FieldMissingFromOlderFile_FallsBackToDefaultForThatField()
    {
        Directory.CreateDirectory(_tempDir);
        // Only PendingVersion present — an older/hand-edited marker missing AttemptCount/
        // QuarantinedVersion must not throw and must fall back to each field's own default.
        File.WriteAllText(Path.Combine(_tempDir, "update-health.json"), """{"PendingVersion":"1.9.0"}""");

        var state = UpdateHealthMarker.Load(_tempDir);

        Assert.Equal("1.9.0", state.PendingVersion);
        Assert.Equal(0, state.AttemptCount);
        Assert.Null(state.QuarantinedVersion);
    }

    [Fact]
    public void RecordPendingUpdate_ThenLoad_RoundTrips()
    {
        UpdateHealthMarker.RecordPendingUpdate(_tempDir, "1.9.0");

        var state = UpdateHealthMarker.Load(_tempDir);
        Assert.Equal("1.9.0", state.PendingVersion);
        Assert.Equal(0, state.AttemptCount);
    }

    [Fact]
    public void RecordPendingUpdate_DoesNotClearAnExistingQuarantine()
    {
        UpdateHealthMarker.Quarantine(_tempDir, "1.8.0");

        UpdateHealthMarker.RecordPendingUpdate(_tempDir, "1.9.0");

        var state = UpdateHealthMarker.Load(_tempDir);
        Assert.Equal("1.9.0", state.PendingVersion);
        Assert.Equal("1.8.0", state.QuarantinedVersion);
    }

    [Fact]
    public void RecordLaunchAttempt_NoMarker_LeavesFileUntouched()
    {
        var state = UpdateHealthMarker.RecordLaunchAttempt(_tempDir, "1.9.0");

        Assert.Null(state.PendingVersion);
        Assert.Equal(0, state.AttemptCount);
        Assert.False(File.Exists(Path.Combine(_tempDir, "update-health.json")));
    }

    [Fact]
    public void RecordLaunchAttempt_MarkerForDifferentVersion_LeavesAttemptCountUnchanged()
    {
        UpdateHealthMarker.RecordPendingUpdate(_tempDir, "1.9.0");

        var state = UpdateHealthMarker.RecordLaunchAttempt(_tempDir, "1.8.0"); // running a different build

        Assert.Equal("1.9.0", state.PendingVersion);
        Assert.Equal(0, state.AttemptCount);
    }

    [Fact]
    public void RecordLaunchAttempt_MarkerForCurrentVersion_IncrementsAndPersists()
    {
        UpdateHealthMarker.RecordPendingUpdate(_tempDir, "1.9.0");

        UpdateHealthMarker.RecordLaunchAttempt(_tempDir, "1.9.0");
        UpdateHealthMarker.RecordLaunchAttempt(_tempDir, "1.9.0");
        var third = UpdateHealthMarker.RecordLaunchAttempt(_tempDir, "1.9.0");

        Assert.Equal(3, third.AttemptCount);
        Assert.Equal(3, UpdateHealthMarker.Load(_tempDir).AttemptCount); // persisted, not just returned
    }

    [Fact]
    public void ClearPending_RemovesPendingVersionAndResetsAttemptCount()
    {
        UpdateHealthMarker.RecordPendingUpdate(_tempDir, "1.9.0");
        UpdateHealthMarker.RecordLaunchAttempt(_tempDir, "1.9.0");

        UpdateHealthMarker.ClearPending(_tempDir);

        var state = UpdateHealthMarker.Load(_tempDir);
        Assert.Null(state.PendingVersion);
        Assert.Equal(0, state.AttemptCount);
    }

    [Fact]
    public void ClearPending_PreservesAnExistingQuarantine()
    {
        UpdateHealthMarker.Quarantine(_tempDir, "1.8.0");
        UpdateHealthMarker.RecordPendingUpdate(_tempDir, "1.9.0");

        UpdateHealthMarker.ClearPending(_tempDir);

        Assert.Equal("1.8.0", UpdateHealthMarker.Load(_tempDir).QuarantinedVersion);
    }

    [Fact]
    public void ClearPending_NothingPending_DoesNotCreateFile()
    {
        UpdateHealthMarker.ClearPending(_tempDir);

        Assert.False(File.Exists(Path.Combine(_tempDir, "update-health.json")));
    }

    [Fact]
    public void Quarantine_ClearsAnyPendingVerifyState()
    {
        UpdateHealthMarker.RecordPendingUpdate(_tempDir, "1.9.0");
        UpdateHealthMarker.RecordLaunchAttempt(_tempDir, "1.9.0");

        UpdateHealthMarker.Quarantine(_tempDir, "1.9.0");

        var state = UpdateHealthMarker.Load(_tempDir);
        Assert.Equal("1.9.0", state.QuarantinedVersion);
        Assert.Null(state.PendingVersion);
        Assert.Equal(0, state.AttemptCount);
    }

    [Fact]
    public void IsQuarantined_NothingQuarantined_ReturnsFalse()
    {
        Assert.False(UpdateHealthMarker.IsQuarantined(_tempDir, new Version(9, 9, 9)));
    }

    [Fact]
    public void IsQuarantined_ExactMatch_ReturnsTrue()
    {
        UpdateHealthMarker.Quarantine(_tempDir, "1.9.0");

        Assert.True(UpdateHealthMarker.IsQuarantined(_tempDir, new Version(1, 9, 0)));
    }

    [Fact]
    public void IsQuarantined_StrictlyNewerCandidate_ClearsQuarantine_AndReturnsFalse()
    {
        UpdateHealthMarker.Quarantine(_tempDir, "1.9.0");

        bool skipped = UpdateHealthMarker.IsQuarantined(_tempDir, new Version(1, 9, 1));

        Assert.False(skipped);
        Assert.Null(UpdateHealthMarker.Load(_tempDir).QuarantinedVersion); // side effect: cleared
    }

    [Fact]
    public void IsQuarantined_AfterClearingViaNewerRelease_ThatSameNewerReleaseIsNoLongerSkipped()
    {
        UpdateHealthMarker.Quarantine(_tempDir, "1.9.0");
        UpdateHealthMarker.IsQuarantined(_tempDir, new Version(1, 9, 1)); // clears the quarantine

        Assert.False(UpdateHealthMarker.IsQuarantined(_tempDir, new Version(1, 9, 1)));
    }

    [Fact]
    public void IsQuarantined_OlderCandidateThanQuarantined_ReturnsFalse()
    {
        // Not expected in practice (CheckForUpdateAsync only ever offers releases newer than
        // CurrentVersion), but must fail safe rather than incorrectly skip a lower version.
        UpdateHealthMarker.Quarantine(_tempDir, "2.0.0");

        Assert.False(UpdateHealthMarker.IsQuarantined(_tempDir, new Version(1, 9, 0)));
    }

    [Fact]
    public void FullLifecycle_ThreeFailedLaunchesThenRestore_QuarantineBlocksReapply_NewerReleaseClearsIt()
    {
        // Simulates: update to 1.9.0 applied, three crash-looping launches, caller rolls back and
        // quarantines 1.9.0, then a fixed 1.9.1 ships.
        UpdateHealthMarker.RecordPendingUpdate(_tempDir, "1.9.0");
        UpdateHealthMarker.State state = UpdateHealthMarker.RecordLaunchAttempt(_tempDir, "1.9.0");
        Assert.Equal(1, state.AttemptCount);
        state = UpdateHealthMarker.RecordLaunchAttempt(_tempDir, "1.9.0");
        Assert.Equal(2, state.AttemptCount);
        state = UpdateHealthMarker.RecordLaunchAttempt(_tempDir, "1.9.0");
        Assert.Equal(3, state.AttemptCount);
        Assert.True(state.AttemptCount >= UpdateHealthMarker.RollbackAttemptThreshold);

        UpdateHealthMarker.Quarantine(_tempDir, "1.9.0");
        Assert.True(UpdateHealthMarker.IsQuarantined(_tempDir, new Version(1, 9, 0)));

        // A fixed re-release supersedes the quarantine.
        Assert.False(UpdateHealthMarker.IsQuarantined(_tempDir, new Version(1, 9, 1)));
        Assert.Null(UpdateHealthMarker.Load(_tempDir).QuarantinedVersion);
    }
}
