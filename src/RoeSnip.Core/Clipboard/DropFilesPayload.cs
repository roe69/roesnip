using System.Text;

namespace RoeSnip.Core.Clipboard;

/// <summary>Builds the CF_HDROP clipboard payload: the byte layout Explorer, Discord, Slack, Word
/// and every other "paste a file" target reads when you copy a file in Explorer. This is the only
/// clipboard format that can carry a GIF/MP4 - there is no "animated image" clipboard format on
/// Windows, so a recording goes on the clipboard as a FILE REFERENCE, which is exactly what the
/// paste targets that matter accept.
///
/// Layout (DROPFILES followed by the file list, one contiguous HGLOBAL block):
///   DWORD pFiles  - byte offset from the start of the block to the first file name (= 20 here)
///   POINT pt      - drop point, meaningless for a clipboard copy, zeroed
///   BOOL  fNC     - non-client drop, zeroed
///   BOOL  fWide   - 1: the names below are UTF-16, not ANSI
///   then each full path as a null-terminated UTF-16 string, with one extra null terminating the
///   whole list (so a single path ends in TWO nulls).
///
/// Pure byte packing with no Win32 dependency so it is unit-testable and shared by both apps; the
/// HGLOBAL allocation and SetClipboardData call live in each app's own platform layer.</summary>
public static class DropFilesPayload
{
    /// <summary>sizeof(DROPFILES): DWORD + POINT(2 x LONG) + BOOL + BOOL, no padding on either
    /// architecture (all fields are 4 bytes and 4-byte aligned).</summary>
    public const int HeaderSize = 20;

    public static byte[] Build(IReadOnlyList<string> fullPaths)
    {
        ArgumentNullException.ThrowIfNull(fullPaths);
        if (fullPaths.Count == 0)
        {
            throw new ArgumentException("CF_HDROP needs at least one path.", nameof(fullPaths));
        }

        var names = new StringBuilder();
        foreach (string path in fullPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("CF_HDROP paths must be non-empty.", nameof(fullPaths));
            }
            names.Append(path).Append('\0');
        }
        names.Append('\0'); // list terminator, on top of the last name's own terminator

        byte[] nameBytes = Encoding.Unicode.GetBytes(names.ToString());
        byte[] payload = new byte[HeaderSize + nameBytes.Length];

        WriteInt32(payload, 0, HeaderSize); // pFiles
        // pt.x, pt.y, fNC all stay zero.
        WriteInt32(payload, 16, 1); // fWide = TRUE (the names are UTF-16)
        nameBytes.CopyTo(payload, HeaderSize);
        return payload;
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}
