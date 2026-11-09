// Presentation projection adapted from CombatSolver src/UI/SolverOverlaySnapshot.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors). The source is Sts2Solver's
// CombatUiSnapshot; card, potion, choice and kill names are resolved here on the UI thread so the
// published snapshot only carries stable ids and numbers.
using Sts2Solver.Ai.Combat.Policy;

namespace Sts2Solver;

internal enum SolverOverlayTone
{
    Accent,
    Success,
    Danger,
    Muted,
}

internal enum SolverOverlayActionVisualKind
{
    Other,
    Attack,
    Skill,
    Power,
    Negative,
    Potion,
}

internal sealed record SolverOverlayActionSnapshot(
    string Title,
    string TargetName,
    string? ChoiceText,
    IReadOnlyList<string> RelicLabels,
    IReadOnlyList<string> Kills,
    string Tooltip,
    SolverOverlayActionVisualKind VisualKind,
    int ReplayCount)
{
    public int Index { get; init; }

    public bool HasSamePresentation(SolverOverlayActionSnapshot other)
        => Title == other.Title && TargetName == other.TargetName
            && ChoiceText == other.ChoiceText && Tooltip == other.Tooltip
            && VisualKind == other.VisualKind && ReplayCount == other.ReplayCount
            && RelicLabels.SequenceEqual(other.RelicLabels)
            && Kills.SequenceEqual(other.Kills);
}

internal sealed record SolverOverlayTurnSnapshot(
    int Turn,
    IReadOnlyList<string> TurnStartChoices,
    IReadOnlyList<SolverOverlayActionSnapshot> Actions,
    SolverOverlayActionSnapshot? EndTurnAction,
    int? EnemyHpDamageLost,
    int HpLoss,
    int HpRecovered,
    int EnergyLeft,
    bool CombatEnded)
{
    public int StartActionIndex { get; init; }
}

/// <summary>One part of the strategy outcome row; <see cref="Satisfied"/> picks green or amber.</summary>
internal sealed record SolverStrategyOutcome(string Text, bool Satisfied);

