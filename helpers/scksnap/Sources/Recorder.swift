// Recorder.swift - one MP4 take: SCStream (region sourceRect, cursor, system audio) + microphone
// (AVCaptureSession) -> AVAssetWriter (H.264 via VideoToolbox, AAC). Every sample is handled on
// one serial queue. Pause drops samples captured while paused and shifts later ones back by the
// paused time (PauseTimeline), so the file has no gap.

import AVFoundation
import CoreMedia
import CoreVideo
import Foundation
import ScreenCaptureKit

let AUDIO_RATE: Int64 = 48_000

func hostNowNs() -> Int64 {
    nanos(CMClockGetTime(CMClockGetHostTimeClock()))
}

func nanos(_ time: CMTime) -> Int64 {
    CMTimeConvertScale(time, timescale: 1_000_000_000, method: .default).value
}

/// Capture-clock -> media-clock mapping across pauses. A sample captured inside a pause maps to
/// nil (dropped); any other sample is shifted back by the pauses that ENDED before it was captured,
/// so samples still queued from before a pause keep their place.
struct PauseTimeline {
    private(set) var pausedAt: Int64?
    private var completed: [(start: Int64, end: Int64)] = []

    mutating func pause(at t: Int64) {
        if pausedAt == nil { pausedAt = t }
    }

    mutating func resume(at t: Int64) {
        guard let start = pausedAt else { return }
        if t > start { completed.append((start, t)) }
        pausedAt = nil
    }

    func map(_ t: Int64) -> Int64? {
        if let start = pausedAt, t >= start { return nil }
        var offset: Int64 = 0
        for pause in completed {
            if t >= pause.end {
                offset += pause.end - pause.start
            } else if t >= pause.start {
                return nil
            }
        }
        return t - offset
    }

    /// Media time of the take's end: the open pause's start when stopped while paused (the review
    /// span after a soft stop is not part of the take), else now.
    func endTime(now: Int64) -> Int64 {
        let t = pausedAt ?? now
        var offset: Int64 = 0
        for pause in completed where t >= pause.end {
            offset += pause.end - pause.start
        }
        return t - offset
    }
}

/// Mixes up to two Float32 interleaved stereo 48 kHz sources onto one sample timeline anchored at
/// creation. A source that falls more than 200 ms behind the other counts as silence (a stopped
/// microphone must not stall system audio).
final class AudioMixer {
    private final class Source {
        var fifo: [Float] = []
        var startFrame: Int64 = 0 // timeline index of fifo[0]
        var nextFrame: Int64?     // where the next contiguous buffer lands
        var endFrame: Int64 { startFrame + Int64(fifo.count / 2) }
    }

    private static let contiguousTolerance: Int64 = 2_400 // 50 ms
    private static let maxLag: Int64 = 9_600             // 200 ms
    private static let maxChunk: Int64 = 4_800           // 100 ms per output buffer

    private let sources: [Source]
    private let anchorNs: Int64
    private var outPos: Int64?

    init(sourceCount: Int, anchorNs: Int64) {
        sources = (0..<sourceCount).map { _ in Source() }
        self.anchorNs = anchorNs
    }

    func push(source index: Int, ptsNs: Int64, interleaved samples: [Float]) {
        let source = sources[index]
        let frames = Int64(samples.count / 2)
        guard frames > 0 else { return }
        var at = (ptsNs - anchorNs) * AUDIO_RATE / 1_000_000_000
        if let next = source.nextFrame, abs(at - next) <= AudioMixer.contiguousTolerance { at = next }
        var skip: Int64 = 0
        if source.fifo.isEmpty {
            source.startFrame = at
        } else if at > source.endFrame {
            let gap = at - source.endFrame
            if gap <= AudioMixer.maxLag {
                source.fifo.append(contentsOf: [Float](repeating: 0, count: Int(gap * 2)))
            } else {
                source.fifo.removeAll()
                source.startFrame = at
            }
        } else if at < source.endFrame {
            skip = source.endFrame - at
        }
        if let out = outPos, at + skip < out { skip = out - at } // already mixed past this
        if skip < frames {
            if source.fifo.isEmpty { source.startFrame = at + skip }
            source.fifo.append(contentsOf: samples[Int(skip * 2)...])
        }
        source.nextFrame = at + frames
        if outPos == nil { outPos = source.startFrame }
    }

