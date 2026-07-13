namespace RoeSnip.Core.Recording;

/// <summary>The two recordable output containers - mirrors the WPF app's own
/// <c>RoeSnip.RecordingFormat</c> (Program.cs) verbatim in shape, moved into Core so the portable
/// RecordingController (item 20) can name it without a WPF-side reference.</summary>
public enum RecordingFormat { Mp4, Gif }
