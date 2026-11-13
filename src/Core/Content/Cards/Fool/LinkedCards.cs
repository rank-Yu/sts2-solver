// Native game 0.113.0 (16ac0fe3). Arrow source and melody identity are resolved by the kernel.
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Content.Powers;
namespace Sts2Solver.Content.Cards.Fool;

[ContentId("CHO_LIMBS")]
public sealed class ChoLimbs : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Block(ref x);
        if (!x.NeedsChoice) FoolEffects.Block(ref x);
    }
    public override decimal ModifyOwnEnergyCost(in Battle battle, Rules rules, in Card card, decimal amount)
        => amount > 0 && (Kernel.PlayedMelody(battle, CardTag.Wah) || Kernel.PlayedMelody(battle, CardTag.Ki))
            ? Math.Max(0m, amount - 2m) : amount;
}

[ContentId("CODA")]
public sealed class Coda : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];
    public override void OnPlay(ref Exec x) => FoolEffects.Attack(ref x);
    public override decimal ModifyOwnEnergyCost(in Battle battle, Rules rules, in Card card, decimal amount)
        => Math.Max(0m, amount - 2m * Kernel.MelodiesThisTurn(battle));
}

[ContentId("WAH_HAMMER")]
public sealed class WahHammer : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];
    public override void OnPlay(ref Exec x) => FoolEffects.Attack(ref x);
    public override decimal ModifyOwnDamageMultiplier(ref Exec x, in DamageContext damage)
        => damage.Target != Limits.NoOwner && Kernel.ImpaledPowerIndex(x.B, damage.Target) >= 0 ? 2m : 1m;
}

[ContentId("BOUNCE")]
public sealed class Bounce : CardDef
{
    public override IReadOnlyList<string> VarNames => ["StretchPower"];
    public override IReadOnlyList<string> AppliedPowers => ["STRETCH_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<StretchPower>(ref x, FoolEffects.Int(ref x, 0));
        if (!x.NeedsChoice) Kernel.CurrentPlayCard(ref x).ReturnToHandNextTurn = true;
    }
}

[ContentId("GROWTH")]
public sealed class Growth : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationExtra"];
    public override IReadOnlyList<string> AppliedPowers => ["STRETCH_POWER"];
    public override void OnPlay(ref Exec x)
    {
        Span<int> selected = stackalloc int[x.B.HandCount];
        int count = 0;
        for (int i = 0; i < x.B.HandCount; i++)
            if (x.B.Cards[i].Id == Registry.CardId<Sadness>()) selected[count++] = i;
        int amount = FoolEffects.Int(ref x, 0) * count;
        Kernel.ExhaustSelected(ref x, Pile.Hand, selected[..count]);
        if (!x.NeedsChoice) FoolEffects.Apply<StretchPower>(ref x, amount);
    }
}

[ContentId("MOURNING_BLOW")]
public sealed class MourningBlow : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SADNESS", 0)];
    public override void OnPlay(ref Exec x) => FoolEffects.Attack(ref x);
    public override Pile ResultLocation(in Battle battle, Rules rules, in Card card, Pile baseLocation,
        out PilePosition position)
    {
        position = PilePosition.Bottom;
        return baseLocation == Pile.Discard ? Pile.Hand : baseLocation;
    }
    public override void AfterPlayCompleted(ref Exec x, bool lastInSeries)
    {
        if (!lastInSeries || x.B.Player.IsDead) return;
        Kernel.ReplaceCard(ref x, Pile.Play, x.PlayPosition, Kernel.CreateCard(x.R, Registry.CardId<Sadness>()));
        if (!x.NeedsChoice) Kernel.SyncCurrentPlayCard(ref x);
    }
}

