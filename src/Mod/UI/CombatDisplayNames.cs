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
/// are frozen by <c>CombatId</c> at capture time so route snapshots never read a live model later.
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
    private static Dictionary<string, string> _monsterNames = new(StringComparer.Ordinal);

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

    internal static CombatDisplayNames Capture(CombatState state)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("显示名必须在游戏主线程捕获。");
        EnsureCache();
        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("找不到本地玩家。");
        Dictionary<uint, string> creatures = [];
        Dictionary<string, int> totals = new(StringComparer.Ordinal);
        foreach (Creature creature in state.Enemies)
        {
            string baseName = BaseMonsterName(creature);
            totals[baseName] = totals.GetValueOrDefault(baseName) + 1;
        }
        Dictionary<string, int> ordinals = new(StringComparer.Ordinal);
        foreach (Creature creature in state.Enemies)
        {
            if (creature.CombatId is not uint combatId)
                continue;
            string baseName = BaseMonsterName(creature);
            int total = totals[baseName];
            string name = baseName;
            if (total > 1)
            {
                int ordinal = ordinals.GetValueOrDefault(baseName) + 1;
                ordinals[baseName] = ordinal;
                name = $"{baseName} #{ordinal}";
            }
            creatures[combatId] = name;
        }
        string playerName = player.Character?.Title.GetFormattedText() ?? "玩家";
        string ostyName = player.Osty?.Name ?? "Osty";
        return new CombatDisplayNames(creatures, playerName, ostyName);
    }

    internal string Card(string nativeId, int upgrade)
    {
        if (string.IsNullOrEmpty(nativeId))
            return nativeId;
        EnsureCache();
        return _cardNames.TryGetValue((nativeId, upgrade), out string? name)
            ? name
            : upgrade == 0 ? nativeId : $"{nativeId}+{upgrade}";
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
            Dictionary<string, string> monsters = new(StringComparer.Ordinal);
            foreach (MonsterModel monster in ModelDb.All.OfType<MonsterModel>())
                monsters.TryAdd(monster.Id.Entry, monster.Title.GetFormattedText());
            _cardNames = cards;
            _potionNames = potions;
            _relicNames = relics;
            _monsterNames = monsters;
            _cacheLanguage = language;
        }
    }

    private static string BaseMonsterName(Creature creature)
        => creature.Monster?.Id.Entry is { } monsterId
            ? _monsterNames.GetValueOrDefault(monsterId, monsterId)
            : creature.Name;
}