    /// Mixed buffers ready to write: (timeline host ns, interleaved samples). final drains all.
    func drain(final: Bool) -> [(ptsNs: Int64, samples: [Float])] {
        guard var pos = outPos else { return [] }
        let delivered = sources.filter { $0.nextFrame != nil }
        guard let maxEnd = delivered.map({ $0.endFrame }).max() else { return [] }
        var limit = maxEnd
        if !final, let minEnd = delivered.map({ $0.endFrame }).min() {
            limit = max(minEnd, maxEnd - AudioMixer.maxLag)
        }
        guard limit > pos else { return [] }

        var chunks: [(ptsNs: Int64, samples: [Float])] = []
        while pos < limit {
            let end = min(limit, pos + AudioMixer.maxChunk)
            var mixed = [Float](repeating: 0, count: Int(end - pos) * 2)
            for source in sources {
                let from = max(pos, source.startFrame)
                let to = min(end, source.endFrame)
                if from < to {
                    let srcOffset = Int(from - source.startFrame) * 2
                    let dstOffset = Int(from - pos) * 2
                    for i in 0..<(Int(to - from) * 2) {
                        mixed[dstOffset + i] += source.fifo[srcOffset + i]
                    }
                }
            }
            for i in 0..<mixed.count {
                mixed[i] = min(1, max(-1, mixed[i]))
            }
            chunks.append((anchorNs + pos * 1_000_000_000 / AUDIO_RATE, mixed))
            pos = end
        }
        for source in sources {
            let consumed = min(Int64(source.fifo.count / 2), max(0, pos - source.startFrame))
            if consumed > 0 {
                source.fifo.removeFirst(Int(consumed * 2))
                source.startFrame += consumed
            }
        }
        outPos = pos
        return chunks
    }
}

/// Float32 interleaved stereo from a 48 kHz LPCM sample buffer (Float32 or Int16, interleaved or
/// not, mono duplicated). nil for anything else.
func interleavedStereo(_ sampleBuffer: CMSampleBuffer) -> [Float]? {
    guard let format = CMSampleBufferGetFormatDescription(sampleBuffer),
          let asbdPointer = CMAudioFormatDescriptionGetStreamBasicDescription(format) else { return nil }
    let asbd = asbdPointer.pointee
    guard asbd.mFormatID == kAudioFormatLinearPCM, Int64(asbd.mSampleRate) == AUDIO_RATE else { return nil }
    let isFloat = asbd.mFormatFlags & kAudioFormatFlagIsFloat != 0
    let nonInterleaved = asbd.mFormatFlags & kAudioFormatFlagIsNonInterleaved != 0
    let channels = Int(asbd.mChannelsPerFrame)
    guard channels >= 1, (isFloat && asbd.mBitsPerChannel == 32) || (!isFloat && asbd.mBitsPerChannel == 16)
    else { return nil }
    let frames = CMSampleBufferGetNumSamples(sampleBuffer)
    guard frames > 0 else { return nil }

    var sizeNeeded = 0
    CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
        sampleBuffer, bufferListSizeNeededOut: &sizeNeeded, bufferListOut: nil, bufferListSize: 0,
        blockBufferAllocator: nil, blockBufferMemoryAllocator: nil, flags: 0, blockBufferOut: nil)
    guard sizeNeeded > 0 else { return nil }
    let listMemory = UnsafeMutableRawPointer.allocate(
        byteCount: sizeNeeded, alignment: MemoryLayout<AudioBufferList>.alignment)
    defer { listMemory.deallocate() }
    let list = listMemory.bindMemory(to: AudioBufferList.self, capacity: 1)
    var blockBuffer: CMBlockBuffer?
    let status = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
        sampleBuffer, bufferListSizeNeededOut: nil, bufferListOut: list, bufferListSize: sizeNeeded,
        blockBufferAllocator: nil, blockBufferMemoryAllocator: nil,
        flags: kCMSampleBufferFlag_AudioBufferList_Assure16ByteAlignment, blockBufferOut: &blockBuffer)
    guard status == noErr else { return nil }
    let buffers = UnsafeMutableAudioBufferListPointer(list)

    func sample(_ channel: Int, _ frame: Int) -> Float {
        let bufferIndex = nonInterleaved ? channel : 0
        guard bufferIndex < buffers.count, let data = buffers[bufferIndex].mData else { return 0 }
        let index = nonInterleaved ? frame : frame * channels + channel
        if isFloat {
            let count = Int(buffers[bufferIndex].mDataByteSize) / 4
            return index < count ? data.assumingMemoryBound(to: Float.self)[index] : 0
        }
        let count = Int(buffers[bufferIndex].mDataByteSize) / 2
        return index < count ? Float(data.assumingMemoryBound(to: Int16.self)[index]) / 32768 : 0
    }

    var out = [Float](repeating: 0, count: frames * 2)
    for frame in 0..<frames {
        out[frame * 2] = sample(0, frame)
        out[frame * 2 + 1] = sample(channels > 1 ? 1 : 0, frame)
    }
    return out
}

