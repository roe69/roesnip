using System;
using System.IO;
using RoeSnip.Core.Diagnostics;
using Xunit;

namespace RoeSnip.Core.Tests.Diagnostics;

/// <summary>FileLog is a process-wide static sink, so every test here first drives it into a known
/// state (Initialize with its own isolated temp directory, or ResetForTests for the
/// before-Initialize case) rather than relying on xunit's test execution order within the class.</summary>
public sealed class FileLogTests : IDisposable
{
    private readonly string _tempDir;

    public FileLogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RoeSnipFileLogTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // Leave the sink pointed nowhere real so a later test class's own Console.Error-only
        // assumptions (if any is ever added) aren't surprised by a stale temp path.
        FileLog.ResetForTests();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Write_AfterInitialize_AppendsTimestampedLineToLogFile()
    {
        FileLog.Initialize(_tempDir);

        FileLog.Write("hello from a test");

        string logPath = Path.Combine(_tempDir, "roesnip.log");
        Assert.True(File.Exists(logPath));
        string content = File.ReadAllText(logPath);
        Assert.Contains("hello from a test", content);
        // UTC-timestamped: a bracketed ISO-ish prefix precedes the message on its line.
        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}Z\] hello from a test", content.TrimEnd('\r', '\n'));
    }

    [Fact]
    public void Write_MultipleCalls_AppendsRatherThanOverwrites()
    {
        FileLog.Initialize(_tempDir);

        FileLog.Write("first line");
        FileLog.Write("second line");

        string content = File.ReadAllText(Path.Combine(_tempDir, "roesnip.log"));
        Assert.Contains("first line", content);
        Assert.Contains("second line", content);
        Assert.True(content.IndexOf("first line", StringComparison.Ordinal) < content.IndexOf("second line", StringComparison.Ordinal));
    }

    [Fact]
    public void Initialize_RotatesOversizedLogFile()
    {
        string logPath = Path.Combine(_tempDir, "roesnip.log");
        // Seed a log file already past the 1 MB rotation threshold before Initialize ever runs,
        // mirroring a tray resident that's been up for a long time across many launches.
        File.WriteAllText(logPath, new string('x', 1024 * 1024 + 1));

        FileLog.Initialize(_tempDir);

        string rotatedPath = logPath + ".1";
        Assert.True(File.Exists(rotatedPath));
        Assert.True(new FileInfo(rotatedPath).Length > 1024 * 1024);
        // The active file was moved out from under itself, not copied — nothing oversized remains
        // at the live path until the next Write() recreates it fresh.
        Assert.False(File.Exists(logPath));

        FileLog.Write("fresh after rotation");
        string freshContent = File.ReadAllText(logPath);
        Assert.Contains("fresh after rotation", freshContent);
        Assert.True(freshContent.Length < 1024 * 1024);
    }

    [Fact]
    public void Initialize_RotationOverwritesAnyExistingPreviousLogFile()
    {
        string logPath = Path.Combine(_tempDir, "roesnip.log");
        string rotatedPath = logPath + ".1";
        File.WriteAllText(rotatedPath, "stale rotated content from an even earlier run");
        File.WriteAllText(logPath, new string('y', 1024 * 1024 + 1));

        FileLog.Initialize(_tempDir);

        // At most one previous file: the new rotation overwrote the stale .1 rather than piling up.
        string rotatedContent = File.ReadAllText(rotatedPath);
        Assert.DoesNotContain("stale rotated content", rotatedContent);
        Assert.True(rotatedContent.Length > 1024 * 1024);
    }

    [Fact]
    public void Write_WithLockedLogFile_DoesNotThrow()
    {
        FileLog.Initialize(_tempDir);
        string logPath = Path.Combine(_tempDir, "roesnip.log");

        using (var lockedStream = new FileStream(logPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var exception = Record.Exception(() => FileLog.Write("this line can't be written, file is locked"));
            Assert.Null(exception);
        }
    }

    [Fact]
    public void Write_BeforeInitialize_DoesNotThrow()
    {
        FileLog.ResetForTests();

        var exception = Record.Exception(() => FileLog.Write("no sink configured yet"));

        Assert.Null(exception);
    }
}
