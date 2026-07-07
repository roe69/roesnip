# scksnap — RoeSnip's macOS capture helper

Small Swift binary implementing WP-X5's capture matrix (PLAN-XPLAT.md §3.5 / DESIGN-XPLAT.md).
ScreenCaptureKit is async-only and not sanely drivable via portable P/Invoke, so the real capture
logic lives here; `src/RoeSnip.Platform.MacOS/ScksnapHelperClient.cs` shells out to this binary and
parses its output. **Not buildable on the Windows dev machine** — built by the GitHub Actions
workflow `.github/workflows/build-scksnap.yml` on a macOS runner.

## CLI contract (keep in sync with ScksnapHelperClient.cs)

```
scksnap list                  # JSON display array on stdout; no TCC permission needed; exit 0
scksnap capture <displayID>   # writes header+pixels temp file, prints its path on stdout; exit 0
```

`list` JSON element shape (keys sorted):
`{"edrHeadroom":2.0,"edrPotentialHeadroom":2.0,"heightPx":2234,"id":1,"isPrimary":true,
"name":"Built-in Display","scale":2.0,"widthPx":3456,"x":0,"y":0}` — `x/y/widthPx/heightPx` are physical
pixels (global desktop coords, top-left origin), `scale` is pixels-per-point, `edrHeadroom` is the
current NSScreen max EDR component value (1.0 = SDR), `edrPotentialHeadroom` the display capability.

Exit codes: `0` success, `1` generic capture failure, `2` usage, `3` display not found,
**`82` TCC Screen Recording denied** (first-class error — surfaced in the UI, not a generic failure).

Capture matrix: macOS 15+ on Apple Silicon with an EDR-capable display →
`SCScreenshotManager` + `SCStreamConfiguration.captureDynamicRange = .hdrLocalDisplay` → FP16 RGBA
extended-linear-sRGB (EDR convention: 1.0 == SDR white → `SdrWhiteInBufferUnits = 1.0`);
macOS 14 / Intel / SDR display → SCK plain SDR → BGRA8 sRGB passthrough; pre-14 →
`CGDisplayCreateImage` → BGRA8. Always the FULL display (`captureImageInRect` is 15.2+ only;
cropping happens in Core on the .NET side).

The binary temp-file wire format ("SCKSNAP1", 96-byte little-endian header + raw pixel rows) is
documented field-by-field at the top of `Sources/Scksnap.swift` and mirrored by
`ScksnapHelperClient.ParseFrameFile`.

## Build (what the workflow does)

```sh
swiftc -parse-as-library -O -target arm64-apple-macos13.0  -o scksnap-arm64  Sources/Scksnap.swift
swiftc -parse-as-library -O -target x86_64-apple-macos13.0 -o scksnap-x86_64 Sources/Scksnap.swift
lipo -create -output scksnap scksnap-arm64 scksnap-x86_64
codesign --force --sign - --identifier net.roelite.roesnip.scksnap scksnap
```

Ad-hoc signed with a **stable identifier** for TCC Screen Recording attribution (DESIGN-XPLAT.md;
same precedent as the jxa-helper). Caveat: ad-hoc signatures have a per-build designated
requirement, so a TCC grant may still need re-granting after a rebuild on some macOS versions —
the stable identifier keeps the System Settings entry coherent, it is not a full Developer-ID
substitute.

## Shipping

The Actions artifact is `scksnap.tar.gz` (tar preserves the executable bit + signature, which
`upload-artifact`'s zip does not). Deploy by extracting `scksnap` next to the published `RoeSnip`
executable (flat publish) or into `Contents/MacOS/` (app bundle) — `ScksnapHelperClient` looks in
`AppContext.BaseDirectory`, which is that directory in both layouts; `ROESNIP_SCKSNAP_PATH`
overrides the location for testing.
