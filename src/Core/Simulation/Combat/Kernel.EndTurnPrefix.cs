namespace Sts2Solver.Simulation.Combat;

/// <summary>Why an end-turn action did or did not expose a shareable player-turn-start prefix.</summary>
public enum EndTurnPrefixStatus
{
    /// <summary>No prefix was captured.</summary>
    NotPrepared = 0,

    /// <summary>The prefix was captured and can be continued (possibly with no player-start half).</summary>
    Prepared,

    /// <summary>The parent battle was already over.</summary>
    NotInProgress,

    /// <summary>A choice was requested before the player-turn-start boundary.</summary>
    ChoiceBeforePlayerStart,

    /// <summary>The round takes an extra turn; the prefix is not shared.</summary>
    ExtraTurn,
}

/// <summary>
/// Image of an end-turn action stopped immediately before <c>AdvanceRoundPlayerStart</c>. It carries
/// the battle and every <see cref="Exec"/> transient that the player-turn-start half can read, so
/// <see cref="Kernel.StepEndTurnFromPrefix"/> is equivalent to replaying the whole action from the
/// parent. Capture writes straight into the caller's storage: the battle bytes are copied once and
/// the rest of the image is never cleared or copied as a whole.
/// </summary>
public struct EndTurnPrefix
{
    internal Battle _battle;
    internal ExecContinuation _continuation;
    internal EndTurnPrefixStatus _status;
    internal bool _playerStartPending;

    /// <summary>
    /// Replaces <paramref name="target"/> with the image at <paramref name="battle"/>. The status is
    /// written last, so a target that is not <see cref="EndTurnPrefixStatus.Prepared"/> never exposes
    /// these bytes to a caller.
    /// </summary>
    internal static void Capture(ref EndTurnPrefix target, in Battle battle,
        in ExecContinuation continuation, EndTurnPrefixStatus status, bool playerStartPending)
    {
        Battle.CopyPacked(in battle, ref target._battle);
        target._continuation = continuation;
        target._playerStartPending = playerStartPending;
        target._status = status;
    }

    internal readonly bool IsPrepared => _status == EndTurnPrefixStatus.Prepared;

    internal readonly EndTurnPrefixStatus Status => _status;

    /// <summary>A copy of the captured battle (used by diagnostics only).</summary>
    internal readonly Battle State => _battle;
}

public static partial class Kernel
{
    /// <summary>
    /// Executes the player turn end and the enemy turn from a copy of <paramref name="parent"/> and
    /// stops before the player turn start. The parent is never modified. When the prefix is not
    /// shareable the caller must run the ordinary complete <see cref="Step"/> instead.
    /// </summary>
    public static EndTurnPrefixStatus TryPrepareEndTurn(ref Battle parent, Rules rules,
        ref EndTurnPrefix prefix)
    {
        prefix._status = EndTurnPrefixStatus.NotPrepared;
        if (!IsInProgress(parent))
            return prefix._status = EndTurnPrefixStatus.NotInProgress;
        Battle.CopyPacked(in parent, ref prefix._battle);
        Exec x = new(ref prefix._battle, rules, PlayerAction.EndTurn());
        if (!AdvanceRoundToPlayerStart(ref x, out bool takingExtraTurn))
        {
            if (x.NeedsChoice)
                return prefix._status = EndTurnPrefixStatus.ChoiceBeforePlayerStart;
            // The round ended before the player turn start. The continuation has no player-start
            // half to run; it still reuses the exact post-enemy-turn image for the final settlement.
            prefix._continuation = x.CaptureContinuation();
            prefix._playerStartPending = false;
            return prefix._status = EndTurnPrefixStatus.Prepared;
        }
        if (takingExtraTurn)
            return prefix._status = EndTurnPrefixStatus.ExtraTurn;
        prefix._continuation = x.CaptureContinuation();
        prefix._playerStartPending = true;
        return prefix._status = EndTurnPrefixStatus.Prepared;
    }

    /// <summary>
    /// Executes a complete end-turn action and, when possible, also returns the player-turn-start
    /// prefix captured at the split point. This is the one-execution form of
    /// <see cref="TryPrepareEndTurn"/> followed by <see cref="StepEndTurnFromPrefix"/>: the first
    /// selection leaf pays for the first half once and its siblings reuse the returned image.
    /// </summary>
    public static StepResult StepEndTurnSharingPrefix(ref Battle battle, in PlayerAction action,
        Rules rules, out ChoiceRequest request, out bool historyRisk, ref EndTurnPrefix prefix,
        out int brightestFlameMaxHpSpent)
    {
        if (!IsInProgress(battle))
            throw new InvalidOperationException("战斗已经结束，不能再执行动作。");
        if (action.Kind != PlayerActionKind.EndTurn)
            throw new ArgumentOutOfRangeException(nameof(action), action.Kind, "前缀捕获只接受 EndTurn 动作。");
        prefix._status = EndTurnPrefixStatus.NotPrepared;
        Exec x = new(ref battle, rules, action);
        if (AdvanceRoundToPlayerStart(ref x, out bool takingExtraTurn))
        {
            if (!takingExtraTurn)
            {
                EndTurnPrefix.Capture(ref prefix, in battle, x.CaptureContinuation(),
                    EndTurnPrefixStatus.Prepared, playerStartPending: true);
            }
            else
            {
                prefix._status = EndTurnPrefixStatus.ExtraTurn;
            }
            AdvanceRoundPlayerStart(ref x, takingExtraTurn);
        }
        else if (!x.NeedsChoice)
        {
            EndTurnPrefix.Capture(ref prefix, in battle, x.CaptureContinuation(),
                EndTurnPrefixStatus.Prepared, playerStartPending: false);
        }
        else
        {
            prefix._status = EndTurnPrefixStatus.ChoiceBeforePlayerStart;
        }
        if (!x.NeedsChoice)
            AdvanceForcedTurnEnds(ref x);
        StepResult result = FinishStep(ref x, action, out request, out historyRisk);
        brightestFlameMaxHpSpent = x.BrightestFlameMaxHpSpent;
        return result;
    }

    /// <summary>
    /// Continues a prepared end-turn prefix through the player turn start, applying the choices
    /// carried by <paramref name="action"/>. The battle is replaced by the prefix image before the
    /// continuation runs; every field of the result (battle bytes, request and history risk) is
    /// identical to <see cref="Step"/> from the original parent.
    /// </summary>
    public static StepResult StepEndTurnFromPrefix(ref Battle battle, in PlayerAction action,
        Rules rules, in EndTurnPrefix prefix, out ChoiceRequest request, out bool historyRisk,
        out int brightestFlameMaxHpSpent)
    {
        if (!prefix.IsPrepared)
            throw new InvalidOperationException("EndTurn 前缀尚未准备。");
        if (action.Kind != PlayerActionKind.EndTurn)
            throw new ArgumentOutOfRangeException(nameof(action), action.Kind, "前缀续跑只接受 EndTurn 动作。");
        Battle.CopyPacked(in prefix._battle, ref battle);
        Exec x = new(ref battle, rules, action);
        x.RestoreContinuation(prefix._continuation);
        if (prefix._playerStartPending)
            AdvanceRoundPlayerStart(ref x);
        if (!x.NeedsChoice)
            AdvanceForcedTurnEnds(ref x);
        StepResult result = FinishStep(ref x, action, out request, out historyRisk);
        brightestFlameMaxHpSpent = x.BrightestFlameMaxHpSpent;
        return result;
    }
}
