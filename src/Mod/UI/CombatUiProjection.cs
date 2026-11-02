// Route projection adapted from CombatSolver src/UI/SolverOverlaySnapshot.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// The data source is the Sts2Solver ReplayedRoute and coordinator result, not the old engine.
using Sts2Solver.Ai.Combat;
using Sts2Solver.Autoplay;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver;

/// <summary>Builds immutable UI records from already-captured route/search data on the main thread.</summary>
internal static class CombatUiProjection
{
    internal static CombatUiSnapshot Idle(SearchMemoryBudgetResolution budget)
        => new()
        {
            Phase = CombatUiPhase.Idle,
            Mode = SolverSettings.Current.Automation.Mode,
            AutomaticCalculationEnabled = SolverSettings.Current.Automation.Mode
                is SolverMode.AutoSearch or SolverMode.FullAuto,
            Memory = Memory(budget),
            Settings = Settings(budget),
        };

    internal static CombatUiSnapshot SessionStarted(string root, int turn, int playerHp, int playerMaxHp,
        SearchMemoryBudgetResolution budget)
        => new()
        {
            Phase = CombatUiPhase.WaitingTurn,
            Root = root,
            Turn = turn,
            PlayerHp = playerHp,
            PlayerMaxHp = playerMaxHp,
            Memory = Memory(budget),
            Settings = Settings(budget),
        };

    internal static SettingsUiSnapshot Settings(SearchMemoryBudgetResolution budget)
    {
        SearchThreadCountResolution threads = SearchThreadCount.Resolve();
        return new SettingsUiSnapshot
        {
            SearchThreads = threads.Threads,
            SearchThreadsDefault = SearchThreadCount.DefaultFor(threads.LogicalProcessors),
            MemoryBudgetBytes = budget.Bytes,
            MemoryBudgetDefaultBytes = SearchMemoryBudget.DefaultBytes(),
        };
    }

    internal static MemoryUiSnapshot Memory(SearchMemoryBudgetResolution budget)
        => new()
        {
            BudgetBytes = budget.Bytes,
            BudgetSource = budget.Source,
            ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
            WorkingSetBytes = Environment.WorkingSet,
            GcAvailableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            SampledAtMilliseconds = Environment.TickCount64,
        };

    internal static SearchUiSnapshot SearchStarted(int turn, string reason, SearchPerformanceTier tier,
        SearchPerformanceProfile profile, int searchThreads, long startedAtMilliseconds)
        => new()
        {
            Phase = SearchUiPhase.Running,
            Tier = tier,
            Turn = turn,
            Reason = reason,
            StartedAtMilliseconds = startedAtMilliseconds,
            SearchThreads = searchThreads,
            BudgetMilliseconds = checked((long)profile.BaselineWidth * profile.MillisecondsPerWidth),
            Status = "running",
        };

    internal static SearchUiSnapshot SearchFailed(string errorCode, int turn, string reason,
        SearchPerformanceTier tier, SearchPerformanceProfile profile, int searchThreads,
        long startedAtMilliseconds)
        => new()
        {
            Phase = SearchUiPhase.Completed,
            Tier = tier,
            Turn = turn,
            Reason = reason,
            StartedAtMilliseconds = startedAtMilliseconds,
            SearchThreads = searchThreads,
            ElapsedMilliseconds = Math.Max(0, Environment.TickCount64 - startedAtMilliseconds),
            BudgetMilliseconds = checked((long)profile.BaselineWidth * profile.MillisecondsPerWidth),
            Status = "failed",
            SearchStatus = "invalid",
            ErrorCode = errorCode,
            Victory = false,
        };

    internal static SearchUiSnapshot SearchCancelled(int turn, string reason,
        SearchPerformanceTier tier, SearchPerformanceProfile profile, int searchThreads,
        long startedAtMilliseconds, SearchMemberRunUi[] memberRuns)
        => new()
        {
            Phase = SearchUiPhase.Completed,
            Tier = tier,
            Turn = turn,
            Reason = reason,
            StartedAtMilliseconds = startedAtMilliseconds,
            SearchThreads = searchThreads,
            ElapsedMilliseconds = Math.Max(0, Environment.TickCount64 - startedAtMilliseconds),
            ExpandedNodes = memberRuns.Sum(static run => (long)run.ExpandedNodes),
            Transitions = memberRuns.Sum(static run => (long)run.Transitions),
            MemberRuns = memberRuns,
            BudgetMilliseconds = checked((long)profile.BaselineWidth * profile.MillisecondsPerWidth),
            Status = "cancelled",
            SearchStatus = "cancelled",
            ErrorCode = "cancelled",
            Victory = false,
        };

