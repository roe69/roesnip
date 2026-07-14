using System;
using System.IO;
using RoeSnip.Core.Diagnostics;
using Xunit;

namespace RoeSnip.Core.Tests.Diagnostics;

public sealed class CrashMarkerTests : IDisposable
{
    private readonly string _tempDir;

    public CrashMarkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RoeSnipCrashMarkerTests_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Write_CreatesFileWithVersionAndUtcTimestamp()
    {
        CrashMarker.Write(_tempDir, "1.8.0");

        string path = Path.Combine(_tempDir, "crash-marker.txt");
        Assert.True(File.Exists(path));
        string content = File.ReadAllText(path);
        Assert.Contains("1.8.0", content);
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}Z", content);
    }

    [Fact]
    public void Write_CreatesDirectoryIfMissing()
    {
        Assert.False(Directory.Exists(_tempDir));

        CrashMarker.Write(_tempDir, "1.8.0");

        Assert.True(Directory.Exists(_tempDir));
        Assert.True(File.Exists(Path.Combine(_tempDir, "crash-marker.txt")));
    }

    [Fact]
    public void Write_CalledTwice_OverwritesRatherThanAppends()
    {
        CrashMarker.Write(_tempDir, "1.7.0");
        CrashMarker.Write(_tempDir, "1.8.0");

        string content = File.ReadAllText(Path.Combine(_tempDir, "crash-marker.txt"));
        Assert.DoesNotContain("1.7.0", content);
        Assert.Contains("1.8.0", content);
    }

    [Fact]
    public void Write_WithUnwritableDirectory_DoesNotThrow()
    {
        // A file path masquerading as a directory: Directory.CreateDirectory fails hard against it,
        // exercising the same failure shape as a locked/permission-denied config directory in the
        // field — the whole point of this class is that its caller (an unhandled-exception handler
        // already unwinding) can never be handed a second exception.
        string blockingFile = Path.Combine(Path.GetTempPath(), "RoeSnipCrashMarkerBlock_" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blockingFile, "not a directory");
        try
        {
            string unwritableDir = Path.Combine(blockingFile, "nested");

            var exception = Record.Exception(() => CrashMarker.Write(unwritableDir, "1.8.0"));

            Assert.Null(exception);
        }
        finally
        {
            try { File.Delete(blockingFile); } catch { /* best-effort cleanup */ }
        }
    }
}
