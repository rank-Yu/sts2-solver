// Ported from CombatSolver src/Search/GrowthPolicy.cs at a62f1850 (MIT License, Copyright (c) 2026 Torch1230 and contributors); only the members Sts2Solver reads.
namespace Sts2Solver.Ai.Combat.Policy;

internal enum GrowthSource
{
    HandOfGreed, TheHunt, Feed, Royalties, Alchemize, GeneticAlgorithm, TheScythe, Goopy, ForbiddenGrimoire,
    MadScience, ShowOff,
}

/// <summary>
/// 成长向量里第三方来源那一半，按 <see cref="GrowthSourceMirrors"/> 登记的 id 存。
/// </summary>
/// <remarks>
/// <para>
/// 原版来源是 <see cref="GrowthValues"/> 上的固定 int 字段，走热路径；第三方来源数量不定，
/// 只能另开一处。这里用一个「按 id 序数升序、不存 0 值」的数组：为空时是 <c>null</c>，
/// 于是没有任何 mod 登记时，第三方部分不额外分配或追加非零条目。
/// </para>
/// <para>
/// 键用 id 而不是登记序号，是为了让<b>没登记的 id 也能原样留着</b>。额度存在设置文件里，
/// 玩家可能临时停用某个 mod；如果按序号存，停用期间写回设置就会把那份额度冲掉，重新启用后
/// 玩家得再填一遍。按 id 存的话，读进来认不出的条目照样带着走、照样写回去，只是这一局不生效。
/// </para>
/// </remarks>
internal readonly struct GrowthExtras : IEquatable<GrowthExtras>
{
    /// <summary>额度上限，与原版字段同一口径。</summary>
    private const int MaximumValue = 1000;

    private readonly KeyValuePair<string, int>[]? _entries;

    private GrowthExtras(KeyValuePair<string, int>[]? entries) => _entries = entries;

    /// <summary>一条都没有。空表时下游可以整段跳过。</summary>
    public bool IsEmpty => _entries is null;

    /// <summary>按 id 序数升序列出所有非 0 条目。</summary>
    public ReadOnlySpan<KeyValuePair<string, int>> Entries => _entries;

    public int Total
    {
        get
        {
            int total = 0;
            if (_entries is not null)
            {
                foreach (KeyValuePair<string, int> entry in _entries)
                    total = checked(total + entry.Value);
            }
            return total;
        }
    }

    public int Get(string id)
    {
        int index = IndexOf(id);
        return index < 0 ? 0 : _entries![index].Value;
    }

    public GrowthExtras With(string id, int value)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        int index = IndexOf(id);
        if (index >= 0)
        {
            if (value == _entries![index].Value)
                return this;
            if (value == 0)
            {
                if (_entries.Length == 1)
                    return default;
                KeyValuePair<string, int>[] shrunk = new KeyValuePair<string, int>[_entries.Length - 1];
                Array.Copy(_entries, shrunk, index);
                Array.Copy(_entries, index + 1, shrunk, index, shrunk.Length - index);
                return new GrowthExtras(shrunk);
            }
            KeyValuePair<string, int>[] replaced = (KeyValuePair<string, int>[])_entries.Clone();
            replaced[index] = new KeyValuePair<string, int>(id, value);
            return new GrowthExtras(replaced);
        }
        if (value == 0)
            return this;
        int insertAt = ~index;
        KeyValuePair<string, int>[] grown = new KeyValuePair<string, int>[(_entries?.Length ?? 0) + 1];
        if (_entries is not null)
        {
            Array.Copy(_entries, grown, insertAt);
            Array.Copy(_entries, insertAt, grown, insertAt + 1, _entries.Length - insertAt);
        }
        grown[insertAt] = new KeyValuePair<string, int>(id, value);
        return new GrowthExtras(grown);
    }

    /// <summary>把这份额度乘到 <paramref name="rewards"/> 那份计数上。</summary>
    public int Credit(GrowthExtras rewards)
    {
        if (_entries is null || rewards._entries is null)
            return 0;
        int credit = 0;
        foreach (KeyValuePair<string, int> reward in rewards._entries)
            credit = checked(credit + Get(reward.Key) * reward.Value);
        return credit;
    }

    public void ValidateBudgets()
    {
        if (_entries is null)
            return;
        foreach (KeyValuePair<string, int> entry in _entries)
        {
            if (string.IsNullOrEmpty(entry.Key))
                throw new InvalidDataException("Third-party growth source ID must not be empty.");
            if (entry.Value is < 0 or > MaximumValue)
                throw new InvalidDataException($"Growth budget {entry.Key} must be in 0..{MaximumValue} HP.");
        }
    }

    public void AppendFingerprint(ref StateFingerprintBuilder fingerprint)
    {
        if (_entries is null)
            return;
        fingerprint.Add('E');
        foreach (KeyValuePair<string, int> entry in _entries)
        {
            fingerprint.Add(entry.Key);
            fingerprint.Add(entry.Value);
        }
    }

    private int IndexOf(string id)
    {
        if (_entries is null)
            return -1;
        int low = 0;
        int high = _entries.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = string.CompareOrdinal(_entries[middle].Key, id);
            if (comparison == 0)
                return middle;
            if (comparison < 0)
                low = middle + 1;
            else
                high = middle - 1;
        }
        return ~low;
    }

    public bool Equals(GrowthExtras other)
    {
        if (_entries is null || other._entries is null)
            return _entries is null && other._entries is null;
        if (ReferenceEquals(_entries, other._entries))
            return true;
        if (_entries.Length != other._entries.Length)
            return false;
        for (int index = 0; index < _entries.Length; index++)
        {
            if (!string.Equals(_entries[index].Key, other._entries[index].Key, StringComparison.Ordinal)
                || _entries[index].Value != other._entries[index].Value)
            {
                return false;
            }
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is GrowthExtras other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        if (_entries is not null)
        {
            foreach (KeyValuePair<string, int> entry in _entries)
            {
                hash.Add(entry.Key, StringComparer.Ordinal);
                hash.Add(entry.Value);
            }
        }
        return hash.ToHashCode();
    }

    public override string ToString()
        => _entries is null
            ? "[]"
            : "[" + string.Join(", ", _entries.Select(entry => entry.Key + " = " + entry.Value)) + "]";

    public static bool operator ==(GrowthExtras left, GrowthExtras right) => left.Equals(right);

    public static bool operator !=(GrowthExtras left, GrowthExtras right) => !left.Equals(right);
}

