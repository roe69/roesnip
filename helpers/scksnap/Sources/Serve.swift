// Serve.swift - `scksnap serve`: a long-lived helper the app starts once and drives over
// stdin/stdout with length-prefixed binary messages (mirrored by
// src/RoeSnip.Platform.MacOS/ScksnapProtocol.cs - keep in sync). All integers little-endian.
//
//   request   u32 length (bytes after this field) | u8 op | u32 id | payload
//   response  u32 length (bytes after this field) | u32 id | u8 status | payload
//   event     a response with id 0: payload = u8 kind | UTF-8 message (kind 1 = recording failed)
//
// status: 0 ok, 1 failure, 2 bad request, 3 display not found, 82 Screen Recording denied; a
// non-zero status carries a UTF-8 message as its payload.
//
//   op 1 list            -                                      -> UTF-8 JSON (same as `scksnap list`)
//   op 2 snapshot        u8 flags (1 = background: never prompt for TCC) | u32 n | u32 displayId * n
//                        (n = 0: every display)                 -> u32 count | (u32 size | SCKSNAP1) * count
//   op 3 snapshotRegion  u32 displayId | i32 x | i32 y | i32 w | i32 h (display pixels)
//                                                               -> same as snapshot, count 1
//   op 4 startRecord     u32 displayId | i32 x | i32 y | i32 w | i32 h | u32 fps
//                        | u8 flags (1 cursor, 2 mic, 4 system audio) | u32 videoBps | u32 audioBps
//                        | u16 pathBytes | UTF-8 path           -> u8 audio (1 mic, 2 system audio)
//   op 5 pause / op 6 resume                                    -> -
//   op 7 stop            -                                      -> u32 videoFrames | u32 dropped | u32 durationMs
//   op 8 setOrigin       i32 x | i32 y                          -> -
//   op 9 ping            -                                      -> u32 protocol version
//   op 10 quit           -                                      -> - (then exits)
//
// Requests run on two serial lanes: recording commands (4-8) in order on one, everything else on
// the other, so a slow snapshot never delays a pause. The helper exits when stdin closes.

import AppKit
import Foundation
import ScreenCaptureKit

let PROTOCOL_VERSION: UInt32 = 1

let OP_LIST: UInt8 = 1
let OP_SNAPSHOT: UInt8 = 2
let OP_SNAPSHOT_REGION: UInt8 = 3
let OP_START_RECORD: UInt8 = 4
let OP_PAUSE: UInt8 = 5
let OP_RESUME: UInt8 = 6
let OP_STOP: UInt8 = 7
let OP_SET_ORIGIN: UInt8 = 8
let OP_PING: UInt8 = 9
let OP_QUIT: UInt8 = 10

let EVENT_RECORDING_FAILED: UInt8 = 1

struct Request {
    let op: UInt8
    let id: UInt32
    let payload: Data
}

extension Data {
    func littleEndian<T: FixedWidthInteger>(_ type: T.Type, at offset: Int) -> T {
        var value: T = 0
        let start = startIndex + offset
        _ = Swift.withUnsafeMutableBytes(of: &value) { dst in
            copyBytes(to: dst, from: start..<(start + MemoryLayout<T>.size))
        }
        return T(littleEndian: value)
    }
}

struct PayloadReader {
    let data: Data
    var offset = 0

    init(_ data: Data) { self.data = data }

    mutating func read<T: FixedWidthInteger>(_ type: T.Type) throws -> T {
        let size = MemoryLayout<T>.size
        guard offset + size <= data.count else { throw ScksnapError.badRequest("truncated request") }
        let value = data.littleEndian(T.self, at: offset)
        offset += size
        return value
    }

    mutating func bytes(_ count: Int) throws -> Data {
        guard count >= 0, offset + count <= data.count else { throw ScksnapError.badRequest("truncated request") }
        let start = data.startIndex + offset
        offset += count
        return data.subdata(in: start..<(start + count))
    }
}

