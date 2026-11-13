using System.Numerics;

namespace Sts2Solver.Simulation.Combat;

/// <summary>Immutable root-owned roster; only per-instance mutable values live in Battle.</summary>
public sealed class RelicRoster
{
    public static RelicRoster Empty { get; } = new([], [], 0);
    private readonly Relic[] _fixed;
    private readonly sbyte[] _stateSlots;
    public int Count => _fixed.Length;
    public int StateCount { get; }
    private readonly ushort[] _active;
    private readonly int _listenerCapacity;
    internal int PenNibIndex { get; } = -1;
    private readonly ushort[][] _hooks = new ushort[128][];
    internal ReadOnlySpan<ushort> Active => _active;
    internal int ListenerCapacity => _listenerCapacity;
    // Dispatch requests one hook at a time; each table is already filtered at root creation.
    internal ReadOnlySpan<ushort> Listeners(HookMask hook) => _hooks[BitOperations.TrailingZeroCount((ulong)hook)];
    internal ReadOnlySpan<ushort> Listeners(HookMaskExt hook) => _hooks[64 + BitOperations.TrailingZeroCount((ulong)hook)];

    private RelicRoster(Relic[] fixedRows, sbyte[] stateSlots, int stateCount)
    {
        _fixed = fixedRows; _stateSlots = stateSlots; StateCount = stateCount;
        List<ushort> active = [];
        List<ushort>?[] hooks = new List<ushort>?[128];
        for (int i = 0; i < fixedRows.Length; i++)
        {
            if ((fixedRows[i].Flags & RelicFlags.Melted) != 0) continue;
            ushort index = checked((ushort)i);
            active.Add(index);
            RelicDef def = Registry.Relic(fixedRows[i].Id);
            if (def.NativeId == "PEN_NIB") PenNibIndex = i;
            for (int half = 0; half < 2; half++)
            {
                ulong mask = half == 0 ? (ulong)def.Hooks : (ulong)def.HooksExt;
                while (mask != 0)
                {
                    int bit = half * 64 + BitOperations.TrailingZeroCount(mask);
                    (hooks[bit] ??= []).Add(index);
                    mask &= mask - 1;
                }
            }
        }
        _active = [.. active];
        for (int i = 0; i < _hooks.Length; i++)
        {
            _hooks[i] = hooks[i]?.ToArray() ?? [];
            _listenerCapacity = Math.Max(_listenerCapacity, _hooks[i].Length);
        }
    }

    public static RelicRoster Create(ReadOnlySpan<Relic> rows)
    {
        if (rows.IsEmpty) return Empty;
        if (rows.Length > Limits.RelicRoster)
            throw new CapacityException("RelicRoster", Limits.RelicRoster);
        Relic[] fixedRows = rows.ToArray();
        sbyte[] slots = new sbyte[rows.Length];
        int count = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i].Id >= Registry.RelicCount)
                throw new UnsupportedException($"Unknown relic {rows[i].Id}.");
            if (Registry.Relic(rows[i].Id).HasCombatState)
            {
                if (count == Limits.RelicStates)
                    throw new CapacityException("RelicStates", Limits.RelicStates);
                slots[i] = checked((sbyte)count++);
                fixedRows[i].Counter = 0;
                fixedRows[i].Aux = 0;
                fixedRows[i].Flags &= RelicFlags.Melted;
            }
            else slots[i] = -1;
        }
        return new(fixedRows, slots, count);
    }

    public ushort Id(int index) => _fixed[index].Id;
    public Relic Fixed(int index) => _fixed[index];
    public int StateSlot(int index) => _stateSlots[index];

    public Relic Read(in Battle battle, int index)
    {
        Relic value = _fixed[index];
        int slot = _stateSlots[index];
        if (slot >= 0)
        {
            value.Counter = battle.RelicStates[slot].Counter;
            value.Aux = battle.RelicStates[slot].Aux;
            value.Flags |= battle.RelicStateFlags[slot];
        }
        return value;
    }

    /// <summary>Root/import-only validation and initialization, never an expansion-time scan.</summary>
    public void Import(ReadOnlySpan<Relic> rows, ref Battle battle)
    {
        if (rows.Length != Count)
            throw new InvalidDataException("Relic roster count differs from the root context.");
        // Validate every fixed field before changing the destination.
        for (int i = 0; i < Count; i++)
        {
            Relic r = rows[i], expected = _fixed[i];
            int slot = _stateSlots[i];
            if (r.Id != expected.Id || (r.Flags & RelicFlags.Melted) != (expected.Flags & RelicFlags.Melted)
                || (slot < 0 && (r.Counter != expected.Counter || r.Aux != expected.Aux || r.Flags != expected.Flags)))
                throw new InvalidDataException($"Relic roster entry {i} differs from the root context.");
        }
        battle.RelicStates = default;
        battle.RelicStateFlags = default;
        battle.RelicStateCount = checked((byte)StateCount);
        for (int i = 0; i < Count; i++)
        {
            int slot = _stateSlots[i];
            if (slot < 0) continue;
            battle.RelicStates[slot] = new RelicState { Counter = rows[i].Counter, Aux = rows[i].Aux };
            battle.RelicStateFlags[slot] = rows[i].Flags & ~RelicFlags.Melted;
        }
    }

    public bool Equivalent(RelicRoster other)
    {
        if (Count != other.Count) return false;
        for (int i = 0; i < Count; i++)
        {
            Relic a = _fixed[i], b = other._fixed[i];
            if (a.Id != b.Id || a.Flags != b.Flags || a.Counter != b.Counter || a.Aux != b.Aux) return false;
        }
        return true;
    }
}
