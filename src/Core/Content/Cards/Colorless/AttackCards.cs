using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Colorless;

[ContentId("DRAMATIC_ENTRANCE")]
public sealed class DramaticEntrance : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);
}

[ContentId("FLASH_OF_STEEL")]
public sealed class FlashOfSteel : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Cards"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        // CorePowerSupport.ApplyCardPowers settles newly dead enemies after the mirror attack and
        // before the card-specific draw compensation; HittableEnemies/Hellraiser read that phase.
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice)
            return;
        Kernel.Draw(ref x, CardEffects.Var(x, 1), fromHandDraw: false);
    }
}

[ContentId("MIND_BLAST")]
public sealed class MindBlast : CardDef
{
    // Native CalculatedDamageVar: CalculationBase + ExtraDamage * draw pile card count.
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];

    public override void OnPlay(ref Exec x)
    {
        decimal damage = CardEffects.Var(x, 0) + CardEffects.Var(x, 1) * x.B.DrawCount;
        CardEffects.Attack(ref x, damage);
    }

    public override bool TryGetCalculatedDamage(ref Exec x, in Card self, byte target, bool selfInHand, out decimal damage)
    {
        CardStats stats = x.R.Card(self.Id, self.Upgrade);
        damage = stats.Vars[0] + self.Var0 + (stats.Vars[1] + self.Var1) * x.B.DrawCount;
        return true;
    }
}

[ContentId("REND")]
public sealed class Rend : CardDef
{
    // Native CalculatedDamageVar: CalculationBase + ExtraDamage * the target's non-temporary
    // debuff count (CalculatedVarSpecRegistry.TryMultiplier, Rend case).
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];

    public override bool TryGetCalculatedDamage(ref Exec x, in Card self, byte target, bool selfInHand, out decimal damage)
    {
        CardStats stats = x.R.Card(self.Id, self.Upgrade);
        damage = stats.Vars[0] + self.Var0
            + (stats.Vars[1] + self.Var1) * CountTargetDebuffs(x, target);
        return true;
    }

    public override void OnPlay(ref Exec x)
    {
        decimal damage = CardEffects.Var(x, 0)
            + CardEffects.Var(x, 1) * CountTargetDebuffs(x, x.Target);
        CardEffects.Attack(ref x, damage);
    }

    private static int CountTargetDebuffs(in Exec x, byte target)
    {
        int debuffs = 0;
        if (target == Limits.NoOwner)
            return 0;
        for (int index = 0; index < x.B.PowerCount; index++)
        {
            ref readonly Power power = ref x.B.Powers[index];
            if (power.Owner != target)
                continue;
            PowerDef def = Registry.Power(power.Id);
            if (def.TypeForAmount(power.Amount) == PowerType.Debuff && !def.IsTemporaryPower)
                debuffs++;
        }
        return debuffs;
    }
}

[ContentId("SALVO")]
public sealed class Salvo : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.RetainHandPower>(),
            1, Limits.PlayerOwner);
    }
}

[ContentId("ULTIMATE_STRIKE")]
public sealed class UltimateStrike : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);
}

[ContentId("VOLLEY")]
public sealed class Volley : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
    {
        // HasEnergyCostX: one random-target hit per energy spent (ResolveEnergyXValue).
        CardEffects.Attack(ref x, CardEffects.Var(x, 0), Kernel.ResolveEnergyXValue(ref x));
    }
}

[ContentId("FISTICUFFS")]
public sealed class Fisticuffs : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // CorePowerSupport's Fisticuffs case: the mirrored attack, the deferred enemy-death pass (the
    // existing engine settles deaths before this compensation), then block equal to the sum of this
    // card's damage results (TotalDamage + OverkillDamage).
    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice)
            return;
        if (x.CardDamageTotal > 0)
            Kernel.GainBlock(ref x, Limits.PlayerOwner, x.CardDamageTotal, ValueProps.Move, fromCard: true);
    }
}

[ContentId("GOLD_AXE")]
public sealed class GoldAxe : CardDef
{
    // CalculatedDamage: CalculationBase + ExtraDamage * finished card plays this combat
    // (CalculatedVarSpecRegistry's GoldAxe multiplier).
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];

    public override void OnPlay(ref Exec x)
    {
        decimal damage = CardEffects.Var(x, 0)
            + CardEffects.Var(x, 1) * x.B.Counter(Counter.FinishedCardPlays);
        CardEffects.Attack(ref x, damage);
    }

    public override bool TryGetCalculatedDamage(ref Exec x, in Card self, byte target, bool selfInHand, out decimal damage)
    {
        CardStats stats = x.R.Card(self.Id, self.Upgrade);
        damage = stats.Vars[0] + self.Var0
            + (stats.Vars[1] + self.Var1) * x.B.Counter(Counter.FinishedCardPlays);
        return true;
    }
}

[ContentId("HAND_OF_GREED")]
public sealed class HandOfGreed : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Gold"];

    public override bool GainsGoldInCombat => true;

    // CorePowerSupport's HandOfGreed case: mirrored attack, enemy-death pass, then gold when this
    // card's hit killed the target and every target power allows a fatal kill.
    public override void OnPlay(ref Exec x)
    {
        // CorePowerSupport captures targetDeathTriggersFatal before the card's effects run, so the
        // check must not read the powers after the target's death cleanup removed them.
        byte target = x.Target;
        bool fatal = target != Limits.NoOwner && Kernel.TargetDeathTriggersFatal(x.B, target);
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice || !x.CardKilledTarget || target == Limits.NoOwner)
            return;
        if (x.B.Creature(target).IsAlive || !fatal)
            return;
        // PlayerCmd.GainGold: the modifiers (BowlerHat, Ectoplasm) settle before the balance and
        // DragonFruit's AfterGoldGained. CorePowerSupport records the growth reward after the gain
        // and counts only the gold that was actually added toward long-term resource value.
        int gained = Kernel.GainGold(ref x, (int)CardEffects.Var(x, 1));
        if (gained > 0)
            x.B.AddCounter(Counter.LongTermResourceValue, gained);
        GrowthRewards.Add(ref x.B, GrowthRewardSource.HandOfGreed);
    }
}

