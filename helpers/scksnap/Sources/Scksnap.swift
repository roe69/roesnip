// Scksnap.swift - RoeSnip's macOS capture helper ("scksnap"): entry point, display enumeration,
// one-shot capture and the SCKSNAP1 frame format. Serve.swift holds the long-lived serve mode,
// SnapshotStreams.swift the kept-open snapshot streams, Recorder.swift MP4 recording.
//
// ScreenCaptureKit is async-only and not sanely drivable via portable P/Invoke, so all capture
// logic lives in this Swift binary; the .NET side (RoeSnip.Platform.MacOS) talks to it.
//
// Verbs:
//   scksnap list
//       Prints a JSON array of active displays to stdout (CoreGraphics enumeration + NSScreen
//       metadata - requires NO Screen Recording permission) and exits 0.
//   scksnap capture <displayID>
//       One-shot capture of the FULL display (SCScreenshotManager), writes header + raw pixels to
//       a temp file, prints that file's path to stdout, exits 0. The caller deletes the temp file.
//   scksnap stream-capture <displayID>
//       Same output as `capture`, but through the kept-open-stream path serve mode uses (opens a
//       stream, takes its first frame). For A/B checks of the two paths on a Mac.
//   scksnap serve
//       Long-lived helper speaking the length-prefixed protocol in Serve.swift over stdin/stdout.
//
// Capture matrix (one-shot):
//   macOS 15+ AND Apple Silicon AND display reports EDR potential > 1.0:
//       SCScreenshotManager with SCStreamConfiguration(preset: .captureHDRScreenshotLocalDisplay)
//       -> FP16 RGBA, extended linear sRGB (EDR convention: 1.0 == SDR reference white).
//   macOS 14 (or Intel, or SDR-only display):
//       SCScreenshotManager plain (SDR) -> BGRA8 sRGB.
//   macOS 13 (the build floor): CGDisplayCreateImage -> BGRA8 sRGB.
// Every capture excludes the windows of the parent process (RoeSnip itself), like
// WDA_EXCLUDEFROMCAPTURE on Windows.
//
// Exit codes / serve status codes (mirrored by ScksnapProtocol.cs - keep in sync):
//   0   success
//   1   capture/encode failure (generic)
//   2   usage error / bad request
//   3   display not found
//   82  TCC Screen Recording permission denied (first-class, UI-surfaced error)
//
// Frame format "SCKSNAP1" (all integers/doubles little-endian; both macOS archs are LE):
//   offset  size  field
//   0       8     magic: ASCII "SCKSNAP1"
//   8       4     headerSize (UInt32) - byte offset of pixel data; 96 in this version
//   12      4     formatCode (UInt32): 1 = FP16 RGBA extended-linear-sRGB (8 B/px, R,G,B,A halfs),
//                                      2 = BGRA8 sRGB (4 B/px, B,G,R,A bytes)
//   16      4     width  (UInt32, pixels)
//   20      4     height (UInt32, pixels)
//   24      4     strideBytes (UInt32) - tightly packed: width * bytesPerPixel
//   28      4     displayId (UInt32, CGDirectDisplayID)
//   32      4     boundsX (Int32, physical px, global desktop coords, top-left origin)
//   36      4     boundsY (Int32)
//   40      4     boundsW (Int32)
//   44      4     boundsH (Int32)
//   48      8     scale (Float64) - display backing scale (pixels per point)
//   56      8     edrHeadroom (Float64) - max EDR component value at capture time; 1.0 = SDR
//   64      4     isPrimary (UInt32, 0/1)
//   68      4     source (UInt32): 0 = unspecified (one-shot), 1 = kept-open stream, 2 = screenshot
//   72      4     frameAgeMs (UInt32) - stream frames: ms since the frame arrived; else 0
//   76      8     sequence (UInt64) - stream frames: per-stream frame counter; else 0
//   84      12    reserved (zeros)
//   96      ...   pixel data, strideBytes * height bytes, row 0 = top row

import AppKit
import CoreGraphics
import CoreImage
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

