using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat.Scoring;

/// <summary>
/// Per-card content facts used by the remaining-turn estimate. Variable names and target types are
/// fixed by the card definition; caching them by card id keeps the hot path free of string scans.
/// </summary>
internal readonly struct CardContentFacts
{
    internal readonly sbyte DamageVar;
    internal readonly sbyte RepeatVar;
    internal readonly int[] PoisonVars;
    internal readonly int[] EnvenomVars;

    private CardContentFacts(ushort id)
    {
        IReadOnlyList<string> names = Registry.Card(id).VarNames;
        DamageVar = VarIndex(names, "Damage");
        RepeatVar = VarIndex(names, "Repeat");
        List<int> poison = [];
        List<int> envenom = [];
        for (int index = 0; index < names.Count; index++)
        {
            if (names[index].Contains("Poison", StringComparison.OrdinalIgnoreCase))
                poison.Add(index);
            else if (names[index].Contains("Envenom", StringComparison.OrdinalIgnoreCase))
                envenom.Add(index);
        }
        PoisonVars = [.. poison];
        EnvenomVars = [.. envenom];
    }

    private static sbyte VarIndex(IReadOnlyList<string> names, string name)
    {
        for (int index = 0; index < names.Count; index++)
        {
            if (names[index] == name)
                return checked((sbyte)index);
        }
        return -1;
    }

    /// <summary>All card content facts, built once for the process's content registry.</summary>
    private static readonly CardContentFacts[] ById = Build();

    /// <summary>0 = target not known yet, 1 = not all enemies, 2 = all enemies.</summary>
    private static readonly byte[] AllEnemies = new byte[Registry.CardCount];

    internal static ref readonly CardContentFacts Get(ushort id) => ref ById[id];

    private static CardContentFacts[] Build()
    {
        CardContentFacts[] facts = new CardContentFacts[Registry.CardCount];
        for (int id = 0; id < facts.Length; id++)
            facts[id] = new CardContentFacts((ushort)id);
        return facts;
    }

    /// <summary>
    /// Target type is a compiled rule-table value; cache it by card id after the first lookup. All
    /// current card definitions use the same target for every upgrade level, so one slot per id is
    /// sufficient. The benign race only recomputes the same content value.
    /// </summary>
    internal static bool IsAllEnemies(ushort id, in CardStats stats)
    {
        byte value = AllEnemies[id];
        if (value == 0)
            AllEnemies[id] = value = (byte)(stats.Target == TargetType.AllEnemies ? 2 : 1);
        return value == 2;
    }
}
