// In-process deployment of one replayed combat action without Godot. The card/potion/end-turn
// entry points and the enqueue-capture pattern are adapted from src/Mod/Combat/ActionDeployer.cs,
// which follows CombatSolver src/Runtime/SolverController.cs at a62f1850 (MIT, Copyright (c) 2026
// Torch1230 and contributors). The offline host drives async continuations with MainLoopContext and
// answers card selections through NativeChoiceSelector.
using System.Diagnostics;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using NativeCreature = MegaCrit.Sts2.Core.Entities.Creatures.Creature;

namespace Differential;

internal sealed class OfflineDeploymentException(string reason, string detail)
    : InvalidOperationException($"{reason}: {detail}")
{
    internal string Reason { get; } = reason;
}

/// <summary>
/// Executes one <see cref="ReplayedAction"/> through the original combat entry points and imports
/// the resulting native boundary with the same options the whole-combat deployment uses.
/// </summary>
internal sealed class NativeActionExecutor(
    MainLoopContext loop,
    CombatState combat,
    Player player,
    Rules rules,
    LiveCombatTracker tracker)
{
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan BoundaryTimeout = TimeSpan.FromSeconds(120);

    internal CombatState Combat => combat;
    internal Player Player => player;

    /// <summary>Deploys one action and returns the imported native boundary.</summary>
    internal Battle Execute(ReplayedAction planned)
    {
        Validate(planned);
        PlannedChoice[] explicitChoices = [.. planned.Choices.Where(static choice => !choice.Implicit)];
        NativeChoiceSelector selector = new(explicitChoices, player,
            $"turn={planned.Before.PlayerTurn}:action={planned.Index}:{planned.Action.Kind}");
        // Production releases PlannedNativeSelector before the next turn's setup page, so the
        // native SetupPlayerTurn task pauses at CardSelectCmd.Selector == null. An EndTurn action
        // therefore answers through LocalSelector for its whole execution: end-of-turn choices and
        // the next turn's setup choices take the native paused branch, while the same frozen plan
        // still supplies every selection. Other actions keep the ordinary selector.
        bool endTurn = planned.Action.Kind == PlayerActionKind.EndTurn;
        using IDisposable selectorScope = endTurn
            ? CardSelectCmd.PushSelector(selector, localOnly: true)
            : NativeChoiceSelector.Activate(selector);
        TerminalBattleCapture.Begin(() => TerminalBattleCapture.Import(
            CombatManager.Instance.DebugOnlyGetState(), tracker, rules, planned.After, player));
        try
        {
            switch (planned.Action.Kind)
            {
                case PlayerActionKind.PlayCard:
                    PlayCard(planned);
                    break;
                case PlayerActionKind.UsePotion:
                    UsePotion(planned);
                    break;
                case PlayerActionKind.EndTurn:
                    EndTurn(planned);
                    break;
                default:
                    throw new OfflineDeploymentException("action_kind",
                        $"未知动作 {planned.Action.Kind}");
            }
            WaitForBoundary(planned.After);
            if (planned.After.Outcome == Outcome.None || CombatManager.Instance.IsInProgress)
                selector.AssertConsumed();
            else if (selector.PendingCount > 0)
            {
                // The combat ended before a post-damage choice was requested; the terminal state
                // has no later boundary that could observe the unused choice.
            }
        }
        catch (NativeChoiceMismatchException error)
        {
            throw new OfflineDeploymentException("choice_mismatch", error.Message);
        }
        finally
        {
            TerminalBattleCapture.End();
            selectorScope.Dispose();
        }
        if (planned.After.Outcome != Outcome.None)
            return planned.After;
        CombatState? liveState = CombatManager.Instance.DebugOnlyGetState();
        if (liveState == null)
            throw new OfflineDeploymentException("live_missing", "动作完成后找不到战斗状态。");
        Player livePlayer = LocalContext.GetMe(liveState)
            ?? throw new OfflineDeploymentException("live_player_missing", "动作完成后找不到本地玩家。");
        combat = liveState;
        player = livePlayer;
        return tracker.ImportForComparison(liveState, livePlayer, rules, planned.After);
    }

    private void Validate(ReplayedAction planned)
    {
        if (!ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), combat))
            throw new OfflineDeploymentException("combat_changed", "部署途中战斗状态已更换。");
        if (!CombatManager.Instance.IsInProgress)
            throw new OfflineDeploymentException("combat_over", "战斗已结束。");
        PlayerCombatState? live = player.PlayerCombatState;
        if (live == null || combat.CurrentSide != CombatSide.Player || live.Phase != PlayerTurnPhase.Play)
        {
            throw new OfflineDeploymentException("turn_drift",
                $"当前不是玩家出牌阶段：side={combat.CurrentSide} phase={live?.Phase.ToString() ?? "-"}");
        }
        Battle expected = planned.Before;
        if (live.TurnNumber != expected.PlayerTurn)
        {
            throw new OfflineDeploymentException("turn_drift",
                $"回合 {live.TurnNumber} 与预测 {expected.PlayerTurn} 不符。");
        }
        if (combat.RoundNumber != expected.Round)
        {
            throw new OfflineDeploymentException("turn_drift",
                $"轮次 {combat.RoundNumber} 与预测 {expected.Round} 不符。");
        }
        if (live.Energy != expected.Energy)
        {
            throw new OfflineDeploymentException("energy_drift",
                $"能量 {live.Energy} 与预测 {expected.Energy} 不符。");
        }
        if (live.Stars != expected.Stars)
        {
            throw new OfflineDeploymentException("stars_drift",
                $"星能 {live.Stars} 与预测 {expected.Stars} 不符。");
        }
        if (live.Hand.Cards.Count != expected.HandCount)
        {
            throw new OfflineDeploymentException("hand_drift",
                $"手牌 {live.Hand.Cards.Count} 张与预测 {expected.HandCount} 张不符。");
        }
        if (player.PotionSlots.Count != expected.PotionSlotCount)
        {
            throw new OfflineDeploymentException("potion_slot_drift",
                $"药水槽 {player.PotionSlots.Count} 与预测 {expected.PotionSlotCount} 不符。");
        }
    }

    private void PlayCard(ReplayedAction planned)
    {
        PlayerCombatState live = player.PlayerCombatState
            ?? throw new OfflineDeploymentException("hand_drift", "玩家没有战斗牌堆。");
        int handIndex = planned.Action.HandIndex;
        if (handIndex >= live.Hand.Cards.Count || handIndex >= planned.Before.HandCount)
        {
            throw new OfflineDeploymentException("card_missing",
                $"手牌位置 {handIndex} 越界（n={live.Hand.Cards.Count}）。");
        }
        CardModel card = live.Hand.Cards[handIndex];
        Card predicted = planned.Before.Cards[handIndex];
        CardIdentity expected = CardIdentity.From(in predicted);
        CardIdentity actual = CardIdentity.From(card);
        if (expected != actual)
        {
            throw new OfflineDeploymentException("card_mismatch",
                $"手牌 {handIndex} 是 {actual}，预测 {expected}。");
        }
        NativeCreature? target = ResolveTarget(planned.Before, planned.Action.Target);
        if (!card.CanPlayTargeting(target))
        {
            card.CanPlay(out UnplayableReason reason, out AbstractModel? preventer);
            throw new OfflineDeploymentException("card_unplayable",
                $"卡 {actual} 不能打到目标：target_valid={card.IsValidTarget(target)} " +
                $"reason={reason} preventer={preventer?.Id.Entry ?? "-"}");
        }
        GameAction action = EnqueueAndCapture(
            candidate => candidate is PlayCardAction playCard
                && ReferenceEquals(playCard.Player, player)
                && ReferenceEquals(playCard.NetCombatCard.ToCardModelOrNull(), card),
            () =>
            {
                if (!card.TryManualPlay(target))
                {
                    throw new OfflineDeploymentException("card_unplayable",
                        $"卡 {actual} 在入队时失去可用状态。");
                }
            });
        AwaitAction(action, "play_card");
    }

    private void UsePotion(ReplayedAction planned)
    {
        int slot = planned.Action.PotionSlot;
        if (slot >= player.PotionSlots.Count || slot >= planned.Before.PotionSlotCount)
        {
            throw new OfflineDeploymentException("potion_missing",
                $"药水槽 {slot} 越界（n={player.PotionSlots.Count}）。");
        }
        PotionModel? potion = player.GetPotionAtSlotIndex(slot);
        ushort stored = planned.Before.Potions[slot];
        if (potion == null || stored == 0)
        {
            throw new OfflineDeploymentException("potion_missing",
                $"药水槽 {slot} 是空的，预测 {PredictedPotionName(stored)}。");
        }
        string expectedId = Registry.Potion((ushort)(stored - 1)).NativeId;
        if (!string.Equals(potion.Id.Entry, expectedId, StringComparison.Ordinal))
        {
            throw new OfflineDeploymentException("potion_mismatch",
                $"药水槽 {slot} 是 {potion.Id.Entry}，预测 {expectedId}。");
        }
        NativeCreature? target = ResolveTarget(planned.Before, planned.Action.Target);
        if (target == null && potion.IsValidTarget(player.Creature))
            target = player.Creature;
        if (!potion.IsValidTarget(target))
        {
            throw new OfflineDeploymentException("potion_target",
                $"药水 {expectedId} 的目标 {planned.Action.Target} 无效（type={potion.TargetType}）。");
        }
        GameAction action = EnqueueAndCapture(
            candidate => candidate is UsePotionAction usePotion
                && ReferenceEquals(usePotion.Player, player)
                && usePotion.PotionIndex == (uint)slot,
            () => potion.EnqueueManualUse(target));
        AwaitAction(action, "use_potion");
    }

    private void EndTurn(ReplayedAction planned)
    {
        int turn = player.PlayerCombatState?.TurnNumber
            ?? throw new OfflineDeploymentException("turn_drift", "玩家没有战斗牌堆。");
        GameAction action = EnqueueAndCapture(
            candidate => candidate is EndPlayerTurnAction endTurn
                && endTurn.OwnerId == player.NetId,
            () =>
            {
                CombatManager.Instance.OnEndedTurnLocally();
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                    new EndPlayerTurnAction(player, turn));
            });
        AwaitAction(action, "end_turn");
    }

    /// <summary>Target owner byte to the live creature, checking its CombatId and vitals.</summary>
    private NativeCreature? ResolveTarget(in Battle expected, byte owner)
    {
        if (owner == Limits.NoOwner)
            return null;
        if (owner == Limits.PlayerOwner)
            return player.Creature;
        if (owner == Limits.OstyOwner)
        {
            return player.Osty
                ?? throw new OfflineDeploymentException("target_missing", "预测有 Osty，但原生没有。");
        }
        int enemyIndex = owner - Limits.FirstEnemyOwner;
        if (enemyIndex < 0 || enemyIndex >= expected.EnemyCount)
        {
            throw new OfflineDeploymentException("target_missing",
                $"目标 owner={owner} 超出预测敌人表。");
        }
        Enemy predicted = expected.Enemies[enemyIndex];
        NativeCreature? target = combat.GetCreature(predicted.CombatId);
        if (target == null || !target.IsAlive)
        {
            throw new OfflineDeploymentException("target_missing",
                $"目标 CombatId={predicted.CombatId} 不在原生战斗中或已死亡。");
        }
        return target;
    }

    private GameAction EnqueueAndCapture(Func<GameAction, bool> matches, Action enqueue)
    {
        TaskCompletionSource<GameAction> captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ActionExecutor executor = RunManager.Instance.ActionExecutor;
        void OnBeforeActionExecuted(GameAction action)
        {
            if (matches(action))
                captured.TrySetResult(action);
        }
        executor.BeforeActionExecuted += OnBeforeActionExecuted;
        try
        {
            enqueue();
            try
            {
                loop.RunUntilCompleted(captured.Task, ActionTimeout, "等待动作入队");
            }
            catch (TimeoutException)
            {
                throw new OfflineDeploymentException("action_not_enqueued",
                    "动作已请求但没有进入原版动作队列。");
            }
            return captured.Task.GetAwaiter().GetResult();
        }
        finally
        {
            executor.BeforeActionExecuted -= OnBeforeActionExecuted;
        }
    }

    private void AwaitAction(GameAction action, string kind)
    {
        loop.RunUntilCompleted(action.CompletionTask, ActionTimeout, $"等待 {kind}");
        if (action.Exception != null)
        {
            throw new OfflineDeploymentException("action_failed",
                $"{kind} 抛出 {action.Exception.GetBaseException().GetType().Name}: " +
                action.Exception.GetBaseException().Message);
        }
        if (action.State == GameActionState.Canceled)
            throw new OfflineDeploymentException("action_canceled", $"{kind} 被原版取消。");
    }

    private void WaitForBoundary(in Battle expected)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (true)
        {
            CombatState? live = CombatManager.Instance.DebugOnlyGetState();
            bool inProgress = CombatManager.Instance.IsInProgress;
            if (live == null || !inProgress)
            {
                if (expected.Outcome != Outcome.None)
                {
                    WaitForQueueIdle(watch);
                    return;
                }
                throw new OfflineDeploymentException("combat_ended",
                    $"预测还在进行，原生战斗却已结束（turn={expected.PlayerTurn}）。");
            }
            if (expected.Outcome != Outcome.None)
            {
                loop.Pump(TimeSpan.FromMilliseconds(5));
                if (watch.Elapsed > BoundaryTimeout)
                {
                    throw new OfflineDeploymentException("boundary_timeout",
                        $"预测已结束，但原生战斗仍在进行（turn={expected.PlayerTurn}）。");
                }
                continue;
            }
            Player? current = LocalContext.GetMe(live);
            if (current?.PlayerCombatState is { } state
                && live.CurrentSide == CombatSide.Player
                && state.Phase == PlayerTurnPhase.Play
                && state.TurnNumber == expected.PlayerTurn
                && RunManager.Instance.ActionExecutor.FinishedExecutingActions().IsCompleted)
            {
                return;
            }
            if (watch.Elapsed > BoundaryTimeout)
            {
                throw new OfflineDeploymentException("boundary_timeout",
                    $"等待预测边界 turn={expected.PlayerTurn} phase={expected.Phase} 超时。" +
                    $"live_side={live.CurrentSide} live_phase={current?.PlayerCombatState?.Phase} " +
                    $"live_turn={current?.PlayerCombatState?.TurnNumber} in_progress={inProgress}");
            }
            loop.Pump(TimeSpan.FromMilliseconds(5));
        }
    }

    private void WaitForQueueIdle(Stopwatch watch)
    {
        _ = watch;
        Task finished = RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        loop.RunUntilCompleted(finished, BoundaryTimeout, "战斗结束清理");
    }

    private static string PredictedPotionName(ushort stored)
        => stored == 0 ? "-" : Registry.Potion((ushort)(stored - 1)).NativeId;

    private readonly record struct CardIdentity(string Id, int Upgrade)
    {
        internal static CardIdentity From(in Card card)
            => new(Registry.Card(card.Id).NativeId, card.Upgrade);

        internal static CardIdentity From(CardModel card)
            => new(card.Id.Entry, card.CurrentUpgradeLevel);
    }
}
