using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Merchant;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Helpers;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.MonsterMoves.Intents;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rewards;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Sts2SimExport;

/// <summary>
/// Deterministic play policy used only to produce a variety of real run states. It is not a search
/// AI: it plays powers first, values block against the current enemy attack intents, attacks the
/// lowest-HP enemy, and otherwise plays the highest-valued card. Map choices prefer elites while
/// healthy and rest sites while hurt so that the export covers all room types without dying out
/// in act one.
/// </summary>
internal sealed class DecisionSource : IRunDecisionSource, ICombatOutcomeOverride
{
    private readonly RunState _runState;
    private readonly Rng _rng;
    private readonly CombatOutcomeCoordinator _outcomes;
    private readonly HashSet<string> _attemptedPotionPurchases = new(StringComparer.Ordinal);
    private MerchantInventory? _lastShopInventory;

    internal DecisionSource(RunState runState, string seed, CombatOutcomeCoordinator outcomes)
    {
        _runState = runState;
        _rng = new Rng(unchecked((ulong)StringHelper.GetDeterministicHashCode("fs1-policy:" + seed)));
        _outcomes = outcomes;
    }

    public InjectedCombatOutcome? TryInject(RunState run, CombatRoom room) =>
        _outcomes.TryInject(run, room);

    public Task<MapPoint> ChooseMapPointAsync(IReadOnlyList<MapPoint> options)
    {
        Player player = _runState.Players[0];
        int hpPercent = player.Creature.MaxHp == 0
            ? 100
            : player.Creature.CurrentHp * 100 / player.Creature.MaxHp;

        MapPoint? boss = options.FirstOrDefault(static point => point.PointType == MapPointType.Boss);
        if (boss != null)
            return Task.FromResult(boss);

        MapPoint? rest = options.FirstOrDefault(static point => point.PointType == MapPointType.RestSite);
        if (rest != null && hpPercent < 70)
            return Task.FromResult(rest);

        MapPoint? elite = options.FirstOrDefault(static point => point.PointType == MapPointType.Elite);
        if (elite != null && hpPercent >= 80 && _rng.NextInt(3) == 0)
            return Task.FromResult(elite);

        MapPoint? monster = options.FirstOrDefault(static point => point.PointType == MapPointType.Monster);
        if (monster != null)
            return Task.FromResult(monster);

        if (rest != null)
            return Task.FromResult(rest);

        if (elite != null)
            return Task.FromResult(elite);

        MapPoint? unknown = options.FirstOrDefault(static point => point.PointType == MapPointType.Unknown);
        if (unknown != null)
            return Task.FromResult(unknown);

        return Task.FromResult(options[0]);
    }

    public async Task<CombatDecision> ChooseCombatActionAsync(CombatState state)
    {
        Player player = state.Players[0];
        PlayerCombatState? playerState = player.PlayerCombatState;
        if (playerState == null || !state.IsLiveCombat())
            return new CombatDecision.EndTurn();

        int incoming = EstimateIncomingDamage(state);
        int unblocked = Math.Max(0, incoming - player.Creature.Block);
        int hpPercent = player.Creature.MaxHp == 0
            ? 100
            : player.Creature.CurrentHp * 100 / player.Creature.MaxHp;
        bool trace = Environment.GetEnvironmentVariable("FS1_TRACE") == "1";
        if (hpPercent <= 30 || unblocked >= player.Creature.CurrentHp)
        {
            PotionModel? potion = FindUsablePotion(state, player);
            if (potion != null)
            {
                Creature? potionTarget = ResolvePotionTarget(state, player, potion);
                if (trace)
                    Console.Error.WriteLine($"TRACE potion {potion.Id.Entry} target={potionTarget?.CombatId}");
                return new CombatDecision.UsePotion(potion, potionTarget);
            }
        }

        List<(CardModel Card, double Heuristic)> candidates = [];
        foreach (CardModel card in playerState.Hand.Cards)
        {
            if (!card.CanPlay(out _))
                continue;
            candidates.Add((card, Score(card, state, player, unblocked)));
        }
        candidates.Sort(static (left, right) => right.Heuristic.CompareTo(left.Heuristic));
        CardModel? best = null;
        double bestScore = double.MinValue;
        for (int index = 0; index < candidates.Count; index++)
        {
            (CardModel card, double heuristic) = candidates[index];
            double simulated = index < 5
                ? await SimulateTurnAsync(state, player, card, incoming, unblocked)
                : double.MinValue / 4;
            double score = simulated + heuristic * 0.01;
            if (score > bestScore)
            {
                best = card;
                bestScore = score;
            }
        }
        if (trace)
        {
            Console.Error.WriteLine(
                $"TRACE round={state.RoundNumber} hp={player.Creature.CurrentHp}/{player.Creature.MaxHp} " +
                $"energy={playerState.Energy} incoming={incoming} block={player.Creature.Block} " +
                $"unblocked={unblocked} hand=" +
                string.Join(",", playerState.Hand.Cards.Select(card =>
                    $"{card.Id.Entry}{(card.CanPlay(out _) ? string.Empty : "!")}:" +
                    $"{Score(card, state, player, unblocked):F0}")));
        }
        if (best == null)
            return new CombatDecision.EndTurn();
        if (trace)
            Console.Error.WriteLine($"TRACE play {best.Id.Entry} target={ResolveTarget(state, player, best)?.CombatId}");
        return new CombatDecision.PlayCard(best, ResolveTarget(state, player, best));
    }

