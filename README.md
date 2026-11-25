# RoeSnip: HDR screenshot tool and screen recorder for Windows, Linux and macOS

[![Latest release](https://img.shields.io/github/v/release/roe69/roesnip?label=release&logo=github&color=FF6B35)](https://github.com/roe69/roesnip/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/roe69/roesnip/total?logo=github&color=FF6B35)](https://github.com/roe69/roesnip/releases)
[![GitHub stars](https://img.shields.io/github/stars/roe69/roesnip?style=flat&logo=github&color=FF6B35)](https://github.com/roe69/roesnip/stargazers)
[![Windows 10 / 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=data:image/svg%2bxml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAyNCAyNCI+PHBhdGggZmlsbD0id2hpdGUiIGQ9Ik0wIDBoMTEuNHYxMS40SDB6TTEyLjYgMEgyNHYxMS40SDEyLjZ6TTAgMTIuNmgxMS40VjI0SDB6TTEyLjYgMTIuNkgyNFYyNEgxMi42eiIvPjwvc3ZnPg==)](https://github.com/roe69/roesnip/releases/latest/download/RoeSnip.exe)
[![Linux](https://img.shields.io/badge/Linux-AppImage-FCC624?logo=linux&logoColor=black)](https://github.com/roe69/roesnip/releases/latest/download/RoeSnip-linux-x64.AppImage)
[![macOS](https://img.shields.io/badge/macOS-dmg-6E6E73?logo=apple&logoColor=white)](https://github.com/roe69/roesnip/releases/latest/download/RoeSnip-osx-arm64.dmg)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)

RoeSnip is a free, open source snipping tool and screen recorder that takes
**HDR-correct screenshots**. Press PrintScreen, the screen dims instantly, drag
over the part you want, draw on it if you like, then copy, save or upload it.
You can also record the same area as an MP4 or GIF.
It runs on **Windows, Linux and macOS**.

![RoeSnip capture overlay: region selection with annotation toolbar](docs/screenshot.png)

## Download

Click the link for your computer. The file starts downloading right away. You
do not need to install .NET or anything else.

| Your computer | Download |
|---|---|
| **Windows 10 (2004 or newer) or 11** | **[RoeSnip.exe](https://github.com/roe69/roesnip/releases/latest/download/RoeSnip.exe)** |
| Windows on ARM | [RoeSnipApp-win-arm64.exe](https://github.com/roe69/roesnip/releases/latest/download/RoeSnipApp-win-arm64.exe) |
| **Mac with Apple silicon (M1 or newer)** | **[RoeSnip-osx-arm64.dmg](https://github.com/roe69/roesnip/releases/latest/download/RoeSnip-osx-arm64.dmg)** |
| Mac with an Intel chip | [RoeSnip-osx-x64.dmg](https://github.com/roe69/roesnip/releases/latest/download/RoeSnip-osx-x64.dmg) |
| **Linux, Intel / AMD (64-bit)** | **[RoeSnip-linux-x64.AppImage](https://github.com/roe69/roesnip/releases/latest/download/RoeSnip-linux-x64.AppImage)** |
| Linux, ARM | [RoeSnip-linux-arm64.AppImage](https://github.com/roe69/roesnip/releases/latest/download/RoeSnip-linux-arm64.AppImage) |

Not sure which Mac you have? Apple menu, About This Mac: "Chip: Apple M..." is
Apple silicon, "Processor: Intel" is Intel.

### How to open it

- **Windows:** double-click the file. RoeSnip starts as an icon in the system
  tray (bottom right, you may need to click the small arrow).
- **Mac:** open the .dmg and drag RoeSnip to Applications. The first time,
  right-click RoeSnip and choose Open. Then allow Screen Recording when macOS
  asks.
- **Linux:** in your file manager, right-click the AppImage, open Properties
  and tick "Allow executing as program" (the wording differs between desktops),
  then double-click it. If your file manager has no such option, run
  `chmod +x RoeSnip-linux-x64.AppImage` once in a terminal.

Then right-click the RoeSnip tray icon and pick **Install RoeSnip**. That keeps
RoeSnip in a permanent place, starts it when you log in and turns on automatic
updates. On Windows, a copy you only double-clicked from Downloads works fine
but does not update itself until you install it.

**About the other files on the release page.** The release page also lists
`.gz` and `.tar.gz` files. RoeSnip downloads those by itself when it updates
(they are smaller). You do not need them.

## How to use

1. Press **PrintScreen**. The screen dims and freezes.
2. **Drag** over the area you want. Press **Esc** to cancel.
3. A toolbar appears next to your selection. Use it to:
   - **Annotate:** rectangle, ellipse, arrow, line, freehand pen, highlighter,
     pixelate (to hide private details) and text. Pick a colour and a size.
     Click any shape you drew to move or resize it. Undo and redo are in the
     toolbar (Ctrl+Z, Ctrl+Y).
   - **Copy** to the clipboard (Ctrl+C), then paste it anywhere.
   - **Save** as a PNG file (Ctrl+S). The folder is set in Settings.
   - **Upload** with the share button (Ctrl+U) to your default share provider.
     Set one up in Settings first.
   - **Record** the selected area as an **MP4 or GIF**. Press PrintScreen again
     to stop and save the recording.
4. **Colour picker:** turn on "Colour picker" in Settings, then click once
   without dragging to read the colour under the pointer (hex, RGB and HSL).

**Tray icon menu.** Right-click the RoeSnip icon for: **Capture**, **Settings**,
**About**, **Check for updates**, **Install RoeSnip** (until it is installed)
and **Exit**. After a capture, **Copy again** and **Save last capture** also
show up, in case you need the last snip a second time.

**Settings.** Change the hotkey, the save folder, the copy and upload keys,
whether RoeSnip starts at login, how often it checks for updates, whether to
copy to the clipboard as soon as you select, and the share provider.

## HDR-correct, in plain words

On an HDR screen, Windows works with brightness levels far beyond what a normal
picture file can hold. Many screenshot tools squash that wrongly, so your
screenshot looks gray, faded or blown out. RoeSnip takes the real HDR frame and
converts it carefully: normal content looks exactly like a screenshot from a
regular monitor, and only truly bright HDR highlights are gently pulled down
instead of clipped. What you see on screen is what you get in the file.

| | Windows 10 2004+ / 11 | Linux (X11, Wayland) | macOS |
|---|---|---|---|
| HDR-correct capture | Yes | Yes on GNOME 50 or newer with HDR turned on for the monitor; normal (SDR) otherwise | Asks macOS for an HDR screenshot on macOS 15 or newer with Apple silicon; SDR otherwise |
| Keep the untouched HDR original (.jxr) | Yes | Yes, for HDR captures | Yes, for HDR screenshots |
| Annotate, colour picker, MP4 / GIF recording | Yes | Yes | Yes |

## Updates

An installed RoeSnip checks GitHub in the background and updates itself. Use
**Check for updates** in the tray menu to check right now. RoeSnip is under
active development and new versions come out often.

Linux and macOS notes:

- **Linux (Wayland):** the first PrintScreen shows your desktop's screen share
  dialog. Choose all monitors or Entire screen, then Share. After that, captures
  are instant.
- **macOS:** the app is only ad-hoc signed, so macOS may ask for Screen
  Recording permission again after an update.
- **Linux without FUSE:** the AppImage needs FUSE. If your system has none, use
  `RoeSnip-linux-x64.tar.gz` or `RoeSnip-linux-arm64.tar.gz` from the
  [release page](https://github.com/roe69/roesnip/releases/latest). That build
  only tells you when a new version is out, it does not update itself.

## Like RoeSnip?

Give it a star: click **Star** at the top right of
[the RoeSnip GitHub page](https://github.com/roe69/roesnip). It helps other
people with HDR screens find it.

## Found a bug? Want a feature?

- **Report a problem or ask for something:**
  [open an issue](https://github.com/roe69/roesnip/issues/new). Say which
  system and RoeSnip version you use, what you did and what you expected.
- **Fixed something yourself?** Pull requests are welcome. See
  [CONTRIBUTING.md](CONTRIBUTING.md).

Please attach the log file to a bug report. It records each capture and any
error. The previous log sits next to it as `roesnip.log.1`.

| System | Log file |
|---|---|
| Windows (`RoeSnip.exe`) | `%APPDATA%\RoeSnip\roesnip.log` |
| Windows (`RoeSnipApp-win-*.exe`) | `%APPDATA%\RoeSnip.App\roesnip.log` |
| Linux | `~/.config/roesnip-app/roesnip.log` |
| macOS | `~/Library/Application Support/RoeSnip.App/roesnip.log` |

## FAQ

### Why are my screenshots washed out on HDR?
With HDR on, Windows composites the desktop at HDR brightness levels. Tools that
treat that as an ordinary 8-bit picture clip the highlights and mis-scale the
midtones, so the result looks gray, faded or blown out. RoeSnip reads the full
frame and tone-maps it: normal content is scaled by your "SDR content
brightness" setting to the exact SDR result, and only real HDR highlights are
rolled off.

### How do I take a screenshot in HDR on Windows 11?
Install RoeSnip, press PrintScreen, drag a region, then copy or save. To also
keep the HDR original, turn on "Save HDR copy" in Settings. It writes a `.jxr`
file next to the PNG.

### Is there an HDR screenshot fix for the Snipping Tool?
RoeSnip replaces it for this purpose. On Windows it asks once whether to take
PrintScreen over from the Snipping Tool. If you say no, it uses Ctrl+PrintScreen
instead. You can change the hotkey in Settings.

### Can I record my screen in HDR?
Recordings are saved as MP4 or GIF from the tone-mapped frames, so colours match
RoeSnip's screenshots. They are normal (SDR) files, not HDR video.

### Is RoeSnip a Lightshot alternative?
It covers the same workflow (hotkey, drag a region, annotate, copy, save,
upload) and adds correct HDR handling on Windows plus MP4 and GIF recording.

### Does it work on Linux and macOS?
Yes. On Linux, HDR captures work on GNOME 50 or newer when HDR is turned on for
that monitor; other setups capture normal (SDR) pictures. macOS uses the HDR
screenshot option only on macOS 15 or newer with Apple silicon.

### Is it free?
Yes. MIT licensed and open source.

## For developers

Build instructions, the command line, the test automation and the design docs
are in [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE).