internal sealed record SolverOverlaySnapshot(
    int ProjectedBattlePotionCount,
    int ProjectedBattleHpLost,
    bool ProjectedBattleHpLossKnown,
    long ReviewedWorldlines,
    string HpOutcomeText,
    int RouteHpRecovered,
    int RoutePostCombatRelicHeal,
    bool OnlyDeathRoutesFound,
    IReadOnlyList<SolverOverlayTurnSnapshot> Turns)
{
    /// <summary>
    /// The strategy outcome row of the shown result: aligned relic counters, unmet targets and realized
    /// growth (CombatSolver <c>SolverStrategyOutcomeText.Capture</c> at 5c773caa). Empty while searching.
    /// </summary>
    public IReadOnlyList<SolverStrategyOutcome> StrategyOutcomes { get; init; } = [];

    /// <summary>“已用药：N瓶（…）” for the potions drunk before the shown result; null when none or while searching.</summary>
    public string? UsedPotionOutcomeText { get; init; }

    /// <summary>“预计用药 / 还要用：N瓶（…）” for the shown result's route potions; null when none or while searching.</summary>
    public string? PlannedPotionOutcomeText { get; init; }

    /// <summary>“预计未追回：N 张牌 / G 金币” when the route ends with stolen resources unrecovered.</summary>
    public string? UnrecoveredLootText { get; init; }

    internal static SolverOverlaySnapshot Build(CombatUiSnapshot snapshot, CombatDisplayNames names)
    {
        SearchUiSnapshot? search = snapshot.Search;
        RouteUiSnapshot? route = snapshot.Route;
        IReadOnlyList<SolverOverlayTurnSnapshot> turns = route != null
            ? route.Turns.Select(turn => FromRouteTurn(turn, names)).ToArray()
            : snapshot.Phase == CombatUiPhase.Searching
                ? search?.RoutePreview is { } preview
                    ? preview.Turns.Select(turn => FromPreviewTurn(turn, names)).ToArray()
                    : search?.CurrentTurnPreview is { } current
                        ? [FromPreviewTurn(current, names)]
                        : []
                : [];
        // While a route is executed the controller has no current result; the outcome comes from
        // the result the shown route belongs to.
        CombatResultUiSnapshot? result = snapshot.Result ?? snapshot.RouteResult;
        // A preview's turns count from the search root; the battle total adds what was lost before.
        int lostBefore = result?.BattleHpLostSoFar ?? search?.BattleHpLostBeforeSearch ?? 0;
        int projectedLost = result?.ProjectedBattleHpLost
            ?? (search?.RoutePreview is { } speculative
                ? lostBefore + speculative.Turns.Sum(static turn => turn.HpLost)
                : 0);
        bool projectedKnown = result?.CombatEndedTurn != null
            || search?.RoutePreview is { } previewKnown && previewKnown.Victory
            || snapshot.Route is { Victory: true };
        int recovered = turns.Sum(static turn => turn.HpRecovered);
        int potions = result?.ProjectedBattlePotionCount
            ?? search?.RoutePreview?.ProjectedBattlePotionCount
            ?? 0;
        // "Only death routes" is a verdict on the finished search. While searching, a member's early
        // candidates are often all deaths even after an earlier member found a survivable route.
        bool onlyDeath = result?.OnlyDeathRoutes
            ?? (snapshot.Phase == CombatUiPhase.Searching
                ? false
                : search?.RoutePreview?.OnlyDeathRoutes ?? false);

        // The one HP outcome text: the result presentation and every later refresh of the same result
        // show it, so the recalculation increase does not drop off on a refresh.
        int increase = result?.ProjectedBattleHpLossIncrease ?? 0;
        string hpOutcome = !projectedKnown
            ? SolverText.Get("预计战损 未知")
            : projectedLost > 0 || lostBefore > 0
                ? increase > 0
                    ? SolverText.Format(
                        $"本局扣血  已 {lostBefore}    预计 {projectedLost} HP    重算增加 {increase} HP")
                    : SolverText.Format(
                        $"本局扣血  已 {lostBefore}    预计 {projectedLost} HP")
                : SolverText.Get("本局扣血  0 HP");
        return new SolverOverlaySnapshot(
            potions,
            projectedLost,
            projectedKnown,
            snapshot.ReviewedWorldlines,
            hpOutcome,
            recovered,
            result?.PostCombatRelicHeal ?? 0,
            onlyDeath,
            turns)
        {
            StrategyOutcomes = snapshot.Phase == CombatUiPhase.Searching ? [] : StrategyOutcomesFor(result),
            UsedPotionOutcomeText = snapshot.Phase == CombatUiPhase.Searching ? null : UsedPotionOutcome(result),
            PlannedPotionOutcomeText = snapshot.Phase == CombatUiPhase.Searching
                ? null
                : PlannedPotionOutcome(result),
            UnrecoveredLootText = snapshot.Phase == CombatUiPhase.Searching ? null : UnrecoveredLoot(result),
        };
    }

    // Ported from CombatSolver src/UI/SolverOverlaySnapshot.cs at 5c773caa (MIT License,
    // Copyright (c) 2026 Torch1230 and contributors): UsedPotionOutcome, PlannedPotionOutcome and the
    // unrecovered-loot text.
    private static string? UsedPotionOutcome(CombatResultUiSnapshot? result)
    {
        if (result is not { PotionsUsedSoFar.Length: > 0 } shown)
            return null;
        int count = shown.PotionsUsedSoFar.Length;
        string names = PotionNames(shown.PotionsUsedSoFar);
        return SolverText.Format($"已用药：{count}瓶（{names}）");
    }

    private static string? PlannedPotionOutcome(CombatResultUiSnapshot? result)
    {
        if (result is not { PlannedPotionIds.Length: > 0 } shown)
            return null;
        int count = shown.PlannedPotionIds.Length;
        string names = PotionNames(shown.PlannedPotionIds);
        return shown.PotionsUsedSoFar.Length == 0
            ? SolverText.Format($"预计用药：{count}瓶（{names}）")
            : SolverText.Format($"还要用：{count}瓶（{names}）");
    }

    private static string? UnrecoveredLoot(CombatResultUiSnapshot? result)
    {
        if (result is not { } shown || shown.UnrecoveredCards <= 0 && shown.UnrecoveredGold <= 0)
            return null;
        int cards = shown.UnrecoveredCards;
        int gold = shown.UnrecoveredGold;
        return SolverText.Format($"预计未追回：{cards} 张牌 / {gold} 金币");
    }

    private static string PotionNames(IEnumerable<string> ids)
        => string.Join(SolverText.IsEnglish ? ", " : "、", ids.Select(CombatDisplayNames.PotionTitle));

    // Ported from CombatSolver src/UI/SolverStrategyOutcomeText.cs at 5c773caa (MIT License,
    // Copyright (c) 2026 Torch1230 and contributors). Targets are fixed one short of triggering, so an
    // unmet one shows a single goal; Meat on the Bone's heal is in the recovered-HP label instead.
    private static IReadOnlyList<SolverStrategyOutcome> StrategyOutcomesFor(CombatResultUiSnapshot? result)
    {
        if (result == null)
            return [];
        List<string> completed = [], pending = [], growth = [];
        foreach (StrategyRelicUi relic in result.StrategyRelics)
        {
            string name = CombatDisplayNames.RelicTitle(relic.RelicId);
            if (relic.Met)
                completed.Add($"{name} {relic.Value}");
            else
                pending.Add(SolverText.Format($"{name} {relic.Goal} 实际 {relic.Value}"));
        }
        foreach (StrategyGrowthUi gain in result.StrategyGrowth)
        {
            string name = SolverGrowthStrategyPage.GainTitle(Enum.Parse<GrowthSource>(gain.Source));
            growth.Add(gain.Count == 1 ? name : $"{name} ×{gain.Count}");
        }
        string separator = SolverText.IsEnglish ? ", " : "、";
        List<SolverStrategyOutcome> parts = [];
        if (completed.Count > 0)
            parts.Add(new(SolverText.Format($"已卡：{string.Join(separator, completed)}"), true));
        if (pending.Count > 0)
            parts.Add(new(SolverText.Format($"未达标：{string.Join(separator, pending)}"), false));
        if (growth.Count > 0)
            parts.Add(new(SolverText.Format($"已获得：{string.Join(separator, growth)}"), true));
        return parts;
    }

    private static SolverOverlayTurnSnapshot FromRouteTurn(RouteTurnUi turn, CombatDisplayNames names)
    {
        List<SolverOverlayActionSnapshot> actions = [];
        SolverOverlayActionSnapshot? endTurn = null;
        foreach (RouteActionUi action in turn.Actions)
        {
            SolverOverlayActionSnapshot built = FromRouteAction(action, names);
            if (action.EndsTurn)
                endTurn = built;
            else
                actions.Add(built);
        }
        return new SolverOverlayTurnSnapshot(
            turn.Turn,
            turn.TurnStartChoices.Select(FormatStartChoice).ToArray(),
            actions,
            endTurn,
            turn.EnemyHpLost > 0 ? turn.EnemyHpLost : null,
            turn.HpLoss,
            turn.HpRecovered,
            turn.EnergyLeft ?? 0,
            turn.CombatEnded)
        {
            StartActionIndex = turn.StartActionIndex,
        };
    }

    private static string FormatStartChoice(TurnStartChoiceUi choice)
    {
        string effect = SolverPanelText.ChoiceEffect(choice.Effect);
        string separator = SolverText.IsEnglish ? ", " : "、";
        string cards = string.Join(separator,
            choice.Cards.Select(card => CombatDisplayNames.CardTitle(card.CardId, card.Upgrade)));
        string source = ResolveStartChoiceSource(choice);
        string colon = SolverText.IsEnglish ? ": " : "：";
        return $"{source}{colon}{effect} {cards}";
    }

    private static string ResolveStartChoiceSource(TurnStartChoiceUi choice)
    {
        string fallback = SolverText.Get("起手选择");
        if (choice.SourceId.Length == 0)
            return fallback;
        string title = choice.SourceKind switch
        {
            "relic" => CombatDisplayNames.RelicTitle(choice.SourceId),
            "power" => CombatDisplayNames.PowerTitle(choice.SourceId),
            "card" => CombatDisplayNames.CardTitle(choice.SourceId, choice.SourceUpgrade),
            "potion" => CombatDisplayNames.PotionTitle(choice.SourceId),
            _ => "",
        };
        // Unknown content must not surface a registry/native id in the pill.
        return title.Length == 0 || string.Equals(title, choice.SourceId, StringComparison.Ordinal)
            ? fallback
            : title;
    }

    private static SolverOverlayActionSnapshot FromRouteAction(RouteActionUi action, CombatDisplayNames names)
    {
        string title = action.Kind switch
        {
            "play_card" => action.CardId is { Length: > 0 } card
                ? CombatDisplayNames.CardTitle(card, action.Upgrade)
                : throw new InvalidDataException("路线 play_card 缺少 CardId。"),
            "use_potion" => action.PotionId is { Length: > 0 } potion
                ? CombatDisplayNames.PotionTitle(potion)
                : throw new InvalidDataException("路线 use_potion 缺少 PotionId。"),
            "end_turn" => SolverText.Get("结束回合"),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action.Kind, "未知路线动作类型。"),
        };
        string target = action.TargetKind switch
        {
            "enemy" => names.Enemy(action.TargetCombatId ?? 0,
                action.TargetMonsterId ?? "", action.TargetOrdinal),
            "player" => names.PlayerName,
            "osty" => names.OstyName,
            _ => "",
        };
        string[] kills = action.Kills
            .Select(kill => names.Enemy(kill.CombatId, kill.MonsterId, kill.Ordinal))
            .ToArray();
        string? choice = ChoiceText(action.Choices);
        string tooltip = Tooltip(title, target, choice, kills, action.Kind == "use_potion");
        return new SolverOverlayActionSnapshot(
            title,
            target,
            choice,
            [],
            kills,
            tooltip,
            Visual(action.CardType, action.Kind),
            action.ReplayCount)
        {
            Index = action.Index,
        };
    }

    private static SolverOverlayTurnSnapshot FromPreviewTurn(SearchTurnPreviewUi turn, CombatDisplayNames names)
    {
        List<SolverOverlayActionSnapshot> actions = [];
        SolverOverlayActionSnapshot? endTurn = null;
        int firstIndex = -1;
        int index = 0;
        foreach (SearchActionPreviewUi action in turn.Actions)
        {
            SolverOverlayActionSnapshot built = FromPreviewAction(action, names) with { Index = index };
            if (firstIndex < 0)
                firstIndex = index;
            if (action.Kind == "end_turn")
                endTurn = built;
            else
                actions.Add(built);
            index++;
        }
        return new SolverOverlayTurnSnapshot(
            turn.Turn,
            [],
            actions,
            endTurn,
            turn.EnemyHpLost > 0 ? turn.EnemyHpLost : null,
            turn.HpLost,
            turn.HpRecovered,
            turn.EnergyLeft,
            turn.CombatEnded)
        {
            StartActionIndex = Math.Max(0, firstIndex),
        };
    }

    private static SolverOverlayTurnSnapshot FromPreviewTurn(SearchCurrentTurnPreviewUi turn,
        CombatDisplayNames names)
    {
        List<SolverOverlayActionSnapshot> actions = [];
        SolverOverlayActionSnapshot? endTurn = null;
        int index = 0;
        foreach (SearchActionPreviewUi action in turn.Actions)
        {
            SolverOverlayActionSnapshot built = FromPreviewAction(action, names) with { Index = index };
            if (action.Kind == "end_turn")
                endTurn = built;
            else
                actions.Add(built);
            index++;
        }
        return new SolverOverlayTurnSnapshot(
            turn.Turn,
            [],
            actions,
            endTurn,
            turn.EnemyHpLost > 0 ? turn.EnemyHpLost : null,
            turn.HpLost,
            turn.HpRecovered,
            turn.EnergyLeft,
            turn.CombatEnded);
    }

    private static SolverOverlayActionSnapshot FromPreviewAction(SearchActionPreviewUi action,
        CombatDisplayNames names)
    {
        string title = action.Kind switch
        {
            "play_card" => action.CardId is { Length: > 0 } card
                ? CombatDisplayNames.CardTitle(card, action.Upgrade)
                : throw new InvalidDataException("路线 play_card 缺少 CardId。"),
            "use_potion" => action.PotionId is { Length: > 0 } potion
                ? CombatDisplayNames.PotionTitle(potion)
                : throw new InvalidDataException("路线 use_potion 缺少 PotionId。"),
            "end_turn" => SolverText.Get("结束回合"),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action.Kind, "未知路线动作类型。"),
        };
        string target = action.TargetKind switch
        {
            "enemy" => names.Enemy(action.TargetCombatId ?? 0,
                action.TargetMonsterId ?? "", action.TargetOrdinal),
            "player" => names.PlayerName,
            "osty" => names.OstyName,
            _ => "",
        };
        string[] kills = action.Kills
            .Select(kill => names.Enemy(kill.CombatId, kill.MonsterId, kill.Ordinal))
            .ToArray();
        string tooltip = Tooltip(title, target, null, kills, action.Kind == "use_potion");
        return new SolverOverlayActionSnapshot(
            title,
            target,
            null,
            [],
            kills,
            tooltip,
            Visual(action.CardType, action.Kind),
            action.ReplayCount);
    }

    private static string? ChoiceText(ChoiceCardUi[][] choices)
    {
        if (choices.Length == 0)
            return null;
        List<string> layers = [];
        foreach (ChoiceCardUi[] layer in choices)
        {
            layers.Add(layer.Length == 0
                ? SolverText.Get("不选")
                : SolverText.Format($"选 {string.Join(SolverText.Get("、"),
                    layer.Select(card => CombatDisplayNames.CardTitle(card.CardId, card.Upgrade)))}"));
        }
        return string.Join(" / ", layers);
    }

    private static string Tooltip(string title, string target, string? choice, string[] kills,
        bool potion)
        => title
            + (potion ? SolverText.Get("（药水）") : string.Empty)
            + (string.IsNullOrEmpty(target) ? string.Empty : $"→{target}")
            + (choice == null ? string.Empty : $"（{choice}）")
            + (kills.Length == 0
                ? string.Empty
                : SolverText.Format($"，击杀 {string.Join(SolverText.Get("、"), kills)}"));

    private static SolverOverlayActionVisualKind Visual(string? cardType, string kind)
    {
        if (kind == "use_potion")
            return SolverOverlayActionVisualKind.Potion;
        return cardType switch
        {
            "attack" => SolverOverlayActionVisualKind.Attack,
            "skill" => SolverOverlayActionVisualKind.Skill,
            "power" => SolverOverlayActionVisualKind.Power,
            "status" or "curse" => SolverOverlayActionVisualKind.Negative,
            _ => SolverOverlayActionVisualKind.Other,
        };
    }
}
