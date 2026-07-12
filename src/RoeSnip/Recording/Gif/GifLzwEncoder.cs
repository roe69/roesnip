using System;
using System.IO;

namespace RoeSnip.Recording.Gif;

/// <summary>A from-scratch GIF-flavor LZW compressor (variable 2-12 bit codes, LSB-first bit
/// packing, 255-byte data sub-blocks) replacing the old design's approach of asking WPF's
/// <c>GifBitmapEncoder</c> to do this per frame and salvaging the bytes back out — see the removed
/// <c>EncodeAndWrite</c> on <see cref="RoeSnip.Recording.GifEncoder"/> for why that was ever
/// necessary in the first place (no incremental multi-Save API) and what it cost.
///
/// One instance is created once and reused for every frame of a recording: the dictionary (an
/// open-addressing hash table mapping (prefixCode, nextSymbolByte) to a code) and the bit/sub-block
/// packing buffers are all instance fields sized once and cleared in place between calls, never
/// reallocated — the same LOH/Gen2 discipline documented on <see cref="RoeSnip.Recording.GifEncoder"/>
/// applies here: this runs at recording cadence, once per emitted frame.
///
/// <see cref="Encode"/> assumes every value in <c>indices</c> is strictly less than
/// <c>1 &lt;&lt; minCodeSize</c> (the caller — GifEncoder's orchestration, which knows this frame's
/// palette size including the reserved transparent index — is responsible for picking a
/// <c>minCodeSize</c> that fits every index actually used) and throws if it finds one that
/// doesn't; that would otherwise silently corrupt the bitstream.</summary>
public sealed class GifLzwEncoder
{
    // Power of two, comfortably above the 4096-code dictionary ceiling, so linear-probe hash
    // lookups always terminate and load factor never exceeds 0.5.
    private const int TableSize = 8192;
    private const int TableMask = TableSize - 1;
    private const int MaxCode = 4096; // 12-bit code space: codes 0..4095

    // -1 in _hashCode marks an empty slot. Reset between frames (and on an internal dictionary-full
    // clear) via a single Array.Fill — the array itself is never reallocated.
    private readonly int[] _hashCode = new int[TableSize];
    private readonly int[] _hashKey = new int[TableSize];

    private readonly byte[] _subBlock = new byte[255];
    private int _subBlockLen;

    private uint _bitBuffer;
    private int _bitCount;

    /// <summary>Writes the LZW minimum-code-size byte, then the compressed data as a chain of
    /// 255-byte (or shorter, for the final one) sub-blocks, then the 0x00 block terminator —
    /// exactly the byte layout a GIF Image Descriptor's data expects, ready to write straight after
    /// it. <paramref name="minCodeSize"/> must be between 2 and 8 inclusive (GIF's own range).</summary>
    public void Encode(Stream output, ReadOnlySpan<byte> indices, int minCodeSize)
    {
        if (minCodeSize < 2 || minCodeSize > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(minCodeSize), "GIF minimum code size must be 2-8.");
        }

        output.WriteByte((byte)minCodeSize);

        int clearCode = 1 << minCodeSize;
        int endCode = clearCode + 1;
        int nextCode = clearCode + 2;
        int codeWidth = minCodeSize + 1;

        _bitBuffer = 0;
        _bitCount = 0;
        _subBlockLen = 0;
        ResetDictionary();

        WriteCode(output, clearCode, codeWidth);

        if (indices.Length > 0)
        {
            int prefix = ValidateSymbol(indices[0], clearCode);
            for (int i = 1; i < indices.Length; i++)
            {
                int symbol = ValidateSymbol(indices[i], clearCode);

                if (TryGetChild(prefix, symbol, out int child))
                {
                    prefix = child;
                    continue;
                }

                WriteCode(output, prefix, codeWidth);

                if (nextCode < MaxCode)
                {
                    Insert(prefix, symbol, nextCode);
                    nextCode++;
                    // Width must grow once a just-inserted code no longer fits in codeWidth bits —
                    // i.e. once the inserted code (nextCode - 1, since nextCode was already bumped
                    // above) reaches 1<<codeWidth, the first value NOT representable in codeWidth
                    // bits (whose max representable value is (1<<codeWidth)-1). That is
                    // nextCode == (1<<codeWidth) + 1. Growing one entry earlier than this (at
                    // nextCode == 1<<codeWidth, i.e. right after inserting the LAST code that still
                    // fits) desyncs the bitstream from every real-world decoder (confirmed against
                    // WPF GifBitmapDecoder and GDI+, which both reject that off-by-one): every code
                    // written between the premature grow point and here would carry one extra
                    // (wasted, and stream-desyncing) bit the decoder does not expect.
                    if (nextCode == (1 << codeWidth) + 1 && codeWidth < 12)
                    {
                        codeWidth++;
                    }
                }
                else
                {
                    // Dictionary is full: reset it and tell the decoder to do the same.
                    WriteCode(output, clearCode, codeWidth);
                    ResetDictionary();
                    nextCode = clearCode + 2;
                    codeWidth = minCodeSize + 1;
                }

                prefix = symbol;
            }
            WriteCode(output, prefix, codeWidth);
        }

        WriteCode(output, endCode, codeWidth);
        FlushBits(output);
        FlushSubBlock(output);
        output.WriteByte(0x00); // block terminator
    }

    private static int ValidateSymbol(byte value, int clearCode)
    {
        if (value >= clearCode)
        {
            throw new ArgumentException(
                $"Index value {value} does not fit the chosen minCodeSize (root alphabet is 0..{clearCode - 1}).");
        }
        return value;
    }

    private void ResetDictionary() => Array.Fill(_hashCode, -1);

    private bool TryGetChild(int prefix, int symbol, out int code)
    {
        int key = (prefix << 8) | symbol;
        int idx = HashIndex(key);
        while (_hashCode[idx] != -1)
        {
            if (_hashKey[idx] == key)
            {
                code = _hashCode[idx];
                return true;
            }
            idx = (idx + 1) & TableMask;
        }
        code = -1;
        return false;
    }

    private void Insert(int prefix, int symbol, int code)
    {
        int key = (prefix << 8) | symbol;
        int idx = HashIndex(key);
        while (_hashCode[idx] != -1)
        {
            idx = (idx + 1) & TableMask;
        }
        _hashCode[idx] = code;
        _hashKey[idx] = key;
    }

    private static int HashIndex(int key) => (int)(((uint)key * 2654435761u) >> 19) & TableMask;

    private void WriteCode(Stream output, int code, int codeWidth)
    {
        _bitBuffer |= (uint)code << _bitCount;
        _bitCount += codeWidth;
        while (_bitCount >= 8)
        {
            AppendByte(output, (byte)(_bitBuffer & 0xFF));
            _bitBuffer >>= 8;
            _bitCount -= 8;
        }
    }

    private void FlushBits(Stream output)
    {
        if (_bitCount > 0)
        {
            AppendByte(output, (byte)(_bitBuffer & 0xFF));
            _bitBuffer = 0;
            _bitCount = 0;
        }
    }

    private void AppendByte(Stream output, byte b)
    {
        _subBlock[_subBlockLen++] = b;
        if (_subBlockLen == 255)
        {
            output.WriteByte(255);
            output.Write(_subBlock, 0, 255);
            _subBlockLen = 0;
        }
    }

    private void FlushSubBlock(Stream output)
    {
        if (_subBlockLen > 0)
        {
            output.WriteByte((byte)_subBlockLen);
            output.Write(_subBlock, 0, _subBlockLen);
            _subBlockLen = 0;
        }
    }
}
