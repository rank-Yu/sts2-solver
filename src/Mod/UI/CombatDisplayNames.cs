// Adapted from CombatSolver src/Runtime/SolverDisplayNames.cs and src/UI/SolverActionTextIdentity.cs
// at 5c773caa (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// Names use Sts2Solver native ids, are cached per language and freeze live enemies by CombatId.
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using Sts2Solver.Simulation.Combat;

using Creature = MegaCrit.Sts2.Core.Entities.Creatures.Creature;

namespace Sts2Solver;

/// <summary>
/// Main-thread display-name capture. Card/potion/relic names are cached per language; live enemies
/// are frozen by <c>CombatId</c> for the whole combat (the first capture that sees an enemy names it)
/// so route snapshots never read a live model later and names never shift after a death.
/// Predicted summons that are absent from the live roster fall back to the monster table and a
/// stable ordinal within the same monster type.
/// </summary>
internal sealed class CombatDisplayNames
{
    private static readonly object CacheGate = new();
    private static string? _cacheLanguage;
    private static Dictionary<(string Id, int Upgrade), string> _cardNames = [];
    private static Dictionary<string, string> _potionNames = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _relicNames = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _powerNames = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _monsterNames = new(StringComparer.Ordinal);
    private static WeakReference<CombatState>? _rosterCombat;
    private static string? _rosterLanguage;
    private static Dictionary<uint, string> _rosterNames = [];
    private static Dictionary<string, int> _rosterOrdinals = new(StringComparer.Ordinal);
    // Fallback names already shown for predicted enemies that are not live yet, by (CombatId, monster
    // id). The live capture adopts them, so a summon keeps the name its pills showed before it appeared.
    private static Dictionary<(uint CombatId, string MonsterId), string> _shownFallbacks = [];

    private readonly Dictionary<uint, string> _creatures;
    private readonly string _playerName;
    private readonly string _ostyName;

    private CombatDisplayNames(Dictionary<uint, string> creatures, string playerName, string ostyName)
    {
        _creatures = creatures;
        _playerName = playerName;
        _ostyName = ostyName;
    }

    internal string PlayerName => _playerName;

    internal string OstyName => _ostyName;

    /// <summary>Empty name table for a snapshot applied while no native combat state is available.</summary>
    internal static CombatDisplayNames CaptureEmpty()
        => new([], "玩家", "Osty");

