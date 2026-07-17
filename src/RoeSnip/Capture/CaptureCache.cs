using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Capture;

/// <summary>Persisted per-monitor memo of "Desktop Duplication is broken here" (the NVIDIA + HDR
/// black-frame quirk, DESIGN.md "Failure modes"), keyed by GDI DeviceName. The old memo was a
/// process-lifetime static, so every fresh app launch re-paid the doomed DD attempt (device
/// creation + the AcquireNextFrame retry budget, per monitor) before falling back to WGC. This
/// cache lives in its own small JSON file at <c>%APPDATA%\RoeSnip\capture-cache.json</c>
/// (deliberately NOT settings.json — it's derived machine state, not user preference) so the
/// very first capture after a relaunch goes straight to WGC for known-broken monitors.
/// Contract: loaded lazily on first query, saved on first change; a missing or corrupt file means
/// an empty memo — reading or writing this cache must never crash or block a capture.</summary>
public sealed class CaptureCache
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string CacheFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RoeSnip", "capture-cache.json");

    /// <summary>The process-wide instance backed by the real %APPDATA% file. Static so the
    /// in-memory memo also spans CaptureService instances (they are created per capture).</summary>
    public static CaptureCache Default { get; } = new(CacheFilePath);

    private readonly string _path;
    private readonly object _gate = new();
    private HashSet<string>? _ddBrokenDeviceNames; // null until first use (lazy load)

    // Post-resume grace (sleep-stall fix): 0 = never resumed. Static, not per-instance — the
    // resume event is machine state and CaptureService creates a fresh instance per capture.
    private static long s_lastResumeTick;
    private static readonly long PostResumeGraceMs = 30_000;

    /// <summary>Called by TrayApp when the machine resumes from sleep. For the next ~30 s,
    /// <see cref="MarkDesktopDuplicationBroken"/> is a no-op: the wake transition produces
    /// TRANSIENT capture failures (ghost "WinDisc" displays mid-topology-settle, secure-desktop
    /// access denials, DXGI outputs that don't exist yet) that used to poison the persisted
    /// "DD is broken here" memo FOREVER — this cache is meant to memoize the permanent
    /// NVIDIA+HDR black-frame quirk, not a driver that hasn't finished waking. A capture during
    /// the window still falls back to WGC normally; it just isn't memoized.</summary>
    public static void NotePowerResume() =>
        System.Threading.Volatile.Write(ref s_lastResumeTick, Environment.TickCount64);

    private static bool InPostResumeGrace()
    {
        long resumed = System.Threading.Volatile.Read(ref s_lastResumeTick);
        return resumed != 0 && Environment.TickCount64 - resumed < PostResumeGraceMs;
    }

    /// <summary>Path-taking constructor, used by tests to point at an isolated temp path instead
    /// of the real %APPDATA% (same pattern as SettingsStore's path-taking overloads).</summary>
    public CaptureCache(string path)
    {
        _path = path;
    }

    /// <summary>True if Desktop Duplication was previously observed broken for this monitor —
    /// callers should skip DD entirely and go straight to WGC.</summary>
    public bool IsDesktopDuplicationBroken(string deviceName)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _ddBrokenDeviceNames!.Contains(deviceName);
        }
    }

    /// <summary>Records that Desktop Duplication is broken for this monitor and saves the cache
    /// file (best-effort; a failed save is logged and the in-memory memo still applies for the
    /// rest of the process). Saves only when the entry is actually new.</summary>
    public void MarkDesktopDuplicationBroken(string deviceName)
    {
        if (InPostResumeGrace())
        {
            FileLog.Write(
                $"RoeSnip: DD failure for {deviceName} within the post-resume grace window - " +
                "not memoized (transient wake-time failures must not poison the DD-broken cache).");
            return;
        }
        lock (_gate)
        {
            EnsureLoaded();
            if (!_ddBrokenDeviceNames!.Add(deviceName))
            {
                return; // already known — nothing changed, no rewrite
            }
            SaveBestEffort();
        }
    }

    private void EnsureLoaded()
    {
        if (_ddBrokenDeviceNames is not null)
        {
            return;
        }

        _ddBrokenDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            string json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            var dto = JsonSerializer.Deserialize<CacheDto>(json, JsonOptions);
            if (dto?.DdBrokenDeviceNames is null)
            {
                return;
            }

            foreach (string name in dto.DdBrokenDeviceNames)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _ddBrokenDeviceNames.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            // Corrupt/unreadable cache = empty memo; a capture must never fail because of this
            // file. (Broader catch than SettingsStore on purpose — this file is disposable derived
            // state, not user data.)
            FileLog.Write($"RoeSnip: capture cache unreadable/corrupt, starting empty: {ex.Message}");
        }
    }

    private void SaveBestEffort()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var dto = new CacheDto
            {
                DdBrokenDeviceNames = _ddBrokenDeviceNames!.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            };
            File.WriteAllText(_path, JsonSerializer.Serialize(dto, JsonOptions));
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: failed to save capture cache: {ex.Message}");
        }
    }

    /// <summary>On-disk shape of capture-cache.json. Unknown fields are ignored on load
    /// (forward compat), matching SettingsStore's behavior.</summary>
    private sealed class CacheDto
    {
        public int SchemaVersion { get; set; } = 1;
        public List<string>? DdBrokenDeviceNames { get; set; }
    }
}
