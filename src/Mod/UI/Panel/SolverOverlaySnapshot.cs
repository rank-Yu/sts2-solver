// Presentation projection adapted from CombatSolver src/UI/SolverOverlaySnapshot.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors). The source is Sts2Solver's
// CombatUiSnapshot; card, potion, choice and kill names are resolved here on the UI thread so the
// published snapshot only carries stable ids and numbers.
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

internal sealed record SolverOverlaySnapshot(
    int ProjectedBattlePotionCount,
    int ProjectedBattleHpLost,
    bool ProjectedBattleHpLossKnown,
    long ReviewedWorldlines,
    string HpOutcomeText,
    int RouteHpRecovered,
    bool OnlyDeathRoutesFound,
    IReadOnlyList<SolverOverlayTurnSnapshot> Turns)
{

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
        int projectedLost = snapshot.Result?.ProjectedBattleHpLost
            ?? (search?.RoutePreview is { } speculative
                ? speculative.Turns.Sum(static turn => turn.HpLost)
                : 0);
        bool projectedKnown = snapshot.Result?.CombatEndedTurn != null
            || search?.RoutePreview is { } previewKnown && previewKnown.Victory
            || snapshot.Route is { Victory: true };
        int recovered = turns.Sum(static turn => turn.HpRecovered);
        int potions = snapshot.Result?.ProjectedBattlePotionCount
            ?? search?.RoutePreview?.ProjectedBattlePotionCount
            ?? 0;
        bool onlyDeath = snapshot.Result?.OnlyDeathRoutes ?? search?.RoutePreview?.OnlyDeathRoutes ?? false;

        string hpOutcome = !projectedKnown
            ? SolverText.Get("预计战损 未知")
            : projectedLost > 0 || snapshot.Result?.BattleHpLostSoFar > 0
                ? SolverText.Format(
                    $"本局扣血  已 {snapshot.Result?.BattleHpLostSoFar ?? 0}    预计 {projectedLost} HP")
                : SolverText.Get("本局扣血  0 HP");
        return new SolverOverlaySnapshot(
            potions,
            projectedLost,
            projectedKnown,
            snapshot.ReviewedWorldlines,
            hpOutcome,
            recovered,
            onlyDeath,
            turns);
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
            [],
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
