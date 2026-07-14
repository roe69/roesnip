using System;
using System.IO;
using RoeSnip.Core.Updates;
using Xunit;

namespace RoeSnip.Core.Tests.Updates;

/// <summary>Exercises UpdateStatusMarker against isolated temp directories only - never a real
/// per-OS config directory.</summary>
public sealed class UpdateStatusMarkerTests : IDisposable
{
    private readonly string _tempDir;

    public UpdateStatusMarkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "roesnip_update_status_test_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyDefault_AndDoesNotCreateAnything()
    {
        var state = UpdateStatusMarker.Load(_tempDir);

        Assert.Null(state.CheckedUtc);
        Assert.Null(state.Version);
        Assert.Null(state.Outcome);
        Assert.False(Directory.Exists(_tempDir));
    }

    [Fact]
    public void Load_CorruptJson_ReturnsEmptyDefault_AndLeavesFileUntouched()
    {
        Directory.CreateDirectory(_tempDir);
        string path = Path.Combine(_tempDir, "last-update-status.json");
        File.WriteAllText(path, "{ not json");

        var state = UpdateStatusMarker.Load(_tempDir);

        Assert.Null(state.CheckedUtc);
        Assert.Equal("{ not json", File.ReadAllText(path)); // untouched, not rewritten to defaults
    }

    [Fact]
    public void Load_EmptyFile_ReturnsEmptyDefault()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "last-update-status.json"), "");

        var state = UpdateStatusMarker.Load(_tempDir);

        Assert.Null(state.CheckedUtc);
        Assert.Null(state.Outcome);
    }

    [Fact]
    public void Load_FieldMissingFromOlderFile_FallsBackToDefaultForThatField()
    {
        Directory.CreateDirectory(_tempDir);
        // Only Outcome present - a hand-edited/older marker missing CheckedUtc/Version must not
        // throw and must fall back to each field's own default.
        File.WriteAllText(Path.Combine(_tempDir, "last-update-status.json"), """{"Outcome":"UpToDate"}""");

        var state = UpdateStatusMarker.Load(_tempDir);

        Assert.Equal("UpToDate", state.Outcome);
        Assert.Null(state.CheckedUtc);
        Assert.Null(state.Version);
    }

    [Fact]
    public void Record_ThenLoad_RoundTrips()
    {
        UpdateStatusMarker.Record(_tempDir, "1.9.0", "Applied 1.9.0");

        var state = UpdateStatusMarker.Load(_tempDir);
        Assert.Equal("1.9.0", state.Version);
        Assert.Equal("Applied 1.9.0", state.Outcome);
        Assert.NotNull(state.CheckedUtc);
        Assert.True((DateTime.UtcNow - state.CheckedUtc!.Value).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Record_NullVersion_RoundTrips()
    {
        UpdateStatusMarker.Record(_tempDir, version: null, outcome: "UpToDate");

        var state = UpdateStatusMarker.Load(_tempDir);
        Assert.Null(state.Version);
        Assert.Equal("UpToDate", state.Outcome);
    }

    [Fact]
    public void Record_Twice_OverwritesRatherThanAppending()
    {
        UpdateStatusMarker.Record(_tempDir, "1.9.0", "UpToDate");
        UpdateStatusMarker.Record(_tempDir, "1.9.1", "Applied 1.9.1");

        var state = UpdateStatusMarker.Load(_tempDir);
        Assert.Equal("1.9.1", state.Version);
        Assert.Equal("Applied 1.9.1", state.Outcome);
    }

    [Fact]
    public void DescribeLastCheck_NothingRecorded_ReturnsNull()
    {
        Assert.Null(UpdateStatusMarker.DescribeLastCheck(_tempDir));
    }

    [Fact]
    public void DescribeLastCheck_CorruptFile_ReturnsNull()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "last-update-status.json"), "{ not json");

        Assert.Null(UpdateStatusMarker.DescribeLastCheck(_tempDir));
    }

    [Fact]
    public void DescribeLastCheck_UpToDate_RendersFriendlyText()
    {
        UpdateStatusMarker.Record(_tempDir, version: null, outcome: "UpToDate");

        string? summary = UpdateStatusMarker.DescribeLastCheck(_tempDir);

        Assert.NotNull(summary);
        Assert.StartsWith("Last update check: ", summary);
        Assert.EndsWith(" - up to date", summary);
    }

    [Fact]
    public void DescribeLastCheck_Failed_RendersLowercasedReason()
    {
        UpdateStatusMarker.Record(_tempDir, "1.9.0", "Failed: network unreachable");

        string? summary = UpdateStatusMarker.DescribeLastCheck(_tempDir);

        Assert.NotNull(summary);
        Assert.EndsWith(" - failed: network unreachable", summary);
    }

    [Fact]
    public void DescribeLastCheck_Applied_RendersUpdatedToText()
    {
        UpdateStatusMarker.Record(_tempDir, "1.9.0", "Applied 1.9.0");

        string? summary = UpdateStatusMarker.DescribeLastCheck(_tempDir);

        Assert.NotNull(summary);
        Assert.EndsWith(" - updated to 1.9.0", summary);
    }

    [Fact]
    public void DescribeLastCheck_UnrecognizedOutcome_PassesThroughVerbatim()
    {
        UpdateStatusMarker.Record(_tempDir, "1.9.0", "SomeFutureOutcome");

        string? summary = UpdateStatusMarker.DescribeLastCheck(_tempDir);

        Assert.NotNull(summary);
        Assert.EndsWith(" - SomeFutureOutcome", summary);
    }
}
