using System.Buffers.Binary;

namespace RoeSnip.Core.Capture;

/// <summary>The few SPA pods a PipeWire cursor-metadata consumer needs, as raw bytes (spa_pod
/// builders are header-only inline C, so they cannot be P/Invoked). Layout: every pod is
/// {u32 body size, u32 type} plus the body padded to 8 bytes; an object body is
/// {u32 object type, u32 param id} then props {u32 key, u32 flags, pod}; a choice body is
/// {u32 choice type, u32 flags, u32 child size, u32 child type} then the values.</summary>
public static class SpaPod
{
    public const uint TypeId = 3, TypeInt = 4, TypeRectangle = 10, TypeFraction = 11, TypeObject = 15, TypeChoice = 19;
    public const uint ObjectFormat = 0x40003, ObjectParamMeta = 0x40005;
    public const uint ParamEnumFormat = 3, ParamFormat = 4, ParamMeta = 6;
    public const uint KeyMediaType = 1, KeyMediaSubtype = 2, KeyVideoFormat = 0x20001, KeyVideoSize = 0x20003, KeyVideoFramerate = 0x20004;
    public const uint KeyMetaType = 1, KeyMetaSize = 2;
    public const uint MediaTypeVideo = 2, MediaSubtypeRaw = 1;
    public const uint ChoiceRange = 1, ChoiceEnum = 3;
    public const uint MetaHeader = 1, MetaCursor = 5;
    public const int MetaHeaderSize = 32;

    // spa_video_format values (same order as GstVideoFormat).
    public const uint VideoRGBx = 7, VideoBGRx = 8, VideoxRGB = 9, VideoxBGR = 10,
        VideoRGBA = 11, VideoBGRA = 12, VideoARGB = 13, VideoABGR = 14;

    /// <summary>EnumFormat for raw video in any 32-bit RGB layout and any size: it only has to
    /// intersect with the format the other consumer of the node already fixed.</summary>
    public static byte[] RawVideoEnumFormat()
    {
        var w = new Writer();
        w.BeginObject(ObjectFormat, ParamEnumFormat);
        w.Prop(KeyMediaType); w.Id(MediaTypeVideo);
        w.Prop(KeyMediaSubtype); w.Id(MediaSubtypeRaw);
        w.Prop(KeyVideoFormat);
        w.Choice(ChoiceEnum, TypeId, 4, new uint[]
        {
            VideoBGRx, VideoBGRx, VideoBGRA, VideoRGBx, VideoRGBA, VideoxRGB, VideoxBGR, VideoARGB, VideoABGR,
        });
        w.Prop(KeyVideoSize);
        w.Choice(ChoiceRange, TypeRectangle, 8, new uint[] { 1920, 1080, 1, 1, 16384, 16384 });
        w.Prop(KeyVideoFramerate);
        w.Choice(ChoiceRange, TypeFraction, 8, new uint[] { 0, 1, 0, 1, 1000, 1 });
        return w.EndObject();
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

        public void Prop(uint key) { U(key); U(0); }

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
