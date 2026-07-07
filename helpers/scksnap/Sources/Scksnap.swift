// Scksnap.swift — RoeSnip's macOS capture helper ("scksnap").
//
// WP-X5 (PLAN-XPLAT.md §3.5). ScreenCaptureKit is async-only and not sanely drivable via portable
// P/Invoke, so all real capture logic lives in this small Swift binary; the .NET side
// (RoeSnip.Platform.MacOS/ScksnapHelperClient.cs) shells out to it and parses its output.
//
// Verbs:
//   scksnap list
//       Prints a JSON array of active displays to stdout (CoreGraphics enumeration + NSScreen
//       metadata — requires NO Screen Recording permission) and exits 0.
//   scksnap capture <displayID>
//       Captures the FULL display (never a sub-rect — captureImageInRect is macOS 15.2+ only;
//       cropping is the .NET side's job), writes header + raw pixels to a temp file, prints that
//       file's path to stdout, exits 0. The caller deletes the temp file.
//
// Capture matrix (DESIGN-XPLAT.md):
//   macOS 15+ AND Apple Silicon AND display reports EDR potential > 1.0:
//       SCScreenshotManager with SCStreamConfiguration(preset: .captureHDRScreenshotLocalDisplay)
//       -> FP16 RGBA, extended linear sRGB (EDR convention: 1.0 == SDR reference white).
//   macOS 14 (or Intel, or SDR-only display):
//       SCScreenshotManager plain (SDR) -> BGRA8 sRGB.
//   pre-14 (i.e. macOS 13, the binary's own build floor -- see helpers/scksnap/README.md):
//       CGDisplayCreateImage -> BGRA8 sRGB.
//
// Exit codes (mirrored by ScksnapHelperClient.cs — keep in sync):
//   0   success
//   1   capture/encode failure (generic)
//   2   usage error
//   3   display not found
//   82  TCC Screen Recording permission denied (first-class, UI-surfaced error)
//
// Wire format "SCKSNAP1" (all integers/doubles little-endian; both macOS archs are LE):
//   offset  size  field
//   0       8     magic: ASCII "SCKSNAP1"
//   8       4     headerSize (UInt32) — byte offset of pixel data; 96 in this version
//   12      4     formatCode (UInt32): 1 = FP16 RGBA extended-linear-sRGB (8 B/px, R,G,B,A halfs),
//                                      2 = BGRA8 sRGB (4 B/px, B,G,R,A bytes)
//   16      4     width  (UInt32, pixels)
//   20      4     height (UInt32, pixels)
//   24      4     strideBytes (UInt32) — tightly packed: width * bytesPerPixel
//   28      4     displayId (UInt32, CGDirectDisplayID)
//   32      4     boundsX (Int32, physical px, global desktop coords, top-left origin)
//   36      4     boundsY (Int32)
//   40      4     boundsW (Int32)
//   44      4     boundsH (Int32)
//   48      8     scale (Float64) — display backing scale (pixels per point)
//   56      8     edrHeadroom (Float64) — max EDR component value at capture time; 1.0 = SDR
//   64      4     isPrimary (UInt32, 0/1)
//   68      28    reserved (zeros)
//   96      ...   pixel data, strideBytes * height bytes, row 0 = top row

import AppKit
import CoreGraphics
import Darwin
import Foundation
import ScreenCaptureKit

let EXIT_GENERIC_FAILURE: Int32 = 1
let EXIT_USAGE: Int32 = 2
let EXIT_NO_DISPLAY: Int32 = 3
let EXIT_TCC_DENIED: Int32 = 82

let HEADER_SIZE: UInt32 = 96
let FORMAT_FP16_RGBA: UInt32 = 1
let FORMAT_BGRA8: UInt32 = 2

func fail(_ message: String, code: Int32) -> Never {
    FileHandle.standardError.write(Data((message + "\n").utf8))
    exit(code)
}

func usage() -> Never {
    fail("usage: scksnap list | scksnap capture <displayID>", code: EXIT_USAGE)
}

// MARK: - Display enumeration (CoreGraphics + NSScreen; no TCC required)

struct DisplayInfo: Codable {
    let id: UInt32
    let name: String
    let x: Int
    let y: Int
    let widthPx: Int
    let heightPx: Int
    let scale: Double
    let edrHeadroom: Double          // current max EDR component value (1.0 = SDR right now)
    let edrPotentialHeadroom: Double // display capability (potential max)
    let isPrimary: Bool
}

