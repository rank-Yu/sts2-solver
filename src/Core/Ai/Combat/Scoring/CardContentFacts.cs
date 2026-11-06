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
    internal readonly int[] DoomVars;
    internal readonly int[] VulnerableVars;
    internal readonly int[] StrengthPowerVars;
    internal readonly int[] OstyDamageVars;

    /// <summary>
    /// Damaging orbs one play channels (the cards' OnPlay ChannelOrb calls; no variable carries
    /// it). Chaos's random orb counts as one Lightning; X-cost Tempest as two.
    /// </summary>
    internal readonly byte LightningChannels;
    internal readonly byte DarkChannels;
    internal readonly byte GlassChannels;

    /// <summary>Orb types one play channels, as <see cref="OrbTypeBit"/> bits.</summary>
    internal readonly byte ChanneledOrbTypes;

    internal const int RandomOrbType = 32;
    internal const int KnownOrbTypes = 31;
    internal const int KnownOrbTypeCount = 5;

    /// <summary>Bit of a base orb type (Lightning, Frost, Dark, Plasma, Glass); 0 for others.</summary>
    internal static int OrbTypeBit(string orbId) => orbId switch
    {
        "LIGHTNING_ORB" => 1,
        "FROST_ORB" => 2,
        "DARK_ORB" => 4,
        "PLASMA_ORB" => 8,
        "GLASS_ORB" => 16,
        _ => 0,
    };

    private CardContentFacts(ushort id)
    {
        IReadOnlyList<string> names = Registry.Card(id).VarNames;
        DamageVar = VarIndex(names, "Damage");
        RepeatVar = VarIndex(names, "Repeat");
        List<int> poison = [];
        List<int> envenom = [];
        List<int> doom = [];
        List<int> vulnerable = [];
        List<int> strength = [];
        List<int> osty = [];
        for (int index = 0; index < names.Count; index++)
        {
            if (names[index].Contains("Poison", StringComparison.OrdinalIgnoreCase))
                poison.Add(index);
            else if (names[index].Contains("Envenom", StringComparison.OrdinalIgnoreCase))
                envenom.Add(index);
            else if (names[index] == "DoomPower")
                doom.Add(index);
            else if (names[index] == "VulnerablePower")
                vulnerable.Add(index);
            else if (names[index] == "StrengthPower")
                strength.Add(index);
            else if (names[index] == "OstyDamage")
                osty.Add(index);
        }
        PoisonVars = [.. poison];
        EnvenomVars = [.. envenom];
        DoomVars = [.. doom];
        VulnerableVars = [.. vulnerable];
        StrengthPowerVars = [.. strength];
        OstyDamageVars = [.. osty];
        (LightningChannels, DarkChannels, GlassChannels) = Registry.Card(id).NativeId switch
        {
            "ZAP" or "BALL_LIGHTNING" or "VOLTAIC" or "CHAOS" => ((byte)1, (byte)0, (byte)0),
            "TEMPEST" => ((byte)2, (byte)0, (byte)0),
            "RAINBOW" => ((byte)1, (byte)1, (byte)0),
            "DARKNESS" or "NULL" or "SHADOW_SHIELD" or "CONSUMING_SHADOW" => ((byte)0, (byte)1, (byte)0),
            "REFRACT" or "SPINNER" or "GLASSWORK" => ((byte)0, (byte)0, (byte)1),
            _ => ((byte)0, (byte)0, (byte)0),
        };
        ChanneledOrbTypes = Registry.Card(id).NativeId switch
        {
            "ZAP" or "BALL_LIGHTNING" or "VOLTAIC" or "TEMPEST" => 1,
            "COLD_SNAP" or "ICE_LANCE" or "GLACIER" or "CHILL" or "COOLHEADED" => 2,
            "DARKNESS" or "NULL" or "SHADOW_SHIELD" or "CONSUMING_SHADOW" => 4,
            "METEOR_STRIKE" or "FUSION" => 8,
            "REFRACT" or "SPINNER" or "GLASSWORK" => 16,
            "RAINBOW" => 1 | 2 | 4,
            "CHAOS" => RandomOrbType,
            _ => 0,
        };
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