// Immutable vectors are used for both per-event HP budgets and realized event counts.
internal readonly record struct GrowthValues(
    int HandOfGreed = 0, int TheHunt = 0, int Feed = 0, int Royalties = 0,
    int Alchemize = 0, int GeneticAlgorithm = 0, int TheScythe = 0, int Goopy = 0, int ForbiddenGrimoire = 0,
    int MadScience = 0, int ShowOff = 0)
{
    private readonly GrowthExtras _extras;

    public GrowthExtras Extras
    {
        get => _extras;
        init => _extras = value;
    }

    public bool IsEnabled => this != default;
    public int Total => checked(HandOfGreed + TheHunt + Feed + Royalties + Alchemize + GeneticAlgorithm + TheScythe + Goopy
        + ForbiddenGrimoire + MadScience + ShowOff + _extras.Total);

    public int Get(GrowthSource source) => source switch
    {
        GrowthSource.HandOfGreed => HandOfGreed,
        GrowthSource.TheHunt => TheHunt,
        GrowthSource.Feed => Feed,
        GrowthSource.Royalties => Royalties,
        GrowthSource.Alchemize => Alchemize,
        GrowthSource.GeneticAlgorithm => GeneticAlgorithm,
        GrowthSource.TheScythe => TheScythe,
        GrowthSource.Goopy => Goopy,
        GrowthSource.ForbiddenGrimoire => ForbiddenGrimoire,
        GrowthSource.MadScience => MadScience,
        GrowthSource.ShowOff => ShowOff,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public GrowthValues With(GrowthSource source, int value) => source switch
    {
        GrowthSource.HandOfGreed => this with { HandOfGreed = value },
        GrowthSource.TheHunt => this with { TheHunt = value },
        GrowthSource.Feed => this with { Feed = value },
        GrowthSource.Royalties => this with { Royalties = value },
        GrowthSource.Alchemize => this with { Alchemize = value },
        GrowthSource.GeneticAlgorithm => this with { GeneticAlgorithm = value },
        GrowthSource.TheScythe => this with { TheScythe = value },
        GrowthSource.Goopy => this with { Goopy = value },
        GrowthSource.ForbiddenGrimoire => this with { ForbiddenGrimoire = value },
        GrowthSource.MadScience => this with { MadScience = value },
        GrowthSource.ShowOff => this with { ShowOff = value },
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public int Credit(GrowthValues rewards) => checked(
        HandOfGreed * rewards.HandOfGreed + TheHunt * rewards.TheHunt
        + Feed * rewards.Feed + Royalties * rewards.Royalties
        + Alchemize * rewards.Alchemize + GeneticAlgorithm * rewards.GeneticAlgorithm
        + TheScythe * rewards.TheScythe + Goopy * rewards.Goopy
        + ForbiddenGrimoire * rewards.ForbiddenGrimoire
        + MadScience * rewards.MadScience + ShowOff * rewards.ShowOff + _extras.Credit(rewards._extras));

    public bool Satisfies(GrowthValues required)
    {
        foreach (GrowthSource source in Enum.GetValues<GrowthSource>())
        {
            if (Get(source) < required.Get(source))
                return false;
        }
        foreach (KeyValuePair<string, int> entry in required.Extras.Entries)
        {
            if (_extras.Get(entry.Key) < entry.Value)
                return false;
        }
        return true;
    }

    public void ValidateBudgets()
    {
        foreach (GrowthSource source in Enum.GetValues<GrowthSource>())
        {
            if (Get(source) is < 0 or > 1000)
                throw new InvalidDataException($"Growth budget {source} must be in 0..1000 HP.");
        }
        _extras.ValidateBudgets();
    }

    /// <summary>把内置及第三方来源的计数写进状态指纹。</summary>
    public void AppendFingerprint(ref StateFingerprintBuilder fingerprint)
    {
        fingerprint.Add(HandOfGreed);
        fingerprint.Add(TheHunt);
        fingerprint.Add(Feed);
        fingerprint.Add(Royalties);
        fingerprint.Add(Alchemize);
        fingerprint.Add(GeneticAlgorithm);
        fingerprint.Add(TheScythe);
        fingerprint.Add(Goopy);
        fingerprint.Add(ForbiddenGrimoire);
        fingerprint.Add(MadScience);
        fingerprint.Add(ShowOff);
        _extras.AppendFingerprint(ref fingerprint);
    }

    public override string ToString()
    {
        string vanilla = $"{nameof(GrowthValues)} {{ {nameof(HandOfGreed)} = {HandOfGreed}, "
            + $"{nameof(TheHunt)} = {TheHunt}, {nameof(Feed)} = {Feed}, {nameof(Royalties)} = {Royalties}, "
            + $"{nameof(Alchemize)} = {Alchemize}, {nameof(GeneticAlgorithm)} = {GeneticAlgorithm}, "
            + $"{nameof(TheScythe)} = {TheScythe}, {nameof(Goopy)} = {Goopy}, {nameof(ForbiddenGrimoire)} = {ForbiddenGrimoire}, "
            + $"{nameof(MadScience)} = {MadScience}, {nameof(ShowOff)} = {ShowOff}";
        return _extras.IsEmpty ? vanilla + " }" : vanilla + $", ThirdParty = {_extras} }}";
    }
}
