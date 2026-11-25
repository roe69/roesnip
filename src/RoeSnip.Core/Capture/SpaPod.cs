using System.Buffers.Binary;
using System.Collections.Generic;

namespace RoeSnip.Core.Capture;

/// <summary>The few SPA pods a PipeWire cursor-metadata consumer needs, as raw bytes (spa_pod
/// builders are header-only inline C, so they cannot be P/Invoked). Layout: every pod is
/// {u32 body size, u32 type} plus the body padded to 8 bytes; an object body is
/// {u32 object type, u32 param id} then props {u32 key, u32 flags, pod}; a choice body is
/// {u32 choice type, u32 flags, u32 child size, u32 child type} then the values.</summary>
public static class SpaPod
{
    public const uint TypeId = 3, TypeInt = 4, TypeLong = 5, TypeRectangle = 10, TypeFraction = 11, TypeObject = 15, TypeChoice = 19;
    public const uint ObjectFormat = 0x40003, ObjectParamBuffers = 0x40004, ObjectParamMeta = 0x40005;
    public const uint ParamEnumFormat = 3, ParamFormat = 4, ParamBuffers = 5, ParamMeta = 6;
    public const uint KeyMediaType = 1, KeyMediaSubtype = 2, KeyVideoFormat = 0x20001, KeyVideoSize = 0x20003, KeyVideoFramerate = 0x20004;
    public const uint KeyVideoModifier = 0x20002, KeyVideoTransfer = 0x2000E, KeyVideoPrimaries = 0x2000F;
    public const uint KeyBuffersBuffers = 1, KeyBuffersBlocks = 2, KeyBuffersDataType = 6;
    public const uint ChoiceFlags = 4;
    public const uint PropMandatory = 8, PropDontFixate = 16;
    public const uint DataDmaBuf = 3;
    public const uint VideoRgbaF16 = 78, TransferSmpte2084 = 14, PrimariesBt2020 = 7;
    public const uint KeyMetaType = 1, KeyMetaSize = 2;
    public const uint MediaTypeVideo = 2, MediaSubtypeRaw = 1;
    public const uint ChoiceRange = 1, ChoiceEnum = 3;
    public const uint MetaHeader = 1, MetaCursor = 5;
    public const int MetaHeaderSize = 32;

    // spa_video_format values (same order as GstVideoFormat).
    public const uint VideoRGBx = 7, VideoBGRx = 8, VideoxRGB = 9, VideoxBGR = 10,
        VideoRGBA = 11, VideoBGRA = 12, VideoARGB = 13, VideoABGR = 14;

    /// <summary>EnumFormat for raw video in any 32-bit RGB layout (or half-float RGBA, an HDR node)
    /// and any size: it only has to intersect with the format the other consumer of the node already
    /// fixed.</summary>
    public static byte[] RawVideoEnumFormat()
    {
        var w = new Writer();
        w.BeginObject(ObjectFormat, ParamEnumFormat);
        w.Prop(KeyMediaType); w.Id(MediaTypeVideo);
        w.Prop(KeyMediaSubtype); w.Id(MediaSubtypeRaw);
        w.Prop(KeyVideoFormat);
        w.Choice(ChoiceEnum, TypeId, 4, new uint[]
        {
            VideoBGRx, VideoBGRx, VideoBGRA, VideoRGBx, VideoRGBA, VideoxRGB, VideoxBGR, VideoARGB, VideoABGR, VideoRgbaF16,
        });
        w.Prop(KeyVideoSize);
        w.Choice(ChoiceRange, TypeRectangle, 8, new uint[] { 1920, 1080, 1, 1, 16384, 16384 });
        w.Prop(KeyVideoFramerate);
        w.Choice(ChoiceRange, TypeFraction, 8, new uint[] { 0, 1, 0, 1, 1000, 1 });
        return w.EndObject();
    }