/// A serial async work queue.
final class Lane {
    typealias Work = () async -> Void
    private let continuation: AsyncStream<Work>.Continuation

    init() {
        var captured: AsyncStream<Work>.Continuation?
        let stream = AsyncStream<Work>(bufferingPolicy: .unbounded) { captured = $0 }
        continuation = captured!
        Task.detached {
            for await work in stream {
                await work()
            }
        }
    }

    func enqueue(_ work: @escaping Work) {
        continuation.yield(work)
    }
}

func readExact(_ count: Int) -> Data? {
    var buffer = Data(count: count)
    let ok = buffer.withUnsafeMutableBytes { (raw: UnsafeMutableRawBufferPointer) -> Bool in
        guard let base = raw.baseAddress else { return count == 0 }
        var offset = 0
        while offset < count {
            let n = Darwin.read(0, base + offset, count - offset)
            if n == 0 { return false }
            if n < 0 {
                if errno == EINTR { continue }
                return false
            }
            offset += n
        }
        return true
    }
    return ok ? buffer : nil
}

final class Server {
    private let snapshotLane = Lane()
    private let recordLane = Lane()
    private let snapshots = SnapshotStreams()
    private var recorder: Recorder? // record lane only
    private let outLock = NSLock()
    private let stateLock = NSLock()
    private var shuttingDown = false
    private var observers: [NSObjectProtocol] = []

    func readLoop() {
        while true {
            guard let head = readExact(4) else { break }
            let length = Int(head.littleEndian(UInt32.self, at: 0))
            guard length >= 5, length <= 64 << 20 else {
                log("bad request length \(length); exiting")
                break
            }
            guard let body = readExact(length) else { break }
            let request = Request(
                op: body[body.startIndex],
                id: body.littleEndian(UInt32.self, at: 1),
                payload: body.subdata(in: (body.startIndex + 5)..<body.endIndex))
            dispatch(request)
        }
        shutdown("stdin closed")
    }

    private func dispatch(_ request: Request) {
        switch request.op {
        case OP_START_RECORD, OP_PAUSE, OP_RESUME, OP_STOP, OP_SET_ORIGIN:
            recordLane.enqueue { await self.handle(request) }
        case OP_QUIT:
            send(request.id, 0, Data())
            shutdown("quit requested")
        default:
            snapshotLane.enqueue { await self.handle(request) }
        }
    }

    private func handle(_ request: Request) async {
        do {
            let payload = try await respond(request)
            send(request.id, 0, payload)
        } catch {
            let e = mapError(error)
            send(request.id, UInt8(e.code), Data(e.message.utf8))
        }
        if request.op == OP_SNAPSHOT {
            snapshotLane.enqueue { await self.snapshots.refreshExclusionIfNeeded() }
        }
    }

    func send(_ id: UInt32, _ status: UInt8, _ payload: Data) {
        var head = Data(capacity: 9)
        appendLE(UInt32(5 + payload.count), to: &head)
        appendLE(id, to: &head)
        head.append(status)
        outLock.lock()
        writeAll(1, head)
        writeAll(1, payload)
        outLock.unlock()
    }

    private func sendEvent(_ kind: UInt8, _ message: String) {
        var payload = Data([kind])
        payload.append(Data(message.utf8))
        send(0, 0, payload)
    }

