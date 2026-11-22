using System.Threading;

namespace RoeSnip.Core.Updates;

/// <summary>One-way race between an updater starting its swap and a deadline abandoning it.
/// Whichever call comes first wins; the other then returns false. Lets --self-update-now exit at
/// its deadline only while nothing on disk has been touched (the Windows swap is two moves).</summary>
public sealed class SwapGate
{
    private const int Open = 0;
    private const int Swapping = 1;
    private const int Abandoned = 2;

    private int _state = Open;

    /// <summary>Called right before the first swap step. False when the run was abandoned, in
    /// which case the caller must not touch the install.</summary>
    public bool TryBeginSwap() => Interlocked.CompareExchange(ref _state, Swapping, Open) == Open;

    /// <summary>Called at the deadline. False when the swap already started: wait for it.</summary>
    public bool TryAbandon() => Interlocked.CompareExchange(ref _state, Abandoned, Open) == Open;

    public bool SwapStarted => Volatile.Read(ref _state) == Swapping;
}
