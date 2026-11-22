using System;
using System.IO;
using System.Linq;
using RoeSnip.Core.Updates;
using Xunit;

namespace RoeSnip.Core.Tests.Updates;

public sealed class UpdateStagingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roesnip_staging_" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    public UpdateStagingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Stem => Path.Combine(_dir, "RoeSnip.AppImage.new");

    private string Touch(string name, TimeSpan age, bool directory = false)
    {
        string path = Path.Combine(_dir, name);
        if (directory)
        {
            Directory.CreateDirectory(path);
            Directory.SetLastWriteTimeUtc(path, _now - age);
        }
        else
        {
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, _now - age);
        }

        return path;
    }

    [Fact]
    public void ForProcess_IsUniquePerPid()
    {
        Assert.Equal(Stem + "-42", UpdateStaging.ForProcess(Stem, 42));
        Assert.NotEqual(UpdateStaging.ForProcess(Stem, 42), UpdateStaging.ForProcess(Stem, 43));
    }

    [Theory]
    [InlineData("RoeSnip.AppImage.new", true)]
    [InlineData("RoeSnip.AppImage.new-1234", true)]
    [InlineData("RoeSnip.AppImage.new-1234.gz", true)]
    [InlineData("RoeSnip.AppImage.new.gz", true)]
    [InlineData("RoeSnip.AppImage.new-", false)]
    [InlineData("RoeSnip.AppImage", false)]
    [InlineData("RoeSnip.AppImage.old", false)]
    [InlineData("RoeSnip.AppImage.restore", false)]
    [InlineData("RoeSnip.AppImage.update.lock", false)]
    [InlineData("RoeSnip.AppImage.newer", false)]
    public void IsStagingName_MatchesOnlyStagingLeftovers(string name, bool expected) =>
        Assert.Equal(expected, UpdateStaging.IsStagingName(name, "RoeSnip.AppImage.new", StringComparison.Ordinal));

    [Fact]
    public void FindStale_SkipsFreshDownloads_AndNonStagingFiles()
    {
        string oldLegacy = Touch("RoeSnip.AppImage.new", TimeSpan.FromHours(2));
        string oldPid = Touch("RoeSnip.AppImage.new-100", TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        Touch("RoeSnip.AppImage.new-200", TimeSpan.FromMinutes(5)); // another updater's in-flight download
        Touch("RoeSnip.AppImage.old", TimeSpan.FromDays(3));
        Touch("RoeSnip.AppImage.restore", TimeSpan.FromDays(3));
        Touch("RoeSnip.AppImage", TimeSpan.FromDays(3));

        var stale = UpdateStaging.FindStale(_dir, Stem, _now, UpdateStaging.StaleAge, StringComparison.Ordinal);

        Assert.Equal(new[] { oldLegacy, oldPid }.OrderBy(p => p), stale.OrderBy(p => p));
    }

    [Fact]
    public void FindStale_FindsMacStagingDirectoriesAndArchives()
    {
        string macStem = Path.Combine(_dir, ".RoeSnip-update");
        string oldDir = Touch(".RoeSnip-update-7", TimeSpan.FromHours(3), directory: true);
        string oldTar = Touch(".RoeSnip-update-7.app.tar.gz", TimeSpan.FromHours(3));
        Touch(".RoeSnip-update-8", TimeSpan.FromMinutes(1), directory: true);
        Touch("RoeSnip.app.update.lock", TimeSpan.FromDays(1));

        var stale = UpdateStaging.FindStale(_dir, macStem, _now, UpdateStaging.StaleAge, StringComparison.Ordinal);

        Assert.Equal(new[] { oldDir, oldTar }.OrderBy(p => p), stale.OrderBy(p => p));
    }

    [Fact]
    public void FindStale_MissingDirectory_IsEmpty() =>
        Assert.Empty(UpdateStaging.FindStale(Path.Combine(_dir, "nope"), Stem, _now, UpdateStaging.StaleAge, StringComparison.Ordinal));
}