struct RecordRequest {
    let displayID: CGDirectDisplayID
    let x: Int
    let y: Int
    let width: Int
    let height: Int
    let fps: Int
    let cursor: Bool
    let microphone: Bool
    let systemAudio: Bool
    let videoBitrate: Int
    let audioBitrate: Int
    let path: String
}

struct RecordSummary {
    let videoFrames: Int
    let droppedFrames: Int
    let durationMs: Int64
}

final class Recorder: NSObject, SCStreamOutput, SCStreamDelegate, AVCaptureAudioDataOutputSampleBufferDelegate {
    private static let systemSource = 0
    private static let micSource = 1

    let request: RecordRequest
    let microphoneActive: Bool
    let systemAudioActive: Bool
    var onFailure: ((String) -> Void)?

    private let queue = DispatchQueue(label: "scksnap.record")
    private let micControl = DispatchQueue(label: "scksnap.record.mic")
    private let writer: AVAssetWriter
    private let videoInput: AVAssetWriterInput
    private let adaptor: AVAssetWriterInputPixelBufferAdaptor
    private let audioInput: AVAssetWriterInput?
    private let mixer: AudioMixer?
    private let micSession: AVCaptureSession?
    private var audioFormat: CMAudioFormatDescription?
    private let config: SCStreamConfiguration
    private let scale: Double
    private var stream: SCStream?

    // queue-only state
    private var timeline = PauseTimeline()
    private var sessionStarted = false
    private var t0: Int64 = 0
    private var lastVideoOut: Int64 = -1
    private var lastPixelBuffer: CVPixelBuffer?
    private var nextAudioFrame: Int64 = 0
    private var videoFrames = 0
    private var droppedFrames = 0
    private var stopped = false
    private var failed = false

    init(request: RecordRequest, info: DisplayInfo) throws {
        self.request = request
        scale = info.scale > 0 ? info.scale : 1

        let url = URL(fileURLWithPath: request.path)
        try? FileManager.default.removeItem(at: url)
        let writer: AVAssetWriter
        do {
            writer = try AVAssetWriter(outputURL: url, fileType: .mp4)
        } catch {
            throw ScksnapError.failure("cannot create \(request.path): \(error.localizedDescription)")
        }
        writer.shouldOptimizeForNetworkUse = true
        self.writer = writer

        let videoSettings: [String: Any] = [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: request.width,
            AVVideoHeightKey: request.height,
            AVVideoCompressionPropertiesKey: [
                AVVideoAverageBitRateKey: request.videoBitrate,
                AVVideoExpectedSourceFrameRateKey: request.fps,
                AVVideoMaxKeyFrameIntervalKey: request.fps * 2,
                AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel,
                AVVideoAllowFrameReorderingKey: false,
            ] as [String: Any],
            AVVideoColorPropertiesKey: [
                AVVideoColorPrimariesKey: AVVideoColorPrimaries_ITU_R_709_2,
                AVVideoTransferFunctionKey: AVVideoTransferFunction_ITU_R_709_2,
                AVVideoYCbCrMatrixKey: AVVideoYCbCrMatrix_ITU_R_709_2,
            ],
        ]
        guard writer.canApply(outputSettings: videoSettings, forMediaType: .video) else {
            throw ScksnapError.failure("H.264 cannot encode a \(request.width)x\(request.height) region")
        }
        let video = AVAssetWriterInput(mediaType: .video, outputSettings: videoSettings)
        video.expectsMediaDataInRealTime = true
        guard writer.canAdd(video) else { throw ScksnapError.failure("the MP4 writer refused the video track") }
        writer.add(video)
        videoInput = video
        adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: video, sourcePixelBufferAttributes: nil)