    /// <summary>EnumFormat for GNOME 50's HDR stream: RGBA_F16, PQ (SMPTE 2084) BT.2020, DMA-BUF only
    /// (a mandatory, not-yet-fixated modifier list; mutter fixates it and answers with a Format). The
    /// memfd path of HDR formats paints 8-bit garbage, so no modifier-less variant is offered.</summary>
    public static byte[] HdrEnumFormat(IReadOnlyList<ulong> modifiers)
    {
        var words = new List<uint>();
        void Long(ulong v) { words.Add((uint)v); words.Add((uint)(v >> 32)); }
        Long(modifiers[0]); // default
        foreach (var m in modifiers) Long(m);
        var w = new Writer();
        w.BeginObject(ObjectFormat, ParamEnumFormat);
        w.Prop(KeyMediaType); w.Id(MediaTypeVideo);
        w.Prop(KeyMediaSubtype); w.Id(MediaSubtypeRaw);
        w.Prop(KeyVideoFormat); w.Id(VideoRgbaF16);
        w.Prop(KeyVideoModifier, PropMandatory | PropDontFixate);
        w.Choice(ChoiceEnum, TypeLong, 8, words.ToArray());
        w.Prop(KeyVideoSize);
        w.Choice(ChoiceRange, TypeRectangle, 8, new uint[] { 1920, 1080, 1, 1, 16384, 16384 });
        w.Prop(KeyVideoFramerate);
        w.Choice(ChoiceRange, TypeFraction, 8, new uint[] { 0, 1, 0, 1, 1000, 1 });
        w.Prop(KeyVideoTransfer); w.Id(TransferSmpte2084);
        w.Prop(KeyVideoPrimaries); w.Id(PrimariesBt2020);
        return w.EndObject();
    }

    /// <summary>ParamBuffers asking for DMA-BUF buffers (one block).</summary>
    public static byte[] DmaBufBuffersParam()
    {
        var w = new Writer();
        w.BeginObject(ObjectParamBuffers, ParamBuffers);
        w.Prop(KeyBuffersBuffers);
        w.Choice(ChoiceRange, TypeInt, 4, new uint[] { 4, 2, 8 });
        w.Prop(KeyBuffersBlocks); w.Int(1);
        w.Prop(KeyBuffersDataType);
        w.Choice(ChoiceFlags, TypeInt, 4, new uint[] { 1u << (int)DataDmaBuf });
        return w.EndObject();
    }

    /// <summary>The fields of a negotiated video Format a DMA-BUF consumer needs.</summary>
    public readonly record struct VideoFormat(
        uint Format, int Width, int Height, ulong Modifier, bool HasModifier, bool ModifierIsChoice, uint Transfer, uint Primaries);

    /// <summary>Reads Format, size, modifier and colorimetry from a Format object pod (plain values,
    /// or the default of a choice).</summary>
    public static bool TryReadVideoFormat(ReadOnlySpan<byte> pod, out VideoFormat result)
    {
        result = default;
        if (pod.Length < 16) return false;
        uint bodySize = U32(pod, 0);
        if (U32(pod, 4) != TypeObject || 8 + bodySize > pod.Length) return false;
        int end = 8 + (int)bodySize;
        int at = 16;
        uint format = 0, transfer = 0, primaries = 0;
        int width = 0, height = 0;
        ulong modifier = 0;
        bool hasModifier = false, modifierChoice = false;
        while (at + 16 <= end)
        {
            uint key = U32(pod, at);
            uint size = U32(pod, at + 8);
            uint type = U32(pod, at + 12);
            int body = at + 16;
            if (body + size > end) return false;
            bool choice = type == TypeChoice && size >= 16;
            uint valueType = choice ? U32(pod, body + 12) : type;
            int value = choice ? body + 16 : body;
            switch (key)
            {
                case KeyVideoFormat when valueType == TypeId: format = U32(pod, value); break;
                case KeyVideoTransfer when valueType == TypeId: transfer = U32(pod, value); break;
                case KeyVideoPrimaries when valueType == TypeId: primaries = U32(pod, value); break;
                case KeyVideoSize when valueType == TypeRectangle:
                    width = (int)U32(pod, value);
                    height = (int)U32(pod, value + 4);
                    break;
                case KeyVideoModifier when valueType == TypeLong:
                    hasModifier = true;
                    // A choice that has narrowed to one distinct value is as good as fixed.
                    modifierChoice = choice && DistinctLongs(pod, value, (int)(size - 16) / 8) > 1;
                    modifier = (ulong)U32(pod, value) | ((ulong)U32(pod, value + 4) << 32);
                    break;
            }
            at = body + Pad8((int)size);
        }
        result = new VideoFormat(format, width, height, modifier, hasModifier, modifierChoice, transfer, primaries);
        return format != 0;
    }

