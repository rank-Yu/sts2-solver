// Ported semantics from CombatSolver src/Search/SearchPolicySnapshot.cs and
// src/Runtime/SolverController.cs (CaptureSearchPolicy) at commit a62f1850
// (MIT, Copyright (c) 2026 Torch1230 and contributors). Only the policy values the score, bound
// and request compilers read are represented. The offline defaults are the fixed values the M0
// harness installed (ModRuntime.ApplyFixedBudgetSettings + SolverSettings defaults); the in-game
// Mod supplies SearchPolicyConfiguration captured on the main thread.
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;

using Creature = MegaCrit.Sts2.Core.Entities.Creatures.Creature;

namespace Sts2Solver.Import;

/// <summary>Theft handling captured at the root (<c>TheftPolicy</c>).</summary>
public enum SearchTheftPolicy
{
    PreserveResources,
    LetEscape,
}

/// <summary>Boss HP strategy for act transitions and the final boss (<c>BossHpStrategy</c>).</summary>
public enum SearchBossHpStrategy
{
    ProgressionFirst,
    MinimizeHpLoss,
}

/// <summary>The per-potion modes the current search request understands.</summary>
public enum SearchPotionMode
{
    Smart,
    Disabled,
}

/// <summary>One persisted per-slot potion directive before root reconciliation.</summary>
public readonly record struct SearchPotionDirective(int Slot, string PotionId, SearchPotionMode Mode);

/// <summary>
/// The player settings the in-game Mod captures on the main thread and passes to
/// <see cref="SearchPolicyCapture"/>. Offline fixtures leave it null and keep the M0 defaults.
/// </summary>
public sealed record SearchPolicyConfiguration
{
    public static SearchPolicyConfiguration Default { get; } = new();

    public SearchGrowthBudget GrowthBudgets { get; init; }

    /// <summary>The ten growth cards' rewards are not valued; relic counter goals do not depend on it.</summary>
    public bool IgnoreGrowthRewards { get; init; }

    /// <summary>Combat-cumulative BrightestFlame max HP limit; null is unlimited.</summary>
    public int? BrightestFlameMaxHpLossLimit { get; init; }

    public SearchBossHpStrategy ActTransitionBossHpStrategy { get; init; }
        = SearchBossHpStrategy.ProgressionFirst;

    public SearchBossHpStrategy FinalBossHpStrategy { get; init; }
        = SearchBossHpStrategy.ProgressionFirst;

    public SearchPotionMode PotionDefaultMode { get; init; } = SearchPotionMode.Smart;

    public IReadOnlyList<SearchPotionDirective> PotionDirectives { get; init; } = [];

    /// <summary>Per-combat thief override; null keeps the root-derived default.</summary>
    public SearchTheftPolicy? TheftPolicy { get; init; }

    /// <summary>
    /// The counter relics the search tries to leave one short of triggering, by native relic id
    /// (<see cref="RelicCounterCatalog"/>), each with the extra HP the player allows for it (0 still
    /// prefers the goal among routes of equal HP). A relic without an entry is not a goal.
    /// </summary>
    public IReadOnlyDictionary<string, int> RelicCounterAllowances { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);
}

/// <summary>The ten built-in growth budgets, in <c>GrowthValues</c> order.</summary>
public readonly record struct SearchGrowthBudget(
    int HandOfGreed = 0,
    int TheHunt = 0,
    int Feed = 0,
    int Royalties = 0,
    int Alchemize = 0,
    int GeneticAlgorithm = 0,
    int TheScythe = 0,
    int Goopy = 0,
    int ForbiddenGrimoire = 0,
    int MadScience = 0)
{
    public int Total => checked(HandOfGreed + TheHunt + Feed + Royalties + Alchemize
        + GeneticAlgorithm + TheScythe + Goopy + ForbiddenGrimoire + MadScience);

    internal GrowthValues ToCore() => new(
        HandOfGreed, TheHunt, Feed, Royalties, Alchemize, GeneticAlgorithm,
        TheScythe, Goopy, ForbiddenGrimoire, MadScience);

    internal static SearchGrowthBudget FromCore(GrowthValues value) => new(
        value.HandOfGreed, value.TheHunt, value.Feed, value.Royalties, value.Alchemize,
        value.GeneticAlgorithm, value.TheScythe, value.Goopy, value.ForbiddenGrimoire,
        value.MadScience);
}

