using System.Buffers.Binary;
using System.Text;
using RoeSnip.Core.Capture;

namespace RoeSnip.Platform.MacOS;

/// <summary>One message from <c>scksnap serve</c>. <see cref="Frames"/> is set instead of
/// <see cref="Payload"/> for an ok snapshot/region reply (the frames are read straight off the
/// pipe into their own pixel arrays).</summary>
public sealed record ScksnapMessage(uint Id, byte Status, byte[] Payload, IReadOnlyList<ScksnapFrame>? Frames = null)
{
    public string Text => Encoding.UTF8.GetString(Payload);
}

/// <summary>Stop reply: what the helper wrote into the MP4.</summary>
public readonly record struct ScksnapStopSummary(uint VideoFrames, uint DroppedFrames, uint DurationMs);

/// <summary>Wire format of <c>scksnap serve</c> (helpers/scksnap/Sources/Serve.swift has the
/// table - keep both in sync) and of the SCKSNAP1 frame (Scksnap.swift). Little-endian throughout.
/// Request: u32 length | u8 op | u32 id | payload. Response: u32 length | u32 id | u8 status |
/// payload. Length counts the bytes after the length field.</summary>
public static class ScksnapProtocol
{
    public const uint Version = 1;

    public const byte OpList = 1;
    public const byte OpSnapshot = 2;
    public const byte OpSnapshotRegion = 3;
    public const byte OpStartRecord = 4;
    public const byte OpPause = 5;
    public const byte OpResume = 6;
    public const byte OpStop = 7;
    public const byte OpSetOrigin = 8;
    public const byte OpPing = 9;
    public const byte OpQuit = 10;

    public const byte StatusOk = 0;
    public const byte StatusFailure = 1;
    public const byte StatusBadRequest = 2;
    public const byte StatusNoDisplay = 3;
    public const byte StatusTccDenied = 82;

    /// <summary>Responses with this id are events.</summary>
    public const uint EventId = 0;
    public const byte EventRecordingFailed = 1;

    public const byte RecordCursor = 1;
    public const byte RecordMicrophone = 2;
    public const byte RecordSystemAudio = 4;

    public const byte AudioMicrophoneActive = 1;
    public const byte AudioSystemActive = 2;

    /// <summary>SCKSNAP1 header <c>source</c> field.</summary>
    public const uint SourceUnspecified = 0;
    public const uint SourceStream = 1;
    public const uint SourceScreenshot = 2;

    public const int MaxMessageBytes = 1 << 30;

    private const string FrameMagic = "SCKSNAP1";
    public const int FrameHeaderSize = 96;