[ContentId("PUNCH_OF_JOY")]
public sealed class PunchOfJoy : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("JOY", 0)];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Attack(ref x);
        if (x.NeedsChoice) return;
        Span<Card> candidates = stackalloc Card[x.B.HandCount];
        Span<int> origins = stackalloc int[x.B.HandCount];
        int count = 0;
        for (int i = 0; i < x.B.HandCount; i++)
            if (x.B.Cards[i].Id == Registry.CardId<Sadness>())
            { candidates[count] = x.B.Cards[i]; origins[count++] = i; }
        if (count == 0) return;
        CardSort.SortWithOrigins(candidates[..count], origins[..count], x.R, x.B, Pile.Hand);
        var rng = x.B.RngStream(RngStream.CombatCardSelection).CreateShuffleRng();
        for (int i = count - 1; i > 0; i--)
        {
            int pick = rng.NextInt(i + 1);
            (origins[i], origins[pick]) = (origins[pick], origins[i]);
        }
        Kernel.ReplaceCard(ref x, Pile.Hand, origins[0], Kernel.CreateCard(x.R, Registry.CardId<Joy>()));
    }
}

[ContentId("ANGUISH")]
public sealed class Anguish : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];
    public override bool TryGetCalculatedDamage(ref Exec x, in Card self, byte target, bool selfInHand,
        out decimal damage)
    {
        damage = CardEffects.Var(x.B, x.R, self, 0)
            + CardEffects.Var(x.B, x.R, self, 1) * x.B.Fool.SadnessGenerated;
        return true;
    }
    public override void OnPlay(ref Exec x)
    {
        TryGetCalculatedDamage(ref x, x.Card, x.Target, false, out decimal amount);
        Kernel.CardAttack(ref x, amount, 1, AttackTargeting.Single, x.Target);
    }
}

[ContentId("WAH_SYMPHONY")]
public sealed class WahSymphony : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "ExtraDamage"];
    public override bool TryGetCalculatedDamage(ref Exec x, in Card self, byte target, bool selfInHand,
        out decimal damage)
    {
        int count = 0;
        for (int i = 0; i < x.B.CardCount; i++)
            if ((x.R.Card(x.B.Cards[i].Id, x.B.Cards[i].Upgrade).Tags
                    & (CardTag.Wah | CardTag.Cho | CardTag.Ki)) != 0) count++;
        damage = CardEffects.Var(x.B, x.R, self, 0) + CardEffects.Var(x.B, x.R, self, 1) * count;
        return true;
    }
    public override void OnPlay(ref Exec x)
    {
        TryGetCalculatedDamage(ref x, x.Card, x.Target, false, out decimal amount);
        Kernel.CardAttack(ref x, amount, 1, AttackTargeting.AllOpponents, x.Target);
    }
}

[ContentId("EPIC_RETRIEVAL")]
public sealed class EpicRetrieval : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Energy"];
    public override void OnPlay(ref Exec x) => FoolEffects.Attack(ref x);
    public override void AfterArrowDislodged(ref Exec x, Listener self, byte target, int arrow,
        bool hasCardSource, in Card cardSource)
    {
        if (hasCardSource && cardSource.Id == Id && x.PlayPosition >= 0
            && self.Index == Piles.Start(x.B, Pile.Play) + x.PlayPosition)
            Kernel.GainEnergy(ref x, CardEffects.Var(x.B, x.R, cardSource, 1));
    }
}

[ContentId("WHISTLING_ARROWS")]
public sealed class WhistlingArrows : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];
    public override void OnPlay(ref Exec x) => FoolEffects.Block(ref x);
    public override void AfterArrowShot(ref Exec x, Listener self, int arrow)
    {
        if (arrow < 0) return;
        Pile pile = Piles.PileOf(x.B, self.Index, out int index);
        if (pile == Pile.Discard) Kernel.MoveCard(ref x.B, pile, index, Pile.Hand, PilePosition.Bottom);
    }
}

[ContentId("TRICK_SHOT")]
public sealed class TrickShot : SingleArrowCard
{
    public override Pile ResultLocation(in Battle battle, Rules rules, in Card card, Pile baseLocation,
        out PilePosition position)
    {
        position = PilePosition.Bottom;
        return baseLocation == Pile.Discard && Kernel.AvailableArrows(battle) > 0 ? Pile.Hand : baseLocation;
    }
}