/// <summary>
/// Immutable policy values the score, bound and request compilation read. Root-derived fields
/// (<see cref="Act3BossStrategy"/>, <see cref="TheftPolicy"/> and the growth targets) are captured
/// by <see cref="SearchPolicyCapture"/>; the remaining fields are the player settings the in-game
/// Mod supplies, or the M0 offline defaults a fixture request overrides.
/// </summary>
public sealed record SearchPolicy
{
    /// <summary>Player-configured growth HP budgets (<c>SearchPolicySnapshot.GrowthBudgets</c>).</summary>
    public SearchGrowthBudget GrowthBudgets { get; init; }

    /// <summary><c>GrowthOpportunityTargets.RequiredRewards</c>; derived from the root's cards.</summary>
    public SearchGrowthBudget RequiredGrowthRewards { get; init; }

    /// <summary><c>GrowthOpportunityTargets.UnboundedSources.Count != 0</c>.</summary>
    public bool HasUnboundedGrowthTargets { get; init; }

    /// <summary><c>SearchPolicySnapshot.IsAct3BossEncounter</c>; gates three-act boss interactions.</summary>
    public bool Act3BossStrategy { get; init; }

    public SearchBossHpStrategy ActTransitionBossHpStrategy { get; init; }
        = SearchBossHpStrategy.ProgressionFirst;

    public SearchBossHpStrategy FinalBossHpStrategy { get; init; }
        = SearchBossHpStrategy.ProgressionFirst;

    /// <summary>The ten growth cards' rewards are not valued; relic counter goals do not depend on it.</summary>
    public bool IgnoreGrowthRewards { get; init; }

    /// <summary>Combat-cumulative BrightestFlame max HP limit; null leaves the default path.</summary>
    public int? BrightestFlameMaxHpLossLimit { get; init; }

    public SearchTheftPolicy? TheftPolicy { get; init; }

    /// <summary>The bridge rejects a non-null <c>DevelopmentStrategy</c>.</summary>
    public bool HasDevelopmentStrategy { get; init; }

    /// <summary>A non-empty <c>StrategicEffectMirrors</c> registration is rejected.</summary>
    public bool HasStrategicEffectMirrors { get; init; }

    /// <summary>A non-empty <c>CardRemovalValueMirrors</c> registration is rejected.</summary>
    public bool HasCardRemovalValueMirrors { get; init; }

    /// <summary>A non-empty third-party half of <c>GrowthValues</c> is rejected.</summary>
    public bool HasThirdPartyGrowthSources { get; init; }

    /// <summary>A non-empty third-party half of <c>RequiredRewards</c> is rejected.</summary>
    public bool HasThirdPartyGrowthTargets { get; init; }

    /// <summary>How a potion without a per-slot directive is treated (<c>PotionPolicy</c>).</summary>
    public SearchPotionMode PotionDefaultMode { get; init; } = SearchPotionMode.Smart;

    /// <summary>Per-slot directives already reconciled against the root potion belt.</summary>
    public IReadOnlyList<SearchPotionDirective> PotionDirectives { get; init; } = [];

    /// <summary>Relic counter allowances by native relic id; see <see cref="SearchPolicyConfiguration"/>.</summary>
    public IReadOnlyDictionary<string, int> RelicCounterAllowances { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary><c>SearchPolicySnapshot.EffectiveGrowthBudgets</c>.</summary>
    public SearchGrowthBudget EffectiveGrowthBudgets
        => IgnoreGrowthRewards ? default : GrowthBudgets;

    /// <summary><c>SearchPolicySnapshot.HasGrowthTargets</c>.</summary>
    public bool HasGrowthTargets
        => RequiredGrowthRewards.Total != 0 || HasUnboundedGrowthTargets;

    /// <summary><c>SearchPolicySnapshot.EffectiveHasGrowthTargets</c>.</summary>
    public bool EffectiveHasGrowthTargets => !IgnoreGrowthRewards && HasGrowthTargets;
}

/// <summary>
/// Reconstructs the policy snapshot from the native root. When no configuration is passed, the
/// fixed settings mirror the M0 offline host: <c>ModRuntime.ApplyFixedBudgetSettings</c> left growth
/// budgets at zero, relic strategy enabled with no rules, both boss strategies
/// <c>ProgressionFirst</c>, <c>IgnoreGrowthRewards=false</c>, no potion directives, and no
/// development/third-party registrations. The profile flags default to the harness defaults
/// (<c>ordering=baseline</c>, no continuous threat, no perturbation). Act-3/theft/growth fields are
/// then derived exactly as <c>SolverController.CaptureSearchPolicy</c> does; potion directives are
/// reconciled against the current belt.
/// </summary>
public static class SearchPolicyCapture
{
    public static SearchPolicy Capture(CombatState state, Player player)
        => Capture(state, player, configuration: null);