    /// <summary>A ParamMeta asking for <paramref name="metaType"/> with a fixed size.</summary>
    public static byte[] MetaParam(uint metaType, int size)
    {
        var w = new Writer();
        w.BeginObject(ObjectParamMeta, ParamMeta);
        w.Prop(KeyMetaType); w.Id(metaType);
        w.Prop(KeyMetaSize); w.Int(size);
        return w.EndObject();
    }

    /// <summary>A ParamMeta asking for SPA_META_Cursor with room for up to a
    /// <paramref name="maxBitmap"/> square bitmap (range default=max, min=cursor only), as
    /// pipewiresrc requests it.</summary>
    public static byte[] CursorMetaParam(int maxBitmap)
    {
        int max = SpaCursorMeta.CursorSize + SpaCursorMeta.BitmapSize + maxBitmap * maxBitmap * 4;
        var w = new Writer();
        w.BeginObject(ObjectParamMeta, ParamMeta);
        w.Prop(KeyMetaType); w.Id(MetaCursor);
        w.Prop(KeyMetaSize);
        w.Choice(ChoiceRange, TypeInt, 4, new uint[] { (uint)max, (uint)SpaCursorMeta.CursorSize, (uint)max });
        return w.EndObject();
    }

    /// <summary>Reads SPA_FORMAT_VIDEO_size from a Format object pod (a plain rectangle, or the
    /// default of a choice). False when the pod is not an object or has no size.</summary>
    public static bool TryReadVideoSize(ReadOnlySpan<byte> pod, out int width, out int height)
    {
        width = height = 0;
        if (pod.Length < 16) return false;
        uint bodySize = U32(pod, 0);
        if (U32(pod, 4) != TypeObject || 8 + bodySize > pod.Length) return false;
        int end = 8 + (int)bodySize;
        int at = 16;
        while (at + 16 <= end)
        {
            uint key = U32(pod, at);
            uint size = U32(pod, at + 8);
            uint type = U32(pod, at + 12);
            int body = at + 16;
            if (body + size > end) return false;
            if (key == KeyVideoSize)
            {
                if (type == TypeRectangle && size >= 8)
                {
                    width = (int)U32(pod, body);
                    height = (int)U32(pod, body + 4);
                    return width > 0 && height > 0;
                }
                if (type == TypeChoice && size >= 24 && U32(pod, body + 12) == TypeRectangle)
                {
                    width = (int)U32(pod, body + 16);
                    height = (int)U32(pod, body + 20);
                    return width > 0 && height > 0;
                }
                return false;
            }
            at = body + Pad8((int)size);
        }
        return false;
    }

    private static int DistinctLongs(ReadOnlySpan<byte> pod, int at, int count)
    {
        var seen = new HashSet<ulong>();
        for (int i = 0; i < count && at + i * 8 + 8 <= pod.Length; i++)
        {
            seen.Add((ulong)U32(pod, at + i * 8) | ((ulong)U32(pod, at + i * 8 + 4) << 32));
        }
        return seen.Count;
    }

    private static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(at, 4));

    private static int Pad8(int n) => (n + 7) & ~7;

    private sealed class Writer
    {
        private readonly List<byte> _bytes = new();

        public void BeginObject(uint objectType, uint id)
        {
            U(0); U(TypeObject); // size patched in EndObject
            U(objectType); U(id);
        }

        public void Prop(uint key, uint flags = 0) { U(key); U(flags); }

        public void Id(uint value) { U(4); U(TypeId); U(value); U(0); }

        public void Int(int value) { U(4); U(TypeInt); U((uint)value); U(0); }

        public void Choice(uint choiceType, uint childType, int childSize, uint[] words)
        {
            int body = 16 + words.Length * 4;
            U((uint)body); U(TypeChoice);
            U(choiceType); U(0); U((uint)childSize); U(childType);
            foreach (var v in words) U(v);
            while (_bytes.Count % 8 != 0) _bytes.Add(0);
        }

        public byte[] EndObject()
        {
            var result = _bytes.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), (uint)(result.Length - 8));
            return result;
        }

        private void U(uint v)
        {
            Span<byte> tmp = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(tmp, v);
            _bytes.AddRange(tmp.ToArray());
        }
    }
}
