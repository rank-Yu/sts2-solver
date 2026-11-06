using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Ironclad;

[ContentId("BODY_SLAM")]
public sealed class BodySlam : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];

    public override void OnPlay(ref Exec x)
        => CardEffects.Attack(ref x,
            CardEffects.Var(x, 0) + CardEffects.Var(x, 1) * x.B.Player.Block);

    public override bool TryGetCalculatedDamage(ref Exec x, in Card self, byte target, bool selfInHand, out decimal damage)
    {
        damage = SelfVar(x, self, 0) + SelfVar(x, self, 1) * x.B.Player.Block;
        return true;
    }

    internal static decimal SelfVar(in Exec x, in Card card, int index)
    {
        CardStats stats = x.R.Card(card.Id, card.Upgrade);
        decimal delta = index == 0 ? card.Var0 : card.Var1;
        return stats.Vars[index] + delta;
    }
}

[ContentId("BREAKTHROUGH")]
public sealed class Breakthrough : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "HpLoss"];

    public override void OnPlay(ref Exec x)
    {
        // Breakthrough.cs: lose HP first, with the card as the damage source, then attack.
        CardEffects.LoseHpFromCard(ref x, CardEffects.Var(x, 1));
        if (x.NeedsChoice)
            return;
        CardEffects.GeneralAttack(ref x, 0);
    }
}

[ContentId("PERFECTED_STRIKE")]
public sealed class PerfectedStrike : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];

    public override void OnPlay(ref Exec x)
        => CardEffects.Attack(ref x,
            CardEffects.Var(x, 0) + CardEffects.Var(x, 1) * CardEffects.CountCardsWithTag(x, CardTag.Strike));

    public override bool TryGetCalculatedDamage(ref Exec x, in Card self, byte target, bool selfInHand, out decimal damage)
    {
        damage = BodySlam.SelfVar(x, self, 0)
            + BodySlam.SelfVar(x, self, 1) * CardEffects.CountCardsWithTag(x, CardTag.Strike);
        return true;
    }
}

[ContentId("SWORD_BOOMERANG")]
public sealed class SwordBoomerang : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Repeat"];

    public override void OnPlay(ref Exec x)
        => CardEffects.GeneralAttack(ref x, 0, hitCount: (int)CardEffects.Var(x, 1));
}

[ContentId("CINDER")]
public sealed class Cinder : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        // CardSelectionCardMirrors.CinderOnPlay: Rng.CombatCardSelection.NextItem over the hand.
        if (x.B.HandCount == 0)
            return;
        int index = x.B.RngStream(RngStream.CombatCardSelection).NextInt(x.B.HandCount);
        Kernel.Exhaust(ref x, Pile.Hand, index);
    }
}

[ContentId("MOLTEN_FIST")]
public sealed class MoltenFist : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        // CorePowerSupport.ApplyCardPowers runs ApplyEnemyDeathPowers after the mirror attack
        // and before the card-specific compensation, so the debuff sees the post-death target.
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice)
            return;
        if (x.Target == Limits.NoOwner || !x.B.Creature(x.Target).IsAlive)
            return;
        ushort vulnerablePower = Registry.PowerId<Powers.VulnerablePower>();
        int vulnerable = Kernel.GetAmount(x.B, x.Target, vulnerablePower);
        if (vulnerable > 0)
            Kernel.ApplyPower(ref x, x.Target, vulnerablePower, vulnerable, Limits.PlayerOwner);
    }
}

[ContentId("ANGER")]
public sealed class Anger : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("ANGER", 0), ("ANGER", 1)];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        // CorePowerSupport.ApplyCardPowers runs ApplyEnemyDeathPowers after the mirror attack and
        // before the card-specific compensation. The death pass can remove the last DampenPower
        // caster, which restores the played card's original upgrade before the clone is made.
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice)
            return;
        // The native OnPlay clones the live CardModel after that restore, while Exec.Card is
        // the pre-restore copy taken when the card left the hand.
        Kernel.SyncCurrentPlayCard(ref x);
        // CorePowerSupport.Anger adds a clone of the played card to the discard pile.
        Kernel.AddGeneratedCard(ref x, Kernel.CloneCard(x.Card), Pile.Discard);
        if (x.NeedsChoice)
            return;
        x.B.AddCounter(Counter.AngerCopiesGenerated, 1);
    }
}

[ContentId("SETUP_STRIKE")]
public sealed class SetupStrike : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "StrengthPower"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        // CardEffectSpecRegistry: ApplyTemporaryStrengthGain<SetupStrikePower>.
        Kernel.ApplyTemporaryStrength(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.SetupStrikePower>(),
            (int)CardEffects.Var(x, 1), Limits.PlayerOwner, sign: 1);
    }
}

[ContentId("TWIN_STRIKE")]
public sealed class TwinStrike : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0, hitCount: 2);
}

[ContentId("IRON_WAVE")]
public sealed class IronWave : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Block"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 1);
        CardEffects.GeneralAttack(ref x, 0);
    }
}

[ContentId("POMMEL_STRIKE")]
public sealed class PommelStrike : CardDef
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

[ContentId("THUNDERCLAP")]
public sealed class Thunderclap : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "VulnerablePower"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        // ApplyCardPowers settles newly dead enemies after the mirrored attack and before its
        // card-specific switch; Thunderclap's Vulnerable must therefore see any Wrigglers spawned by
        // the Phrog Parasite the attack just killed.
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice)
            return;
        CardEffects.ApplyToAllEnemies(ref x, Registry.PowerId<Powers.VulnerablePower>(), 1);
    }
}

[ContentId("HEADBUTT")]
public sealed class Headbutt : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);

    public override void OwnChoice(ref Exec x)
    {
        // GetSpec: MoveToDrawTop, Discard, 1, the discard pile without the played card (which is in
        // the play pile during resolution).
        Span<int> options = stackalloc int[x.B.DiscardCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.MoveToDrawTop, Pile.Discard, options, 1, selected, out int chosen))
            return;
        Kernel.MoveSelected(ref x.B, Pile.Discard, selected[..chosen], Pile.Draw, PilePosition.Top);
    }
}