    public static SearchPolicy Capture(CombatState state, Player player,
        SearchPolicyConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(player);
        if (state.Players.Count != 1)
        {
            throw new UnsupportedImportException(ImportReason.MultiPlayer,
                $"玩家数 {state.Players.Count}");
        }
        if (player.PlayerCombatState is null)
        {
            throw new UnsupportedImportException(ImportReason.Unrepresentable,
                "玩家没有 PlayerCombatState");
        }
        SearchPolicyConfiguration settings = configuration ?? SearchPolicyConfiguration.Default;
        if (settings.BrightestFlameMaxHpLossLimit is < 0 or > 1000)
        {
            throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                $"至亮之焰最大生命消耗上限必须在 0..1000 或 null：{settings.BrightestFlameMaxHpLossLimit}");
        }
        SearchTheftPolicy? theft = ResolveTheftPolicy(state);
        if (settings.TheftPolicy is { } configuredTheft)
        {
            if (theft is null)
            {
                throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                    "本场战斗没有盗贼策略，不能应用盗贼策略覆盖。");
            }
            theft = configuredTheft;
        }
        IReadOnlyList<SearchPotionDirective> directives = ReconcilePotionDirectives(
            settings.PotionDirectives, CurrentPotionSlots(player));
        GrowthTargets targets = GrowthTargets.Capture(state);
        return new SearchPolicy
        {
            GrowthBudgets = settings.GrowthBudgets,
            RequiredGrowthRewards = SearchGrowthBudget.FromCore(targets.Required),
            HasUnboundedGrowthTargets = targets.Unbounded,
            Act3BossStrategy = IsAct3BossEncounter(state),
            ActTransitionBossHpStrategy = settings.ActTransitionBossHpStrategy,
            FinalBossHpStrategy = settings.FinalBossHpStrategy,
            IgnoreGrowthRewards = settings.IgnoreGrowthRewards,
            BrightestFlameMaxHpLossLimit = settings.BrightestFlameMaxHpLossLimit,
            TheftPolicy = theft,
            PotionDefaultMode = settings.PotionDefaultMode,
            PotionDirectives = directives,
            RelicCounterAllowances = ValidateRelicAllowances(settings.RelicCounterAllowances),
        };
    }

    private static IReadOnlyDictionary<string, int> ValidateRelicAllowances(
        IReadOnlyDictionary<string, int> allowances)
    {
        ArgumentNullException.ThrowIfNull(allowances);
        Dictionary<string, int> copy = new(StringComparer.Ordinal);
        foreach ((string relicId, int hp) in allowances)
        {
            if (!RelicCounterCatalog.RelicIds.Contains(relicId) || hp is < 0 or > 1000)
            {
                throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                    $"遗物计数额度无效：{relicId}={hp}");
            }
            copy[relicId] = hp;
        }
        return copy;
    }

    /// <summary>
    /// Keeps only directives whose slot still holds the same potion. Smart is equivalent to no
    /// directive and is dropped, so a file with redundant Smart entries still has a default request.
    /// </summary>
    public static IReadOnlyList<SearchPotionDirective> ReconcilePotionDirectives(
        IReadOnlyList<SearchPotionDirective> persisted,
        IReadOnlyList<(int Slot, string PotionId)> current)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(current);
        List<SearchPotionDirective> retained = [];
        foreach (SearchPotionDirective directive in persisted)
        {
            if (directive.Mode == SearchPotionMode.Smart)
                continue;
            if (!Enum.IsDefined(directive.Mode))
            {
                throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                    $"未知药水策略：{directive.Mode}");
            }
            if (directive.Slot < 0 || string.IsNullOrEmpty(directive.PotionId))
            {
                throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                    $"非法药水策略：slot={directive.Slot} potion={directive.PotionId}");
            }
            bool matches = false;
            foreach ((int slot, string potionId) in current)
            {
                if (slot == directive.Slot
                    && string.Equals(potionId, directive.PotionId, StringComparison.Ordinal))
                {
                    matches = true;
                    break;
                }
            }
            if (matches)
                retained.Add(directive);
        }
        retained.Sort(static (left, right) => left.Slot.CompareTo(right.Slot));
        return retained;
    }

    private static List<(int Slot, string PotionId)> CurrentPotionSlots(Player player)
    {
        List<(int Slot, string PotionId)> slots = [];
        for (int slot = 0; slot < player.PotionSlots.Count; slot++)
        {
            if (player.GetPotionAtSlotIndex(slot) is { } potion)
                slots.Add((slot, potion.Id.Entry));
        }
        return slots;
    }

    private static bool IsAct3BossEncounter(CombatState state)
        => state.RunState.CurrentActIndex == 2
            && state.Encounter?.Id.Entry is "TEST_SUBJECT_BOSS" or "AEONGLASS_BOSS" or "QUEEN_BOSS";

    public static bool IsTheftApplicable(CombatState state)
        => ResolveTheftPolicy(state) is not null;

    public static SearchTheftPolicy? ResolveTheftPolicy(CombatState state)
    {
        if (state.Encounter?.Id.Entry is "GREMLIN_MERC_NORMAL" or "THIEVING_HOPPER_WEAK")
            return SearchTheftPolicy.PreserveResources;
        foreach (Creature creature in state.Creatures)
        {
            if (creature.Monster is GremlinMerc or ThievingHopper or FatGremlin)
                return SearchTheftPolicy.PreserveResources;
        }
        return null;
    }
}

