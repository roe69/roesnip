# RoeSnip

RoeSnip is an HDR-correct screenshot tool for Windows.

## Why

On HDR/OLED monitors (or SDR monitors with Windows 11's Advanced Color Management enabled),
Windows composites the desktop in linear scRGB (16-bit float per channel, where `1.0 = 80 nits`
and values can exceed `1.0` for HDR highlights). Legacy screenshot tools — Snipping Tool,
Lightshot, ShareX's default capture path, etc. — grab that buffer and either hard-clip it or
divide it by the wrong constant, which is exactly why screenshots taken with HDR enabled come out
washed-out and gray.

RoeSnip captures the true FP16 scRGB frame straight from Desktop Duplication and does the
conversion correctly:

- Screenshots of ordinary SDR content (the common case) come out **pixel-identical** to a
  screenshot taken on a plain SDR monitor — exact pass-through, matched to the "SDR content
  brightness" slider (SDR white level).
- Genuine HDR highlights get a smooth, hue-preserving rolloff into 8-bit sRGB instead of being
  blown out or looking flat.
- You can optionally save the untouched HDR original alongside the PNG as a `.jxr` (JPEG XR) file,
  the same way Xbox Game Bar does — useful for archiving or re-editing later without having
  already lost the highlight detail.

See `DESIGN.md` for the full behavioral spec and `PLAN.md` for the implementation plan.

## Build

Requires the .NET 8 SDK on Windows (the app is Windows-only — WPF + Desktop Duplication + WIC).

```
dotnet build RoeSnip.sln -c Debug
```

## Test

```
dotnet test
```

This runs the full unit test suite: color-math and tone-map golden values, JPEG XR round-trip
(verifies HDR highlight data actually survives the encoder), and settings persistence
(fail-closed on a missing/corrupt file, atomic save).

## Run

With no arguments, RoeSnip starts as a tray app:

```
dotnet run --project src/RoeSnip
```

It also has a CLI test mode that captures and exits without showing any UI — handy for scripting
and for verifying HDR handling on a given monitor without going through the overlay:

```
dotnet run --project src/RoeSnip -- --diag
dotnet run --project src/RoeSnip -- --capture --monitor 0 --out shot.png --jxr
```

- `--diag` prints per-monitor diagnostics (device name, resolution, Advanced Color on/off, SDR
  white level, max luminance) and exits.
- `--capture [--monitor N] [--out path] [--jxr]` captures one or all monitors, writes a PNG
  (default name `roesnip_capture_monitorN.png`), prints per-monitor stats (min/max/avg captured
  nits), and — with `--jxr` — also writes an untouched HDR `.jxr` copy next to the PNG.

## Default hotkey

**PrintScreen** freezes the screen and opens the region-selection overlay. If Windows is
configured to intercept a bare PrintScreen for Snipping Tool
(`HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled` non-zero), RoeSnip asks once,
on first run, whether to turn that off or fall back to **Ctrl+PrintScreen** instead. The hotkey is
changeable from the tray icon's Settings window at any time.

## Settings

Stored as JSON at:

```
%APPDATA%\RoeSnip\settings.json
```

Covers the hotkey, save directory (defaults to `Pictures\RoeSnip`), whether to always save an HDR
copy alongside the PNG, advanced tone-map knee/peak overrides, run-at-Windows-startup, and
copy-on-select. If this file is missing or corrupt, RoeSnip silently falls back to defaults in
memory without touching the file on disk (fail-closed) — a broken settings file is never
overwritten, so it can still be inspected or repaired by hand.

## Publish

Single-file, self-contained win-x64 build:

```
dotnet publish src/RoeSnip -p:PublishProfile=win-x64
```

Output lands in `src/RoeSnip/bin/Release/net8.0-windows10.0.22621.0/win-x64/publish/RoeSnip.exe`.
