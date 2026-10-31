using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat.Policy;

/// <summary>Root-frozen activation or continuous-gain preference for opt-in supplemental searches.</summary>
internal sealed class SetupSearchPolicy
{
    private readonly int[] _initialAmounts;
    private readonly int[] _activationAmounts;
    internal bool Continuous { get; }

    private SetupSearchPolicy(int[] initial, int[] activation, bool continuous = false)
    {
        Continuous = continuous;
        _initialAmounts = initial; _activationAmounts = activation;
    }

    internal static SetupSearchPolicy Capture(in Battle root, Rules rules, bool continuous = false)
    {
        int[] initial = new int[Registry.PowerCount], activation = new int[Registry.PowerCount];
        for (int i = 0; i < root.PowerCount; i++)
            if (root.Powers[i].Owner == Limits.PlayerOwner)
                initial[root.Powers[i].Id] = Math.Max(0, root.Powers[i].Amount);
        if (continuous)
            return new(initial, activation, continuous: true);
        Dictionary<string, int> names = new(StringComparer.OrdinalIgnoreCase);
        for (int id = 0; id < activation.Length; id++)
            names.Add(Normalize(Registry.Power((ushort)id).NativeId), id);
        for (int i = 0; i < root.CardCount; i++)
        {
            ref readonly Card card = ref root.Cards[i];
            if (Kernel.ResolveCardType(root, rules, card) != CardType.Power) continue;
            CardDef definition = Registry.Card(card.Id);
            if (Registry.TryPower(definition.NativeId + "_POWER", out ushort direct))
                activation[direct] = Math.Max(activation[direct], 1);
            CardStats stats = rules.Card(card.Id, card.Upgrade);
            for (int j = 0; j < definition.VarNames.Count && j < stats.Vars.Length; j++)
            {
                if (!names.TryGetValue(Normalize(definition.VarNames[j]), out int power)) continue;
                decimal delta = card.Private > 0 ? DecimalVars.Delta(in root, in card, j)
                    : j == 0 ? card.Var0 : j == 1 ? card.Var1 : 0m;
                int amount = Math.Max(0, checked((int)(stats.Vars[j] + delta)));
                activation[power] = Math.Max(activation[power], amount);
            }
        }
        return new(initial, activation, continuous);
    }

    private static string Normalize(string value)
    {
        string name = value.Replace("_", "");
        return name.EndsWith("Power", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
    }

    // Both preferences are bounded ranking heuristics, not guaranteed HP savings.
    // Continuous gain values the new share of each effective buff, including potion sources.
    internal double PreferenceHp(in Power power, int potential)
    {
        if (!Continuous)
            return _activationAmounts[power.Id] > 0 && potential > 0
                && (long)power.Amount - _initialAmounts[power.Id] >= _activationAmounts[power.Id] ? 8d : 0d;
        int initial = _initialAmounts[power.Id];
        double addedShare = power.Amount <= initial ? 0 : (power.Amount - initial) / (double)power.Amount;
        double value = potential * addedShare;
        return value <= 0 ? 0 : 8d * value / (1d + value);
    }

    internal void Encode(BinaryWriter writer)
    {
        writer.Write(_initialAmounts.Length);
        for (int i = 0; i < _initialAmounts.Length; i++)
        { writer.Write(_initialAmounts[i]); writer.Write(_activationAmounts[i]); }
        writer.Write(false); // reserved: focused setup is removed.
        writer.Write(-1);
        writer.Write(0u);
    }

    internal static SetupSearchPolicy Decode(BinaryReader reader, bool continuous = false)
    {
        int count = Codec.ReadCount(reader, ushort.MaxValue + 1, "能力展开基线");
        if (count != Registry.PowerCount) throw new InvalidDataException("能力展开注册表不符。");
        int[] initial = new int[count], activation = new int[count];
        for (int i = 0; i < count; i++)
        {
            initial[i] = reader.ReadInt32(); activation[i] = reader.ReadInt32();
            if (initial[i] < 0 || activation[i] < 0) throw new InvalidDataException("能力展开参数不能为负。");
        }
        bool focused = Codec.ReadBool(reader);
        int slot = reader.ReadInt32(); uint combatId = reader.ReadUInt32();
        if (focused || slot != -1 || combatId != 0)
            throw new InvalidDataException("能力展开目标已删除，只接受无目标。");
        return new(initial, activation, continuous);
    }

    internal void ValidateRoot(in Battle root, Rules rules)
    {
        SetupSearchPolicy captured = Capture(root, rules, Continuous);
        if (!_initialAmounts.AsSpan().SequenceEqual(captured._initialAmounts)
            || !_activationAmounts.AsSpan().SequenceEqual(captured._activationAmounts))
            throw new InvalidDataException("能力展开参数与搜索根不符。");
    }
}