    private func respond(_ request: Request) async throws -> Data {
        var reader = PayloadReader(request.payload)
        switch request.op {
        case OP_PING:
            var out = Data()
            appendLE(PROTOCOL_VERSION, to: &out)
            return out

        case OP_LIST:
            let infos = await MainActor.run { displayInfos() }
            guard let json = displaysJson(infos) else { throw ScksnapError.failure("JSON encoding failed") }
            return json

        case OP_SNAPSHOT:
            let flags = try reader.read(UInt8.self)
            let count = Int(try reader.read(UInt32.self))
            guard count <= 64 else { throw ScksnapError.badRequest("too many displays") }
            var ids: [UInt32] = []
            for _ in 0..<count { ids.append(try reader.read(UInt32.self)) }
            guard screenRecordingAllowed(prompt: flags & 1 == 0) else { throw ScksnapError.tccDenied(TCC_MESSAGE) }
            let infos = await MainActor.run { displayInfos() }
            let selected = ids.isEmpty ? infos : ids.compactMap { id in infos.first { $0.id == id } }
            guard !selected.isEmpty else { throw ScksnapError.noDisplay("none of the requested displays is active") }
            return framesPayload(try await snapshots.capture(infos: selected))

        case OP_SNAPSHOT_REGION:
            let displayId = try reader.read(UInt32.self)
            let rect = CGRect(x: Int(try reader.read(Int32.self)), y: Int(try reader.read(Int32.self)),
                width: Int(try reader.read(Int32.self)), height: Int(try reader.read(Int32.self)))
            guard screenRecordingAllowed(prompt: true) else { throw ScksnapError.tccDenied(TCC_MESSAGE) }
            let info = try await activeDisplay(displayId)
            return framesPayload([(info, try await snapshots.captureRegion(info: info, rect: rect))])

        case OP_START_RECORD:
            return try await startRecord(&reader)

        case OP_PAUSE, OP_RESUME:
            guard let rec = recorder else { throw ScksnapError.badRequest("no recording is active") }
            if request.op == OP_PAUSE { rec.pause() } else { rec.resume() }
            return Data()

        case OP_STOP:
            guard let rec = recorder else { throw ScksnapError.badRequest("no recording is active") }
            recorder = nil
            let summary = try await rec.stop()
            log("recording finalized: \(summary.videoFrames) frame(s) (\(summary.droppedFrames) dropped), "
                + String(format: "%.1f s", Double(summary.durationMs) / 1000))
            var out = Data()
            appendLE(UInt32(clamping: summary.videoFrames), to: &out)
            appendLE(UInt32(clamping: summary.droppedFrames), to: &out)
            appendLE(UInt32(clamping: summary.durationMs), to: &out)
            return out

        case OP_SET_ORIGIN:
            guard let rec = recorder else { throw ScksnapError.badRequest("no recording is active") }
            try await rec.setOrigin(x: Int(try reader.read(Int32.self)), y: Int(try reader.read(Int32.self)))
            return Data()

        default:
            throw ScksnapError.badRequest("unknown op \(request.op)")
        }
    }

    private func activeDisplay(_ id: UInt32) async throws -> DisplayInfo {
        let infos = await MainActor.run { displayInfos() }
        guard let info = infos.first(where: { $0.id == id }) else {
            throw ScksnapError.noDisplay("no active display with id \(id)")
        }
        return info
    }

    private func framesPayload(_ frames: [(DisplayInfo, EncodedFrame)]) -> Data {
        var out = Data()
        appendLE(UInt32(frames.count), to: &out)
        for (info, frame) in frames {
            let bytes = encodeFrame(frame, info: info)
            appendLE(UInt32(bytes.count), to: &out)
            out.append(bytes)
        }
        return out
    }

