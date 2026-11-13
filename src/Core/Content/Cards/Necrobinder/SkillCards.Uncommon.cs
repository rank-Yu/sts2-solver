using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Necrobinder;

/// <summary>
/// <c>Deathbringer</c>: for each hittable enemy in roster order, apply Doom then Weak
/// (<c>CardOnPlaySupport.Apply</c>'s Deathbringer case).
/// </summary>
[ContentId("DEATHBRINGER")]
public sealed class Deathbringer : CardDef
{
    public override IReadOnlyList<string> VarNames => ["DoomPower", "WeakPower"];

    public override void OnPlay(ref Exec x)
    {
        int doom = (int)NecrobinderContent.Var(ref x, 0);
        int weak = (int)NecrobinderContent.Var(ref x, 1);
        ushort doomId = Registry.PowerId<Powers.DoomPower>();
        ushort weakId = Registry.PowerId<Powers.WeakPower>();
        // Native ApplyAll enumerates a HittableEnemies snapshot; a Doom application can kill its target
        // (Sleight of Flesh) and compact the live roster, which must not skip later enemies.
        Span<byte> owners = stackalloc byte[Limits.Enemies];
        int count = x.B.RosterCount;
        for (int position = 0; position < count; position++)
            owners[position] = Kernel.EnemyOwner(x.B.Roster[position]);
        for (int position = 0; position < count; position++)
        {
            byte enemy = owners[position];
            if (!Kernel.IsHittable(x.B, enemy))
                continue;
            Kernel.ApplyPower(ref x, enemy, doomId, doom, Limits.PlayerOwner);
            if (x.NeedsChoice)
                return;
            Kernel.ApplyPower(ref x, enemy, weakId, weak, Limits.PlayerOwner);
            if (x.NeedsChoice)
                return;
        }
    }
}

[ContentId("PUTREFY")]
public sealed class Putrefy : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Power"];

    public override void OnPlay(ref Exec x)
    {
        int amount = (int)NecrobinderContent.Var(ref x, 0);
        CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.WeakPower>(), 0);
        if (x.NeedsChoice || x.Target == Limits.NoOwner)
            return;
        Kernel.ApplyPower(ref x, x.Target, Registry.PowerId<Powers.VulnerablePower>(), amount,
            Limits.PlayerOwner);
    }
}

/// <summary>
/// <c>NoEscape</c>: apply Doom equal to CalculationBase + CalculationExtra × floor(target Doom /
/// DoomThreshold) (<c>CalculatedVarSpecRegistry.NoEscape</c>).
/// </summary>
[ContentId("NO_ESCAPE")]
public sealed class NoEscape : CardDef
{
    public override IReadOnlyList<string> VarNames => ["DoomThreshold", "CalculationBase", "CalculationExtra"];

    public override void OnPlay(ref Exec x)
    {
        if (x.Target == Limits.NoOwner)
            return;
        int doom = Kernel.GetAmount(x.B, x.Target, Registry.PowerId<Powers.DoomPower>());
        decimal multiplier = Math.Floor(doom / NecrobinderContent.Var(ref x, 0));
        int amount = (int)NecrobinderContent.Calculated(ref x, 1, 2, multiplier);
        Kernel.ApplyPower(ref x, x.Target, Registry.PowerId<Powers.DoomPower>(), amount,
            Limits.PlayerOwner);
    }
}

/// <summary>
/// <c>DeathsDoor</c>: gain block once, plus the Repeat variable's count when Doom was applied by the
/// player this turn (<c>CardEffectSpecRegistry</c>'s DeathsDoor case reads
/// <c>WasDoomAppliedThisTurn</c>).
/// </summary>
[ContentId("DEATHS_DOOR")]
public sealed class DeathsDoor : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block", "Repeat"];

    public override void OnPlay(ref Exec x)
    {
        int gains = 1;
        if (x.B.Counter(Counter.DoomAppliedThisTurn) > 0)
            gains += (int)NecrobinderContent.Var(ref x, 1);
        for (int index = 0; index < gains; index++)
        {
            CardEffects.GeneralBlock(ref x, 0);
            if (x.NeedsChoice)
                return;
        }
    }
}