    public Task<ShopDecision> ChooseShopActionAsync(MerchantInventory inventory, Player player)
    {
        if (!ReferenceEquals(inventory, _lastShopInventory))
        {
            _lastShopInventory = inventory;
            _attemptedPotionPurchases.Clear();
        }

        MerchantRelicEntry? relic = inventory.Relics
            .Where(static entry => !entry.Purchased)
            .OrderBy(static entry => entry.Price)
            .FirstOrDefault(entry => entry.Price <= player.Gold && entry.Price <= 300);
        if (relic != null)
            return Task.FromResult<ShopDecision>(new ShopDecision.BuyRelic(relic));

        if (!inventory.CardRemoval.Purchased
            && player.Gold >= inventory.CardRemoval.Price)
        {
            CardModel? removable = player.Deck.Cards.FirstOrDefault(static card =>
                card.IsRemovable &&
                (card.Id.Entry.StartsWith("STRIKE_", StringComparison.Ordinal) ||
                 card.Id.Entry.StartsWith("DEFEND_", StringComparison.Ordinal)));
            if (removable != null)
                return Task.FromResult<ShopDecision>(
                    new ShopDecision.BuyCardRemoval(removable));
        }

        if (player.PotionSlots.Any(static potion => potion is null))
        {
            MerchantPotionEntry? potion = inventory.Potions
                .Where(static entry => !entry.Purchased)
                .Where(entry => !_attemptedPotionPurchases.Contains(entry.Potion.Id.Entry))
                .OrderBy(static entry => entry.Price)
                .FirstOrDefault(entry => entry.Price <= player.Gold);
            if (potion != null)
            {
                _attemptedPotionPurchases.Add(potion.Potion.Id.Entry);
                return Task.FromResult<ShopDecision>(new ShopDecision.BuyPotion(potion));
            }
        }

        return Task.FromResult<ShopDecision>(new ShopDecision.Leave());
    }

    public Task<RewardDecision> ChooseRewardActionAsync(RewardsSet rewards)
    {
        RewardDecisionClassification classification = RewardDecisionClassifier.Classify(rewards);
        if (classification is RewardDecisionClassification.Choice choice)
        {
            RewardDecision.TakeCard? best = choice.Candidates
                .OfType<RewardDecision.TakeCard>()
                .OrderByDescending(static candidate => CardRewardScore(candidate.Card))
                .FirstOrDefault();
            if (best != null)
                return Task.FromResult<RewardDecision>(best);
        }
        return Task.FromResult(RewardDecisionClassifier.ChooseDefault(rewards));
    }

    private static int SmithScore(CardModel card)
    {
        if (string.Equals(card.Id.Entry, "BASH", StringComparison.Ordinal))
            return 500;
        int type = card.Type switch
        {
            CardType.Power => 300,
            CardType.Attack => 200,
            CardType.Skill => 100,
            _ => 0,
        };
        int rarity = card.Rarity switch
        {
            CardRarity.Rare => 30,
            CardRarity.Uncommon => 20,
            CardRarity.Common => 10,
            _ => 0,
        };
        return type + rarity;
    }

    private static int CardRewardScore(CardModel card)
    {
        int rarity = card.Rarity switch
        {
            CardRarity.Rare => 300,
            CardRarity.Uncommon => 200,
            CardRarity.Common => 100,
            _ => 0,
        };
        int type = card.Type switch
        {
            CardType.Power => 40,
            CardType.Attack => 20,
            CardType.Skill => 10,
            _ => 0,
        };
        return rarity + type;
    }