    internal static SearchUiSnapshot SearchFinished(CombatSearchResult outcome,
        SearchMemberRunUi[] memberRuns, int turn, string reason, SearchPerformanceTier tier,
        SearchPerformanceProfile profile, string? errorCode, long startedAtMilliseconds)
    {
        string status = outcome.Status == CombatSearchStatus.Cancelled
            ? "cancelled"
            : outcome.Beam != null ? "ok" : "failed";
        string? stopReason = null;
        string[] stops = outcome.Members
            .Where(static member => member.Ran && member.StopReason.Length > 0)
            .Select(static member => member.StopReason)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static stop => stop, StringComparer.Ordinal)
            .ToArray();
        if (stops.Length > 0)
            stopReason = string.Join(",", stops);
        string? boundaryCode = outcome.Members.Any(static member => member.StopReason == "time")
            ? "time"
            : outcome.Members.Any(static member => member.StopReason is "budget" or "max-turns")
                ? "node"
                : null;
        return new SearchUiSnapshot
        {
            Phase = SearchUiPhase.Completed,
            Tier = tier,
            Turn = turn,
            Reason = reason,
            StartedAtMilliseconds = startedAtMilliseconds,
            SearchThreads = outcome.SearchThreads,
            BudgetMilliseconds = outcome.PlannedBudgetMilliseconds > 0
                ? outcome.PlannedBudgetMilliseconds
                : checked((long)profile.BaselineWidth * profile.MillisecondsPerWidth),
            ElapsedMilliseconds = outcome.ElapsedMilliseconds,
            ExpandedNodes = outcome.TotalExpandedNodes,
            Transitions = memberRuns.Sum(static run => (long)run.Transitions),
            RouteActions = outcome.Beam?.BestRoute.Length ?? 0,
            Victory = outcome.Beam?.BestIsVictory ?? false,
            Status = status,
            SearchStatus = outcome.Status.ToString().ToLowerInvariant(),
            StopReason = stopReason,
            BoundaryCode = boundaryCode,
            ErrorCode = errorCode,
            SelectedMember = outcome.SelectedMember,
            MembersRun = outcome.MembersRun,
            MembersSkippedByTime = outcome.MembersSkippedByTime,
            SoftTimeRisk = outcome.SoftTimeRisk,
            MemoryBudgetExceeded = outcome.MemoryBudgetExceeded,
            SearchMemoryUsedBytes = outcome.SearchMemoryUsedBytes,
            SearchMemoryPeakBytes = outcome.SearchMemoryPeakBytes,
            WorkspaceOverflowParents = outcome.WorkspaceOverflowParents,
            SearchWorkspacePeakBytes = outcome.SearchWorkspacePeakBytes,
            SearchWorkspaceGrowablePeakBytes = outcome.SearchWorkspaceGrowablePeakBytes,
            ManagedHeapPeakBytes = outcome.ManagedHeapPeakBytes,
            WorkingSetPeakBytes = outcome.WorkingSetPeakBytes,
            Members = outcome.Members.Select(static (member, index) => new SearchMemberUi
            {
                Index = index,
                Width = member.Member.Width,
                Evaluation = member.Member.Evaluation.ToString(),
                Retention = member.Member.Retention.ToString(),
                Potions = member.Member.Potions.ToString(),
                Ran = member.Ran,
                SkippedReason = member.SkippedReason,
                StopReason = member.StopReason,
                ElapsedMilliseconds = member.ElapsedMilliseconds,
                ExpandedNodes = member.ExpandedNodes,
                Victory = member.Victory,
                Adopted = member.Adopted,
                MemoryPeakBytes = member.MemoryPeakBytes,
                ManagedHeapPeakBytes = member.ManagedHeapPeakBytes,
                WorkingSetPeakBytes = member.WorkingSetPeakBytes,
                HpLost = member.HpLost,
                PotionUses = member.PotionUses,
            }).ToArray(),
            MemberRuns = memberRuns,
        };
    }

    internal static RouteUiSnapshot Route(ReplayedRoute route, CombatDisplayNames names,
        int currentTurn, int currentActionIndex, int completedActions, bool reused)
        => Route(route.Actions, route.Rules, route.Victory, names, currentTurn,
            currentActionIndex, completedActions, reused, []);

    internal static RouteUiSnapshot Route(CombatRoutePlan plan, CombatDisplayNames names,
        int currentTurn, int currentActionIndex, int completedActions, bool reused)
        => Route(plan.Actions, plan.Rules, plan.Victory, names, currentTurn,
            currentActionIndex, completedActions, reused, plan.RootSetupChoices);

    private static RouteUiSnapshot Route(ReplayedAction[] routeActions, Rules rules, bool victory,
        CombatDisplayNames names, int currentTurn, int currentActionIndex, int completedActions,
        bool reused, PlannedChoice[] rootSetupChoices)
    {
        List<RouteTurnUi> turns = [];
        List<RouteActionUi>? turnActions = null;
        int turnNumber = 0;
        int startIndex = 0;
        int hpBefore = 0;
        int hpAfter = 0;
        int hpLoss = 0;
        int hpRecovered = 0;
        int enemyHpLost = 0;
        bool combatEnded = false;
        int? energyLeft = null;
        int rootTurn = routeActions.Length > 0 ? routeActions[0].Before.PlayerTurn : 0;
        TurnStartChoiceUi[] rootChoices = StartChoices(rootSetupChoices);
        for (int index = 0; index < routeActions.Length; index++)
        {
            ReplayedAction action = routeActions[index];
            if (turnActions == null || turnNumber != action.Before.PlayerTurn)
            {
                if (turnActions != null)
                {
                    turns.Add(new RouteTurnUi
                    {
                        Turn = turnNumber,
                        StartActionIndex = startIndex,
                        HpBefore = hpBefore,
                        HpAfter = hpAfter,
                        HpLoss = hpLoss,
                        HpRecovered = hpRecovered,
                        EnemyHpLost = enemyHpLost,
                        CombatEnded = combatEnded,
                        EnergyLeft = energyLeft,
                        TurnStartChoices = turnNumber == rootTurn ? rootChoices : [],
                        Actions = [.. turnActions],
                    });
                }
                turnActions = [];
                energyLeft = null;
                turnNumber = action.Before.PlayerTurn;
                startIndex = action.Index;
                hpBefore = action.Before.Player.CurrentHp;
                hpLoss = 0;
                hpRecovered = 0;
                enemyHpLost = 0;
                combatEnded = false;
            }
            hpAfter = action.After.Player.CurrentHp;
            hpLoss += Math.Max(0, action.After.Counter(Counter.CumulativeHpLost)
                - action.Before.Counter(Counter.CumulativeHpLost));
            hpRecovered += Math.Max(0, action.After.Counter(Counter.RecoveredHp)
                - action.Before.Counter(Counter.RecoveredHp));
            enemyHpLost += EnemyHpLost(action.Before, action.After);
            combatEnded = action.After.Outcome != Outcome.None;
            if (action.Action.Kind == PlayerActionKind.EndTurn)
                energyLeft = action.Before.Energy;
            turnActions.Add(Action(action, names, rules));
        }
        if (turnActions != null)
        {
            turns.Add(new RouteTurnUi
            {
                Turn = turnNumber,
                StartActionIndex = startIndex,
                HpBefore = hpBefore,
                HpAfter = hpAfter,
                HpLoss = hpLoss,
                HpRecovered = hpRecovered,
                EnemyHpLost = enemyHpLost,
                CombatEnded = combatEnded,
                EnergyLeft = energyLeft,
                TurnStartChoices = turnNumber == rootTurn ? rootChoices : [],
                Actions = [.. turnActions],
            });
        }
        return new RouteUiSnapshot
        {
            StartTurn = routeActions.Length > 0 ? routeActions[0].Before.PlayerTurn : 0,
            CurrentTurn = currentTurn,
            CurrentActionIndex = currentActionIndex,
            CompletedActions = completedActions,
            ActionCount = routeActions.Length,
            Victory = victory,
            Reused = reused,
            Turns = [.. turns],
        };
    }

    private static TurnStartChoiceUi[] StartChoices(PlannedChoice[] choices)
    {
        List<TurnStartChoiceUi> mapped = [];
        foreach (PlannedChoice choice in choices)
        {
            if (choice.Request.Effect == ChoiceEffect.ApplyKnowledgeCurse)
                continue;
            Card[] options = choice.OptionsAreGenerated ? choice.GeneratedOptions : choice.PileOptions;
            List<ChoiceCardUi> cards = [];
            foreach (int index in choice.SelectedIndexes())
            {
                Card card = options[index];
                cards.Add(new ChoiceCardUi
                {
                    CardId = Registry.Card(card.Id).NativeId,
                    Upgrade = card.Upgrade,
                });
            }
            mapped.Add(new TurnStartChoiceUi
            {
                Source = "起手选择",
                Effect = choice.Request.Effect.ToString(),
                Cards = [.. cards],
            });
        }
        return [.. mapped];
    }

    private static RouteActionUi Action(ReplayedAction action, CombatDisplayNames names, Rules rules)
    {
        PlayerAction kind = action.Action;
        string? cardId = null;
        string? cardType = null;
        int upgrade = 0;
        int replayCount = 0;
        string? potionId = null;
        if (kind.Kind == PlayerActionKind.PlayCard && kind.HandIndex < action.Before.HandCount)
        {
            Card card = action.Before.Cards[kind.HandIndex];
            cardId = Registry.Card(card.Id).NativeId;
            upgrade = card.Upgrade;
            replayCount = card.ReplayCount;
            cardType = CardTypeName(Kernel.ResolveCardType(action.Before, rules, card));
        }
        else if (kind.Kind == PlayerActionKind.UsePotion && kind.PotionSlot < action.Before.PotionSlotCount)
        {
            ushort stored = action.Before.Potions[kind.PotionSlot];
            if (stored != 0)
                potionId = Registry.Potion((ushort)(stored - 1)).NativeId;
        }
        string targetKind = "";
        uint? targetCombatId = null;
        string? targetMonsterId = null;
        int targetOrdinal = 0;
        if (kind.Target == Limits.PlayerOwner)
        {
            targetKind = "player";
        }
        else if (kind.Target == Limits.OstyOwner)
        {
            targetKind = "osty";
        }
        else if (kind.Target >= Limits.FirstEnemyOwner)
        {
            int enemy = kind.Target - Limits.FirstEnemyOwner;
            if (enemy < action.Before.EnemyCount)
            {
                targetKind = "enemy";
                targetCombatId = action.Before.Enemies[enemy].CombatId;
                targetMonsterId = Registry.Monster(action.Before.Enemies[enemy].MonsterId).NativeId;
                targetOrdinal = MonsterOrdinal(action.Before, enemy);
            }
        }
        return new RouteActionUi
        {
            Index = action.Index,
            Kind = kind.Kind switch
            {
                PlayerActionKind.PlayCard => "play_card",
                PlayerActionKind.UsePotion => "use_potion",
                PlayerActionKind.EndTurn => "end_turn",
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            },
            CardId = cardId,
            Upgrade = upgrade,
            CardType = cardType,
            PotionId = potionId,
            TargetCombatId = targetCombatId,
            TargetKind = targetKind,
            TargetMonsterId = targetMonsterId,
            TargetOrdinal = targetOrdinal,
            Choices = action.Choices.Select(Choice).ToArray(),
            Kills = Kills(action.Before, action.After),
            HpBefore = action.Before.Player.CurrentHp,
            HpAfter = action.After.Player.CurrentHp,
            EnergyBefore = action.Before.Energy,
            EnergyAfter = action.After.Energy,
            ReplayCount = replayCount,
            EndsTurn = kind.Kind == PlayerActionKind.EndTurn,
        };
    }

    private static ChoiceCardUi[] Choice(PlannedChoice choice)
    {
        List<ChoiceCardUi> cards = [];
        foreach (int index in choice.SelectedIndexes())
        {
            Card card = choice.OptionsAreGenerated
                ? choice.GeneratedOptions[index]
                : choice.PileOptions[index];
            cards.Add(new ChoiceCardUi
            {
                CardId = Registry.Card(card.Id).NativeId,
                Upgrade = card.Upgrade,
            });
        }
        return [.. cards];
    }

    private static string? CardTypeName(CardType type) => type switch
    {
        CardType.None => null,
        CardType.Attack => "attack",
        CardType.Skill => "skill",
        CardType.Power => "power",
        CardType.Status => "status",
        CardType.Curse => "curse",
        CardType.Quest => "quest",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未知卡牌类型。"),
    };

    private static int EnemyHpLost(in Battle before, in Battle after)
    {
        int lost = 0;
        int count = Math.Min(before.EnemyCount, after.EnemyCount);
        for (int index = 0; index < count; index++)
            lost += Math.Max(0, before.Enemies[index].Creature.CurrentHp
                - after.Enemies[index].Creature.CurrentHp);
        return lost;
    }

    private static int MonsterOrdinal(in Battle battle, int enemyIndex)
    {
        ushort monsterId = battle.Enemies[enemyIndex].MonsterId;
        int total = 0;
        int ordinal = 0;
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            if (battle.Enemies[index].MonsterId != monsterId)
                continue;
            total++;
            if (index == enemyIndex)
                ordinal = total;
        }
        return total > 1 ? ordinal : 0;
    }

    private static KillUi[] Kills(in Battle before, in Battle after)
    {
        List<KillUi> kills = [];
        int count = Math.Min(before.EnemyCount, after.EnemyCount);
        for (int index = 0; index < count; index++)
        {
            if (!before.Enemies[index].Creature.IsAlive || after.Enemies[index].Creature.IsAlive)
                continue;
            Enemy enemy = before.Enemies[index];
            int ordinal = 0;
            int sameType = 0;
            for (int other = 0; other < before.EnemyCount; other++)
            {
                if (before.Enemies[other].MonsterId != enemy.MonsterId)
                    continue;
                sameType++;
                if (other == index)
                    ordinal = sameType;
            }
            kills.Add(new KillUi
            {
                CombatId = enemy.CombatId,
                MonsterId = Registry.Monster(enemy.MonsterId).NativeId,
                Ordinal = sameType > 1 ? ordinal : 0,
            });
        }
        return [.. kills];
    }
}
