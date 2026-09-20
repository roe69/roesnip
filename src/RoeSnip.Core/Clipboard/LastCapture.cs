namespace RoeSnip.Core.Clipboard;

/// <summary>A still capture the resident is holding on to after the overlay closed, so it can be
/// put back on the clipboard or written to a file without being taken again.
///
/// The recording flow keeps a finished take reachable through its own prompt and a staged file
/// (see <see cref="ClipboardStaging"/>); a still has neither - the overlay IS its post-capture UI,
/// and confirming closes it - so the pixels are what gets kept, and the tray menu is where they
/// are acted on.
///
/// Raw BGRA8 rather than an SdrImage because the two apps have their own copies of that type (the
/// frozen WPF RoeSnip.Imaging.SdrImage and RoeSnip.Core.Imaging.SdrImage); each wraps
/// <see cref="Pixels"/> back into its own, which costs nothing - the buffer is shared, not
/// copied.</summary>
public sealed record KeptCapture(int Width, int Height, byte[] Pixels, DateTime TakenLocal)
{
    /// <summary>What the capture is, for a menu label: size and when it was taken, so the user can
    /// tell whether the thing being offered is the one they want back.</summary>
    public string Describe() => $"{Width} x {Height}, {TakenLocal:HH:mm}";

    /// <summary>A free path in <paramref name="directory"/> named for the moment the capture was
    /// taken - the same roesnip_yyyyMMdd_HHmmss.png name the overlay's own Save offers, so a
    /// capture saved from the tray is indistinguishable from one saved on the spot. Saving the
    /// same kept capture twice must not overwrite the first file, hence the _2/_3 suffixes. The
    /// directory is not created here; the caller does that and reports its own failure.</summary>
    public string SuggestSavePath(string directory)
    {
        string stem = $"roesnip_{TakenLocal:yyyyMMdd_HHmmss}";
        string path = Path.Combine(directory, stem + ".png");
        for (int n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(directory, $"{stem}_{n}.png");
        }
        return path;
    }
}

/// <summary>The one still capture the resident is holding, replaced by the next one.
///
/// In memory, never on disk: the pixels are already the bytes that went to the clipboard, and a
/// temp file would be one more thing to prune (and to leak). One capture costs its own selection -
/// a few MB typically, ~33 MB for a whole 4K screen - and is dropped the moment another capture
/// replaces it or the process exits. Nothing expires it on a timer: an offer that quietly stops
/// working is worse than the memory.</summary>
public static class LastCapture
{
    private static KeptCapture? s_current;

    /// <summary>The kept capture, or null when nothing has been captured yet this session.</summary>
    public static KeptCapture? Current => Volatile.Read(ref s_current);

    /// <summary>Raised, on whichever thread did it, when the kept capture is replaced or dropped.
    /// For UI that cannot ask lazily: a WinForms menu re-reads Current when it opens, a native tray
    /// menu has no such hook and has to be told.</summary>
    public static event Action? Changed;

    public static void Keep(int width, int height, byte[] pixels, DateTime takenLocal)
    {
        Volatile.Write(ref s_current, new KeptCapture(width, height, pixels, takenLocal));
        Changed?.Invoke();
    }

    public static void Clear()
    {
        Volatile.Write(ref s_current, null);
        Changed?.Invoke();
    }
}
