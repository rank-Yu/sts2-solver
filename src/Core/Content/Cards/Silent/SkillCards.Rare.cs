using Sts2Solver.Content.Powers;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Silent;

[ContentId("SHADOW_STEP")]
public sealed class ShadowStep : CardDef
{
    public override void OnPlay(ref Exec x)
    {
        // CardOnPlaySupport.ShadowStep: discard the whole hand, then apply ShadowStepPower 1.
        Kernel.DiscardAllInHand(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.ShadowStepPower>(), 1, Limits.PlayerOwner);
    }
}

[ContentId("STORM_OF_STEEL")]
public sealed class StormOfSteel : ShivGeneratingCardDef
{
    public override void OnPlay(ref Exec x)
    {
        // CardPileOnPlaySupport.ReplaceHandWithShivs: discard the whole hand, then add one Shiv per
        // discarded card (upgraded when this card is upgraded).
        int discarded = x.B.HandCount;
        Kernel.DiscardAllInHand(ref x, 0);
        if (x.NeedsChoice)
            return;
        SilentContent.GenerateShivs(ref x, discarded, upgraded: x.Card.Upgrade != 0);
    }
}

/// <summary>
/// Native <c>Nightmare</c>: choose a hand card, then apply an instanced NightmarePower that clones the
/// (affliction-cleared) selected card at the next BeforeHandDraw. Existing engine:
/// <c>CardChoiceSupport.GetSpec</c>'s Nightmare case, <c>CardChoiceResolution.ApplyNightmare</c> and
/// <c>SimulatedCombatState.SetNightmareSelection</c>.
/// </summary>
[ContentId("NIGHTMARE")]
public sealed class Nightmare : CardDef
{
    public override bool IsIdentityContent => true;

    public override bool MayGenerateIdentityContent => true;

    public override IReadOnlyList<string> AppliedPowers => ["NIGHTMARE_POWER"];

    // Nightmare's OnPlay only starts the hand selection; the power application is its post-choice
    // effect (CardChoiceResolution.ApplyNightmare), so the card effect itself is empty.
    public override void OnPlay(ref Exec x)
    {
    }

    public override void OwnChoice(ref Exec x)
    {
        Span<int> options = stackalloc int[x.B.HandCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.Nightmare, Pile.Hand, options, 1, selected,
                out int chosen))
            return;
        if (chosen == 0)
            return;
        if (!Registry.TryPower("NIGHTMARE_POWER", out ushort power))
            throw new UnsupportedException("Nightmare 需要 NIGHTMARE_POWER 定义。");
        Card snapshot = Kernel.CloneCard(Piles.At(ref x.B, Pile.Hand, selected[0]));
        snapshot.AfflictionId = 0;
        snapshot.AfflictionAmount = 0;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, power, 3, Limits.PlayerOwner);
        if (x.NeedsChoice)
            return;
        int row = Kernel.FindLastPower(x.B, Limits.PlayerOwner, power);
        if (row < 0)
            return;
        int slot = Kernel.AllocateDetachedCard(ref x.B, snapshot);
        x.B.Powers[row].Aux0 = slot + 1;
    }
}

[ContentId("CORROSIVE_WAVE")]
public sealed class CorrosiveWave : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CorrosiveWave"];

    public override void OnPlay(ref Exec x)
        => CardEffects.ApplyToOwner(ref x, Registry.PowerId<Powers.CorrosiveWavePower>(), 0);
}

[ContentId("MALAISE")]
public sealed class Malaise : CardDef
{
    // CorePowerSupport.ApplyCardPowers.Malaise: X value (after ModifyXValue) plus one when upgraded;
    // Strength -malaise then Weak malaise on the target. The card has no dynamic variables.
    public override void OnPlay(ref Exec x)
    {
        if (x.Target == Limits.NoOwner)
            return;
        int amount = Kernel.ResolveEnergyXValue(ref x) + (x.Card.Upgrade != 0 ? 1 : 0);
        Kernel.ApplyPower(ref x, x.Target, Registry.PowerId<Powers.StrengthPower>(), -amount,
            Limits.PlayerOwner);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, x.Target, Registry.PowerId<Powers.WeakPower>(), amount,
            Limits.PlayerOwner);
    }
}

[ContentId("BULLET_TIME")]
public sealed class BulletTime : CardDef
{
    // CardOnPlaySupport.BulletTime: every non-X hand card becomes free this turn, then NoDraw 1.
    public override void OnPlay(ref Exec x)
    {
        for (int index = 0; index < x.B.HandCount; index++)
        {
            ref Card card = ref x.B.Cards[index];
            if ((x.R.Card(card.Id, card.Upgrade).CostsX))
                continue;
            Kernel.MakeFreeThisTurn(x.R, ref card);
        }
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.NoDrawPower>(), 1,
            Limits.PlayerOwner);
    }
}

[ContentId("OUTBREAK")]
public sealed class Outbreak : CardDef
{
    public override IReadOnlyList<string> VarNames => ["PoisonPower"];

