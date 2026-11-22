# scksnap - RoeSnip's macOS capture helper

Small Swift binary holding all ScreenCaptureKit work (it is async-only and not sanely drivable
via portable P/Invoke). `src/RoeSnip.Platform.MacOS/` talks to it: `ScksnapServer.cs` to the
long-lived serve mode, `ScksnapHelperClient.cs` to the one-shot verbs (the fallback). **Not
buildable on the Windows/Linux dev machines** - built by `.github/workflows/build-scksnap.yml`
and the macOS leg of `release.yml` on macOS runners.

## Verbs

```
scksnap list                         # JSON display array on stdout; no TCC permission needed; exit 0
scksnap capture <displayID>          # one-shot screenshot -> temp SCKSNAP1 file, prints its path
scksnap stream-capture <displayID>   # same output through the kept-open-stream path (A/B checks)
scksnap serve                        # long-lived; length-prefixed protocol on stdin/stdout
```

`list` JSON element shape (keys sorted):
`{"edrHeadroom":2.0,"edrPotentialHeadroom":2.0,"heightPx":2234,"id":1,"isPrimary":true,
"name":"Built-in Display","scale":2.0,"widthPx":3456,"x":0,"y":0}` - `x/y/widthPx/heightPx` are physical
pixels (global desktop coords, top-left origin), `scale` is pixels-per-point, `edrHeadroom` is the
current NSScreen max EDR component value (1.0 = SDR), `edrPotentialHeadroom` the display capability.

Exit codes (and serve status codes): `0` success, `1` generic failure, `2` usage / bad request,
`3` display not found, **`82` TCC Screen Recording denied** (first-class, surfaced in the UI).

The serve protocol table is at the top of `Sources/Serve.swift`, the SCKSNAP1 frame table at the
top of `Sources/Scksnap.swift`; `ScksnapProtocol.cs` mirrors both (unit tests in
`tests/RoeSnip.Core.Tests/ScksnapProtocolTests.cs`). Keep all three in sync.

Capture matrix: macOS 15+ on Apple Silicon with an EDR-capable display -> HDR (FP16 RGBA
extended-linear-sRGB, 1.0 == SDR white); otherwise BGRA8 sRGB; macOS 13 one-shot uses
`CGDisplayCreateImage`. Always the full display; cropping happens on the .NET side (snapshots)
or in the helper (GIF region frames, MP4 sourceRect). The parent process (RoeSnip) is excluded
from every capture.

## Build (what the workflows do)

```sh
for arch in arm64 x86_64; do
  swiftc -parse-as-library -O -target ${arch}-apple-macos13.0 \
    -Xlinker -sectcreate -Xlinker __TEXT -Xlinker __info_plist -Xlinker Info.plist \
    -o scksnap-${arch} Sources/*.swift
done
lipo -create -output scksnap scksnap-arm64 scksnap-x86_64
codesign --force --sign - --identifier net.roelite.roesnip.scksnap scksnap
```

`Info.plist` is embedded for `NSMicrophoneUsageDescription`; the app bundle's Info.plist
(release.yml) carries the same key, since macOS attributes the helper's microphone use to
RoeSnip.app. Ad-hoc signed with a stable identifier for TCC attribution; ad-hoc signatures
change per build, so a grant may need re-granting after an update.

## Shipping

The Actions artifact is `scksnap.tar.gz` (tar keeps the executable bit and signature). Deploy by
extracting `scksnap` next to the published `RoeSnip` executable (flat publish) or into
`Contents/MacOS/` (app bundle) - the client looks in `AppContext.BaseDirectory`;
`ROESNIP_SCKSNAP_PATH` overrides the location for testing.