let SOURCE_UNSPECIFIED: UInt32 = 0
let SOURCE_STREAM: UInt32 = 1
let SOURCE_SCREENSHOT: UInt32 = 2

let TCC_MESSAGE = "Screen Recording permission is denied. Grant it to 'scksnap' in "
    + "System Settings > Privacy & Security > Screen Recording, then retry."

enum ScksnapError: Error {
    case tccDenied(String)
    case noDisplay(String)
    case badRequest(String)
    case failure(String)

    var code: Int32 {
        switch self {
        case .tccDenied: return EXIT_TCC_DENIED
        case .noDisplay: return EXIT_NO_DISPLAY
        case .badRequest: return EXIT_USAGE
        case .failure: return EXIT_GENERIC_FAILURE
        }
    }

    var message: String {
        switch self {
        case .tccDenied(let m), .noDisplay(let m), .badRequest(let m), .failure(let m): return m
        }
    }

    var isTccDenied: Bool {
        if case .tccDenied = self { return true }
        return false
    }
}

/// Writes all bytes to a file descriptor; never raises (FileHandle.write throws an ObjC exception
/// on EPIPE, which would kill serve mode when the parent goes away).
func writeAll(_ fd: Int32, _ data: Data) {
    data.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
        guard let base = raw.baseAddress else { return }
        var offset = 0
        while offset < raw.count {
            let n = Darwin.write(fd, base + offset, raw.count - offset)
            if n < 0 {
                if errno == EINTR { continue }
                return
            }
            offset += n
        }
    }
}

/// One stderr line, prefixed "scksnap: " (serve mode: the .NET side copies these into its log).
func log(_ message: String) {
    writeAll(2, Data(("scksnap: " + message + "\n").utf8))
}

func fail(_ message: String, code: Int32) -> Never {
    writeAll(2, Data((message + "\n").utf8))
    exit(code)
}

func fail(_ error: ScksnapError) -> Never {
    fail("scksnap: " + error.message, code: error.code)
}

func usage() -> Never {
    fail("usage: scksnap list | capture <displayID> | stream-capture <displayID> | serve", code: EXIT_USAGE)
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

    // Physical-pixel global bounds: points * this display's own scale. With MIXED per-display
    // scales a single physical-pixel desktop space is an approximation; same-scale setups are exact.
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
func displayInfos() -> [DisplayInfo] {
    activeDisplayIDs().map { displayInfo(for: $0) }
}

func displaysJson(_ infos: [DisplayInfo]) -> Data? {
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.sortedKeys]
    return try? encoder.encode(infos)
}

@MainActor
func runList() -> Never {
    guard let json = displaysJson(displayInfos()) else {
        fail("scksnap: JSON encoding failed", code: EXIT_GENERIC_FAILURE)
    }
    writeAll(1, json)
    writeAll(1, Data("\n".utf8))
    exit(0)
}

// MARK: - TCC (Screen Recording permission)

/// True when Screen Recording is granted. With prompt, asks macOS once (shows the system prompt
/// and registers the helper in System Settings); the user's answer lands asynchronously, so a
/// first-ever request still returns false.
func screenRecordingAllowed(prompt: Bool) -> Bool {
    if CGPreflightScreenCaptureAccess() { return true }
    if !prompt { return false }
    _ = CGRequestScreenCaptureAccess()
    return CGPreflightScreenCaptureAccess()
}

func isAppleSilicon() -> Bool {
    var value: Int32 = 0
    var size = MemoryLayout<Int32>.size
    if sysctlbyname("hw.optional.arm64", &value, &size, nil, 0) == 0 {
        return value == 1
    }
    return false
}

/// HDR capture is macOS 15+ on Apple Silicon and an EDR-capable display; everything else is SDR
/// BGRA8 passthrough (the same path Windows SDR monitors use).
func wantsHdr(_ info: DisplayInfo) -> Bool {
    isAppleSilicon() && info.edrPotentialHeadroom > 1.0
}

func mapError(_ error: Error) -> ScksnapError {
    if let e = error as? ScksnapError { return e }
    if let scError = error as? SCStreamError, scError.code == .userDeclined {
        return .tccDenied(TCC_MESSAGE + " (SCStreamError.userDeclined)")
    }
    let ns = error as NSError
    return .failure("\(ns.domain) \(ns.code): \(ns.localizedDescription)")
}

