using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Settings;

namespace RoeSnip.Core.Capture;

/// <summary>Persisted per-monitor memo of "Desktop Duplication is broken here" (the NVIDIA + HDR
/// black-frame quirk, DESIGN.md "Failure modes"), keyed by GDI DeviceName. The old memo was a
/// process-lifetime static, so every fresh app launch re-paid the doomed DD attempt (device
/// creation + the AcquireNextFrame retry budget, per monitor) before falling back to WGC. This
/// cache lives in its own small JSON file at <c>capture-cache.json</c> in the portable config
/// directory (deliberately NOT settings.json — it's derived machine state, not user preference) so
/// the very first capture after a relaunch goes straight to WGC for known-broken monitors.
/// Contract: loaded lazily on first query, saved on first change; a missing or corrupt file means
/// an empty memo — reading or writing this cache must never crash or block a capture.</summary>
public sealed class CaptureCache
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string CacheFilePath => Path.Combine(ConfigPaths.ConfigDirectory, "capture-cache.json");

    /// <summary>The process-wide instance backed by the real per-OS config file. Static so the
    /// in-memory memo also spans CaptureService instances (they are created per capture).</summary>
    public static CaptureCache Default { get; } = new(CacheFilePath);

    private readonly string _path;
    private readonly object _gate = new();
    private HashSet<string>? _ddBrokenDeviceNames; // null until first use (lazy load)

    /// <summary>Path-taking constructor, used by tests to point at an isolated temp path instead
    /// of the real config directory (same pattern as SettingsStore's path-taking overloads).</summary>
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

    /// <summary>Removes a memo entry (used by FallbackCaptureBackend's stale-memo self-healing:
    /// when capture fails everywhere while memoized capturers were skipped, the memo may be wrong).
    /// Saves only when something was actually removed.</summary>
    public void Unmark(string deviceName)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (!_ddBrokenDeviceNames!.Remove(deviceName))
            {
                return;
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
            return;
        }

        MigrateBareDeviceNameEntries();
    }

    /// <summary>C1 audit fix: the pre-generalization cache (the frozen WPF app, and this cache's
    /// own shape before FallbackCaptureBackend's per-capturer-slot keying landed) stored bare
    /// <c>DeviceName</c> entries with no <c>"::{capturerIndex}"</c> suffix.
    /// <see cref="FallbackCaptureBackend"/> now keys per-capturer-slot as
    /// <c>"{DeviceName}::{i}"</c>, so a bare entry never matches anything and is silently dead
    /// weight — meaning an upgrading NVIDIA+HDR user would re-pay the doomed Desktop Duplication
    /// attempt once per monitor after upgrading, exactly the cold-start cost this cache exists to
    /// avoid. Migrate bare entries to <c>"{DeviceName}::0"</c> (slot 0 == the primary capturer —
    /// Desktop Duplication on Windows, the portal on Linux) once, on load, and drop the bare form.
    /// This is the cache loader's job regardless of whether anything currently produces a bare
    /// entry — with P5's config-directory split this only matters again once the two apps
    /// converge onto one shared cache file.</summary>
    private void MigrateBareDeviceNameEntries()
    {
        List<string>? bareEntries = null;
        foreach (string name in _ddBrokenDeviceNames!)
        {
            if (!name.Contains("::", StringComparison.Ordinal))
            {
                (bareEntries ??= new List<string>()).Add(name);
            }
        }
        if (bareEntries is null)
        {
            return;
        }

        bool changed = false;
        foreach (string bare in bareEntries)
        {
            _ddBrokenDeviceNames!.Remove(bare);
            if (_ddBrokenDeviceNames!.Add($"{bare}::0"))
            {
                changed = true;
            }
        }
        if (changed)
        {
            SaveBestEffort();
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
