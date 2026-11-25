# Contributing to RoeSnip

Bug reports, feature requests and pull requests are welcome. To just use
RoeSnip, see the [README](README.md).

- Bugs and ideas: [open an issue](https://github.com/roe69/roesnip/issues/new)
  with your OS, the RoeSnip version, what you did and what you expected.
  Attach `roesnip.log` (paths are in the README).
- Fixes: fork, branch, and [open a pull request](https://github.com/roe69/roesnip/pulls)
  against `main`. Keep a change small and say how you tested it.

## Build from source

Needs the .NET 8 SDK. The WPF app (`src/RoeSnip`) needs Windows 10 2004+ to
build; the cross-platform app builds on Windows, Linux and macOS.

```sh
dotnet build RoeSnip.sln -c Release
dotnet test RoeSnip.sln

# single-file exe -> src/RoeSnip/bin/Release/.../win-x64/publish/RoeSnip.exe
dotnet publish src/RoeSnip/RoeSnip.csproj -c Release -p:PublishProfile=win-x64
```

`src/RoeSnip.App` multi-targets, so publishing it needs `-f`, for example
`dotnet publish src/RoeSnip.App -c Release -f net8.0 -r linux-x64 --self-contained`
(`-f net8.0-windows10.0.22621.0` for Windows). Some Core tests that decode PNGs
fail on a Linux box without libSkiaSharp; [TESTING.md](TESTING.md) records what
has been verified on which OS.

Enable the repo's git hooks once per clone: `git config core.hooksPath .githooks`
(they reject non-ASCII and AI-credit lines in commit messages and run a build
check before push).

## Project layout

| Path | What |
|---|---|
| `src/RoeSnip/` | WPF app, Windows only. Tray, overlay, annotations, recording, sharing, self-update. Ships as `RoeSnip.exe`. |
| `src/RoeSnip.App/` | Avalonia app: the cross-platform port (Linux, macOS, and Windows as `RoeSnipApp-win-*.exe`). |
| `src/RoeSnip.Core/` | Shared logic: settings, tone mapping, updates, overlay math, diagnostics. |
| `src/RoeSnip.Platform.Windows/`, `.Linux/`, `.MacOS/` | Per-OS capture, hotkey and recording backends. |
| `helpers/scksnap/` | Swift helper for macOS capture and recording. |
| `tests/` | `RoeSnip.Tests` (WPF), `RoeSnip.Core.Tests`, `RoeSnip.App.Tests`, `RoeSnip.Platform.Windows.Tests`. |
| `docs/PARITY.md` | Checklist of WPF features and their Avalonia status. |

`RoeSnip.App` references `RoeSnip.Platform.Linux` only for Linux builds, so any
use of `RoeSnip.Platform.Linux.*` in App code must sit inside `#if LINUX_BACKEND`.

Design docs: [DESIGN.md](DESIGN.md) (capture paths, tone mapping, overlay
semantics, failure modes), [PLAN.md](PLAN.md), [DESIGN-XPLAT.md](DESIGN-XPLAT.md)
and [PLAN-XPLAT.md](PLAN-XPLAT.md) (the cross-platform port).

## Tools for developers and AI agents

### Command line

`RoeSnip.exe` (WPF) and the Avalonia app share these headless modes:

```sh
RoeSnip.exe --diag
RoeSnip.exe --capture [--monitor N] [--out shot.png] [--jxr]
```

- `--diag` prints per-monitor diagnostics (device, resolution, Advanced Color
  on or off, SDR white level, max luminance) and exits.
- `--capture` captures one or all monitors without the overlay, writes PNGs,
  prints min, max and average captured nits, and with `--jxr` also writes the
  untouched HDR original next to each PNG.

The Avalonia app (`src/RoeSnip.App`) adds two verbs that signal the resident
instance, or start one if none is running:

- `RoeSnip capture` opens the snip overlay (what the hotkey does).
- `RoeSnip settings` opens the Settings window.

Any other argument prints usage and exits 1. `--self-update-now` runs one
update and exits.

### Dev automation pipe

A dev-only JSON pipe lets a test drive the real overlay code without mouse
input. Start the resident with it on, then talk to it with `--auto`:

```sh
RoeSnip.exe --automation            # or set ROESNIP_AUTOMATION=1
RoeSnip.exe --auto state
RoeSnip.exe --auto trigger
RoeSnip.exe --auto '{"cmd":"select","x":100,"y":80,"w":800,"h":600}'
```

Without the flag the pipe does not exist. The pipe is `RoeSnip-Automation`
for the WPF app and `RoeSnip.App-Automation` for the Avalonia app, so the two
never drive each other. Commands include `state`, `trigger`, `select`, `zoom`,
`toast`, `record`, `preset`, `fps`, `chrome`, `escape`, `screenshot`,
`confirm`, `settings`, `lastcapture` and `tray`. [TESTING.md](TESTING.md)
lists every command, its fields and what has been verified.

Never drive RoeSnip with synthetic mouse or UI Automation input (it can steal
real input and gives flaky results). Use the pipe, or ask a person to click.
`--enable-elevated-startup`, `--disable-elevated-startup` and
`--record-smoketest` are internal and not for general use.

### Logs

| App | Log |
|---|---|
| WPF `RoeSnip.exe` | `%APPDATA%\RoeSnip\roesnip.log` |
| Avalonia on Windows | `%APPDATA%\RoeSnip.App\roesnip.log` |
| Linux | `~/.config/roesnip-app/roesnip.log` (screencast tokens sit in the same folder) |
| macOS | `~/Library/Application Support/RoeSnip.App/roesnip.log` |

The previous log is `roesnip.log.1`. Useful lines:

- Windows capture timing: `hotkey-to-dim`, `WGC capture ... session=`,
  `capture-to-overlay N ms`, `first-overlays-visible`, `all-overlays-visible`.
- Windows power and memory: `console display off/on`, `display-on capture
  re-warm`, `idle memory trim`.
- Linux Wayland: `screencast snapshot session open in N ms`, `stream mapping:`,
  `screen share for a capture: Allowed|Declined|Unsupported`, `portal app id
  registered as ...`.
- Linux drag latency: `drag <mode> N moves in T ms ...`. Avalonia's own errors
  go to `journalctl --user | grep 'RoeSnip\['`.
- macOS: `scksnap serve started in N ms`, `scksnap snapshot N/N display(s) in
  N ms`.
- Self-update: `update already in progress by another RoeSnip process;
  skipping`.

To summarize a log, replace digits with `N` and count:
`sed -E 's/[0-9]+/N/g' roesnip.log | sort | uniq -c | sort -rn`.

### Docs to read first

- [docs/PARITY.md](docs/PARITY.md): every WPF feature and its Avalonia status.
- [DESIGN.md](DESIGN.md): capture paths, tone mapping, overlay behavior,
  failure modes.
- [TESTING.md](TESTING.md): the automation pipe and what has been verified on
  which OS.

### Platform notes

- **Linux install:** the tray's Install item moves the AppImage to
  `~/.local/share/RoeSnip/RoeSnip.AppImage` (follows `XDG_DATA_HOME`), adds
  `~/.local/share/applications/net.roelite.roesnip.desktop` and, for start at
  login, `~/.config/autostart/net.roelite.roesnip.desktop`.
- **Linux hotkey:** the SharpHook hook only works on X11. On GNOME under Wayland
  RoeSnip adds a custom keybinding that runs `RoeSnip capture`, and clears
  GNOME's own Print, Shift+Print or Alt+Print binding when the PrintScreen
  takeover setting is on. On KDE Plasma 6 and Hyprland it binds through the
  `org.freedesktop.portal.GlobalShortcuts` portal. If nothing works, bind a
  desktop shortcut to `RoeSnip capture` by hand.
- **Wayland capture:** instant capture reads a kept-open screen share. Its
  dialog only opens from the first capture or the Settings button, never on its
  own. On GNOME the desktop screenshot portal (shutter sound and flash) is not
  used.
- **macOS install:** the tray's Install item copies the app to
  `~/Applications/RoeSnip.app` and adds
  `~/Library/LaunchAgents/net.roelite.roesnip.plist` for start at login.
- **Settings file:** `%APPDATA%\RoeSnip\settings.json` for the WPF app. A
  missing or corrupt file falls back to defaults in memory and is not
  overwritten.

## Parity rule

A change to the WPF app is ported to the Avalonia app, as its own commit, and
ticked off in `docs/PARITY.md`. A change to shared behavior goes in
`RoeSnip.Core` where it can serve both.

## Release assets

Self-update downloads release assets by exact name (`UpdateAssetNames.cs` and
`.github/workflows/release.yml`). Never rename an asset.

## Commit titles

One short, plain sentence saying what changed for the user, in the style of the
existing history:

```
Updates no longer fail when two copies check at once
Linux: PrintScreen freezes the whole screen again, top bar and dock included
Windows: the snip overlay fits every monitor when monitors use different scaling
```

Prefix a platform (`Linux:`, `Windows:`) when the change is only there.
Internal changes with no user-visible effect start with `- `, for example
`- remove unused LinuxOverlayHelper`. Use plain ASCII: no emoji, em dashes or
curly quotes.
