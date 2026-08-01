using System.Text;
using RoeSnip.Core.Clipboard;
using Xunit;

namespace RoeSnip.Core.Tests.Clipboard;

public sealed class DropFilesPayloadTests
{
    [Fact]
    public void Build_SinglePath_HasTheHeaderShellExpects()
    {
        byte[] payload = DropFilesPayload.Build(new[] { @"C:\temp\clip.gif" });

        // pFiles: the file list starts immediately after the 20-byte DROPFILES header.
        Assert.Equal(DropFilesPayload.HeaderSize, BitConverter.ToInt32(payload, 0));
        // pt.x, pt.y, fNC: all meaningless for a clipboard copy, all zero.
        Assert.Equal(0, BitConverter.ToInt32(payload, 4));
        Assert.Equal(0, BitConverter.ToInt32(payload, 8));
        Assert.Equal(0, BitConverter.ToInt32(payload, 12));
        // fWide: the names are UTF-16, not ANSI. Getting this wrong makes the shell read the path
        // as mojibake rather than failing outright, so it is asserted explicitly.
        Assert.Equal(1, BitConverter.ToInt32(payload, 16));
    }

    [Fact]
    public void Build_SinglePath_EndsWithTwoNulls()
    {
        const string path = @"C:\temp\clip.gif";
        byte[] payload = DropFilesPayload.Build(new[] { path });

        string names = Encoding.Unicode.GetString(payload, DropFilesPayload.HeaderSize,
            payload.Length - DropFilesPayload.HeaderSize);

        // One terminator for the name, one more for the list: a single-file CF_HDROP ends "\0\0".
        Assert.Equal(path + "\0\0", names);
    }

    [Fact]
    public void Build_MultiplePaths_SeparatesWithNullsAndTerminatesTheList()
    {
        byte[] payload = DropFilesPayload.Build(new[] { @"C:\a.mp4", @"C:\b.gif" });

        string names = Encoding.Unicode.GetString(payload, DropFilesPayload.HeaderSize,
            payload.Length - DropFilesPayload.HeaderSize);

        Assert.Equal("C:\\a.mp4\0C:\\b.gif\0\0", names);
    }

    [Fact]
    public void Build_NonAsciiPath_RoundTripsAsUtf16()
    {
        const string path = @"C:\temp\スクリーン ショット.mp4";
        byte[] payload = DropFilesPayload.Build(new[] { path });

        string names = Encoding.Unicode.GetString(payload, DropFilesPayload.HeaderSize,
            payload.Length - DropFilesPayload.HeaderSize);

        Assert.Equal(path + "\0\0", names);
    }

    [Fact]
    public void Build_NoPaths_Throws()
    {
        Assert.Throws<ArgumentException>(() => DropFilesPayload.Build(Array.Empty<string>()));
    }

    [Fact]
    public void Build_BlankPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => DropFilesPayload.Build(new[] { "   " }));
    }
}
