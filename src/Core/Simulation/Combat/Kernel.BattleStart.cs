namespace Sts2Solver.Simulation.Combat;

public static partial class Kernel
{
    /// <summary>
    /// The first player turn of a freshly created combat. Native <c>StartTurn</c> with
    /// <c>RoundNumber == 1</c>: no block clear, no round increment, then energy, hand draw,
    /// <c>AfterPlayerTurnStart</c>, side-turn-start and the auto-pre-play phase. Later turns use
    /// <see cref="AdvanceRoundPlayerStart"/>.
    /// </summary>
    public static void StartFirstPlayerTurn(ref Exec x)
    {
        x.B.Round = 1;
        x.B.PlayerTurn = 1;
        x.B.Phase = PlayerPhase.Start;
        x.ChoiceTiming = ChoiceTiming.PlayerTurnStart;
        // The native first turn does not run BeginSideTurn's per-turn counter reset; room-entry
        // gains (DivineRight's StarsGainedThisTurn) are part of the captured root.
        SnapshotPowerAmountsAtTurnStart(ref x.B, ParticipantMask(Limits.PlayerOwner));
        ReadOnlySpan<byte> player = [Limits.PlayerOwner];
        BeforeSideTurnStart(ref x, Side.Player, player);
        if (x.NeedsChoice)
            return;
        ApplyFirstTurnPlating(ref x);

        // Native StartTurn calls Hook.AfterBlockCleared after Creature.AfterTurnStart; on turn 1 the
        // player's AfterTurnStart returns before ClearBlock, so only the hook pass would run. No
        // supported start-of-combat source applies BlockNextTurn, so there is nothing to dispatch.

        if (HookDispatch.ShouldPlayerResetEnergy(x.B, x.R, Limits.PlayerOwner))
            x.B.Energy = 0;
        int energyNextTurn = ConsumeEnergyNextTurn(ref x.B);
        x.B.Energy = (int)Math.Clamp((long)x.B.Energy + Math.Max(0, ModifiedMaxEnergy(ref x)) + energyNextTurn,
            0, Limits.ValueClamp);
        PowersAfterEnergyReset(ref x);
        if (x.NeedsChoice)
            return;
        RelicPass(ref x, RelicPhase.AfterEnergyReset, Side.Player);
        if (x.NeedsChoice)
            return;
        RelicPass(ref x, RelicPhase.AfterEnergyResetLate, Side.Player);
        if (x.NeedsChoice)
            return;

        RelicBeforeHandDraw(ref x);
        if (x.NeedsChoice)
            return;
        BeforeHandDraw(ref x);
        if (x.NeedsChoice)
            return;
        ResolvePowerAmountChanges(ref x);
        if (x.NeedsChoice)
            return;
        int drawCount = ModifiedHandDraw(ref x, BaseHandDrawCount + DrawNextTurnNotYetSnapshotted(x.B));
        drawCount = ApplyFirstTurnDrawOrder(ref x.B, drawCount);
        Draw(ref x, drawCount, fromHandDraw: true);
        if (x.NeedsChoice)
            return;
        AfterPlayerTurnStart(ref x);
        if (x.NeedsChoice)
            return;
        StartPlayerSideTurn(ref x);
        if (x.NeedsChoice)
            return;
        ApplyEnemyDeathPowers(ref x);
        TriggerAfterTurnStartOrbs(ref x);
        if (x.NeedsChoice)
            return;
        x.B.Phase = PlayerPhase.AutoPrePlay;
        AutoPrePlay(ref x);
        if (x.NeedsChoice)
            return;
        x.B.Phase = PlayerPhase.Play;
        CheckWinCondition(ref x.B);
    }

    /// <summary>
    /// Native <c>PlatingPower.BeforeSideTurnStart</c>: an enemy with Plating gains its amount as
    /// unpowered block at the first player side turn start. The normal kernel relic/power passes
    /// restrict listeners to the participating side, so the opening pass applies these rows directly.
    /// </summary>
    private static void ApplyFirstTurnPlating(ref Exec x)
    {
        if (!Registry.TryPower("PLATING_POWER", out ushort plating))
            return;
        for (int index = 0; index < x.B.PowerCount; index++)
        {
            ref readonly Power power = ref x.B.Powers[index];
            if (power.Id != plating || power.Amount <= 0 || power.Owner < Limits.FirstEnemyOwner)
                continue;
            Kernel.GainBlock(ref x, power.Owner, power.Amount, ValueProps.Unpowered, fromCard: false);
        }
    }

    private static void RelicBeforeHandDraw(ref Exec x)
        => RelicPass(ref x, RelicPhase.BeforeHandDraw, Side.Player, snapshot: true);

    /// <summary>
    /// Native turn-one draw order: cards whose enchantment starts at the bottom move to the draw
    /// pile's bottom first, then Innate cards move to the top; the hand draw is at least the number
    /// of innate cards and is capped by <see cref="Limits.MaxHandSize"/>.
    /// </summary>
    private static int ApplyFirstTurnDrawOrder(ref Battle battle, int drawCount)
    {
        int start = Piles.Start(battle, Pile.Draw);
        int count = battle.DrawCount;
        int bottom = 0;
        for (int index = 0; index < count; index++)
        {
            ref Card card = ref battle.Cards[start + index];
            if (card.EnchantmentId == 0)
                continue;
            EnchantmentDef enchantment = Registry.Enchantment((ushort)(card.EnchantmentId - 1));
            if (!enchantment.StartsAtBottomOfDrawPile)
                continue;
            Card value = card;
            for (int move = index; move < count - 1; move++)
                battle.Cards[start + move] = battle.Cards[start + move + 1];
            battle.Cards[start + count - 1] = value;
            bottom++;
        }
        int innate = 0;
        int searchable = count - bottom;
        for (int index = 0; index < searchable; index++)
        {
            if ((battle.Cards[start + index].Keywords & Keywords.Innate) == 0)
                continue;
            Card value = battle.Cards[start + index];
            for (int move = index; move > 0; move--)
                battle.Cards[start + move] = battle.Cards[start + move - 1];
            battle.Cards[start] = value;
            innate++;
        }
        if (innate == 0)
            return drawCount;
        return Math.Min(Math.Max(drawCount, innate), Limits.MaxHandSize);
    }
}
