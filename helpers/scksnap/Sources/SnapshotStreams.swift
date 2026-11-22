// SnapshotStreams.swift - kept-open SCStream per display holding its latest complete frame, so a
// serve-mode snapshot is a copy of a frame that is already there (no process start, no SCK setup).
// SCK delivers a frame only when the screen changes; the newest complete frame stays current until
// the next one. Streams close on wake / display changes (Serve.swift) and reopen on next use.

import CoreImage
import CoreMedia
import CoreVideo
import Foundation
import ScreenCaptureKit

func nowNs() -> UInt64 {
    clock_gettime_nsec_np(CLOCK_UPTIME_RAW)
}

/// Shareable content fetched at most once per request (each fetch costs tens of ms).
final class ContentCache {
    private var content: SCShareableContent?

    func get() async throws -> SCShareableContent {
        if let content = content { return content }
        let fetched = try await shareableContent()
        content = fetched
        return fetched
    }
}

/// The pixel buffer of a COMPLETE frame (idle/blank/suspended frames carry no new image).
func completeFrame(_ sampleBuffer: CMSampleBuffer) -> CVPixelBuffer? {
    guard CMSampleBufferIsValid(sampleBuffer),
          let attachments = CMSampleBufferGetSampleAttachmentsArray(
              sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
          let rawStatus = attachments.first?[SCStreamFrameInfo.status] as? Int,
          let status = SCFrameStatus(rawValue: rawStatus),
          status == .complete
    else { return nil }
    return CMSampleBufferGetImageBuffer(sampleBuffer)
}

struct StreamSnapshot {
    let pixelBuffer: CVPixelBuffer
    let ageNs: UInt64
    let sequence: UInt64
}

final class DisplayStream: NSObject, SCStreamOutput, SCStreamDelegate {
    let displayID: CGDirectDisplayID
    let width: Int
    let height: Int
    let hdr: Bool
    var excludesParent: Bool
    var stream: SCStream?
    let queue: DispatchQueue

    private let lock = NSLock()
    private var latest: CVPixelBuffer?
    private var latestAtNs: UInt64 = 0
    private var sequence: UInt64 = 0
    private var deadReason: String?

    init(displayID: CGDirectDisplayID, width: Int, height: Int, hdr: Bool, excludesParent: Bool) {
        self.displayID = displayID
        self.width = width
        self.height = height
        self.hdr = hdr
        self.excludesParent = excludesParent
        self.queue = DispatchQueue(label: "scksnap.snapshot.\(displayID)")
        super.init()
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer,
                of type: SCStreamOutputType) {
        guard type == .screen, let pixelBuffer = completeFrame(sampleBuffer) else { return }
        let at = nowNs()
        lock.lock()
        latest = pixelBuffer
        latestAtNs = at
        sequence &+= 1
        lock.unlock()
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        let reason = mapError(error).message
        lock.lock()
        deadReason = reason
        latest = nil
        lock.unlock()
        log("snapshot stream for display \(displayID) stopped: \(reason)")
    }

    var dead: String? {
        lock.lock()
        defer { lock.unlock() }
        return deadReason
    }

    func current() -> StreamSnapshot? {
        lock.lock()
        defer { lock.unlock() }
        guard let pixelBuffer = latest, deadReason == nil else { return nil }
        let now = nowNs()
        return StreamSnapshot(pixelBuffer: pixelBuffer,
            ageNs: now > latestAtNs ? now - latestAtNs : 0, sequence: sequence)
    }

    func waitForFrame(seconds: Double) async -> StreamSnapshot? {
        let deadline = nowNs() + UInt64(max(0, seconds) * 1_000_000_000)
        while true {
            if let snapshot = current() { return snapshot }
            if dead != nil || nowNs() >= deadline { return nil }
            try? await Task.sleep(nanoseconds: 10_000_000)
        }
    }

    func close() async {
        if let stream = stream {
            try? await stream.stopCapture()
        }
        stream = nil
        lock.lock()
        latest = nil
        lock.unlock()
    }
}

/// Used from one serial lane only (Serve.swift), so it needs no locking of its own.
final class SnapshotStreams {
    private var streams: [CGDirectDisplayID: DisplayStream] = [:]
    private var lastExclusionAttemptNs: UInt64 = 0
    private lazy var ciContext: CIContext = {
        var options: [CIContextOption: Any] = [.cacheIntermediates: false]
        if let linear = CGColorSpace(name: CGColorSpace.extendedLinearSRGB) {
            options[.workingColorSpace] = linear
        }
        return CIContext(options: options)
    }()

    /// Full-display frames for infos: the stream's latest frame, else one screenshot. A TCC denial
    /// throws; any other per-display failure omits that display (throws only when all failed).
    func capture(infos: [DisplayInfo]) async throws -> [(DisplayInfo, EncodedFrame)] {
        await closeGoneDisplays()
        let content = ContentCache()
        var results: [(DisplayInfo, EncodedFrame)] = []
        var firstError: ScksnapError?
        for info in infos {
            do {
                if let frame = try await streamFrame(info: info, content: content,
                    firstFrameWait: 0.5, crop: nil) {
                    results.append((info, frame))
                    continue
                }
                let frame = try await captureViaScreenshot(info: info, content: try await content.get())
                log("display \(info.id): screenshot fallback")
                results.append((info, frame))
            } catch {
                let e = mapError(error)
                if e.isTccDenied { throw e }
                log("display \(info.id) capture failed: \(e.message)")
                if firstError == nil { firstError = e }
            }
        }
        if results.isEmpty, let e = firstError { throw e }
        return results
    }

    /// A crop of one display's latest stream frame (GIF recording). No screenshot fallback.
    func captureRegion(info: DisplayInfo, rect: CGRect) async throws -> EncodedFrame {
        guard let frame = try await streamFrame(info: info, content: ContentCache(),
            firstFrameWait: 1.0, crop: rect) else {
            throw ScksnapError.failure("display \(info.id) has no stream frame")
        }
        return frame
    }

    /// The latest stream frame (opening the stream when needed), or nil when the stream cannot
    /// open or has no frame within firstFrameWait of opening. Throws only on a TCC denial.
    func streamFrame(info: DisplayInfo, content: ContentCache, firstFrameWait: Double,
                     crop: CGRect?) async throws -> EncodedFrame? {
        var hdr = false
        if #available(macOS 15.0, *), wantsHdr(info) { hdr = true }

        if let existing = streams[info.id],
           existing.dead != nil || existing.width != info.widthPx || existing.height != info.heightPx
            || existing.hdr != hdr {
            log("reopening the snapshot stream for display \(info.id) (\(existing.dead ?? "display mode changed"))")
            await existing.close()
            streams[info.id] = nil
        }

        var fresh = false
        if streams[info.id] == nil {
            do {
                streams[info.id] = try await open(info: info, hdr: hdr, content: content)
                fresh = true
            } catch {
                let e = mapError(error)
                if e.isTccDenied { throw e }
                log("snapshot stream for display \(info.id) could not open: \(e.message)")
                return nil
            }
        }
        guard let stream = streams[info.id] else { return nil }

        guard let snapshot = await stream.waitForFrame(seconds: fresh ? firstFrameWait : 0) else {
            log("snapshot stream for display \(info.id) has no frame yet")
            return nil
        }
        guard var frame = render(snapshot.pixelBuffer, hdr: stream.hdr, crop: crop) else {
            log("snapshot stream frame for display \(info.id) could not be converted")
            return nil
        }
        frame.source = SOURCE_STREAM
        frame.ageMs = UInt32(min(snapshot.ageNs / 1_000_000, UInt64(UInt32.max)))
        frame.sequence = snapshot.sequence
        return frame
    }

    private func open(info: DisplayInfo, hdr: Bool, content: ContentCache) async throws -> DisplayStream {
        let started = nowNs()
        let shareable = try await content.get()
        guard let display = shareable.displays.first(where: { $0.displayID == info.id }) else {
            throw ScksnapError.noDisplay("display \(info.id) not found in shareable content")
        }
        let parent = parentApplication(in: shareable)

        let config: SCStreamConfiguration
        if hdr, #available(macOS 15.0, *) {
            config = SCStreamConfiguration(preset: .captureHDRStreamLocalDisplay)
        } else {
            config = SCStreamConfiguration()
            config.pixelFormat = kCVPixelFormatType_32BGRA
            config.colorSpaceName = CGColorSpace.sRGB
        }
        config.width = info.widthPx
        config.height = info.heightPx
        config.showsCursor = false
        config.queueDepth = 4
        config.minimumFrameInterval = CMTime(value: 1, timescale: 30)
        if #available(macOS 14.0, *) { config.captureResolution = .best }

        let displayStream = DisplayStream(displayID: info.id, width: info.widthPx, height: info.heightPx,
            hdr: hdr, excludesParent: parent != nil)
        let stream = SCStream(filter: displayFilter(display, excluding: parent), configuration: config,
            delegate: displayStream)
        do {
            try stream.addStreamOutput(displayStream, type: .screen, sampleHandlerQueue: displayStream.queue)
            try await stream.startCapture()
        } catch {
            throw mapError(error)
        }
        displayStream.stream = stream
        let ms = (nowNs() - started) / 1_000_000
        log("snapshot stream open for display \(info.id) in \(ms) ms (\(info.widthPx)x\(info.heightPx) "
            + "\(hdr ? "HDR" : "SDR"), \(parent != nil ? "RoeSnip windows excluded" : "RoeSnip not listed yet"))")
        return displayStream
    }

    /// Streams opened before RoeSnip had a window SCK lists do not exclude it yet; retried at most
    /// every 5 s, after a snapshot's response went out.
    func refreshExclusionIfNeeded() async {
        let lacking = streams.values.filter { !$0.excludesParent && $0.dead == nil && $0.stream != nil }
        guard !lacking.isEmpty else { return }
        let now = nowNs()
        guard now - lastExclusionAttemptNs > 5_000_000_000 else { return }
        lastExclusionAttemptNs = now
        guard let content = try? await shareableContent(), let app = parentApplication(in: content) else {
            return
        }
        for displayStream in lacking {
            guard let display = content.displays.first(where: { $0.displayID == displayStream.displayID }),
                  let stream = displayStream.stream else { continue }
            do {
                try await stream.updateContentFilter(displayFilter(display, excluding: app))
                displayStream.excludesParent = true
                log("snapshot stream for display \(displayStream.displayID) now excludes RoeSnip windows")
            } catch {
                log("snapshot stream filter update failed for display \(displayStream.displayID): "
                    + mapError(error).message)
            }
        }
    }

    func closeAll(reason: String) async {
        guard !streams.isEmpty else { return }
        let count = streams.count
        for displayStream in streams.values {
            await displayStream.close()
        }
        streams.removeAll()
        log("closed \(count) snapshot stream(s): \(reason)")
    }

    private func closeGoneDisplays() async {
        let active = Set(activeDisplayIDs())
        for (id, displayStream) in streams where !active.contains(id) {
            await displayStream.close()
            streams[id] = nil
            log("closed the snapshot stream for display \(id) (display gone)")
        }
    }

    /// Copies the buffer (or the crop, top-left pixel rect) out: BGRA rows straight from the buffer,
    /// anything else (the HDR stream's format) through Core Image into FP16 extended linear sRGB.
    func render(_ pixelBuffer: CVPixelBuffer, hdr: Bool, crop: CGRect?) -> EncodedFrame? {
        let bufferW = CVPixelBufferGetWidth(pixelBuffer)
        let bufferH = CVPixelBufferGetHeight(pixelBuffer)
        var x = 0, y = 0, w = bufferW, h = bufferH
        if let crop = crop {
            x = max(0, min(Int(crop.minX), bufferW))
            y = max(0, min(Int(crop.minY), bufferH))
            w = max(0, min(Int(crop.width), bufferW - x))
            h = max(0, min(Int(crop.height), bufferH - y))
        }
        guard w > 0, h > 0 else { return nil }
        if !hdr && CVPixelBufferGetPixelFormatType(pixelBuffer) == kCVPixelFormatType_32BGRA {
            return copyBgra(pixelBuffer, x: x, y: y, w: w, h: h)
        }
        return ciRender(pixelBuffer, x: x, y: y, w: w, h: h, bufferH: bufferH, fp16: hdr)
    }

    private func copyBgra(_ pixelBuffer: CVPixelBuffer, x: Int, y: Int, w: Int, h: Int) -> EncodedFrame? {
        CVPixelBufferLockBaseAddress(pixelBuffer, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(pixelBuffer, .readOnly) }
        guard let base = CVPixelBufferGetBaseAddress(pixelBuffer) else { return nil }
        let bytesPerRow = CVPixelBufferGetBytesPerRow(pixelBuffer)
        let rowBytes = w * 4
        var data = Data(count: rowBytes * h)
        data.withUnsafeMutableBytes { (dst: UnsafeMutableRawBufferPointer) in
            guard let out = dst.baseAddress else { return }
            for row in 0..<h {
                memcpy(out + row * rowBytes, base + (y + row) * bytesPerRow + x * 4, rowBytes)
            }
        }
        return EncodedFrame(formatCode: FORMAT_BGRA8, width: w, height: h, stride: rowBytes, pixels: data)
    }

    private func ciRender(_ pixelBuffer: CVPixelBuffer, x: Int, y: Int, w: Int, h: Int, bufferH: Int,
                          fp16: Bool) -> EncodedFrame? {
        guard let space = CGColorSpace(name: fp16 ? CGColorSpace.extendedLinearSRGB : CGColorSpace.sRGB) else {
            return nil
        }
        let image = CIImage(cvPixelBuffer: pixelBuffer)
        // Core Image's origin is bottom-left.
        let bounds = CGRect(x: x, y: bufferH - y - h, width: w, height: h)
        let bytesPerPixel = fp16 ? 8 : 4
        let rowBytes = w * bytesPerPixel
        var data = Data(count: rowBytes * h)
        let context = ciContext
        data.withUnsafeMutableBytes { (dst: UnsafeMutableRawBufferPointer) in
            guard let out = dst.baseAddress else { return }
            context.render(image, toBitmap: out, rowBytes: rowBytes, bounds: bounds,
                format: fp16 ? .RGBAh : .BGRA8, colorSpace: space)
        }
        return EncodedFrame(formatCode: fp16 ? FORMAT_FP16_RGBA : FORMAT_BGRA8, width: w, height: h,
            stride: rowBytes, pixels: data)
    }
}
