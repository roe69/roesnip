using System.Buffers.Binary;
using System.Text;
using RoeSnip.Core.Capture;
using RoeSnip.Platform.MacOS;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>The serve-mode wire format must match helpers/scksnap/Sources/Serve.swift and the
/// SCKSNAP1 layout in Scksnap.swift byte for byte; these build the helper's bytes by hand.</summary>
public class ScksnapProtocolTests
{
    private static byte[] Frame(uint displayId, int width, int height, uint source = 0, uint ageMs = 0, ulong sequence = 0)
    {
        int stride = width * 4;
        var bytes = new byte[96 + stride * height];
        var span = bytes.AsSpan();
        Encoding.ASCII.GetBytes("SCKSNAP1").CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 96);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)stride);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], displayId);
        BinaryPrimitives.WriteInt32LittleEndian(span[32..], -1920);
        BinaryPrimitives.WriteInt32LittleEndian(span[36..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], 1920);
        BinaryPrimitives.WriteInt32LittleEndian(span[44..], 1080);
        BinaryPrimitives.WriteUInt64LittleEndian(span[48..], BitConverter.DoubleToUInt64Bits(2.0));
        BinaryPrimitives.WriteUInt64LittleEndian(span[56..], BitConverter.DoubleToUInt64Bits(1.5));
        BinaryPrimitives.WriteUInt32LittleEndian(span[64..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[68..], source);
        BinaryPrimitives.WriteUInt32LittleEndian(span[72..], ageMs);
        BinaryPrimitives.WriteUInt64LittleEndian(span[76..], sequence);
        for (int i = 96; i < bytes.Length; i++) bytes[i] = (byte)i;
        return bytes;
    }

    private static byte[] Response(uint id, byte status, byte[] payload)
    {
        var bytes = new byte[9 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)(5 + payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), id);
        bytes[8] = status;
        payload.CopyTo(bytes, 9);
        return bytes;
    }

    private static byte[] FramesPayload(params byte[][] frames)
    {
        var out_ = new MemoryStream();
        var word = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)frames.Length);
        out_.Write(word);
        foreach (var f in frames)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)f.Length);
            out_.Write(word);
            out_.Write(f);
        }
        return out_.ToArray();
    }

    [Fact]
    public void Request_IsLengthOpIdPayload()
    {
        var bytes = ScksnapProtocol.EncodeRequest(ScksnapProtocol.OpSnapshot, 7, new byte[] { 0xAA, 0xBB });
        Assert.Equal(new byte[] { 7, 0, 0, 0, 2, 7, 0, 0, 0, 0xAA, 0xBB }, bytes);
    }

    [Fact]
    public void SnapshotPayload_IsFlagsCountIds()
    {
        var bytes = ScksnapProtocol.EncodeSnapshot(background: true, new uint[] { 1, 0x01020304 });
        Assert.Equal(new byte[] { 1, 2, 0, 0, 0, 1, 0, 0, 0, 4, 3, 2, 1 }, bytes);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0 }, ScksnapProtocol.EncodeSnapshot(false, Array.Empty<uint>()));
    }

    [Fact]
    public void StartRecordPayload_MatchesSwiftLayout()
    {
        var bytes = ScksnapProtocol.EncodeStartRecord(5, 10, -20, 640, 480, 30,
            ScksnapProtocol.RecordCursor | ScksnapProtocol.RecordSystemAudio, 4_000_000, 128_000, "/tmp/a.mp4");
        var span = bytes.AsSpan();
        Assert.Equal(35 + 10, bytes.Length);
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(span));
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(span[4..]));
        Assert.Equal(-20, BinaryPrimitives.ReadInt32LittleEndian(span[8..]));
        Assert.Equal(640, BinaryPrimitives.ReadInt32LittleEndian(span[12..]));
        Assert.Equal(480, BinaryPrimitives.ReadInt32LittleEndian(span[16..]));
        Assert.Equal(30u, BinaryPrimitives.ReadUInt32LittleEndian(span[20..]));
        Assert.Equal(5, span[24]);
        Assert.Equal(4_000_000u, BinaryPrimitives.ReadUInt32LittleEndian(span[25..]));
        Assert.Equal(128_000u, BinaryPrimitives.ReadUInt32LittleEndian(span[29..]));
        Assert.Equal(10, BinaryPrimitives.ReadUInt16LittleEndian(span[33..]));
        Assert.Equal("/tmp/a.mp4", Encoding.UTF8.GetString(span[35..]));
    }

    [Fact]
    public void ReadMessage_ParsesFramesStraightFromTheStream()
    {
        var payload = FramesPayload(Frame(1, 3, 2, ScksnapProtocol.SourceStream, 42, 9), Frame(2, 1, 1));
        var stream = new MemoryStream(Response(11, ScksnapProtocol.StatusOk, payload));

        var message = ScksnapProtocol.ReadMessage(stream, id => id == 11);

        Assert.NotNull(message);
        Assert.Equal(11u, message!.Id);
        var frames = message.Frames!;
        Assert.Equal(2, frames.Count);
        Assert.Equal(1u, frames[0].DisplayId);
        Assert.Equal(3, frames[0].Width);
        Assert.Equal(2, frames[0].Height);
        Assert.Equal(12, frames[0].Stride);
        Assert.Equal(RectPhysical.FromSize(-1920, 0, 1920, 1080), frames[0].BoundsPx);
        Assert.Equal(2.0, frames[0].Scale);
        Assert.Equal(1.5, frames[0].EdrHeadroom);
        Assert.True(frames[0].IsPrimary);
        Assert.Equal(ScksnapProtocol.SourceStream, frames[0].Source);
        Assert.Equal(42u, frames[0].AgeMs);
        Assert.Equal(9ul, frames[0].Sequence);
        Assert.Equal((byte)96, frames[0].Pixels[0]);
        Assert.Equal(24, frames[0].Pixels.Length);
        Assert.Equal(4, frames[1].Pixels.Length);
        Assert.Null(ScksnapProtocol.ReadMessage(stream, _ => true)); // clean end
    }

    [Fact]
    public void ReadMessage_ErrorStatusAndEventsCarryText()
    {
        var bytes = Response(3, ScksnapProtocol.StatusTccDenied, Encoding.UTF8.GetBytes("denied"))
            .Concat(Response(ScksnapProtocol.EventId, 0, new byte[] { ScksnapProtocol.EventRecordingFailed, (byte)'x' }))
            .ToArray();
        var stream = new MemoryStream(bytes);

        var denied = ScksnapProtocol.ReadMessage(stream, _ => true)!;
        Assert.Equal(ScksnapProtocol.StatusTccDenied, denied.Status);
        Assert.Null(denied.Frames); // non-ok replies never parse as frames
        Assert.Equal("denied", denied.Text);

        var ev = ScksnapProtocol.ReadMessage(stream, _ => true)!;
        Assert.Equal(ScksnapProtocol.EventId, ev.Id);
        Assert.Equal(new byte[] { ScksnapProtocol.EventRecordingFailed, (byte)'x' }, ev.Payload);
    }

    [Fact]
    public void ReadMessage_TruncatedStreamThrows()
    {
        var full = Response(4, ScksnapProtocol.StatusOk, FramesPayload(Frame(1, 2, 2)));
        Assert.Throws<EndOfStreamException>(() =>
            ScksnapProtocol.ReadMessage(new MemoryStream(full[..^3]), _ => true));
        Assert.Throws<EndOfStreamException>(() =>
            ScksnapProtocol.ReadMessage(new MemoryStream(full[..2]), _ => true));
    }

    [Fact]
    public void ReadMessage_FrameLargerThanPayloadIsRejected()
    {
        var payload = FramesPayload(Frame(1, 2, 2));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 10_000);
        Assert.Throws<InvalidDataException>(() =>
            ScksnapProtocol.ReadMessage(new MemoryStream(Response(1, 0, payload)), _ => true));
    }

    [Fact]
    public void ParseFrame_OneShotFileKeepsZeroStreamFields()
    {
        var frame = ScksnapProtocol.ParseFrame(Frame(7, 4, 1), "test");
        Assert.Equal(7u, frame.DisplayId);
        Assert.Equal(ScksnapProtocol.SourceUnspecified, frame.Source);
        Assert.Equal(0ul, frame.Sequence);
        Assert.Throws<CaptureException>(() => ScksnapProtocol.ParseFrame(Frame(7, 4, 1)[..100], "test"));
    }

    [Fact]
    public void ParseStop_ReadsFramesDroppedDuration()
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 300);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 10_000);
        Assert.Equal(new ScksnapStopSummary(300, 2, 10_000), ScksnapProtocol.ParseStop(payload));
    }
}