[ContentId("HOBBLING_SHOT")]
public sealed class HobblingShot : SingleArrowCard
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Power"];
    public override IReadOnlyList<string> AppliedPowers => ["IMPALED_POWER", "WEAK_POWER", "VULNERABLE_POWER"];
    public override void OnPlay(ref Exec x)
    {
        bool shot = Kernel.ShootArrow(ref x, FoolEffects.Var(ref x, 0));
        if (!shot || x.NeedsChoice) return;
        FoolEffects.Apply<WeakPower>(ref x, FoolEffects.Int(ref x, 1), true);
        if (!x.NeedsChoice) FoolEffects.Apply<VulnerablePower>(ref x, FoolEffects.Int(ref x, 1), true);
    }
}

[ContentId("MELODIC_SHOT")]
public sealed class MelodicShot : SingleArrowCard
{
    public override IReadOnlyList<string> VarNames => ["Damage", "CalculationBase", "CalculationExtra"];
    public override void OnPlay(ref Exec x)
        => Kernel.ShootArrow(ref x, FoolEffects.Var(ref x, 0),
            FoolEffects.Int(ref x, 1) + FoolEffects.Int(ref x, 2) * Kernel.MelodiesThisTurn(x.B));
}

internal static class FoolChoices
{
    internal static void Pick(ref Exec x, Pile from, Pile to, bool shootOnly)
    {
        int size = Piles.Count(x.B, from), start = Piles.Start(x.B, from), count = 0;
        Span<int> options = stackalloc int[size];
        for (int i = 0; i < size; i++)
        {
            Card card = x.B.Cards[start + i];
            if (!shootOnly || (x.R.Card(card.Id, card.Upgrade).Tags & CardTag.Shoot) != 0)
                options[count++] = i;
        }
        Span<int> selected = stackalloc int[1];
        ChoiceEffect effect = to == Pile.Hand ? ChoiceEffect.MoveToHand : ChoiceEffect.MoveToDrawTop;
        if (!Kernel.ChooseCards(ref x, effect, from, options[..count], 1, selected, out int chosen)) return;
        Kernel.MoveSelected(ref x.B, from, selected[..chosen], to,
            to == Pile.Hand ? PilePosition.Bottom : PilePosition.Top);
    }
}

[ContentId("LOCKED_IN")]
public sealed class LockedIn : CardDef
{
    public override void OnPlay(ref Exec x) => FoolChoices.Pick(ref x, Pile.Draw, Pile.Hand, true);
}

[ContentId("ARROW_STAB")]
public sealed class ArrowStab : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Attack(ref x);
        if (!x.NeedsChoice && Kernel.AvailableArrows(x.B) > 0 && x.B.HandCount < x.B.MaxHandSize)
            FoolChoices.Pick(ref x, Pile.Discard, Pile.Hand, true);
    }
}

[ContentId("RECALL")]
public sealed class Recall : CardDef
{
    public override void OnPlay(ref Exec x)
    {
        if (!FoolEffects.Impaled(ref x)) return;
        Kernel.DislodgeAllArrows(ref x, x.Target);
        if (!x.NeedsChoice) FoolChoices.Pick(ref x, Pile.Discard, Pile.Draw, false);
    }
}

[ContentId("CHO_CHORUS")]
public sealed class ChoChorus : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Block(ref x);
        if (x.NeedsChoice) return;
        int count = 0;
        Span<int> selected = stackalloc int[x.B.HandCount];
        for (int i = 0; i < x.B.HandCount; i++)
            if ((x.R.Card(x.B.Cards[i].Id, x.B.Cards[i].Upgrade).Tags & (CardTag.Wah | CardTag.Ki)) != 0)
                selected[count++] = i;
        Span<byte> marks = stackalloc byte[count];
        Span<byte> outer = stackalloc byte[count];
        int saved = Kernel.ReserveTransientMarks(ref x, count, out int first);
        Kernel.MarkPileCards(ref x.B, Pile.Hand, selected[..count], marks, outer, first);
        try
        {
            for (int i = 0; i < count; i++)
            {
                if (Kernel.TryFindUniqueTransientMark(x.B, marks[i], out Pile pile, out int index))
                    Kernel.AutoPlay(ref x, pile, index);
                if (x.NeedsChoice) return;
            }
        }
        finally
        {
            Kernel.RestoreMarkedCards(ref x.B, marks, outer);
            Kernel.ReleaseTransientMarks(ref x, saved, first, count);
        }
    }
}