/// <summary>
/// Native <c>Delay</c>: gain <c>Block</c>, then apply EnergyNextTurnPower. Existing engine:
/// the inferred block recipe plus <c>CardEffectSpecRegistry</c>'s Delay energy application.
/// </summary>
[ContentId("DELAY")]
public sealed class Delay : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block", "Energy"];

    public override IReadOnlyList<string> AppliedPowers => ["ENERGY_NEXT_TURN_POWER"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice)
            return;
        if (!Registry.TryPower("ENERGY_NEXT_TURN_POWER", out ushort power))
            throw new UnsupportedException("Delay 需要 ENERGY_NEXT_TURN_POWER 定义。");
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, power, (int)NecrobinderContent.Var(ref x, 1),
            Limits.PlayerOwner);
    }
}

/// <summary>
/// <c>Spur</c>: summon Osty for Summon, then heal it for Heal
/// (<c>CardOnPlaySupport.Batch043</c>'s Spur case).
/// </summary>
[ContentId("SPUR")]
public sealed class Spur : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Summon", "Heal"];

    public override void OnPlay(ref Exec x)
    {
        Kernel.SummonOsty(ref x, (int)NecrobinderContent.Var(ref x, 0));
        if (x.NeedsChoice)
            return;
        Kernel.HealOsty(ref x, (int)NecrobinderContent.Var(ref x, 1));
    }
}

/// <summary>
/// <c>Cleanse</c>: summon Osty, then exhaust one draw-pile card
/// (<c>CardOnPlaySupport.Batch043</c> plus <c>CardChoiceSupport</c>'s Cleanse Exhaust spec).
/// </summary>
[ContentId("CLEANSE")]
public sealed class Cleanse : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Summon"];

    public override void OnPlay(ref Exec x)
    {
        Kernel.SummonOsty(ref x, (int)NecrobinderContent.Var(ref x, 0));
    }

    public override void OwnChoice(ref Exec x)
    {
        Span<int> options = stackalloc int[x.B.DrawCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.Exhaust, Pile.Draw, options, 1, selected,
                out int chosen))
        {
            return;
        }
        if (chosen > 0)
            Kernel.Exhaust(ref x, Pile.Draw, selected[0]);
    }
}

/// <summary>
/// <c>Dirge</c>: X summons plus X Soul cards added to the draw pile at random positions
/// (<c>CardOnPlaySupport.Batch043</c>'s ApplyDirge).
/// </summary>
[ContentId("DIRGE")]
public sealed class Dirge : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Summon"];

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SOUL", 0), ("SOUL", 1)];

    public override void OnPlay(ref Exec x)
    {
        int xValue = Kernel.ResolveEnergyXValue(ref x);
        int summon = (int)NecrobinderContent.Var(ref x, 0);
        for (int index = 0; index < xValue; index++)
        {
            Kernel.SummonOsty(ref x, summon);
            if (x.NeedsChoice)
                return;
        }
        bool upgraded = x.Card.Upgrade > 0;
        for (int index = 0; index < xValue; index++)
        {
            Card soul = NecrobinderContent.CreateSoul(x.B, x.R, upgraded);
            if (!Kernel.AddGeneratedCard(ref x, soul, Pile.Draw, PilePosition.Random))
                return;
            if (x.NeedsChoice)
                return;
        }
    }
}

/// <summary>
/// <c>BorrowedTime</c>: gain Energy, then apply BorrowedTimePower for ExtraCost
/// (<c>CardOnPlaySupport</c>: energy first, then the power).
/// </summary>
[ContentId("BORROWED_TIME")]
public sealed class BorrowedTime : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Energy", "ExtraCost"];

    public override void OnPlay(ref Exec x)
    {
        Kernel.GainEnergy(ref x, NecrobinderContent.Var(ref x, 0));
        if (x.NeedsChoice)
            return;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.BorrowedTimePower>(),
            (int)NecrobinderContent.Var(ref x, 1), Limits.PlayerOwner);
    }
}

/// <summary>
/// <c>EnfeeblingTouch</c>: apply a temporary Strength loss to the target
/// (<c>CardOnPlaySupport</c>'s ApplyTemporaryStrengthLoss).
/// </summary>
[ContentId("ENFEEBLING_TOUCH")]
public sealed class EnfeeblingTouch : CardDef
{
    public override IReadOnlyList<string> VarNames => ["StrengthLoss"];

