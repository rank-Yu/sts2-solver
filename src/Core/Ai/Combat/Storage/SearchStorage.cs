using System.Runtime.CompilerServices;

namespace Sts2Solver.Ai.Combat;

/// <summary>
/// Reusable search-owned memory. Chunks and fixed arrays are retained between solves of the same
/// storage; the next solve rents them again before allocating. The ledger counts the retained blocks,
/// so a multi-round widening loop stays under one budget instead of paying for every round's
/// high-water mark.
/// </summary>
internal sealed class SearchStorage
{
    private readonly List<Array> _chunks = [];
    private readonly List<(Type Type, int Length, Array Array)> _arrays = [];
    private readonly Dictionary<(Type Type, int Length), Stack<Array>> _freeChunks = [];
    private readonly Dictionary<(Type Type, int Length), Stack<Array>> _freeArrays = [];
    private int[] _intScratch = [];

    internal SearchMemoryLedger Memory { get; } = new();

    /// <summary>Total bytes allocated by this storage over its lifetime.</summary>
    internal long AllocatedBytes => Memory.UsedBytes;

    /// <summary>
    /// Growable int scratch for the boundary node compaction (mark/remap arrays). It survives solves
    /// and is rounded to a power of two so the next solve reuses it.
    /// </summary>
    internal int[] IntScratch(int minimumLength)
    {
        if (_intScratch.Length >= minimumLength)
            return _intScratch;
        int capacity = 1;
        while (capacity < minimumLength)
            capacity <<= 1;
        long previous = (long)_intScratch.Length * sizeof(int);
        long next = (long)capacity * sizeof(int);
        Memory.Charge(next, "scratch");
        int[] replacement = new int[capacity];
        Memory.Release(previous);
        _intScratch = replacement;
        return _intScratch;
    }

    /// <summary>
    /// Starts one solve: clears the budget-exceeded flag, makes every retained block rentable, and
    /// keeps the retained bytes on the ledger.
    /// </summary>
    internal void BeginSolve(long limitBytes)
    {
        Memory.BeginSolve(limitBytes);
        _freeChunks.Clear();
        for (int index = 0; index < _chunks.Count; index++)
        {
            Array chunk = _chunks[index];
            if (!chunk.GetType().GetElementType()!.IsValueType)
                Array.Clear(chunk);
            Push(_freeChunks, (chunk.GetType().GetElementType()!, chunk.Length), chunk);
        }
        _freeArrays.Clear();
        for (int index = 0; index < _arrays.Count; index++)
        {
            (Type type, int length, Array array) = _arrays[index];
            if (!type.IsValueType) Array.Clear(array);
            Push(_freeArrays, (type, length), array);
        }
    }

    /// <summary>Forget a replaced scratch array so its logical allocation no longer remains reserved.</summary>
    internal void DiscardArray<T>(T[] array)
    {
        int index = _arrays.FindIndex(item => ReferenceEquals(item.Array, array));
        if (index < 0)
            throw new InvalidOperationException("搜索存储没有该数组。");
        _arrays.RemoveAt(index);
        Memory.ReleaseBlock((long)array.Length * Unsafe.SizeOf<T>(), "array");
    }

    /// <summary>
    /// Returns a chunk to the allocator after a state-arena compaction. The block is removed from the
    /// retained set and its bytes leave the ledger, so later solves must rent a new block.
    /// </summary>
    internal void DiscardChunk<T>(T[] chunk)
    {
        int index = _chunks.FindIndex(item => ReferenceEquals(item, chunk));
        if (index < 0)
            throw new InvalidOperationException("搜索存储没有该区块。");
        _chunks.RemoveAt(index);
        Memory.ReleaseBlock((long)chunk.Length * Unsafe.SizeOf<T>(), "chunk");
    }

    /// <summary>Rents a fixed-size chunk, reusing a retained block of the same element type and length.</summary>
    internal T[] RentChunk<T>(int length)
        => Rent<T>(length, _freeChunks, chunk: true);

    /// <summary>Rents a fixed-size array, reusing a retained block of the same element type and length.</summary>
    internal T[] RentArray<T>(int length)
        => Rent<T>(length, _freeArrays, chunk: false);

    private T[] Rent<T>(int length, Dictionary<(Type Type, int Length), Stack<Array>> free,
        bool chunk)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        (Type Type, int Length) key = (typeof(T), length);
        if (free.TryGetValue(key, out Stack<Array>? stack) && stack.Count > 0)
        {
            T[] reused = (T[])stack.Pop();
            Array.Clear(reused);
            return reused;
        }
        long bytes = (long)length * Unsafe.SizeOf<T>();
        Memory.Charge(bytes, chunk ? "chunk" : "array");
        T[] array = new T[length];
        if (chunk)
            _chunks.Add(array);
        else
            _arrays.Add((typeof(T), length, array));
        return array;
    }

    private static void Push<TKey>(Dictionary<TKey, Stack<Array>> free, TKey key, Array array)
        where TKey : notnull
    {
        if (!free.TryGetValue(key, out Stack<Array>? stack))
        {
            stack = new Stack<Array>();
            free.Add(key, stack);
        }
        stack.Push(array);
    }
}

/// <summary>
/// Field storage for the structure-of-arrays route nodes and the routing/commitment side tables.
/// Chunks are rented from the reusable <see cref="SearchStorage"/>; <see cref="Clear"/> only resets
/// the count so a compaction never copies the whole store.
/// </summary>
internal sealed class ChunkedList<T>
{
    private const int ChunkShift = 12;
    private const int ChunkSize = 1 << ChunkShift;
    private const int ChunkMask = ChunkSize - 1;

    private readonly SearchStorage _storage;
    private readonly List<T[]> _chunks = [];

    internal ChunkedList(SearchStorage storage)
        => _storage = storage;

    internal int Count { get; private set; }

    internal T this[int index]
    {
        get => _chunks[index >> ChunkShift][index & ChunkMask];
        set
        {
            int chunk = index >> ChunkShift;
            if (chunk >= _chunks.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            _chunks[chunk][index & ChunkMask] = value;
        }
    }

    internal void Add(T value)
    {
        int chunk = Count >> ChunkShift;
        if (chunk == _chunks.Count)
            _chunks.Add(_storage.RentChunk<T>(ChunkSize));
        _chunks[chunk][Count & ChunkMask] = value;
        Count++;
    }

    internal void Move(int from, int to)
    {
        if (to >= Count)
            throw new ArgumentOutOfRangeException(nameof(to));
        this[to] = this[from];
    }

    internal void Truncate(int count)
    {
        if ((uint)count > (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(count));
        Count = count;
    }

    internal void Clear()
    {
        Count = 0;
        _chunks.Clear();
    }
}