    public Task<RestSiteDecision> ChooseRestSiteActionAsync(
        Player player, IReadOnlyList<RestSiteDecision> candidates)
    {
        IReadOnlyList<RestSiteDecision> enabled = candidates
            .Where(static candidate => candidate.IsEnabled).ToList();
        if (enabled.Count == 0)
            throw new InvalidOperationException("休息点没有可用选项。");

        bool hurt = player.Creature.CurrentHp * 100 <= player.Creature.MaxHp * 65;
        if (!hurt)
        {
            RestSiteDecision? smith = enabled
                .OfType<RestSiteDecision.Smith>()
                .Where(static smith => smith.Card.IsUpgradable)
                .OrderByDescending(static smith => SmithScore(smith.Card))
                .FirstOrDefault();
            if (smith != null)
                return Task.FromResult<RestSiteDecision>(smith);
        }
        RestSiteDecision? heal = enabled.OfType<RestSiteDecision.Heal>().FirstOrDefault();
        if (heal != null)
            return Task.FromResult(heal);
        return Task.FromResult(enabled[0]);
    }

    private static Creature? ResolveTarget(CombatState state, Player player, CardModel card)
    {
        return card.TargetType switch
        {
            TargetType.AnyEnemy => state.HittableEnemies
                .OrderBy(static enemy => enemy.CurrentHp)
                .FirstOrDefault(),
            TargetType.AnyAlly or TargetType.AnyPlayer => player.Creature,
            _ => null,
        };
    }

    private static int EstimateIncomingDamage(CombatState state)
    {
        int total = 0;
        IReadOnlyList<Creature> players = state.PlayerCreatures;
        foreach (Creature enemy in state.Enemies)
        {
            if (!enemy.IsAlive || enemy.Monster?.NextMove is not { } move)
                continue;
            foreach (AbstractIntent intent in move.Intents)
            {
                if (intent is AttackIntent attack)
                    total += attack.GetTotalDamage(players, enemy);
            }
        }
        return total;
    }

    private static async Task<double> SimulateTurnAsync(CombatState state, Player player,
        CardModel card, int incoming, int unblocked)
    {
        try
        {
            CombatState clone = state.Clone(out CombatCloneMap map);
            clone.CardSelectionSource = FirstChoiceSelection.Instance;
            Player clonedPlayer = map.Player(player);
            CardModel clonedCard = map.Card(card);
            Creature? clonedTarget = ResolveTarget(clone, clonedPlayer, clonedCard);
            int hpBefore = clonedPlayer.Creature.CurrentHp;
            int enemyHpBefore = EnemyHp(clone);
            int aliveBefore = clone.Enemies.Count(static enemy => enemy.IsAlive);
            int powersBefore = PowerCount(clone);
            await clone.Engine!.PlayCardAsync(clonedPlayer, clonedCard, clonedTarget);
            clone.Engine.CheckWinCondition();
            if (clone.Engine.Won)
                return 1_000_000;
            if (!clone.Engine.IsInProgress)
                return -1_000_000;
            if (!await PlayGreedyRestOfTurnAsync(clone, clonedPlayer))
                return double.MinValue / 4;
            if (clone.Engine.IsInProgress)
                await clone.Engine.EndPlayerTurnAsync();
            if (clone.Engine.Won)
                return 1_000_000;
            if (!clone.Engine.IsInProgress)
                return -1_000_000;
            Player nextPlayer = clone.Players[0];
            int hpAfter = nextPlayer.Creature.CurrentHp;
            int enemyHpAfter = EnemyHp(clone);
            int aliveAfter = clone.Enemies.Count(static enemy => enemy.IsAlive);
            int powersAfter = PowerCount(clone);
            int energyAfter = nextPlayer.PlayerCombatState!.Energy;
            int handAfter = nextPlayer.PlayerCombatState.Hand.Cards.Count;
            int blockAfter = nextPlayer.Creature.Block;
            double value = (enemyHpBefore - enemyHpAfter) * 160;
            value -= (hpBefore - hpAfter) * 1_000;
            value += blockAfter * 100;
            value += energyAfter * 5;
            value += handAfter * 10;
            value += (aliveBefore - aliveAfter) * 2_000;
            value += (powersAfter - powersBefore) * 200;
            return value;
        }
        catch (InvalidOperationException)
        {
            return double.MinValue / 4;
        }
        catch (NotSupportedException)
        {
            return double.MinValue / 4;
        }
        catch (ArgumentException)
        {
            return double.MinValue / 4;
        }
    }

