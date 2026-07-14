using System;
using System.IO;
using RoeSnip.Core.Updates;
using Xunit;

namespace RoeSnip.Core.Tests.Updates;

/// <summary>Exercises SelfExtractCleanup against isolated temp directories only - the internal
/// (baseDirectory, tempNetRoot) overload stands in for AppContext.BaseDirectory/%TEMP%\.net so
/// these tests never touch the real machine-wide %TEMP%.</summary>
public sealed class SelfExtractCleanupTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempNetRoot;
    private readonly string _assemblyParent;

    public SelfExtractCleanupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "roesnip_selfextract_test_" + Guid.NewGuid().ToString("N"));
        _tempNetRoot = Path.Combine(_tempDir, ".net");
        _assemblyParent = Path.Combine(_tempNetRoot, "RoeSnip");
        Directory.CreateDirectory(_assemblyParent);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string CreateExtractionDir(string name, int fileSizeBytes = 100)
    {
        string dir = Path.Combine(_assemblyParent, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "payload.dll"), new byte[fileSizeBytes]);
        return dir;
    }

    [Fact]
    public void NotUnderTempNetRoot_NoOps_LeavesEverythingUntouched()
    {
        // A plain dev/portable run: AppContext.BaseDirectory is the project's own bin/publish
        // output, nowhere near %TEMP%\.net - must not touch or even look inside _assemblyParent.
        string devBinDir = Path.Combine(_tempDir, "bin", "Release", "net8.0");
        Directory.CreateDirectory(devBinDir);
        string sibling = CreateExtractionDir("sibling-hash-1");

        SelfExtractCleanup.CleanupSiblingExtractionDirs(devBinDir, _tempNetRoot);

        Assert.True(Directory.Exists(sibling));
    }

    [Fact]
    public void UnderTempNetRoot_DeletesSiblingDirs_KeepsOwnDir()
    {
        string ownDir = CreateExtractionDir("own-hash");
        string sibling1 = CreateExtractionDir("sibling-hash-1");
        string sibling2 = CreateExtractionDir("sibling-hash-2");

        SelfExtractCleanup.CleanupSiblingExtractionDirs(ownDir, _tempNetRoot);

        Assert.True(Directory.Exists(ownDir));
        Assert.False(Directory.Exists(sibling1));
        Assert.False(Directory.Exists(sibling2));
    }

    [Fact]
    public void UnderTempNetRoot_NoSiblings_OwnDirSurvives_NoThrow()
    {
        string ownDir = CreateExtractionDir("own-hash-only");

        SelfExtractCleanup.CleanupSiblingExtractionDirs(ownDir, _tempNetRoot);

        Assert.True(Directory.Exists(ownDir));
    }

    [Fact]
    public void ParentMissing_NoOp_DoesNotThrow()
    {
        string ownDir = Path.Combine(_assemblyParent, "own-hash");
        // ownDir itself need not exist for the path-prefix/no-op logic to be exercised safely -
        // this covers a first-ever launch where the .NET host hasn't even created the parent yet.
        SelfExtractCleanup.CleanupSiblingExtractionDirs(ownDir, _tempNetRoot);
    }

    [Fact]
    public void SiblingLocked_SkippedWithoutThrowing_OthersStillRemoved()
    {
        string ownDir = CreateExtractionDir("own-hash");
        string lockedSibling = CreateExtractionDir("locked-sibling");
        string removableSibling = CreateExtractionDir("removable-sibling");

        string lockedFile = Path.Combine(lockedSibling, "payload.dll");
        using (var stream = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            SelfExtractCleanup.CleanupSiblingExtractionDirs(ownDir, _tempNetRoot);

            // Held open with FileShare.None - Directory.Delete must fail and be swallowed, leaving
            // the folder in place for a future sweep once whatever process holds it exits.
            Assert.True(Directory.Exists(lockedSibling));
        }

        Assert.True(Directory.Exists(ownDir));
        Assert.False(Directory.Exists(removableSibling));
    }
}
