using System.Runtime.CompilerServices;

namespace Sts2Solver.Simulation.Combat;

public static partial class Kernel
{
    /// <summary>
    /// Executes one player action to the next stable boundary, mirroring the action branches of
    /// <c>CombatBeamSolver.Replay</c>. On <see cref="StepResult.NeedsChoice"/> the battle is
    /// partial and must be discarded.
    /// </summary>
    public static StepResult Step(ref Battle battle, in PlayerAction action, Rules rules,
        out ChoiceRequest request)
        => Step(ref battle, action, rules, out request, out _, out _);

    /// <summary>
    /// As <see cref="Step(ref Battle, in PlayerAction, Rules, out ChoiceRequest)"/>, also
    /// reporting whether a mirrored path recorded a prediction risk history entry during the action
    /// (used by the routing trait's pure input; transient, not battle state).
    /// </summary>
    public static StepResult Step(ref Battle battle, in PlayerAction action, Rules rules,
        out ChoiceRequest request, out bool historyRisk)
        => Step(ref battle, action, rules, out request, out historyRisk, out _);

    /// <summary>
    /// As <see cref="Step(ref Battle, in PlayerAction, Rules, out ChoiceRequest)"/>, also reporting
    /// whether the action reached the enemy-side turn-end pass. Route replay reads this to reproduce
    /// the native final turn counter; the search path ignores it.
    /// </summary>
    internal static StepResult Step(ref Battle battle, in PlayerAction action, Rules rules,
        out ChoiceRequest request, out bool historyRisk, out bool enemySideTurnEndReached)
        => Step(ref battle, action, rules, out request, out historyRisk, out enemySideTurnEndReached,
            out _);