// MARK: - Content filters

func shareableContent() async throws -> SCShareableContent {
    do {
        return try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
    } catch {
        throw mapError(error)
    }
}

/// The parent process (RoeSnip) as SCK sees it; nil while it has no windows SCK lists.
func parentApplication(in content: SCShareableContent) -> SCRunningApplication? {
    let parent = getppid()
    return content.applications.first { $0.processID == parent }
}

func displayFilter(_ display: SCDisplay, excluding app: SCRunningApplication?) -> SCContentFilter {
    if let app = app {
        return SCContentFilter(display: display, excludingApplications: [app], exceptingWindows: [])
    }
    return SCContentFilter(display: display, excludingWindows: [])
}

// MARK: - Pixel conversion

/// Renders any CGImage into FP16 RGBA in EXTENDED LINEAR sRGB (Core's Fp16ScRgb layout under the
/// macOS EDR convention: 1.0 == SDR white, highlights above 1.0). Extended colorspaces do not clamp;
/// CoreGraphics converts from the source colorspace. Screen captures are opaque, so premultiplied
/// == straight alpha.
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

/// Renders a CGImage into BGRA8 sRGB. byteOrder32Little + premultipliedFirst is BGRA in memory.
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

// MARK: - Frames

struct EncodedFrame {
    let formatCode: UInt32
    let width: Int
    let height: Int
    let stride: Int
    let pixels: Data
    var source: UInt32 = SOURCE_UNSPECIFIED
    var ageMs: UInt32 = 0
    var sequence: UInt64 = 0
}

func appendLE<T: FixedWidthInteger>(_ value: T, to data: inout Data) {
    withUnsafeBytes(of: value.littleEndian) { data.append(contentsOf: $0) }
}

/// SCKSNAP1 header + pixels (field table at the top of this file).
func encodeFrame(_ frame: EncodedFrame, info: DisplayInfo) -> Data {
    var out = Data()
    out.reserveCapacity(Int(HEADER_SIZE) + frame.pixels.count)
    out.append(contentsOf: Array("SCKSNAP1".utf8))
    appendLE(HEADER_SIZE, to: &out)
    appendLE(frame.formatCode, to: &out)
    appendLE(UInt32(frame.width), to: &out)
    appendLE(UInt32(frame.height), to: &out)
    appendLE(UInt32(frame.stride), to: &out)
    appendLE(info.id, to: &out)
    appendLE(Int32(info.x), to: &out)
    appendLE(Int32(info.y), to: &out)
    appendLE(Int32(info.widthPx), to: &out)
    appendLE(Int32(info.heightPx), to: &out)
    appendLE(info.scale.bitPattern, to: &out)
    appendLE(info.edrHeadroom.bitPattern, to: &out)
    appendLE(UInt32(info.isPrimary ? 1 : 0), to: &out)
    appendLE(frame.source, to: &out)
    appendLE(frame.ageMs, to: &out)
    appendLE(frame.sequence, to: &out)
    out.append(Data(count: Int(HEADER_SIZE) - out.count)) // reserved padding to HEADER_SIZE
    out.append(frame.pixels)
    return out
}

@available(macOS 14.0, *)
func sckScreenshot(display: SCDisplay, filter: SCContentFilter, info: DisplayInfo, hdr: Bool) async throws -> CGImage {
    // The HDR preset configures dynamic range + pixel format + color space together; setting only
    // captureDynamicRange on a plain configuration leaves 8-bit BGRA and clamps HDR to SDR.
    let config: SCStreamConfiguration
    if hdr, #available(macOS 15.0, *) {
        config = SCStreamConfiguration(preset: .captureHDRScreenshotLocalDisplay)
    } else {
        config = SCStreamConfiguration()
    }
    config.width = info.widthPx
    config.height = info.heightPx
    config.showsCursor = false
    config.captureResolution = .best
    do {
        return try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
    } catch {
        throw mapError(error)
    }
}