    public static byte[] EncodeRequest(byte op, uint id, ReadOnlySpan<byte> payload)
    {
        var bytes = new byte[9 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)(5 + payload.Length));
        bytes[4] = op;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), id);
        payload.CopyTo(bytes.AsSpan(9));
        return bytes;
    }

    public static byte[] EncodeSnapshot(bool background, IReadOnlyList<uint> displayIds)
    {
        var bytes = new byte[5 + 4 * displayIds.Count];
        bytes[0] = background ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), (uint)displayIds.Count);
        for (int i = 0; i < displayIds.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5 + 4 * i), displayIds[i]);
        }
        return bytes;
    }

    public static byte[] EncodeRegion(uint displayId, int x, int y, int width, int height)
    {
        var bytes = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, displayId);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), x);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), y);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), height);
        return bytes;
    }

    public static byte[] EncodeStartRecord(
        uint displayId, int x, int y, int width, int height, int fps, byte flags,
        uint videoBitsPerSecond, uint audioBitsPerSecond, string outputPath)
    {
        byte[] path = Encoding.UTF8.GetBytes(outputPath);
        if (path.Length == 0 || path.Length > ushort.MaxValue)
        {
            throw new ArgumentException("Recording path is empty or too long.", nameof(outputPath));
        }
        var bytes = new byte[35 + path.Length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, displayId);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], x);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], y);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], width);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], height);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)fps);
        span[24] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(span[25..], videoBitsPerSecond);
        BinaryPrimitives.WriteUInt32LittleEndian(span[29..], audioBitsPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(span[33..], (ushort)path.Length);
        path.CopyTo(span[35..]);
        return bytes;
    }

    public static byte[] EncodeOrigin(int x, int y)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, x);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), y);
        return bytes;
    }

    public static ScksnapStopSummary ParseStop(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12)
        {
            throw new CaptureException($"scksnap stop reply is {payload.Length} bytes, expected 12.");
        }
        return new ScksnapStopSummary(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]));
    }

    /// <summary>Reads one response. Returns null on a clean end of stream before a message starts;
    /// throws <see cref="EndOfStreamException"/> when the stream ends inside one.
    /// <paramref name="framesReply"/> says, per id, whether an ok reply carries frames.</summary>
    public static ScksnapMessage? ReadMessage(Stream stream, Func<uint, bool> framesReply)
    {
        Span<byte> head = stackalloc byte[9];
        int got = ReadAtMost(stream, head[..4]);
        if (got == 0) return null;
        if (got < 4) throw new EndOfStreamException("scksnap stream ended inside a message header.");
        ReadExactly(stream, head[4..]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(head);
        if (length < 5 || length > MaxMessageBytes)
        {
            throw new InvalidDataException($"scksnap message length {length} is out of range.");
        }
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
        byte status = head[8];
        int payloadLength = (int)length - 5;
        if (status == StatusOk && id != EventId && framesReply(id))
        {
            return new ScksnapMessage(id, status, Array.Empty<byte>(), ReadFrames(stream, payloadLength));
        }
        var payload = new byte[payloadLength];
        ReadExactly(stream, payload);
        return new ScksnapMessage(id, status, payload);
    }

    /// <summary>u32 count | (u32 size | SCKSNAP1 frame) * count, exactly <paramref name="payloadLength"/>
    /// bytes. Pixels land in one array per frame (no whole-payload copy).</summary>
    public static IReadOnlyList<ScksnapFrame> ReadFrames(Stream stream, int payloadLength)
    {
        if (payloadLength < 4) throw new InvalidDataException("scksnap frames payload is shorter than its count.");
        Span<byte> word = stackalloc byte[4];
        ReadExactly(stream, word);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(word);
        long remaining = payloadLength - 4;
        var frames = new List<ScksnapFrame>((int)Math.Min(count, 64));
        var header = new byte[FrameHeaderSize];
        for (uint i = 0; i < count; i++)
        {
            if (remaining < 4) throw new InvalidDataException("scksnap frames payload is truncated.");
            ReadExactly(stream, word);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(word);
            remaining -= 4;
            if (size < FrameHeaderSize || size > remaining)
            {
                throw new InvalidDataException($"scksnap frame size {size} does not fit the payload.");
            }
            ReadExactly(stream, header);
            var parsed = ParseHeader(header, size, "scksnap serve frame");
            int extraHeader = (int)parsed.HeaderSize - FrameHeaderSize;
            Skip(stream, extraHeader);
            var pixels = new byte[parsed.PixelBytes];
            ReadExactly(stream, pixels);
            Skip(stream, size - parsed.HeaderSize - parsed.PixelBytes);
            remaining -= size;
            frames.Add(parsed.ToFrame(pixels));
        }
        Skip(stream, remaining);
        return frames;
    }

    /// <summary>Decodes one complete SCKSNAP1 frame (header + pixel rows).</summary>
    public static ScksnapFrame ParseFrame(ReadOnlySpan<byte> bytes, string what)
    {
        if (bytes.Length < FrameHeaderSize)
        {
            throw new CaptureException($"{what} has a bad or missing SCKSNAP1 header.");
        }
        var parsed = ParseHeader(bytes[..FrameHeaderSize], (uint)bytes.Length, what);
        var pixels = bytes.Slice((int)parsed.HeaderSize, (int)parsed.PixelBytes).ToArray();
        return parsed.ToFrame(pixels);
    }

    private readonly record struct FrameHeader(
        uint HeaderSize, uint FormatCode, int Width, int Height, int Stride, uint DisplayId,
        RectPhysical Bounds, double Scale, double EdrHeadroom, bool IsPrimary,
        uint Source, uint AgeMs, ulong Sequence)
    {
        public long PixelBytes => (long)Stride * Height;

        public ScksnapFrame ToFrame(byte[] pixels) => new(
            FormatCode, Width, Height, Stride, DisplayId, Bounds, Scale, EdrHeadroom, IsPrimary, pixels,
            Source, AgeMs, Sequence);
    }

    private static FrameHeader ParseHeader(ReadOnlySpan<byte> span, uint totalSize, string what)
    {
        if (Encoding.ASCII.GetString(span[..8]) != FrameMagic)
        {
            throw new CaptureException($"{what} has a bad or missing SCKSNAP1 header.");
        }
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
        uint formatCode = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(span[20..]);
        uint stride = BinaryPrimitives.ReadUInt32LittleEndian(span[24..]);
        if (headerSize < FrameHeaderSize || headerSize > totalSize)
        {
            throw new CaptureException($"{what} reports invalid headerSize {headerSize}.");
        }
        if (width == 0 || height == 0 || stride == 0 || width > 65536 || height > 65536 || stride > 1 << 20)
        {
            throw new CaptureException($"{what} reports degenerate geometry {width}x{height} stride {stride}.");
        }
        long pixelBytes = (long)stride * height;
        if (totalSize - headerSize < pixelBytes)
        {
            throw new CaptureException(
                $"{what} is truncated: expected {pixelBytes} pixel bytes after the {headerSize}-byte header, " +
                $"found {totalSize - headerSize}.");
        }
        return new FrameHeader(
            headerSize, formatCode, (int)width, (int)height, (int)stride,
            BinaryPrimitives.ReadUInt32LittleEndian(span[28..]),
            RectPhysical.FromSize(
                BinaryPrimitives.ReadInt32LittleEndian(span[32..]),
                BinaryPrimitives.ReadInt32LittleEndian(span[36..]),
                BinaryPrimitives.ReadInt32LittleEndian(span[40..]),
                BinaryPrimitives.ReadInt32LittleEndian(span[44..])),
            BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64LittleEndian(span[48..])),
            BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64LittleEndian(span[56..])),
            BinaryPrimitives.ReadUInt32LittleEndian(span[64..]) != 0,
            BinaryPrimitives.ReadUInt32LittleEndian(span[68..]),
            BinaryPrimitives.ReadUInt32LittleEndian(span[72..]),
            BinaryPrimitives.ReadUInt64LittleEndian(span[76..]));
    }

    private static int ReadAtMost(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        if (ReadAtMost(stream, buffer) != buffer.Length)
        {
            throw new EndOfStreamException("scksnap stream ended inside a message.");
        }
    }

    private static void Skip(Stream stream, long count)
    {
        if (count <= 0) return;
        Span<byte> scratch = stackalloc byte[4096];
        while (count > 0)
        {
            int chunk = (int)Math.Min(count, scratch.Length);
            ReadExactly(stream, scratch[..chunk]);
            count -= chunk;
        }
    }
}
