using System;
using System.IO;
using Avalonia.Platform.Storage;
using RoeSnip.Core.Recording;

namespace RoeSnip.App.Recording;

/// <summary>File name, extension and picker filter for a saved take - the same values as the WPF
/// app's RecordingController.PickSavePath (roesnip_yyyyMMdd_HHmmss.mp4|.gif, "MP4 video (*.mp4)",
/// "GIF image (*.gif)", AddExtension).</summary>
public static class RecordingSaveNames
{
    public const string AutosaveEnvVar = "ROESNIP_RECORD_AUTOSAVE";

    public static string Extension(RecordingFormat format) => format == RecordingFormat.Mp4 ? ".mp4" : ".gif";

    public static string SuggestedFileName(RecordingFormat format, DateTime now) =>
        $"roesnip_{now:yyyyMMdd_HHmmss}{Extension(format)}";

    /// <summary>Test hook: the save target inside ROESNIP_RECORD_AUTOSAVE, or null when it is unset.</summary>
    public static string? AutosavePath(RecordingFormat format, DateTime now)
    {
        string? dir = Environment.GetEnvironmentVariable(AutosaveEnvVar);
        return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, SuggestedFileName(format, now));
    }

    /// <summary>WPF's AddExtension: a name typed without an extension gets the take's own. A name
    /// with another extension is the user's choice and is kept.</summary>
    public static string EnsureExtension(string path, RecordingFormat format) =>
        Path.HasExtension(path) ? path : path + Extension(format);

    public static FilePickerFileType FileType(RecordingFormat format) => format == RecordingFormat.Mp4
        ? new FilePickerFileType("MP4 video (*.mp4)")
        {
            Patterns = new[] { "*.mp4" },
            MimeTypes = new[] { "video/mp4" },
            AppleUniformTypeIdentifiers = new[] { "public.mpeg-4" },
        }
        : new FilePickerFileType("GIF image (*.gif)")
        {
            Patterns = new[] { "*.gif" },
            MimeTypes = new[] { "image/gif" },
            AppleUniformTypeIdentifiers = new[] { "com.compuserve.gif" },
        };
}
