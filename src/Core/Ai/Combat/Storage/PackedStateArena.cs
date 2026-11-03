using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat;

/// <summary>
/// Append-only byte arena for immutable packed <see cref="Battle"/> records. Every block has
/// <see cref="PayloadBytes"/> bytes of payload plus one full <see cref="Battle"/> of tail slack, so a
/// <c>ref Battle</c> view at any record start can be read and written as a complete struct without
/// crossing the array boundary. Only the packed prefix participates in identity; the slack and
/// inter-record padding are undefined.
///
/// Records are copied into the arena on insert. <see cref="Move"/> moves offsets only; a subsequent
/// <see cref="Truncate"/> slides the surviving records forward in place and releases the tail blocks,
/// which also lowers the search-memory ledger.
/// </summary>
internal sealed class PackedStateArena
{
    /// <summary>
    /// Payload bytes per block (before the full-struct tail slack). 2 MiB keeps the one-state tail
    /// reservation near 4.5% of a block at the 1023-card struct size.
    /// </summary>
    internal const int PayloadBytes = 1 << 21;

    /// <summary>Complete block length: payload plus one aligned <see cref="Battle"/>.</summary>
    internal const int BlockBytes = PayloadBytes + Battle.ExpectedSize;

    /// <summary>Dead-byte threshold for an in-place compaction after a truncation.</summary>
    private const int CompactionDeadBytes = PayloadBytes / 2;

    private readonly SearchStorage _storage;
    private readonly List<byte[]> _blocks = [];
    private readonly long[] _offsets;
    private long _tail;
    private int _block;
    private long _liveBytes;
    private long _reservedOffset;
    private bool _reserved;

    internal PackedStateArena(int maxCapacity, SearchStorage storage)
    {
        _storage = storage;
        _offsets = storage.RentArray<long>(Math.Max(1, maxCapacity));
    }

    internal int Count { get; private set; }

    /// <summary>Logical bytes currently occupied by records, including alignment padding.</summary>
    internal long UsedBytes => _tail;

    internal bool IsReserved => _reserved;

    /// <summary>Absolute byte offset of a record; used by the compaction contract checks.</summary>
    internal long OffsetOf(int index)
    {
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _offsets[index];
    }

    /// <summary>Byte offset inside its block for an absolute record offset.</summary>
    internal static int InBlockOffset(long offset) => (int)(offset % BlockBytes);

