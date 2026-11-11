// Main-thread deployment of one replayed action. Card/potion/end-turn entry points and the
// enqueue-capture pattern are adapted from CombatSolver src/Runtime/SolverController.cs
// DeployCurrentTurn/EnqueueAndCaptureActionAsync at a62f1850 (MIT, Copyright (c) 2026 Torch1230 and
// contributors). Choices use the automated ICardSelector surface instead of the visible-screen
// driver, so headless and visible runs take the same card-selection path.
using System.Diagnostics;
using Godot;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Import;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using NativeCreature = MegaCrit.Sts2.Core.Entities.Creatures.Creature;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace Sts2Solver;

internal sealed class DeploymentFailureException(string reason, string detail)
    : InvalidOperationException($"{reason}: {detail}")
{
    internal string Reason { get; } = reason;
}

internal sealed class ActionDeployer(
    NGame host,
    CombatState combat,
    Player player,
    Rules rules,
    LiveCombatTracker tracker)
{
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan BoundaryTimeout = TimeSpan.FromSeconds(180);

    // Set while an EndTurn deployment keeps its selector for the enemy turn; main thread only.
    private static Action? _releaseEndTurnSelector;

    /// <summary>
    /// Releases the EndTurn deployment selector at the next turn's setup interception, after every
    /// enemy-turn choice has been asked. No-op when no EndTurn deployment is waiting.
    /// </summary>
    internal static void ReleaseEndTurnSelectorForSetup()
    {
        Action? release = _releaseEndTurnSelector;
        _releaseEndTurnSelector = null;
        release?.Invoke();
    }

    internal NGame Host => host;

    internal CombatState Combat => combat;

    internal Player Player => player;

    /// <summary>Executes one replayed action and waits for its predicted boundary.</summary>
    internal async Task<Battle> ExecuteAsync(ReplayedAction planned, CancellationToken token)
    {
        Validate(planned);
        PlannedChoice[] explicitChoices = [.. planned.Choices.Where(static choice => !choice.Implicit)];
#if STS2_SOLVER_TESTING
        if (planned.Action.Kind == PlayerActionKind.EndTurn
            && CombatControllerTestHooks.ForceChoicePlanUnderflowOnce)
        {
            CombatControllerTestHooks.ForceChoicePlanUnderflowOnce = false;
            explicitChoices = [];
        }
#endif
        PlannedNativeSelector selector = new(explicitChoices, player,
            $"turn={planned.Before.PlayerTurn}:action={planned.Index}:{planned.Action.Kind}");
        IDisposable? selectorScope = PlannedNativeSelector.Activate(selector);
        bool endTurnSelectorReleased = false;
        Action? endTurnRelease = null;
        try
        {
            switch (planned.Action.Kind)
            {
                case PlayerActionKind.PlayCard:
                    await PlayCardAsync(planned, token);
                    break;
                case PlayerActionKind.UsePotion:
                    await UsePotionAsync(planned, token);
                    break;
                case PlayerActionKind.EndTurn:
                    await EndTurnAsync(planned, token);
                    break;
                default:
                    throw new DeploymentFailureException("action_kind",
                        $"未知动作 {planned.Action.Kind}");
            }
            // The EndTurn action itself has completed, but the enemy turn has not run yet and can
            // still ask planned choices (KnowledgeDemon CURSE_OF_KNOWLEDGE). When the next turn may
            // open a visible setup page, the selector therefore stays active through the enemy turn
            // and is released by the setup interception itself: the native gate requires
            // Selector == null, and the setup transaction (not this route) answers the next
            // turn's PlayerTurnStart choices.
            if (planned.Action.Kind == PlayerActionKind.EndTurn
                && PlayerTurnSetupRuntime.MayNeedNextTurnSetup(player))
            {
                endTurnRelease = () =>
                {
                    selectorScope?.Dispose();
                    selectorScope = null;
                    endTurnSelectorReleased = true;
                    ModLog.Info(
                        $"[Sts2Solver] DEPLOY_END_TURN_SELECTOR_RELEASED turn={planned.Before.PlayerTurn} " +
                        $"next={planned.Before.PlayerTurn + 1} pending_choices={selector.PendingCount}");
                };
                _releaseEndTurnSelector = endTurnRelease;
            }
            await WaitForBoundaryAsync(planned.After, token, () => selector.FailureDetail);
            if (selector.FailureDetail is { } mismatch)
                throw new DeploymentFailureException("choice_mismatch", mismatch);
            // Native selectors return an empty selection without asking once the combat is over or
            // ending (CardSelectCmd.FromHand), so an attack that kills the last enemy can leave a
            // planned post-damage choice unrequested. The predicted action is terminal, its choice
            // has no observable effect, and no later boundary compares it; do not turn that into a
            // deployment failure.
            if (endTurnSelectorReleased)
            {
                if (selector.PendingCount > 0)
                    ModLog.Info(
                        $"[Sts2Solver] DEPLOY_CHOICE_SUPERSEDED turn={planned.Before.PlayerTurn} " +
                        $"index={planned.Index} action={planned.Action} pending={selector.PendingCount}");
            }
            else if (planned.After.Outcome == Outcome.None || CombatManager.Instance.IsInProgress)
            {
                if (selector.UnconsumedDetail() is { } unconsumed)
                    throw new DeploymentFailureException("choice_mismatch", unconsumed);
            }
            else if (selector.PendingCount > 0)
            {
                ModLog.Info(
                    $"[Sts2Solver] DEPLOY_CHOICE_SKIPPED_TERMINAL turn={planned.Before.PlayerTurn} " +
                    $"index={planned.Index} action={planned.Action} pending={selector.PendingCount}");
            }
        }
        finally
        {
            if (endTurnRelease != null && ReferenceEquals(_releaseEndTurnSelector, endTurnRelease))
                _releaseEndTurnSelector = null;
            selectorScope?.Dispose();
        }
        if (planned.After.Outcome != Outcome.None)
        {
            ModLog.Info(
                $"[Sts2Solver] DEPLOY_TERMINAL turn={planned.Before.PlayerTurn} " +
                $"index={planned.Index} action={planned.Action} outcome={planned.After.Outcome}");
            return planned.After;
        }
        await SettleAsync(planned, token);
        CombatState? liveState = CombatManager.Instance.DebugOnlyGetState();
        if (liveState == null)
            throw new DeploymentFailureException("live_missing", "动作完成后找不到战斗状态。");
        Player livePlayer = LocalContext.GetMe(liveState)
            ?? throw new DeploymentFailureException("live_player_missing", "动作完成后找不到本地玩家。");
        combat = liveState;
        player = livePlayer;
        static DeploymentFailureException ImportFailure(string kind, string detail)
            => new(kind, detail);
        try
        {
            return tracker.ImportForComparison(liveState, livePlayer, rules, planned.After);
        }
        catch (UnsupportedImportException error)
        { throw ImportFailure("import_failed", $"reason={error.Reason} {error.Detail}"); }
        catch (UnsupportedException error)
        { throw ImportFailure("import_unsupported", error.Message); }
        catch (CapacityException error)
        { throw ImportFailure("import_capacity", $"region={error.Region} {error.Message}"); }
        catch (InvalidOperationException error)
        { throw ImportFailure("import_invalid_operation", error.Message); }
    }

    /// <summary>Validates the live turn, hand, potion and target against the predicted state.</summary>
    internal void Validate(ReplayedAction planned)
    {
        if (!NGame.IsMainThread())
            throw new DeploymentFailureException("not_main_thread", "部署必须在游戏主线程。");
        if (!ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), combat))
            throw new DeploymentFailureException("combat_changed", "部署途中战斗状态已更换。");
        if (!CombatManager.Instance.IsInProgress)
            throw new DeploymentFailureException("combat_over", "战斗已结束。");
        PlayerCombatState? live = player.PlayerCombatState;
        if (live == null || combat.CurrentSide != CombatSide.Player || live.Phase != PlayerTurnPhase.Play)
        {
            throw new DeploymentFailureException("turn_drift",
                $"当前不是玩家出牌阶段：side={combat.CurrentSide} phase={live?.Phase.ToString() ?? "-"}");
        }
        Battle expected = planned.Before;
        if (live.TurnNumber != expected.PlayerTurn)
        {
            throw new DeploymentFailureException("turn_drift",
                $"回合 {live.TurnNumber} 与预测 {expected.PlayerTurn} 不符。");
        }
        if (combat.RoundNumber != expected.Round)
        {
            throw new DeploymentFailureException("turn_drift",
                $"轮次 {combat.RoundNumber} 与预测 {expected.Round} 不符。");
        }
        if (live.Energy != expected.Energy)
        {
            throw new DeploymentFailureException("energy_drift",
                $"能量 {live.Energy} 与预测 {expected.Energy} 不符。");
        }
        if (live.Stars != expected.Stars)
        {
            throw new DeploymentFailureException("stars_drift",
                $"星能 {live.Stars} 与预测 {expected.Stars} 不符。");
        }
        if (live.Hand.Cards.Count != expected.HandCount)
        {
            throw new DeploymentFailureException("hand_drift",
                $"手牌 {live.Hand.Cards.Count} 张与预测 {expected.HandCount} 张不符。");
        }
        if (player.PotionSlots.Count != expected.PotionSlotCount)
        {
            throw new DeploymentFailureException("potion_slot_drift",
                $"药水槽 {player.PotionSlots.Count} 与预测 {expected.PotionSlotCount} 不符。");
        }
    }

    private async Task PlayCardAsync(ReplayedAction planned, CancellationToken token)
    {
        PlayerCombatState live = player.PlayerCombatState
            ?? throw new DeploymentFailureException("hand_drift", "玩家没有战斗牌堆。");
        int handIndex = planned.Action.HandIndex;
        if (handIndex >= live.Hand.Cards.Count || handIndex >= planned.Before.HandCount)
        {
            throw new DeploymentFailureException("card_missing",
                $"手牌位置 {handIndex} 越界（n={live.Hand.Cards.Count}）。");
        }
        CardModel card = live.Hand.Cards[handIndex];
        Card predicted = planned.Before.Cards[handIndex];
        NativeCardIdentity expected = NativeCardIdentity.From(predicted);
        NativeCardIdentity actual = NativeCardIdentity.From(card);
        if (expected != actual)
        {
            throw new DeploymentFailureException("card_mismatch",
                $"手牌 {handIndex} 是 {actual}，预测 {expected}。");
        }
        NativeCreature? target = ResolveTarget(planned.Before, planned.Action.Target);
        if (!card.CanPlayTargeting(target))
        {
            card.CanPlay(out UnplayableReason reason, out AbstractModel? preventer);
            throw new DeploymentFailureException("card_unplayable",
                $"卡 {actual} 不能打到目标：target_valid={card.IsValidTarget(target)} " +
                $"reason={reason} preventer={preventer?.Id.Entry ?? "-"}");
        }
        GameAction action = await EnqueueAndCaptureAsync(
            candidate => candidate is PlayCardAction playCard
                && ReferenceEquals(playCard.Player, player)
                && ReferenceEquals(playCard.NetCombatCard.ToCardModelOrNull(), card),
            () =>
            {
                if (!card.TryManualPlay(target))
                    throw new DeploymentFailureException("card_unplayable",
                        $"卡 {actual} 在入队时失去可用状态。");
            }, token);
        await AwaitActionAsync(action, "play_card");
    }

    private async Task UsePotionAsync(ReplayedAction planned, CancellationToken token)
    {
        int slot = planned.Action.PotionSlot;
        if (slot >= player.PotionSlots.Count || slot >= planned.Before.PotionSlotCount)
        {
            throw new DeploymentFailureException("potion_missing",
                $"药水槽 {slot} 越界（n={player.PotionSlots.Count}）。");
        }
        PotionModel? potion = player.GetPotionAtSlotIndex(slot);
        ushort stored = planned.Before.Potions[slot];
        if (potion == null || stored == 0)
        {
            throw new DeploymentFailureException("potion_missing",
                $"药水槽 {slot} 是空的，预测 {PredictedPotionName(stored)}。");
        }
        string expectedId = Registry.Potion((ushort)(stored - 1)).NativeId;
        if (!string.Equals(potion.Id.Entry, expectedId, StringComparison.Ordinal))
        {
            throw new DeploymentFailureException("potion_mismatch",
                $"药水槽 {slot} 是 {potion.Id.Entry}，预测 {expectedId}。");
        }
        NativeCreature? target = ResolveTarget(planned.Before, planned.Action.Target);
        // PotionModel.EnqueueManualUse substitutes the owner for an untargeted self potion; mirror
        // that before validating, otherwise a Self potion with Target=NoOwner looks invalid.
        if (target == null && potion.IsValidTarget(player.Creature))
            target = player.Creature;
        if (!potion.IsValidTarget(target))
        {
            throw new DeploymentFailureException("potion_target",
                $"药水 {expectedId} 的目标 {planned.Action.Target} 无效" +
                $"（type={potion.TargetType}）。");
        }
        GameAction action = await EnqueueAndCaptureAsync(
            candidate => candidate is UsePotionAction usePotion
                && ReferenceEquals(usePotion.Player, player)
                && usePotion.PotionIndex == (uint)slot,
            () => potion.EnqueueManualUse(target), token);
        await AwaitActionAsync(action, "use_potion");
    }

    private async Task EndTurnAsync(ReplayedAction planned, CancellationToken token)
    {
        int turn = player.PlayerCombatState?.TurnNumber
            ?? throw new DeploymentFailureException("turn_drift", "玩家没有战斗牌堆。");
        GameAction action = await EnqueueAndCaptureAsync(
            candidate => candidate is EndPlayerTurnAction endTurn
                && ReferenceEquals(endTurn, candidate)
                && endTurn.OwnerId == player.NetId,
            () =>
            {
                CombatManager.Instance.OnEndedTurnLocally();
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                    new EndPlayerTurnAction(player, turn));
            }, token);
        await AwaitActionAsync(action, "end_turn");
    }

    /// <summary>Target owner byte to the live creature, checking its CombatId and vitals.</summary>
    internal NativeCreature? ResolveTarget(in Battle expected, byte owner)
    {
        if (owner == Limits.NoOwner)
            return null;
        if (owner == Limits.PlayerOwner)
            return player.Creature;
        if (owner == Limits.OstyOwner)
        {
            return player.Osty
                ?? throw new DeploymentFailureException("target_missing", "预测有 Osty，但原生没有。");
        }
        int enemyIndex = owner - Limits.FirstEnemyOwner;
        if (enemyIndex < 0 || enemyIndex >= expected.EnemyCount)
        {
            throw new DeploymentFailureException("target_missing",
                $"目标 owner={owner} 超出预测敌人表。");
        }
        Enemy predicted = expected.Enemies[enemyIndex];
        NativeCreature? target = combat.GetCreature(predicted.CombatId);
        if (target == null || !target.IsAlive)
        {
            throw new DeploymentFailureException("target_missing",
                $"目标 CombatId={predicted.CombatId} 不在原生战斗中或已死亡。");
        }
        return target;
    }

    private async Task<GameAction> EnqueueAndCaptureAsync(Func<GameAction, bool> matches,
        Action enqueue, CancellationToken token)
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
            return await captured.Task.WaitAsync(ActionTimeout, token);
        }
        catch (TimeoutException)
        {
            throw new DeploymentFailureException("action_not_enqueued",
                "动作已请求但没有进入原版动作队列。");
        }
        finally
        {
            executor.BeforeActionExecuted -= OnBeforeActionExecuted;
        }
    }

    private async Task AwaitActionAsync(GameAction action, string kind)
    {
        await action.CompletionTask.WaitAsync(ActionTimeout);
        if (action.Exception != null)
        {
            throw new DeploymentFailureException("action_failed",
                $"{kind} 抛出 {action.Exception.GetBaseException().GetType().Name}: " +
                action.Exception.GetBaseException().Message);
        }
        if (action.State == GameActionState.Canceled)
            throw new DeploymentFailureException("action_canceled", $"{kind} 被原版取消。");
    }

    /// <summary>Waits until the live combat reaches the predicted stable boundary.</summary>
    private async Task WaitForBoundaryAsync(Battle expected, CancellationToken token,
        Func<string?>? choiceFailure = null)
    {
        Stopwatch watch = Stopwatch.StartNew();
        Stopwatch? mismatchWatch = null;
        while (true)
        {
            if (choiceFailure?.Invoke() is { } mismatch)
            {
                mismatchWatch ??= Stopwatch.StartNew();
                if (mismatchWatch.Elapsed > TimeSpan.FromSeconds(30))
                {
                    throw new DeploymentFailureException("choice_mismatch", mismatch);
                }
            }
            token.ThrowIfCancellationRequested();
            CombatState? live = CombatManager.Instance.DebugOnlyGetState();
            if (live == null || !CombatManager.Instance.IsInProgress)
            {
                if (expected.Outcome != Outcome.None)
                    return;
                throw new DeploymentFailureException("combat_ended",
                    $"预测还在进行，原生战斗却已结束（turn={expected.PlayerTurn}）。");
            }
            if (expected.Outcome != Outcome.None)
            {
                await CombatFrameWaiter.NextFrameAsync(host);
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
                if (choiceFailure?.Invoke() is { } failureDetail)
                    throw new DeploymentFailureException("choice_mismatch", failureDetail);
                throw new DeploymentFailureException("boundary_timeout",
                    $"等待预测边界 turn={expected.PlayerTurn} phase={expected.Phase} 超时。" +
                    $"live_side={live.CurrentSide} live_phase={current?.PlayerCombatState?.Phase} " +
                    $"live_turn={current?.PlayerCombatState?.TurnNumber} in_progress={CombatManager.Instance.IsInProgress}");
            }
            await CombatFrameWaiter.NextFrameAsync(host);
        }
    }

    /// <summary>
    /// Lets action visuals/timers finish. Turn starts use the existing 1.5s settle; mid-turn actions
    /// use a shorter wait, and instant combat speed shortens it further.
    /// </summary>
    private async Task SettleAsync(ReplayedAction planned, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Task finished = RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        if (!finished.IsCompleted)
            await finished.WaitAsync(TimeSpan.FromSeconds(30));
        if (planned.After.Outcome == Outcome.None
            && planned.After.PlayerTurn > planned.Before.PlayerTurn)
        {
            await CombatFrameWaiter.SettleAsync(host);
            return;
        }
        bool instant = SaveManager.Instance.PrefsSave.FastMode == FastModeType.Instant;
        double seconds = instant ? 0.05 : 0.2;
        await host.ToSignal(host.GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
        await CombatFrameWaiter.NextFrameAsync(host);
    }

    private static string PredictedPotionName(ushort stored)
        => stored == 0 ? "-" : Registry.Potion((ushort)(stored - 1)).NativeId;
}