    internal static CombatDisplayNames Capture(CombatState state)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("显示名必须在游戏主线程捕获。");
        EnsureCache();
        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("找不到本地玩家。");
        // The panel captures names on every snapshot. Recomputing the duplicate-name ordinals from the
        // live roster renumbered the survivors after a death ("#3" became "#2"), which changed the
        // target text of every later pill and rebuilt the route rows mid-deployment. A name, once
        // given in this combat, is kept (dead enemies included, for the executed rows); enemies that
        // appear later continue the ordinals of their monster name.
        CombatState? named = null;
        _rosterCombat?.TryGetTarget(out named);
        if (!ReferenceEquals(named, state)
            || !string.Equals(_rosterLanguage, _cacheLanguage, StringComparison.Ordinal))
        {
            _rosterCombat = new WeakReference<CombatState>(state);
            _rosterLanguage = _cacheLanguage;
            _rosterNames = [];
            _rosterOrdinals = new(StringComparer.Ordinal);
            _shownFallbacks = [];
        }
        Dictionary<string, int> fresh = new(StringComparer.Ordinal);
        foreach (Creature creature in state.Enemies)
        {
            if (creature.CombatId is uint combatId && !_rosterNames.ContainsKey(combatId))
            {
                string baseName = BaseMonsterName(creature);
                fresh[baseName] = fresh.GetValueOrDefault(baseName) + 1;
            }
        }
        foreach (Creature creature in state.Enemies)
        {
            if (creature.CombatId is not uint combatId || _rosterNames.ContainsKey(combatId))
                continue;
            string baseName = BaseMonsterName(creature);
            int used = _rosterOrdinals.GetValueOrDefault(baseName);
            string name = used == 0 && fresh[baseName] == 1 ? baseName : $"{baseName} #{used + 1}";
            int next = used + 1;
            // A summon whose predicted pills already named it keeps that name; renaming it here
            // changed the target text of later pills and rebuilt their rows mid-deployment.
            if (creature.Monster?.Id.Entry is { } monsterId
                && _shownFallbacks.TryGetValue((combatId, monsterId), out string? shown))
            {
                name = shown;
                int mark = shown.LastIndexOf(" #", StringComparison.Ordinal);
                if (mark >= 0 && int.TryParse(shown.AsSpan(mark + 2), out int ordinal))
                    next = Math.Max(next, ordinal);
            }
            _rosterOrdinals[baseName] = next;
            _rosterNames[combatId] = name;
        }
        Dictionary<uint, string> creatures = new(_rosterNames);
        string playerName = player.Character?.Title.GetFormattedText() ?? "玩家";
        string ostyName = player.Osty?.Name ?? "Osty";
        return new CombatDisplayNames(creatures, playerName, ostyName);
    }

    internal string Card(string nativeId, int upgrade) => CardTitle(nativeId, upgrade);

    /// <summary>Relic title in the current language, by native id.</summary>
    internal static string RelicTitle(string nativeId)
    {
        EnsureCache();
        return _relicNames.TryGetValue(nativeId, out string? name) ? name : nativeId;
    }

    /// <summary>Power title in the current language, by native id.</summary>
    internal static string PowerTitle(string nativeId)
    {
        EnsureCache();
        return _powerNames.TryGetValue(nativeId, out string? name) ? name : nativeId;
    }

    /// <summary>Card title in the current language; the panel re-resolves it after a language change.</summary>
    /// <summary>
    /// Resolves an exact native id carried by an import rejection to its localized title. The id
    /// must match the model table exactly; free-form diagnostic text is never treated as a name.
    /// </summary>
    internal static bool TryContentTitle(string importReason, string? nativeId, out string title)
    {
        title = "";
        if (string.IsNullOrEmpty(nativeId))
            return false;
        EnsureCache();
        switch (importReason)
        {
            case "unsupported-card":
                if (_cardNames.TryGetValue((nativeId, 0), out string? card))
                {
                    title = card;
                    return true;
                }
                return false;
            case "unsupported-power":
                return _powerNames.TryGetValue(nativeId, out title!);
            case "unsupported-relic":
                return _relicNames.TryGetValue(nativeId, out title!);
            case "unsupported-potion":
                return _potionNames.TryGetValue(nativeId, out title!);
            case "unsupported-monster":
                return _monsterNames.TryGetValue(nativeId, out title!);
            default:
                return false;
        }
    }

    internal static string CardTitle(string nativeId, int upgrade)
    {
        if (string.IsNullOrEmpty(nativeId))
            return nativeId;
        EnsureCache();
        return _cardNames.TryGetValue((nativeId, upgrade), out string? name)
            ? name
            : upgrade == 0 ? nativeId : $"{nativeId}+{upgrade}";
    }

    /// <summary>Potion title in the current language, by native id.</summary>
    internal static string PotionTitle(string nativeId)
    {
        EnsureCache();
        return _potionNames.TryGetValue(nativeId, out string? name) ? name : nativeId;
    }

    internal string Card(Card card) => Card(Registry.Card(card.Id).NativeId, card.Upgrade);

    internal string Potion(ushort id)
    {
        EnsureCache();
        string nativeId = Registry.Potion(id).NativeId;
        return _potionNames.TryGetValue(nativeId, out string? name) ? name : nativeId;
    }

    internal string Relic(ushort id)
    {
        EnsureCache();
        string nativeId = Registry.Relic(id).NativeId;
        return _relicNames.TryGetValue(nativeId, out string? name) ? name : nativeId;
    }

    internal string Monster(ushort id)
    {
        EnsureCache();
        string nativeId = Registry.Monster(id).NativeId;
        return _monsterNames.TryGetValue(nativeId, out string? name) ? name : nativeId;
    }

    internal string[] Choice(PlannedChoice choice)
    {
        if (choice.OptionCount == 0)
            return [];
        List<string> names = [];
        foreach (int index in choice.SelectedIndexes())
        {
            Card card = choice.OptionsAreGenerated
                ? choice.GeneratedOptions[index]
                : choice.PileOptions[index];
            names.Add(Card(card));
        }
        return [.. names];
    }

    internal string Enemy(in Battle battle, int enemyIndex)
    {
        Enemy enemy = battle.Enemies[enemyIndex];
        if (_creatures.TryGetValue(enemy.CombatId, out string? captured))
            return captured;
        int total = 0;
        int ordinal = 0;
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            if (battle.Enemies[index].MonsterId != enemy.MonsterId)
                continue;
            total++;
            if (index == enemyIndex)
                ordinal = total;
        }
        string baseName = Monster(enemy.MonsterId);
        return total > 1 ? $"{baseName} #{ordinal}" : baseName;
    }

    /// <summary>Frozen enemy name from stable identity; falls back to the monster table and ordinal.</summary>
    internal string Enemy(uint combatId, string monsterId, int ordinal)
    {
        if (_creatures.TryGetValue(combatId, out string? captured))
            return captured;
        // A predicted enemy that is not live yet: the first name shown for it is kept until it
        // appears (later predictions may count a different number of the same monster).
        if (_shownFallbacks.TryGetValue((combatId, monsterId), out string? shown))
            return shown;
        string name = MonsterById(monsterId);
        name = ordinal > 0 ? $"{name} #{ordinal}" : name;
        _shownFallbacks[(combatId, monsterId)] = name;
        return name;
    }

    private string MonsterById(string nativeId)
    {
        EnsureCache();
        return _monsterNames.GetValueOrDefault(nativeId, nativeId);
    }

    private static void EnsureCache()
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("显示名缓存只能在游戏主线程使用。");
        string language = LocManager.Instance?.Language ?? "";
        lock (CacheGate)
        {
            if (string.Equals(_cacheLanguage, language, StringComparison.Ordinal))
                return;
            Dictionary<(string Id, int Upgrade), string> cards = [];
            foreach (CardModel canonical in ModelDb.AllCards)
            {
                CardModel card = canonical.ToMutable();
                while (true)
                {
                    cards[(card.Id.Entry, card.CurrentUpgradeLevel)] = card.Title;
                    if (!card.IsUpgradable)
                        break;
                    card.UpgradeInternal();
                    card.FinalizeUpgradeInternal();
                }
            }
            Dictionary<string, string> potions = new(StringComparer.Ordinal);
            foreach (PotionModel potion in ModelDb.All.OfType<PotionModel>())
                potions.TryAdd(potion.Id.Entry, potion.Title.GetFormattedText());
            Dictionary<string, string> relics = new(StringComparer.Ordinal);
            foreach (RelicModel relic in ModelDb.All.OfType<RelicModel>())
                relics.TryAdd(relic.Id.Entry, relic.Title.GetFormattedText());
            Dictionary<string, string> powers = new(StringComparer.Ordinal);
            foreach (PowerModel power in ModelDb.All.OfType<PowerModel>())
                powers.TryAdd(power.Id.Entry, power.Title.GetFormattedText());
            Dictionary<string, string> monsters = new(StringComparer.Ordinal);
            foreach (MonsterModel monster in ModelDb.All.OfType<MonsterModel>())
                monsters.TryAdd(monster.Id.Entry, monster.Title.GetFormattedText());
            _cardNames = cards;
            _potionNames = potions;
            _relicNames = relics;
            _powerNames = powers;
            _monsterNames = monsters;
            _cacheLanguage = language;
        }
    }

    private static string BaseMonsterName(Creature creature)
        => creature.Monster?.Id.Entry is { } monsterId
            ? _monsterNames.GetValueOrDefault(monsterId, monsterId)
            : creature.Name;
}