    private func startRecord(_ reader: inout PayloadReader) async throws -> Data {
        let displayId = try reader.read(UInt32.self)
        let x = Int(try reader.read(Int32.self))
        let y = Int(try reader.read(Int32.self))
        let w = Int(try reader.read(Int32.self))
        let h = Int(try reader.read(Int32.self))
        let fps = Int(try reader.read(UInt32.self))
        let flags = try reader.read(UInt8.self)
        let videoBps = Int(try reader.read(UInt32.self))
        let audioBps = Int(try reader.read(UInt32.self))
        let pathBytes = Int(try reader.read(UInt16.self))
        guard let path = String(data: try reader.bytes(pathBytes), encoding: .utf8), !path.isEmpty,
              w > 0, h > 0, fps > 0 else {
            throw ScksnapError.badRequest("bad startRecord request")
        }

        if let previous = recorder {
            log("a previous recording was still open; finalizing it first")
            recorder = nil
            _ = try? await previous.stop()
        }
        guard screenRecordingAllowed(prompt: true) else { throw ScksnapError.tccDenied(TCC_MESSAGE) }
        let info = try await activeDisplay(displayId)
        let content = try await shareableContent()
        guard let display = content.displays.first(where: { $0.displayID == displayId }) else {
            throw ScksnapError.noDisplay("display \(displayId) not found in shareable content")
        }

        let request = RecordRequest(displayID: displayId, x: x, y: y, width: w, height: h, fps: fps,
            cursor: flags & 1 != 0, microphone: flags & 2 != 0, systemAudio: flags & 4 != 0,
            videoBitrate: videoBps, audioBitrate: audioBps, path: path)
        let rec = try Recorder(request: request, info: info)
        rec.onFailure = { [weak self] message in self?.sendEvent(EVENT_RECORDING_FAILED, message) }
        do {
            try await rec.start(display: display, parent: parentApplication(in: content))
        } catch {
            try? FileManager.default.removeItem(atPath: path)
            throw error
        }
        recorder = rec
        log("recording started: display \(displayId), \(w)x\(h) at (\(x),\(y)), \(fps) fps, "
            + "mic \(rec.microphoneActive ? "on" : "off"), system audio \(rec.systemAudioActive ? "on" : "off")")
        return Data([(rec.microphoneActive ? 1 : 0) | (rec.systemAudioActive ? 2 : 0)])
    }

    /// Finalizes an open recording (bounded at 3 s) and exits.
    func shutdown(_ reason: String) {
        stateLock.lock()
        let already = shuttingDown
        shuttingDown = true
        stateLock.unlock()
        if already { return }
        log("serve exiting: \(reason)")
        DispatchQueue.global().asyncAfter(deadline: .now() + 3) { exit(0) }
        recordLane.enqueue {
            if let rec = self.recorder {
                self.recorder = nil
                _ = try? await rec.stop()
            }
            exit(0)
        }
    }

    /// Snapshot streams go stale across sleep and display changes: close them, the next snapshot
    /// reopens. Called on the main thread.
    @MainActor
    func installObservers() {
        let workspace = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.didWakeNotification, NSWorkspace.screensDidWakeNotification] {
            observers.append(workspace.addObserver(forName: name, object: nil, queue: nil) { [weak self] _ in
                self?.closeSnapshotStreams("system or displays woke")
            })
        }
        observers.append(NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: nil
        ) { [weak self] _ in
            self?.closeSnapshotStreams("display configuration changed")
        })
        // stdin EOF is the normal exit signal; this covers a parent that died without closing it.
        Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            if getppid() == 1 { self?.shutdown("parent process exited") }
        }
    }

    private func closeSnapshotStreams(_ reason: String) {
        snapshotLane.enqueue { await self.snapshots.closeAll(reason: reason) }
    }
}

var serveActivity: NSObjectProtocol?

@MainActor
func runServe() -> Never {
    signal(SIGPIPE, SIG_IGN)
    // No App Nap for the helper: snapshot streams must keep their latest frame current.
    serveActivity = ProcessInfo.processInfo.beginActivity(
        options: [.userInitiatedAllowingIdleSystemSleep, .latencyCritical], reason: "RoeSnip capture helper")
    let app = NSApplication.shared
    app.setActivationPolicy(.prohibited)
    let server = Server()
    server.installObservers()
    Thread { server.readLoop() }.start()
    #if arch(arm64)
    let arch = "arm64"
    #else
    let arch = isAppleSilicon() ? "x86_64 under Rosetta" : "x86_64"
    #endif
    log("serve ready (protocol \(PROTOCOL_VERSION), macOS \(ProcessInfo.processInfo.operatingSystemVersionString), \(arch))")
    // NSApplication's run loop services the main queue (MainActor hops for NSScreen) and delivers
    // the wake / screen-change notifications.
    app.run()
    exit(0)
}