    internal ref readonly Battle this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return ref StateAt(_offsets[index]);
        }
    }

    /// <summary>The state being built in the reserved tail slot.</summary>
    internal ref Battle ReservedState
    {
        get
        {
            if (!_reserved)
                throw new InvalidOperationException("状态池没有已预留的尾部记录。");
            return ref StateAtMutable(_reservedOffset);
        }
    }

    /// <summary>
    /// Reserves the tail slot for one child. The caller has a complete (<see cref="Battle.ExpectedSize"/>)
    /// workspace there, including undefined suffix bytes. The reservation is consumed by
    /// <see cref="CommitReserved"/> or replaced by the next <see cref="Reserve"/>.
    /// </summary>
    internal void Reserve()
    {
        EnsureTailRoom();
        _reservedOffset = _tail;
        _reserved = true;
    }

    /// <summary>Commits the reserved record and advances the tail by <c>Align8(PackedSize)</c>.</summary>
    internal void CommitReserved()
    {
        if (!_reserved)
            throw new InvalidOperationException("状态池没有已预留的尾部记录。");
        int packed = Battle.PackedSize(in ReservedState);
        _offsets[Count] = _reservedOffset;
        Count++;
        _tail += Align8(packed);
        _liveBytes += Align8(packed);
        _reserved = false;
    }

    internal void DiscardReserved() => _reserved = false;

    /// <summary>Copies the packed prefix of a committed state to the tail.</summary>
    internal void Add(in Battle state)
    {
        EnsureTailRoom();
        ref Battle destination = ref StateAtMutable(_tail);
        Battle.CopyPacked(in state, ref destination);
        int packed = Battle.PackedSize(in state);
        _offsets[Count] = _tail;
        Count++;
        _tail += Align8(packed);
        _liveBytes += Align8(packed);
    }

    /// <summary>
    /// Moves the record reference at <paramref name="from"/> to <paramref name="to"/> without copying
    /// its bytes. The destination slot's old bytes become dead and are reclaimed by the next
    /// compaction. Callers must preserve ascending source order when they later compact: every
    /// production caller moves from a higher selected index to a lower destination index in one
    /// ascending pass, so each record's target offset is at or below its source offset.
    /// </summary>
    internal void Move(int from, int to)
    {
        if ((uint)from >= (uint)Count || (uint)to >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(from));
        _offsets[to] = _offsets[from];
    }

    /// <summary>Drops the tail records and compacts the surviving bytes in place when worthwhile.</summary>
    internal void Truncate(int count)
    {
        if ((uint)count > (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(count));
        Count = count;
        _reserved = false;
        long live = 0;
        for (int index = 0; index < count; index++)
            live += Align8(Battle.PackedSize(in StateAt(_offsets[index])));
        _liveBytes = live;
        long dead = _tail - live;
        if (dead >= CompactionDeadBytes && dead * 4 >= _tail)
            Compact();
    }

    internal void Clear()
    {
        Count = 0;
        _tail = 0;
        _block = 0;
        _liveBytes = 0;
        _reserved = false;
    }

    private void EnsureTailRoom()
    {
        while (_block >= _blocks.Count)
            _blocks.Add(_storage.RentChunk<byte>(BlockBytes));
        long blockEnd = (long)(_block + 1) * BlockBytes;
        if (_tail + Battle.ExpectedSize <= blockEnd)
            return;
        _block++;
        _tail = (long)_block * BlockBytes;
        while (_block >= _blocks.Count)
            _blocks.Add(_storage.RentChunk<byte>(BlockBytes));
    }

    private ref readonly Battle StateAt(long offset) => ref StateAtMutable(offset);

    private ref Battle StateAtMutable(long offset)
        => ref Unsafe.As<byte, Battle>(
            ref _blocks[(int)(offset / BlockBytes)][(int)(offset % BlockBytes)]);

    /// <summary>
    /// Slides the live records to the front in place. Production callers reach this through
    /// <see cref="Truncate"/>; KernelChecks calls it directly for the compaction contracts.
    /// </summary>
    internal void Compact()
    {
        long destination = 0;
        for (int index = 0; index < Count; index++)
        {
            long source = _offsets[index];
            int packed = Battle.PackedSize(in StateAt(source));
            int aligned = Align8(packed);
            int destinationInBlock = (int)(destination % BlockBytes);
            // Match EnsureTailRoom: the record's start must leave a complete Battle of room so a
            // later full-struct ref view can never cross the array boundary.
            if (destinationInBlock + Battle.ExpectedSize > BlockBytes)
                destination = (destination / BlockBytes + 1) * BlockBytes;
            if (destination > source)
                throw new InvalidOperationException(
                    "状态池压缩要求偏移按下标递增：目标位置越过了源记录。");
            if (destination != source)
                CopyBytes(source, destination, aligned);
            _offsets[index] = destination;
            destination += aligned;
        }
        int destinationBlock = (int)(destination / BlockBytes);
        int retainedBlock = Math.Min(destinationBlock, _blocks.Count - 1);
        for (int index = _blocks.Count - 1; index > retainedBlock; index--)
        {
            _storage.DiscardChunk(_blocks[index]);
            _blocks.RemoveAt(index);
        }
        _block = retainedBlock;
        _tail = destination;
        _liveBytes = destination;
    }

    private void CopyBytes(long source, long destination, int length)
    {
        int sourceBlock = (int)(source / BlockBytes);
        int destinationBlock = (int)(destination / BlockBytes);
        _blocks[sourceBlock].AsSpan((int)(source % BlockBytes), length)
            .CopyTo(_blocks[destinationBlock].AsSpan((int)(destination % BlockBytes), length));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Align8(int value) => (value + 7) & ~7;
}