func activeDisplayIDs() -> [CGDirectDisplayID] {
    var count: UInt32 = 0
    guard CGGetActiveDisplayList(0, nil, &count) == .success, count > 0 else { return [] }
    var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
    guard CGGetActiveDisplayList(count, &ids, &count) == .success else { return [] }
    return Array(ids.prefix(Int(count)))
}

@MainActor
func nsScreen(for displayID: CGDirectDisplayID) -> NSScreen? {
    let key = NSDeviceDescriptionKey("NSScreenNumber")
    for screen in NSScreen.screens {
        if let number = screen.deviceDescription[key] as? NSNumber, number.uint32Value == displayID {
            return screen
        }
    }
    return nil
}

@MainActor
func displayInfo(for id: CGDirectDisplayID) -> DisplayInfo {
    let boundsPt = CGDisplayBounds(id) // points, global desktop coords, top-left origin
    let mode = CGDisplayCopyDisplayMode(id)
    let pixelW = mode?.pixelWidth ?? CGDisplayPixelsWide(id)
    let pixelH = mode?.pixelHeight ?? CGDisplayPixelsHigh(id)
    let scale = boundsPt.width > 0 ? Double(pixelW) / Double(boundsPt.width) : 1.0

    var name = "Display \(id)"
    var current = 1.0
    var potential = 1.0
    if let screen = nsScreen(for: id) {
        name = screen.localizedName
        current = max(Double(screen.maximumExtendedDynamicRangeColorComponentValue), 1.0)
        potential = max(Double(screen.maximumPotentialExtendedDynamicRangeColorComponentValue), 1.0)
    }

    // Physical-pixel global bounds: points * this display's own scale. NOTE (documented caveat, not
    // a bug): macOS global coordinates are point-based; with MIXED per-display scales a single
    // physical-pixel virtual-desktop coordinate space is an approximation. Same-scale setups (the
    // common case, incl. every laptop-only setup) are exact.
    return DisplayInfo(
        id: id,
        name: name,
        x: Int((boundsPt.origin.x * CGFloat(scale)).rounded()),
        y: Int((boundsPt.origin.y * CGFloat(scale)).rounded()),
        widthPx: pixelW,
        heightPx: pixelH,
        scale: scale,
        edrHeadroom: current,
        edrPotentialHeadroom: potential,
        isPrimary: CGDisplayIsMain(id) != 0)
}

@MainActor
func runList() -> Never {
    var infos = [DisplayInfo]()
    for id in activeDisplayIDs() {
        infos.append(displayInfo(for: id))
    }
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.sortedKeys]
    guard let json = try? encoder.encode(infos) else {
        fail("scksnap: JSON encoding failed", code: EXIT_GENERIC_FAILURE)
    }
    FileHandle.standardOutput.write(json)
    FileHandle.standardOutput.write(Data("\n".utf8))
    exit(0)
}

// MARK: - TCC (Screen Recording permission)

func ensureScreenRecordingPermission() {
    if CGPreflightScreenCaptureAccess() { return }
    // Shows the one-time system prompt (and registers this helper in System Settings > Privacy &
    // Security > Screen Recording). Returns immediately; the user's decision lands asynchronously.
    _ = CGRequestScreenCaptureAccess()
    if !CGPreflightScreenCaptureAccess() {
        fail("scksnap: Screen Recording permission is denied. Grant it to 'scksnap' in "
            + "System Settings > Privacy & Security > Screen Recording, then retry.",
            code: EXIT_TCC_DENIED)
    }
}

func isAppleSilicon() -> Bool {
    var value: Int32 = 0
    var size = MemoryLayout<Int32>.size
    if sysctlbyname("hw.optional.arm64", &value, &size, nil, 0) == 0 {
        return value == 1
    }
    return false
}

// MARK: - Capture

func handleSckError(_ error: Error) -> Never {
    if let scError = error as? SCStreamError, scError.code == .userDeclined {
        fail("scksnap: Screen Recording permission is denied (SCStreamError.userDeclined). Grant it "
            + "in System Settings > Privacy & Security > Screen Recording, then retry.",
            code: EXIT_TCC_DENIED)
    }
    let ns = error as NSError
    fail("scksnap: capture failed: \(ns.domain) \(ns.code): \(ns.localizedDescription)",
        code: EXIT_GENERIC_FAILURE)
}