    // CardOnPlaySupport.Batch042.ApplyOutbreak: Poison every living hittable enemy, then trigger each
    // one's Poison (snapshot order) with the deferred death pass after each.
    public override void OnPlay(ref Exec x)
    {
        ushort poison = Registry.PowerId<Powers.PoisonPower>();
        int amount = (int)CardEffects.Var(x, 0);
        Span<byte> targets = stackalloc byte[Limits.Enemies];
        int count = 0;
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte enemy = Kernel.EnemyOwner(x.B.Roster[position]);
            if (!Kernel.IsHittable(x.B, enemy))
                continue;
            targets[count++] = enemy;
            Kernel.ApplyPower(ref x, enemy, poison, amount, Limits.PlayerOwner);
            if (x.NeedsChoice)
                return;
        }
        Span<byte> single = stackalloc byte[1];
        for (int index = 0; index < count; index++)
        {
            single[0] = targets[index];
            if (!Kernel.TriggerPoison(ref x, single))
                return;
            Kernel.ApplyEnemyDeathPowers(ref x);
            if (x.NeedsChoice)
                return;
        }
    }
}

[ContentId("BLADE_OF_INK")]
public sealed class BladeOfInk : ShivGeneratingCardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override void OnPlay(ref Exec x)
        => SilentContent.GenerateInkyShivs(ref x, (int)CardEffects.Var(x, 0));
}

[ContentId("ADRENALINE")]
public sealed class Adrenaline : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Energy", "Cards"];

    // CardDrawCardMirrors.AdrenalineOnPlay: gain Energy, then draw Cards.
    public override void OnPlay(ref Exec x)
    {
        Kernel.GainEnergy(ref x, CardEffects.Var(x, 0));
        if (x.NeedsChoice)
            return;
        Kernel.Draw(ref x, CardEffects.Var(x, 1), fromHandDraw: false);
    }
}

/// <summary>
/// <c>KnifeTrap</c>: auto-play every Shiv in the exhaust pile at the selected target. An upgraded
/// KnifeTrap upgrades each Shiv first. Existing engine: <c>CardOnPlaySupport.Batch043.ApplyKnifeTrap</c>
/// and <c>CardExecutionSupport.AutoPlay</c>; the exhaust pile is snapshotted in order and each
/// auto-played Shiv exhausts back to the pile, so the next search takes the next byte-equal copy.
/// </summary>
[ContentId("KNIFE_TRAP")]
public sealed class KnifeTrap : CardDef
{
    public override void OnPlay(ref Exec x)
    {
        if (x.Target == Limits.NoOwner)
            throw new UnsupportedException("KnifeTrap 需要目标。");
        int exhaust = x.B.ExhaustCount;
        if (exhaust == 0)
            return;
        Span<Card> shivs = stackalloc Card[exhaust];
        int count = 0;
        int start = Piles.Start(x.B, Pile.Exhaust);
        for (int offset = 0; offset < exhaust; offset++)
        {
            Card card = x.B.Cards[start + offset];
            if ((x.R.Card(card.Id, card.Upgrade).Tags & CardTag.Shiv) != 0)
                shivs[count++] = card;
        }
        bool upgrade = x.Card.Upgrade > 0;
        for (int item = 0; item < count; item++)
        {
            Card wanted = shivs[item];
            int found = -1;
            int pileCount = x.B.ExhaustCount;
            int pileStart = Piles.Start(x.B, Pile.Exhaust);
            for (int offset = 0; offset < pileCount; offset++)
            {
                ref readonly Card candidate = ref x.B.Cards[pileStart + offset];
                if (candidate.Id == wanted.Id && candidate.Upgrade == wanted.Upgrade
                    && (x.R.Card(candidate.Id, candidate.Upgrade).Tags & CardTag.Shiv) != 0)
                {
                    found = offset;
                    break;
                }
            }
            if (found < 0)
                throw new UnsupportedException("KnifeTrap 的 Shiv 在结算前离开了消耗堆。");
            if (upgrade)
            {
                Kernel.UpgradeCard(x.R, ref Piles.At(ref x.B, Pile.Exhaust, found));
            }
            if (!Kernel.AutoPlay(ref x, Pile.Exhaust, found, x.Target))
                return;
            if (x.NeedsChoice)
                return;
        }
    }
}

[ContentId("BURST")]
public sealed class Burst : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Skills"];

    public override IReadOnlyList<string> AppliedPowers => ["BURST_POWER"];

    // CardPowerOnPlaySupport: combat.Apply<BurstPower>(owner, card.DynamicVars["Skills"].IntValue, owner).
    public override void OnPlay(ref Exec x)
        => Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<BurstPower>(), (int)CardEffects.Var(x, 0), Limits.PlayerOwner);
}

[ContentId("SHADOWMELD")]
public sealed class Shadowmeld : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Power"];

    public override IReadOnlyList<string> AppliedPowers => ["SHADOWMELD_POWER"];

    // CardPowerOnPlaySupport.SToZ: combat.Apply<ShadowmeldPower>(owner, "Power", owner).
    public override void OnPlay(ref Exec x)
        => Kernel.ApplyPower(ref x, Limits.PlayerOwner,
            Registry.PowerId<ShadowmeldPower>(), (int)CardEffects.Var(x, 0), Limits.PlayerOwner);
}
