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