/// One full-display screenshot (the one-shot capture matrix). content is reused when the caller
/// already has it.
func captureViaScreenshot(info: DisplayInfo, content: SCShareableContent?) async throws -> EncodedFrame {
    var image: CGImage? = nil
    var hdrCaptured = false
    if #available(macOS 14.0, *) {
        let shareable: SCShareableContent
        if let content = content {
            shareable = content
        } else {
            shareable = try await shareableContent()
        }
        guard let display = shareable.displays.first(where: { $0.displayID == info.id }) else {
            throw ScksnapError.noDisplay("display \(info.id) not found in shareable content")
        }
        let filter = displayFilter(display, excluding: parentApplication(in: shareable))
        var hdr = false
        if #available(macOS 15.0, *), wantsHdr(info) { hdr = true }
        image = try await sckScreenshot(display: display, filter: filter, info: info, hdr: hdr)
        hdrCaptured = hdr
    } else {
        image = CGDisplayCreateImage(info.id) // macOS 13 only (deprecated in 15)
    }
    guard let captured = image else {
        throw ScksnapError.failure("capture produced no image")
    }
    if hdrCaptured {
        guard let pixels = renderFp16ExtendedLinearSrgb(captured) else {
            throw ScksnapError.failure("FP16 conversion failed")
        }
        return EncodedFrame(formatCode: FORMAT_FP16_RGBA, width: captured.width, height: captured.height,
            stride: captured.width * 8, pixels: pixels, source: SOURCE_SCREENSHOT)
    }
    guard let pixels = renderBgra8Srgb(captured) else {
        throw ScksnapError.failure("BGRA8 conversion failed")
    }
    return EncodedFrame(formatCode: FORMAT_BGRA8, width: captured.width, height: captured.height,
        stride: captured.width * 4, pixels: pixels, source: SOURCE_SCREENSHOT)
}

// MARK: - One-shot verbs

func writeFrameFile(_ frame: EncodedFrame, info: DisplayInfo) -> Never {
    // The one-shot format keeps source/age/sequence zero (unspecified).
    var plain = frame
    plain.source = SOURCE_UNSPECIFIED
    plain.ageMs = 0
    plain.sequence = 0
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("scksnap-\(UUID().uuidString).bin")
    do {
        try encodeFrame(plain, info: info).write(to: url)
    } catch {
        fail("scksnap: failed to write \(url.path): \(error.localizedDescription)",
            code: EXIT_GENERIC_FAILURE)
    }
    writeAll(1, Data((url.path + "\n").utf8))
    exit(0)
}

@MainActor
func runCapture(displayID: CGDirectDisplayID, viaStream: Bool) async -> Never {
    guard activeDisplayIDs().contains(displayID) else {
        fail("scksnap: no active display with id \(displayID)", code: EXIT_NO_DISPLAY)
    }
    if !screenRecordingAllowed(prompt: true) {
        fail(.tccDenied(TCC_MESSAGE))
    }
    let info = displayInfo(for: displayID)
    do {
        if viaStream {
            let streams = SnapshotStreams()
            guard let frame = try await streams.streamFrame(info: info, content: ContentCache(),
                firstFrameWait: 2.0, crop: nil) else {
                fail(.failure("the stream delivered no frame"))
            }
            writeFrameFile(frame, info: info)
        } else {
            writeFrameFile(try await captureViaScreenshot(info: info, content: nil), info: info)
        }
    } catch {
        fail(mapError(error))
    }
}

// MARK: - Entry point

@main
struct Scksnap {
    @MainActor
    static func main() {
        let args = Array(CommandLine.arguments.dropFirst())
        guard let verb = args.first else { usage() }
        switch verb {
        case "list":
            runList()
        case "capture", "stream-capture":
            guard args.count == 2, let rawId = UInt32(args[1]) else { usage() }
            let viaStream = verb == "stream-capture"
            Task { @MainActor in
                await runCapture(displayID: CGDirectDisplayID(rawId), viaStream: viaStream)
            }
            dispatchMain()
        case "serve":
            runServe()
        default:
            usage()
        }
    }
}