[ContentId("OMNISLICE")]
public sealed class Omnislice : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // CardOnPlaySupport.Batch042's ApplyOmnislice: hit the target, settle deaths, then deal the hit's
    // TotalDamage + OverkillDamage to every other hittable enemy as Unpowered|Move.
    public override void OnPlay(ref Exec x)
    {
        // AttackContext.AddHit does not call AttackCommand.Execute, so Omnislice must not increment
        // the per-turn attack counters (EchoingSlash uses the same context for its waves).
        CardEffects.GeneralAttack(ref x, 0, recordAttack: false, endAttack: false);
        if (x.NeedsChoice)
            return;
        int amount = x.LastDamageTotal + x.LastDamageOverkill;
        // CardOnPlaySupport.Batch042.ApplyOmnislice passes combat.Enemies (current roster) to the
        // death pass, so a lethal hit that already left the roster does not spawn Infested minions
        // before the copied hit is resolved.
        Kernel.ApplyEnemyDeathPowersInRoster(ref x);
        if (x.NeedsChoice)
            return;
        if (amount <= 0)
        {
            Kernel.EndAttackContext(ref x, Limits.PlayerOwner);
            return;
        }
        Span<byte> targets = stackalloc byte[Limits.Enemies];
        int count = 0;
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte owner = Kernel.EnemyOwner(x.B.Roster[position]);
            if (owner == x.Target || !Kernel.IsHittable(x.B, owner))
                continue;
            targets[count++] = owner;
        }
        if (count == 0)
        {
            Kernel.EndAttackContext(ref x, Limits.PlayerOwner);
            return;
        }
        Kernel.Damage(ref x, targets[..count], amount,
            ValueProps.Unpowered | ValueProps.Move, Limits.PlayerOwner,
            hasCardSource: true, x.Card);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyEnemyDeathPowersInRoster(ref x);
        if (x.NeedsChoice)
            return;
        // AttackContext.DisposeAsync: the original hit and the copied hits are one attack.
        Kernel.EndAttackContext(ref x, Limits.PlayerOwner);
    }
}

[ContentId("SEEKER_STRIKE")]
public sealed class SeekerStrike : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Cards"];

    // CardChoiceSupport.BuildSeekerSpec: StableShuffle the draw pile with CombatCardSelection, take
    // Cards of them, present the whole draw pile filtered to that sample in pile order, then move the
    // chosen card to hand.
    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice || x.B.DrawCount == 0)
            return;
        int draw = x.B.DrawCount;
        Span<Card> order = stackalloc Card[draw];
        Span<int> origins = stackalloc int[draw];
        Piles.ReadCards(x.B, Pile.Draw).CopyTo(order);
        for (int index = 0; index < draw; index++)
            origins[index] = index;
        CardSort.SortWithOrigins(order, origins, x.R);
        ref RngState rng = ref x.B.RngStream(RngStream.CombatCardSelection);
        for (int n = draw; n > 1;)
        {
            n--;
            int k = rng.NextInt(n + 1);
            (origins[k], origins[n]) = (origins[n], origins[k]);
        }
        int take = Math.Min((int)CardEffects.Var(x, 1), draw);
        Span<bool> sampled = stackalloc bool[draw];
        for (int index = 0; index < take; index++)
            sampled[origins[index]] = true;
        Span<int> options = stackalloc int[draw];
        int optionCount = 0;
        for (int index = 0; index < draw; index++)
        {
            if (sampled[index])
                options[optionCount++] = index;
        }
        if (optionCount == 0)
            return;
        // CombatSolver's CardChoiceSupport.BuildSeekerSpec recorded RecordRisk(UnresolvedPlayerChoice)
        // because its simulator could not resolve this choice, and the risk then disabled the
        // incumbent bound for the rest of the combat. This kernel enumerates the choice exactly
        // (ChooseCardsInRange below), so it is no prediction uncertainty: BattleFlags.HasRisk stays
        // for truncated predictions only. The routing history marker is kept.
        x.HistoryRisk = true;
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseCardsInRange(ref x, ChoiceEffect.MoveToHand, Pile.Draw,
                options[..optionCount], 1, 1, selected, out int selectedCount))
            return;
        Kernel.MoveSelected(ref x.B, Pile.Draw, selected[..selectedCount], Pile.Hand,
            PilePosition.Bottom);
    }
}

[ContentId("BOLAS")]
public sealed class Bolas : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    // CardSelectionCardMirrors.BolasOnPlay (attack) plus SimulatedCombatState.RecordCardLifecycle's
    // ReturnsToHandAfterPlaying: the played card joins _returnToHandNextTurn.
    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        int playPosition = x.B.PlayCount - 1;
        if (playPosition >= 0 && x.B.Cards[Piles.Start(x.B, Pile.Play) + playPosition].Id == x.Card.Id)
            Piles.At(ref x.B, Pile.Play, playPosition).ReturnToHandNextTurn = true;
    }
}

[ContentId("THRUMMING_HATCHET")]
public sealed class ThrummingHatchet : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        int playPosition = x.B.PlayCount - 1;
        if (playPosition >= 0 && x.B.Cards[Piles.Start(x.B, Pile.Play) + playPosition].Id == x.Card.Id)
            Piles.At(ref x.B, Pile.Play, playPosition).ReturnToHandNextTurn = true;
    }
}