    /// <summary>
    /// As the six-argument overload, also reporting the max HP requested by BrightestFlame plays in
    /// this action. The search's per-route cap reads it; ordinary callers ignore it.
    /// </summary>
    internal static StepResult Step(ref Battle battle, in PlayerAction action, Rules rules,
        out ChoiceRequest request, out bool historyRisk, out bool enemySideTurnEndReached,
        out int brightestFlameMaxHpSpent)
    {
        if (!IsInProgress(battle))
            throw new InvalidOperationException("战斗已经结束，不能再执行动作。");
        Exec x = new(ref battle, rules, action);
        switch (action.Kind)
        {
            case PlayerActionKind.PlayCard:
                PlayCardAction(ref x, action);
                break;
            case PlayerActionKind.EndTurn:
                AdvanceRound(ref x);
                if (!x.NeedsChoice)
                {
                    x.B.Flags &= ~BattleFlags.PlayerTurnEndRequested;
                }
                break;
            case PlayerActionKind.UsePotion:
                UsePotionAction(ref x, action);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action.Kind, null);
        }
        StepResult result = FinishStep(ref x, action, out request, out historyRisk);
        enemySideTurnEndReached = x.EnemySideTurnEndReached;
        brightestFlameMaxHpSpent = x.BrightestFlameMaxHpSpent;
        return result;
    }

    /// <summary>
    /// SettleReplayActionBoundary and the step result. Shared by the ordinary action dispatch and the
    /// prepared-end-turn continuation, so both paths perform the identical post-boundary work.
    /// </summary>
    private static StepResult FinishStep(ref Exec x, in PlayerAction action, out ChoiceRequest request,
        out bool historyRisk, bool requireAllChoices = true)
    {
        // SettleReplayActionBoundary: resolve the queued changes and renormalize card afflictions.
        // The existing engine skips SettleReplayActionBoundary for every non-None boundary, including
        // EventDefeat (CombatBeamSolver.Expansion.Replay.cs:432). Only the Sts2Solver-internal transient
        // flags and decimal slots are still normalized below.
        if (!x.NeedsChoice && x.B.Outcome != Outcome.EventDefeat)
        {
            ResolvePowerAmountChanges(ref x);
            if (!x.NeedsChoice)
                NormalizeCardAfflictions(ref x);
        }
        if (!x.NeedsChoice && x.R.HasDecimalCardVars)
            DecimalVars.Canonicalize(ref x.B);
        if (!x.NeedsChoice)
            ClearPendingPowerAmountStates(ref x.B);
        if (!x.NeedsChoice && x.R.PenNibRelicIndex >= 0
            && x.R.PenNibRelicIndex < x.B.RelicCount)
        {
            // Projection/normalization: a play cut short before AfterCardPlayed (player death) leaves
            // the doubled-attack latch; no later settlement can read it and the importer projects 0.
            x.B.Relics[x.R.PenNibRelicIndex].Aux = 0;
        }
        historyRisk = x.HistoryRisk;
        if (x.NeedsChoice)
        {
            request = x.Request;
            return StepResult.NeedsChoice;
        }
        if (requireAllChoices && !x.AllChoicesConsumed)
            throw new InvalidOperationException($"动作 {action} 携带了未使用的选择。");
        if (VerifyStableBoundaryInvariants)
            ValidateStableBoundary(x.B);
        request = default;
        return StepResult.Completed;
    }

    /// <summary>
    /// Advances one carried EndTurn action from its verified <c>Before</c> boundary to the native
    /// <c>SetupPlayerTurn</c> entry (player <c>Phase = Start</c>, before the energy reset) and stops.
    /// This is the turn-setup prediction entry: the ordinary EndTurn path still runs
    /// <c>ContinuePlayerPlayPhase</c> to Play. The action may carry more choices than the prefix
    /// consumes (the next turn's setup choices), so the partial finish does not require all of them.
    /// Returns false when the action needs a choice, ends the combat, or takes an extra turn.
    /// </summary>
    internal static bool TryAdvanceToPlayerSetupStart(in Battle endTurnBefore, Rules rules,
        in PlayerAction endTurnAction, out Battle setupStart)
    {
        Unsafe.SkipInit(out setupStart);
        if (endTurnAction.Kind != PlayerActionKind.EndTurn)
            throw new ArgumentOutOfRangeException(nameof(endTurnAction), endTurnAction.Kind,
                "预测 Start 根只接受结束回合动作。");
        Unsafe.SkipInit(out Battle battle);
        Battle.CopyPacked(in endTurnBefore, ref battle);
        Exec x = new(ref battle, rules, endTurnAction);
        if (!AdvanceRoundToPlayerStart(ref x, out bool takingExtraTurn) || takingExtraTurn)
            return false;
        if (!AdvanceRoundPlayerStartPrefix(ref x, takingExtraTurn: false))
            return false;
        x.B.Phase = PlayerPhase.Start;
        x.B.Flags &= ~BattleFlags.PlayerTurnEndRequested;
        StepResult result = FinishStep(ref x, endTurnAction, out _, out _,
            requireAllChoices: false);
        if (result != StepResult.Completed)
            return false;
        Battle.CopyPacked(in x.B, ref setupStart);
        return true;
    }

    /// <summary>
    /// Default-off verification switch set by the search's equivalence mode. When enabled, every
    /// stable boundary asserts the fixed-capacity hand invariant before the state can become a child.
    /// </summary>
    internal static bool VerifyStableBoundaryInvariants { get; set; }

    /// <summary>
    /// Stable-boundary invariant: the hand never exceeds <see cref="Limits.MaxHandSize"/>. Violations
    /// throw instead of being truncated or written past a fixed buffer.
    /// </summary>
    internal static void ValidateStableBoundary(in Battle battle)
    {
        if (battle.HandCount > Limits.MaxHandSize)
            throw new InvalidOperationException(
                $"稳定边界手牌 {battle.HandCount} 超过上限 {Limits.MaxHandSize}。");
    }

    private static void PlayCardAction(ref Exec x, in PlayerAction action)
    {
        if (action.HandIndex >= x.B.HandCount)
            throw new ArgumentOutOfRangeException(nameof(action), action.HandIndex, "手牌位置越界。");
        if (!CanPlayCard(x.B, x.R, action.HandIndex))
            throw new InvalidOperationException($"手牌位置 {action.HandIndex} 的卡当前不能打出。");
        int shuffleEvents = x.B.ShuffleEventCount;
        ManualPlay(ref x, action.HandIndex, action.Target);
        if (x.NeedsChoice)
            return;
        // CompleteCardExecution and the replay loop both run the deferred enemy-death pass.
        ApplyEnemyDeathPowers(ref x);
        ApplyEnemyDeathPowers(ref x);
        // SettleReplayActionBoundary: no supported power reactions or normalizations.
        if (x.B.ShuffleEventCount != shuffleEvents)
            x.B.ShufflesCrossed++;
        CheckWinCondition(ref x.B);
        if ((x.B.Flags & BattleFlags.PlayerTurnEndRequested) != 0)
        {
            x.B.Flags &= ~BattleFlags.PlayerTurnEndRequested;
            if (IsInProgress(x.B))
                AdvanceRound(ref x);
            x.B.Flags &= ~BattleFlags.PlayerTurnEndRequested;
        }
    }

    /// <summary>
    /// Enumerates legal actions in the existing search's order: playable hand cards (each with its
    /// hittable targets), then end turn. Identical cards are not deduplicated here.
    /// </summary>
    public static int LegalActions(in Battle battle, Rules rules, Span<PlayerAction> output)
    {
        if (!IsInProgress(battle))
            return 0;
        if (battle.HandCount > Limits.MaxHandSize)
            throw new InvalidOperationException(
                $"手牌 {battle.HandCount} 超过上限 {Limits.MaxHandSize}，"
                + $"LegalActions 的 {MaxLegalActions} 个固定槽位不再够用。");
        if (output.Length < MaxLegalActions)
            throw new ArgumentException(
                $"LegalActions 需要至少 {MaxLegalActions} 个槽位，实际 {output.Length}。", nameof(output));
        int count = 0;
        for (int hand = 0; hand < battle.HandCount; hand++)
        {
            if (!CanPlayCard(battle, rules, hand))
                continue;
            TargetType targetType = Registry.Card(battle.Cards[hand].Id)
                .ResolveTargetType(battle, rules, battle.Cards[hand]);
            if (targetType == TargetType.AnyEnemy)
            {
                for (int position = 0; position < battle.RosterCount; position++)
                {
                    byte owner = EnemyOwner(battle.Roster[position]);
                    if (IsHittable(battle, owner))
                        output[count++] = PlayerAction.PlayCard(hand, owner);
                }
            }
            else
            {
                output[count++] = PlayerAction.PlayCard(hand, Limits.NoOwner);
            }
        }
        // EnumeratePlannedPotionActions: every potion slot, with TargetsForPotion. The search applies
        // the potion policy (allowed slots, earliest turn, maximum uses) on top of this list.
        for (int slot = 0; slot < battle.PotionSlotCount; slot++)
        {
            ushort stored = battle.Potions[slot];
            if (stored == 0 || !Registry.Potion((ushort)(stored - 1)).Searchable)
                continue;
            switch (rules.Potion((ushort)(stored - 1)).Target)
            {
                case TargetType.AnyEnemy:
                    for (int position = 0; position < battle.RosterCount; position++)
                    {
                        byte owner = EnemyOwner(battle.Roster[position]);
                        if (IsHittable(battle, owner))
                            output[count++] = PlayerAction.UsePotion(slot, owner);
                    }
                    break;
                case TargetType.AnyPlayer or TargetType.Self:
                    if (battle.Player.IsAlive)
                        output[count++] = PlayerAction.UsePotion(slot, Limits.NoOwner);
                    break;
                case TargetType.AllEnemies or TargetType.TargetedNoCreature:
                    output[count++] = PlayerAction.UsePotion(slot, Limits.NoOwner);
                    break;
            }
        }
        output[count++] = PlayerAction.EndTurn();
        return count;
    }

    /// <summary>Upper bound of <see cref="LegalActions"/> output.</summary>
    public const int MaxLegalActions = Limits.MaxHandSize * Limits.Enemies + Limits.PotionSlots * Limits.Enemies + 1;
}