internal readonly record struct GrowthTargets(GrowthValues Required, bool Unbounded)
{
    public static GrowthTargets Capture(CombatState state)
    {
        CardModel[] available = state.Players
            .SelectMany(static player => player.PlayerCombatState!.AllCards)
            .Where(IsAvailable)
            .ToArray();
        int madScienceCapacity = MadScienceCapacity(state);
        bool hasAnyTargets = available.Any(HasGrowthTarget);
        string? dynamicRisk = null;
        if (hasAnyTargets)
            dynamicRisk = DynamicRisk(state, available);
        return CaptureTargets(available, state.Enemies.Count, dynamicRisk, madScienceCapacity);
    }

    private static GrowthTargets CaptureTargets(IReadOnlyList<CardModel> cards, int enemyCount,
        string? dynamicRisk, int madScienceCapacity)
    {
        GrowthValues required = default;
        bool unbounded = false;
        if (dynamicRisk != null)
        {
            foreach (GrowthSource source in Enum.GetValues<GrowthSource>())
            {
                if (source == GrowthSource.MadScience && madScienceCapacity == 0)
                    continue;
                if (!cards.Any(card => MatchesSource(card, source)))
                    continue;
                unbounded = true;
            }
            return new GrowthTargets(required, unbounded);
        }

        GrowthSource[] fatal = cards.Select(FatalSource).Where(static source => source.HasValue)
            .Select(static source => source!.Value).Distinct().OrderBy(static source => source).ToArray();
        if (fatal.Length > 1)
        {
            unbounded = true;
        }
        else if (fatal.Length == 1)
        {
            GrowthSource source = fatal[0];
            int count = source == GrowthSource.HandOfGreed
                ? enemyCount
                : Math.Min(enemyCount, cards.Count(card => FatalSource(card) == source));
            required = required.With(source, count);
        }

        required = required
            .With(GrowthSource.Royalties, CountFixedPlays(cards, static card => card is Royalties))
            .With(GrowthSource.Alchemize, CountFixedPlays(cards, static card => card is Alchemize))
            .With(GrowthSource.GeneticAlgorithm, CountFixedPlays(cards,
                static card => card is GeneticAlgorithm && card.DeckVersion != null))
            .With(GrowthSource.TheScythe, CountFixedPlays(cards,
                static card => card is TheScythe && card.DeckVersion != null))
            .With(GrowthSource.Goopy, CountFixedPlays(cards,
                static card => card.DeckVersion != null && card.Enchantment is Goopy))
            .With(GrowthSource.ForbiddenGrimoire, CountFixedPlays(cards,
                static card => card is ForbiddenGrimoire))
            .With(GrowthSource.MadScience, Math.Min(madScienceCapacity,
                CountFixedPlays(cards, static card => IsImprovementCard(card))));
        return new GrowthTargets(required, unbounded);
    }

    private static int CountFixedPlays(IEnumerable<CardModel> cards, Func<CardModel, bool> predicate)
    {
        int count = 0;
        foreach (CardModel card in cards.Where(predicate))
        {
            int replay = card.GetEnchantedReplayCount();
            if (replay < 0)
            {
                throw new UnsupportedImportException(ImportReason.Unrepresentable,
                    $"成长牌 {card.Id.Entry} 的固定重放次数为负数：{replay}");
            }
            count = checked(count + 1 + replay);
        }
        return count;
    }

    private static GrowthSource? FatalSource(CardModel card) => card switch
    {
        HandOfGreed => GrowthSource.HandOfGreed,
        TheHunt => GrowthSource.TheHunt,
        Feed => GrowthSource.Feed,
        _ => null,
    };

    private static bool MatchesSource(CardModel card, GrowthSource source) => source switch
    {
        GrowthSource.HandOfGreed => card is HandOfGreed,
        GrowthSource.TheHunt => card is TheHunt,
        GrowthSource.Feed => card is Feed,
        GrowthSource.Royalties => card is Royalties,
        GrowthSource.Alchemize => card is Alchemize,
        GrowthSource.GeneticAlgorithm => card is GeneticAlgorithm && card.DeckVersion != null,
        GrowthSource.TheScythe => card is TheScythe && card.DeckVersion != null,
        GrowthSource.Goopy => card.DeckVersion != null && card.Enchantment is Goopy,
        GrowthSource.ForbiddenGrimoire => card is ForbiddenGrimoire,
        GrowthSource.MadScience => IsImprovementCard(card),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    internal static bool HasGrowthTarget(CardModel card)
        => card is HandOfGreed or TheHunt or Feed or Royalties or Alchemize or ForbiddenGrimoire
            || IsImprovementCard(card)
            || card.DeckVersion != null && (card is GeneticAlgorithm or TheScythe || card.Enchantment is Goopy);

    private static bool IsImprovementCard(CardModel card)
        => card is MadScience madScience
            && madScience.TinkerTimeType == CardType.Power
            && madScience.TinkerTimeRider == TinkerTime.RiderEffect.Improvement;

    private static bool IsAvailable(CardModel card)
        => !card.HasBeenRemovedFromState && card.Pile is { Type: not PileType.Exhaust };

    private static int MadScienceCapacity(CombatState state)
    {
        Player player = state.Players.Single();
        int upgradable = PileType.Deck.GetPile(player).Cards.Count(static card => card.IsUpgradable);
        int committed = 0;
        foreach (ImprovementPower power in player.Creature.Powers.OfType<ImprovementPower>())
        {
            if (power.Amount > 0)
                committed = checked(committed + checked((int)decimal.Ceiling(power.Amount)));
        }
        return Math.Max(0, upgradable - committed);
    }

    private static string? DynamicRisk(CombatState state, IReadOnlyList<CardModel> cards)
    {
        if (cards.Any(static card => card is Burst or EchoForm or OneTwoPunch or SignalBoost
                or TagTeam or Nightmare or DualWield or HeirloomHammer or Juggling
                or ImitationLearning or HowlFromBeyond))
        {
            return "built_in:reachable_replay_or_copy_card";
        }
        foreach (Player player in state.Players)
        {
            foreach (PowerModel power in player.Creature.Powers)
            {
                if (power.Amount > 0m && power is BurstPower or DuplicationPower or EchoFormPower
                    or OneTwoPunchPower or SignalBoostPower or TagTeamPower or JugglingPower
                    or ImitationLearningPower or NightmarePower)
                {
                    return "built_in:active_replay_or_copy_power";
                }
            }
            foreach (RelicModel relic in player.Relics)
            {
                if (!relic.IsMelted && relic is ThrowingAxe or BurningSticks)
                    return "built_in:active_replay_or_copy_relic";
            }
            if (player.PotionSlots.Any(static potion => potion?.Id.Entry == "DUPLICATOR"))
                return "built_in:available_duplication_potion";
        }
        return null;
    }
}
