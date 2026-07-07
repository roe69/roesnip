using System;

namespace RoeSnip.Core.Capture;

/// <summary>Physical-pixel rectangle. Identical to the WPF app's type (PLAN.md §2.1) — copy verbatim,
/// only the namespace changed.</summary>
public readonly record struct RectPhysical(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public static RectPhysical FromSize(int left, int top, int width, int height)
        => new(left, top, left + width, top + height);
    public RectPhysical Normalized() => new(
        Math.Min(Left, Right), Math.Min(Top, Bottom),
        Math.Max(Left, Right), Math.Max(Top, Bottom));
}