    public override void OnPlay(ref Exec x)
    {
        if (x.Target == Limits.NoOwner)
            return;
        Kernel.ApplyTemporaryStrength(ref x, x.Target,
            Registry.PowerId<Powers.EnfeeblingTouchPower>(), (int)NecrobinderContent.Var(ref x, 0),
            Limits.PlayerOwner, -1);
    }
}

/// <summary>
/// <c>Dredge</c>: move min(Cards, hand room) cards from the discard pile to hand
/// (<c>CardChoiceSupport</c>'s Dredge spec).
/// </summary>
[ContentId("DREDGE")]
public sealed class Dredge : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override void OnPlay(ref Exec x)
    {
    }

    public override void OwnChoice(ref Exec x)
    {
        int room = Math.Max(0, x.B.MaxHandSize - x.B.HandCount);
        int count = Math.Min((int)NecrobinderContent.Var(ref x, 0), room);
        if (count <= 0 || x.B.DiscardCount == 0)
            return;
        Span<int> options = stackalloc int[x.B.DiscardCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[count];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.MoveToHand, Pile.Discard, options, count,
                selected, out int chosen))
        {
            return;
        }
        if (chosen > 0)
            Kernel.MoveSelected(ref x.B, Pile.Discard, selected[..chosen], Pile.Hand,
                PilePosition.Bottom);
    }
}

/// <summary>
/// <c>CaptureSpirit</c>: unblockable unpowered attack damage with the card as source, then Souls at
/// random draw-pile positions (<c>CardOnPlaySupport.Batch042</c>'s CaptureSpirit case).
/// </summary>
[ContentId("CAPTURE_SPIRIT")]
public sealed class CaptureSpirit : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Cards"];

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SOUL", 0)];

    public override void OnPlay(ref Exec x)
    {
        if (x.Target == Limits.NoOwner)
            return;
        Span<byte> target = stackalloc byte[1];
        target[0] = x.Target;
        Kernel.Damage(ref x, target, NecrobinderContent.Var(ref x, 0),
            ValueProps.Unblockable | ValueProps.Unpowered | ValueProps.Move,
            Limits.PlayerOwner, hasCardSource: true, x.Card);
        if (x.NeedsChoice)
            return;
        int count = (int)NecrobinderContent.Var(ref x, 1);
        for (int index = 0; index < count; index++)
        {
            Card soul = NecrobinderContent.CreateSoul(x.B, x.R, upgraded: false);
            if (!Kernel.AddGeneratedCard(ref x, soul, Pile.Draw, PilePosition.Random)
                || x.NeedsChoice)
                return;
        }
    }
}

/// <summary>
/// <c>Melancholy</c>: player block; after any death that was not prevented, each Melancholy copy in a
/// combat pile loses its Energy variable from this combat's cost (<c>AfterDeathMirrors</c>).
/// </summary>
[ContentId("MELANCHOLY")]
public sealed class Melancholy : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block", "Energy"];

    /// <summary>
    /// The run listeners include the deck copy of this card, but <c>AfterDeathMirrors.HandleMelancholy</c>
    /// resolves it with <c>State.FindCard(card)</c> and does nothing when the card is not in a combat pile.
    /// The deck copy therefore has no effect and need not exist in Sts2Solver (only combat piles are imported).
    /// </summary>
    public override bool DeckCopyOutsideCombatIsInert => true;

    public override void OnPlay(ref Exec x) => CardEffects.GeneralBlock(ref x, 0);

    public override void AfterDeath(ref Exec x, Listener self, byte creature, bool wasRemovalPrevented,
        bool hasCardSource, in Card cardSource)
    {
        if (wasRemovalPrevented || (uint)self.Index >= (uint)x.B.CardCount)
            return;
        ref Card card = ref x.B.Cards[self.Index];
        if (card.Id != Id)
            return;
        int energy = (int)x.R.Card(card.Id, card.Upgrade).Vars[1];
        if (energy > 0)
        {
            Kernel.AddEnergyCostModifier(ref card, -energy, CostType.Relative,
                CostExpiration.EndOfCombat, reduceOnly: false);
        }
    }
}

/// <summary><c>Parse</c>: draw its Cards variable (the inferred owner-draw recipe).</summary>
[ContentId("PARSE")]
public sealed class Parse : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    public override void OnPlay(ref Exec x) => Kernel.Draw(ref x, (int)NecrobinderContent.Var(ref x, 0), fromHandDraw: false);
}