@available(macOS 14.0, *)
func captureViaScreenCaptureKit(
    displayID: CGDirectDisplayID, pixelWidth: Int, pixelHeight: Int, hdr: Bool) async -> CGImage {
    do {
        let content = try await SCShareableContent.excludingDesktopWindows(
            false, onScreenWindowsOnly: true)
        guard let display = content.displays.first(where: { $0.displayID == displayID }) else {
            fail("scksnap: display \(displayID) not found in shareable content", code: EXIT_NO_DISPLAY)
        }
        let filter = SCContentFilter(display: display, excludingWindows: [])

        // HDR (P1 audit fix): hand-building a plain SCStreamConfiguration and only setting
        // captureDynamicRange left every other property (notably pixelFormat) at its default
        // 8-bit BGRA, which silently clamps HDR content back to SDR — captureDynamicRange alone
        // does not repoint the pixel format/color space to something that can actually hold
        // headroom above 1.0. Apple's preset initializer configures the whole HDR screenshot
        // pipeline (dynamic range + pixel format + color space) correctly in one step; this is
        // the WWDC24 "Capture HDR content with ScreenCaptureKit" recommended way to request an
        // HDR screenshot. Width/height/cursor/resolution are still applied on top afterward,
        // exactly as the non-HDR path does.
        let config: SCStreamConfiguration
        if hdr, #available(macOS 15.0, *) {
            config = SCStreamConfiguration(preset: .captureHDRScreenshotLocalDisplay)
        } else {
            config = SCStreamConfiguration()
        }
        config.width = pixelWidth
        config.height = pixelHeight
        config.showsCursor = false
        config.captureResolution = .best
        return try await SCScreenshotManager.captureImage(
            contentFilter: filter, configuration: config)
    } catch {
        handleSckError(error)
    }
}

/// Renders any CGImage (HDR or SDR, whatever its source colorspace/encoding) into FP16 RGBA in the
/// EXTENDED LINEAR sRGB colorspace — Core's Fp16ScRgb layout under the macOS EDR convention
/// (1.0 == SDR reference white; HDR highlights land above 1.0, up to the EDR headroom). Extended
/// colorspaces do not clamp, and CoreGraphics performs the source->dest conversion itself. This is
/// the documented EDR bitmap layout (floatComponents | byteOrder16Little | premultipliedLast at
/// 16 bits/component). Screen captures are opaque (alpha == 1), so premultiplied == straight alpha.
func renderFp16ExtendedLinearSrgb(_ image: CGImage) -> Data? {
    let width = image.width
    let height = image.height
    let stride = width * 8
    guard width > 0, height > 0,
          let space = CGColorSpace(name: CGColorSpace.extendedLinearSRGB) else { return nil }
    var data = Data(count: stride * height)
    let ok = data.withUnsafeMutableBytes { (buf: UnsafeMutableRawBufferPointer) -> Bool in
        let info = CGBitmapInfo.floatComponents.rawValue
            | CGBitmapInfo.byteOrder16Little.rawValue
            | CGImageAlphaInfo.premultipliedLast.rawValue
        guard let ctx = CGContext(
            data: buf.baseAddress, width: width, height: height,
            bitsPerComponent: 16, bytesPerRow: stride, space: space, bitmapInfo: info)
        else { return false }
        ctx.interpolationQuality = .none
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: CGFloat(width), height: CGFloat(height)))
        return true
    }
    return ok ? data : nil
}

/// Renders a CGImage into BGRA8 sRGB (Core's Bgra8Srgb passthrough layout). byteOrder32Little +
/// premultipliedFirst is BGRA in memory; screen content is opaque so premultiplied == straight.
func renderBgra8Srgb(_ image: CGImage) -> Data? {
    let width = image.width
    let height = image.height
    let stride = width * 4
    guard width > 0, height > 0,
          let space = CGColorSpace(name: CGColorSpace.sRGB) else { return nil }
    var data = Data(count: stride * height)
    let ok = data.withUnsafeMutableBytes { (buf: UnsafeMutableRawBufferPointer) -> Bool in
        let info = CGBitmapInfo.byteOrder32Little.rawValue
            | CGImageAlphaInfo.premultipliedFirst.rawValue
        guard let ctx = CGContext(
            data: buf.baseAddress, width: width, height: height,
            bitsPerComponent: 8, bytesPerRow: stride, space: space, bitmapInfo: info)
        else { return false }
        ctx.interpolationQuality = .none
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: CGFloat(width), height: CGFloat(height)))
        return true
    }
    return ok ? data : nil
}

