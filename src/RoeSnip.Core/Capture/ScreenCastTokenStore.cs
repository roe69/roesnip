using RoeSnip.Core.Settings;

namespace RoeSnip.Core.Capture;

/// <summary>Persists the xdg-desktop-portal ScreenCast restore_token (persist_mode=2) so the share
/// dialog appears once. A token is single-use: every successful Start returns a new one that must
/// replace the old. The file is written atomically and, on Unix, readable by the owner only.</summary>
public sealed class ScreenCastTokenStore
{
    private readonly string _path;

    public ScreenCastTokenStore(string path) => _path = path;

    /// <summary>The store for one session purpose ("snapshot", "recording") in the app config dir.</summary>
    public static ScreenCastTokenStore ForPurpose(string purpose)
        => new(Path.Combine(ConfigPaths.ConfigDirectory, $"screencast-{purpose}.token"));

    public string FilePath => _path;

    /// <summary>The saved token, or null when none is saved or the file is unreadable/blank.</summary>
    public string? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            string token = File.ReadAllText(_path).Trim();
            return token.Length == 0 ? null : token;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Saves <paramref name="token"/>; null or blank deletes the file. Never throws.</summary>
    public void Save(string? token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                if (File.Exists(_path)) File.Delete(_path);
                return;
            }
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, token.Trim());
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // best-effort: a lost token only means the share dialog shows again
        }
    }
}
