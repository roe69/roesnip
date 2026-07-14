using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Core.Settings;

/// <summary>JSON persistence for <see cref="RoeSnipSettings"/> at <c>settings.json</c> in the
/// portable per-OS config directory (<see cref="ConfigPaths.ConfigDirectory"/>). Fail-closed per
/// DESIGN.md: any problem reading the file (missing, empty, unreadable, malformed JSON) returns
/// <see cref="RoeSnipSettings.Default"/> WITHOUT writing to or otherwise touching the file — a
/// corrupt file on disk is left exactly as it was found so a user/support session can inspect it
/// later. <see cref="Save(RoeSnipSettings)"/> writes atomically (temp file in the same directory,
/// then <see cref="File.Replace(string, string, string?)"/> / <see cref="File.Move(string, string)"/>)
/// so a crash mid-write can never leave a half-written settings.json.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // Every RoeSnipSettings field is documented to stay JSON-primitive so the file remains
        // human-editable and forward/backward compatible across builds that add or reorder enum
        // members - but Core's settings graph carries fields (Sharing/ShareProviderConfig's nested
        // ProviderSpec.UploadKind/ResponseUrlMode, and the still-to-land GifSizePreset/Mp4SizePreset)
        // whose underlying types embed raw enums that were never routed through that string-parsing
        // convention and would otherwise persist as bare ordinals. A JsonStringEnumConverter fixes
        // that for every enum in the settings graph at once (member NAME survives a reorder; an
        // unknown/hand-typed name throws JsonException same as any other malformed field, which
        // Load's fail-closed catch below already treats as "start from defaults" rather than
        // crashing) - added here ahead of time, mirroring the WPF app's own App/Settings.cs, so it's
        // in place BEFORE any enum field lands rather than as an afterthought retrofit.
        Converters = { new JsonStringEnumConverter() },
    };

    public static string SettingsDirectory => ConfigPaths.ConfigDirectory;

    public static string SettingsFilePath => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>Loads settings from disk. Missing directory/file, empty file, or a file that fails
    /// to parse as valid JSON (or otherwise doesn't deserialize) all return
    /// <see cref="RoeSnipSettings.Default"/> without writing or overwriting anything. Unknown JSON
    /// fields are silently ignored (forward compat); fields absent from the file fall back to the
    /// record's own default values (System.Text.Json does this automatically for record properties
    /// with initializers, since a missing field simply never assigns over the default).</summary>
    public static RoeSnipSettings Load() => Load(SettingsFilePath);

    /// <summary>Overload used by tests to point at an isolated temp path instead of the real
    /// config directory — unit tests must never touch the user's real settings file.</summary>
    public static RoeSnipSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return RoeSnipSettings.Default;
            }

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return RoeSnipSettings.Default;
            }

            var settings = JsonSerializer.Deserialize<RoeSnipSettings>(json, JsonOptions);
            return settings ?? RoeSnipSettings.Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            FileLog.Write($"RoeSnip: settings file unreadable/corrupt, using defaults: {ex.Message}");
            return RoeSnipSettings.Default;
        }
    }

    /// <summary>Atomically saves settings to the portable config directory's settings.json.</summary>
    public static void Save(RoeSnipSettings settings) => Save(settings, SettingsFilePath);

    /// <summary>Overload used by tests to point at an isolated temp path.</summary>
    public static void Save(RoeSnipSettings settings, string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(settings, JsonOptions);

        string tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tempPath, json);

        try
        {
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        catch
        {
            // Best-effort cleanup of the temp file if the atomic swap itself failed.
            try { File.Delete(tempPath); } catch { /* ignore */ }
            throw;
        }
    }
}