// MARK: - Wire format writing

func appendLE<T: FixedWidthInteger>(_ value: T, to data: inout Data) {
    withUnsafeBytes(of: value.littleEndian) { data.append(contentsOf: $0) }
}

func writeFrameFile(
    formatCode: UInt32, width: Int, height: Int, stride: Int,
    info: DisplayInfo, pixels: Data) -> Never {
    var out = Data()
    out.append(contentsOf: Array("SCKSNAP1".utf8))
    appendLE(HEADER_SIZE, to: &out)
    appendLE(formatCode, to: &out)
    appendLE(UInt32(width), to: &out)
    appendLE(UInt32(height), to: &out)
    appendLE(UInt32(stride), to: &out)
    appendLE(info.id, to: &out)
    appendLE(Int32(info.x), to: &out)
    appendLE(Int32(info.y), to: &out)
    appendLE(Int32(info.widthPx), to: &out)
    appendLE(Int32(info.heightPx), to: &out)
    appendLE(info.scale.bitPattern, to: &out)
    appendLE(info.edrHeadroom.bitPattern, to: &out)
    appendLE(UInt32(info.isPrimary ? 1 : 0), to: &out)
    out.append(Data(count: Int(HEADER_SIZE) - out.count)) // reserved padding to HEADER_SIZE
    out.append(pixels)

    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("scksnap-\(UUID().uuidString).bin")
    do {
        try out.write(to: url)
    } catch {
        fail("scksnap: failed to write \(url.path): \(error.localizedDescription)",
            code: EXIT_GENERIC_FAILURE)
    }
    print(url.path)
    exit(0)
}

// MARK: - Entry point

@MainActor
func runCapture(displayID: CGDirectDisplayID) async -> Never {
    guard activeDisplayIDs().contains(displayID) else {
        fail("scksnap: no active display with id \(displayID)", code: EXIT_NO_DISPLAY)
    }
    ensureScreenRecordingPermission()

    let info = displayInfo(for: displayID)
    // HDR presets: macOS 15+ on Apple Silicon only (DESIGN-XPLAT.md capture matrix); additionally
    // require the display to actually be EDR-capable so SDR-only displays flow through the same
    // Bgra8Srgb passthrough path Windows SDR monitors use.
    let wantHdr = isAppleSilicon() && info.edrPotentialHeadroom > 1.0

    var image: CGImage? = nil
    var hdrCaptured = false
    if #available(macOS 15.0, *), wantHdr {
        image = await captureViaScreenCaptureKit(
            displayID: displayID, pixelWidth: info.widthPx, pixelHeight: info.heightPx, hdr: true)
        hdrCaptured = true
    } else if #available(macOS 14.0, *) {
        image = await captureViaScreenCaptureKit(
            displayID: displayID, pixelWidth: info.widthPx, pixelHeight: info.heightPx, hdr: false)
    } else {
        image = CGDisplayCreateImage(displayID) // pre-14 fallback (deprecated in 15; fine pre-14)
    }
    guard let captured = image else {
        fail("scksnap: capture produced no image", code: EXIT_GENERIC_FAILURE)
    }

    if hdrCaptured {
        guard let pixels = renderFp16ExtendedLinearSrgb(captured) else {
            fail("scksnap: FP16 conversion failed", code: EXIT_GENERIC_FAILURE)
        }
        writeFrameFile(
            formatCode: FORMAT_FP16_RGBA, width: captured.width, height: captured.height,
            stride: captured.width * 8, info: info, pixels: pixels)
    } else {
        guard let pixels = renderBgra8Srgb(captured) else {
            fail("scksnap: BGRA8 conversion failed", code: EXIT_GENERIC_FAILURE)
        }
        writeFrameFile(
            formatCode: FORMAT_BGRA8, width: captured.width, height: captured.height,
            stride: captured.width * 4, info: info, pixels: pixels)
    }
}

@main
struct Scksnap {
    @MainActor
    static func main() async {
        let args = Array(CommandLine.arguments.dropFirst())
        guard let verb = args.first else { usage() }
        switch verb {
        case "list":
            runList()
        case "capture":
            guard args.count == 2, let rawId = UInt32(args[1]) else { usage() }
            await runCapture(displayID: CGDirectDisplayID(rawId))
        default:
            usage()
        }
    }
}