        let mic = request.microphone ? Recorder.makeMicSession() : nil
        micSession = mic
        microphoneActive = mic != nil
        systemAudioActive = request.systemAudio
        if mic != nil || request.systemAudio {
            let audioSettings: [String: Any] = [
                AVFormatIDKey: kAudioFormatMPEG4AAC,
                AVSampleRateKey: Int(AUDIO_RATE),
                AVNumberOfChannelsKey: 2,
                AVEncoderBitRateKey: request.audioBitrate,
            ]
            let input = AVAssetWriterInput(mediaType: .audio, outputSettings: audioSettings)
            input.expectsMediaDataInRealTime = true
            guard writer.canAdd(input) else { throw ScksnapError.failure("the MP4 writer refused the audio track") }
            writer.add(input)
            audioInput = input
            mixer = AudioMixer(sourceCount: 2, anchorNs: hostNowNs())
        } else {
            audioInput = nil
            mixer = nil
        }

        let cfg = SCStreamConfiguration()
        cfg.width = request.width
        cfg.height = request.height
        cfg.pixelFormat = kCVPixelFormatType_32BGRA
        cfg.colorSpaceName = CGColorSpace.sRGB
        cfg.showsCursor = request.cursor
        cfg.scalesToFit = false
        cfg.queueDepth = 6
        cfg.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(max(1, request.fps)))
        if #available(macOS 14.0, *) { cfg.captureResolution = .best }
        if request.systemAudio {
            cfg.capturesAudio = true
            cfg.sampleRate = Int(AUDIO_RATE)
            cfg.channelCount = 2
            cfg.excludesCurrentProcessAudio = true
        }
        config = cfg
        super.init()
        config.sourceRect = sourceRect(x: request.x, y: request.y)
    }

    /// Region in display points (sourceRect's unit), top-left origin.
    private func sourceRect(x: Int, y: Int) -> CGRect {
        CGRect(x: Double(x) / scale, y: Double(y) / scale,
            width: Double(request.width) / scale, height: Double(request.height) / scale)
    }

    /// The microphone when macOS allows it; otherwise nil and a log line (the take records without it).
    private static func makeMicSession() -> AVCaptureSession? {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized:
            break
        case .notDetermined:
            AVCaptureDevice.requestAccess(for: .audio) { granted in
                log("microphone permission \(granted ? "granted" : "denied"); applies from the next take")
            }
            log("microphone permission not decided yet; asked macOS, this take records without the microphone")
            return nil
        default:
            log("microphone access is denied (System Settings > Privacy & Security > Microphone); "
                + "recording without the microphone")
            return nil
        }
        guard let device = AVCaptureDevice.default(for: .audio) else {
            log("no microphone found; recording without the microphone")
            return nil
        }
        let session = AVCaptureSession()
        do {
            let input = try AVCaptureDeviceInput(device: device)
            guard session.canAddInput(input) else {
                log("microphone input refused; recording without the microphone")
                return nil
            }
            session.addInput(input)
        } catch {
            log("microphone unavailable (\(error.localizedDescription)); recording without the microphone")
            return nil
        }
        return session
    }

    func start(display: SCDisplay, parent: SCRunningApplication?) async throws {
        if let session = micSession {
            let output = AVCaptureAudioDataOutput()
            output.audioSettings = [
                AVFormatIDKey: kAudioFormatLinearPCM,
                AVSampleRateKey: Int(AUDIO_RATE),
                AVNumberOfChannelsKey: 2,
                AVLinearPCMBitDepthKey: 32,
                AVLinearPCMIsFloatKey: true,
                AVLinearPCMIsNonInterleaved: false,
            ]
            output.setSampleBufferDelegate(self, queue: queue)
            if session.canAddOutput(output) { session.addOutput(output) }
        }

        let newStream = SCStream(filter: displayFilter(display, excluding: parent), configuration: config,
            delegate: self)
        do {
            try newStream.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
            if systemAudioActive {
                try newStream.addStreamOutput(self, type: .audio, sampleHandlerQueue: queue)
            }
            try await newStream.startCapture()
        } catch {
            throw mapError(error)
        }
        stream = newStream
        if let session = micSession {
            micControl.async { session.startRunning() }
        }
    }

    // MARK: commands (lane A)

    func pause() {
        let now = hostNowNs()
        queue.async { self.timeline.pause(at: now) }
        if let session = micSession {
            micControl.async { session.stopRunning() } // releases the mic indicator during a review
        }
    }

    func resume() {
        let now = hostNowNs()
        queue.async { self.timeline.resume(at: now) }
        if let session = micSession {
            micControl.async { session.startRunning() }
        }
    }

    func setOrigin(x: Int, y: Int) async throws {
        guard let stream = stream else { return }
        config.sourceRect = sourceRect(x: x, y: y)
        do {
            try await stream.updateConfiguration(config)
        } catch {
            throw mapError(error)
        }
    }

    func stop() async throws -> RecordSummary {
        if let stream = stream {
            try? await stream.stopCapture()
        }
        stream = nil
        if let session = micSession {
            micControl.sync { session.stopRunning() }
        }
        let now = hostNowNs()
        let started: Bool = queue.sync {
            stopped = true
            return sessionStarted
        }
        guard started else {
            writer.cancelWriting()
            try? FileManager.default.removeItem(at: writer.outputURL)
            throw ScksnapError.failure("no video frame was captured")
        }
        let endOut: Int64 = queue.sync {
            if let mixer = mixer {
                for chunk in mixer.drain(final: true) { writeAudio(ptsNs: chunk.ptsNs, samples: chunk.samples) }
            }
            let end = max(lastVideoOut, timeline.endTime(now: now) - t0)
            // SCK sends frames only on change: repeat the last one at the end so a static tail
            // is part of the take.
            if let last = lastPixelBuffer, end > lastVideoOut, videoInput.isReadyForMoreMediaData {
                if adaptor.append(last, withPresentationTime: CMTime(value: end, timescale: 1_000_000_000)) {
                    lastVideoOut = end
                }
            }
            videoInput.markAsFinished()
            audioInput?.markAsFinished()
            writer.endSession(atSourceTime: CMTime(value: end, timescale: 1_000_000_000))
            return end
        }
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            writer.finishWriting { continuation.resume() }
        }
        if writer.status != .completed {
            throw ScksnapError.failure("MP4 finalize failed: \(writer.error?.localizedDescription ?? "status \(writer.status.rawValue)")")
        }
        let summary: RecordSummary = queue.sync {
            RecordSummary(videoFrames: videoFrames, droppedFrames: droppedFrames, durationMs: endOut / 1_000_000)
        }
        return summary
    }

    // MARK: samples (queue)

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer,
                of type: SCStreamOutputType) {
        switch type {
        case .screen:
            handleVideo(sampleBuffer)
        case .audio:
            handleAudio(sampleBuffer, source: Recorder.systemSource)
        default:
            break
        }
    }

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer,
                       from connection: AVCaptureConnection) {
        handleAudio(sampleBuffer, source: Recorder.micSource)
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        let message = mapError(error).message
        queue.async {
            guard !self.stopped else { return }
            self.reportFailure("the recording stream stopped: \(message)")
        }
    }

    private func reportFailure(_ message: String) {
        guard !failed else { return }
        failed = true
        log(message)
        onFailure?(message)
    }

    private func handleVideo(_ sampleBuffer: CMSampleBuffer) {
        guard !stopped, !failed, let pixelBuffer = completeFrame(sampleBuffer) else { return }
        guard let mapped = timeline.map(nanos(CMSampleBufferGetPresentationTimeStamp(sampleBuffer))) else { return }
        if !sessionStarted {
            guard writer.startWriting() else {
                reportFailure("MP4 writer failed to start: \(writer.error?.localizedDescription ?? "unknown")")
                return
            }
            writer.startSession(atSourceTime: .zero)
            sessionStarted = true
            t0 = mapped
        }
        let out = mapped - t0
        guard out > lastVideoOut else { return }
        guard videoInput.isReadyForMoreMediaData else {
            droppedFrames += 1
            return
        }
        guard adaptor.append(pixelBuffer, withPresentationTime: CMTime(value: out, timescale: 1_000_000_000)) else {
            reportFailure("MP4 video write failed: \(writer.error?.localizedDescription ?? "unknown")")
            return
        }
        lastVideoOut = out
        lastPixelBuffer = pixelBuffer
        videoFrames += 1
    }

    private func handleAudio(_ sampleBuffer: CMSampleBuffer, source: Int) {
        guard !stopped, !failed, let mixer = mixer, let samples = interleavedStereo(sampleBuffer) else { return }
        var pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        if source == Recorder.micSource, let session = micSession, let clock = session.synchronizationClock {
            pts = CMSyncConvertTime(pts, from: clock, to: CMClockGetHostTimeClock())
        }
        mixer.push(source: source, ptsNs: nanos(pts), interleaved: samples)
        for chunk in mixer.drain(final: false) {
            writeAudio(ptsNs: chunk.ptsNs, samples: chunk.samples)
        }
    }

    private func writeAudio(ptsNs: Int64, samples: [Float]) {
        guard sessionStarted, let input = audioInput else { return } // pre-roll before the first frame
        guard let mapped = timeline.map(ptsNs) else { return }
        var frame = (mapped - t0) * AUDIO_RATE / 1_000_000_000
        let count = samples.count / 2
        guard frame + Int64(count) > 0 else { return }
        // Keep the track contiguous: small overlaps (a buffer straddling a pause) are shifted.
        if frame < nextAudioFrame {
            if nextAudioFrame - frame > AUDIO_RATE / 10 { return }
            frame = nextAudioFrame
        }
        guard input.isReadyForMoreMediaData,
              let sampleBuffer = makeAudioBuffer(samples, frame: frame) else { return }
        if input.append(sampleBuffer) {
            nextAudioFrame = frame + Int64(count)
        }
    }

    private func makeAudioBuffer(_ samples: [Float], frame: Int64) -> CMSampleBuffer? {
        if audioFormat == nil {
            var asbd = AudioStreamBasicDescription(
                mSampleRate: Float64(AUDIO_RATE), mFormatID: kAudioFormatLinearPCM,
                mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
                mBytesPerPacket: 8, mFramesPerPacket: 1, mBytesPerFrame: 8,
                mChannelsPerFrame: 2, mBitsPerChannel: 32, mReserved: 0)
            CMAudioFormatDescriptionCreate(allocator: kCFAllocatorDefault, asbd: &asbd, layoutSize: 0,
                layout: nil, magicCookieSize: 0, magicCookie: nil, extensions: nil,
                formatDescriptionOut: &audioFormat)
        }
        guard let format = audioFormat else { return nil }
        let byteCount = samples.count * 4
        var block: CMBlockBuffer?
        guard CMBlockBufferCreateWithMemoryBlock(allocator: kCFAllocatorDefault, memoryBlock: nil,
            blockLength: byteCount, blockAllocator: kCFAllocatorDefault, customBlockSource: nil,
            offsetToData: 0, dataLength: byteCount, flags: kCMBlockBufferAssureMemoryNowFlag,
            blockBufferOut: &block) == kCMBlockBufferNoErr, let blockBuffer = block else { return nil }
        let copied = samples.withUnsafeBytes { (raw: UnsafeRawBufferPointer) -> OSStatus in
            CMBlockBufferReplaceDataBytes(with: raw.baseAddress!, blockBuffer: blockBuffer,
                offsetIntoDestination: 0, dataLength: byteCount)
        }
        guard copied == kCMBlockBufferNoErr else { return nil }
        var sampleBuffer: CMSampleBuffer?
        let status = CMAudioSampleBufferCreateReadyWithPacketDescriptions(
            allocator: kCFAllocatorDefault, dataBuffer: blockBuffer, formatDescription: format,
            sampleCount: samples.count / 2, presentationTimeStamp: CMTime(value: frame, timescale: CMTimeScale(AUDIO_RATE)),
            packetDescriptions: nil, sampleBufferOut: &sampleBuffer)
        return status == noErr ? sampleBuffer : nil
    }
}