    private static async Task<bool> PlayGreedyRestOfTurnAsync(CombatState clone, Player player)
    {
        for (int step = 0; step < 20 && clone.Engine!.IsInProgress; step++)
        {
            PlayerCombatState playerState = player.PlayerCombatState!;
            int incoming = EstimateIncomingDamage(clone);
            int unblocked = Math.Max(0, incoming - player.Creature.Block);
            CardModel? best = null;
            double bestScore = double.MinValue;
            foreach (CardModel candidate in playerState.Hand.Cards)
            {
                if (!candidate.CanPlay(out _))
                    continue;
                double score = Score(candidate, clone, player, unblocked);
                if (score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }
            if (best == null)
                break;
            await clone.Engine.PlayCardAsync(player, best, ResolveTarget(clone, player, best));
            clone.Engine.CheckWinCondition();
        }
        return true;
    }

    private static int EnemyHp(CombatState state)
    {
        int total = 0;
        foreach (Creature enemy in state.Enemies)
        {
            if (enemy.IsAlive)
                total += enemy.CurrentHp;
        }
        return total;
    }

    private static int PowerCount(CombatState state)
    {
        int total = 0;
        foreach (Creature creature in state.Creatures)
            total += creature.Powers.Count();
        return total;
    }

    private static PotionModel? FindUsablePotion(CombatState state, Player player)
    {
        foreach (PotionModel potion in player.PotionSlots.OfType<PotionModel>())
        {
            Creature? target = ResolvePotionTarget(state, player, potion);
            if (PotionCmd.CanUseManually(state, player, potion, target))
                return potion;
        }
        return null;
    }

    private static Creature? ResolvePotionTarget(CombatState state, Player player,
        PotionModel potion)
    {
        return potion.TargetType switch
        {
            TargetType.AnyEnemy => state.HittableEnemies
                .OrderBy(static enemy => enemy.CurrentHp)
                .FirstOrDefault(),
            TargetType.Self or TargetType.AnyPlayer or TargetType.AnyAlly => player.Creature,
            _ => null,
        };
    }

    private sealed class FirstChoiceSelection : ICardSelectionDecisionSource
    {
        internal static readonly FirstChoiceSelection Instance = new();

        public Task<IReadOnlyList<CardModel>> ChooseCardsAsync(CardSelectionRequest request)
        {
            int count = Math.Min(request.MinCount, request.Candidates.Count);
            return Task.FromResult<IReadOnlyList<CardModel>>(
                request.Candidates.Take(count).ToList());
        }
    }

    private static double Score(CardModel card, CombatState state, Player player, int unblocked)
    {
        CardChoiceBaseValues values = card is ICardChoiceBaseValueProvider provider
            && provider.CardChoiceBaseValues is { } existing
            ? existing
            : default;
        double damage = values.Damage;
        double block = values.Block;
        int lowestEnemyHp = state.HittableEnemies
            .Select(static enemy => enemy.CurrentHp)
            .DefaultIfEmpty(int.MaxValue)
            .Min();
        bool aoe = card.TargetType is TargetType.AllEnemies or TargetType.RandomEnemy;
        switch (card.Type)
        {
            case CardType.Power:
                return 10_000 + values.Cards * 100 + values.Energy * 200 + values.Stars * 100;
            case CardType.Attack:
                double attackScore = damage * 10;
                if (aoe)
                    attackScore *= 1.5;
                if (damage > 0 && damage >= lowestEnemyHp)
                    attackScore += 2_000;
                if (damage == 0)
                    attackScore += 40;
                if (unblocked > 0)
                    attackScore += block * 20;
                return attackScore + values.Cards * 50;
            case CardType.Skill:
                double blockScore = block * (unblocked > 0 ? 20 : 2);
                double utility = damage * 8 + values.Cards * 120 + values.Energy * 200
                    + values.Stars * 100;
                if (block == 0 && values.Cards == 0 && values.Energy == 0 && values.Stars == 0
                    && damage == 0)
                    utility += 35;
                return blockScore + utility;
            case CardType.Status:
            case CardType.Curse:
            case CardType.Quest:
                return -1_000;
            default:
                return 10;
        }
    }
}
