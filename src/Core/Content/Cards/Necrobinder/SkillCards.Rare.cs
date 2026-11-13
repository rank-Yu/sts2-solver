using Sts2Solver.Content.Powers;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Necrobinder;

/// <summary>
/// <c>EndOfDays</c>: apply Doom to every hittable enemy, then kill the doomed ones together
/// (<c>DoomPower.GetDoomedCreatures</c> + <c>DoomPower.DoomKill</c>; <c>CardOnPlaySupport.Batch042</c>).
/// </summary>
[ContentId("END_OF_DAYS")]
public sealed class EndOfDays : CardDef
{
    public override IReadOnlyList<string> VarNames => ["DoomPower"];

    public override void OnPlay(ref Exec x)
    {
        int amount = (int)NecrobinderContent.Var(ref x, 0);
        ushort doomId = Registry.PowerId<Powers.DoomPower>();
        Span<byte> doomed = stackalloc byte[Limits.Enemies];
        int count = 0;
        // ApplyEndOfDays resolves power changes after each enemy; a Sleight of Flesh death compacts
        // the live roster, so visit the same snapshot the native loop sees.
        Span<byte> owners = stackalloc byte[Limits.Enemies];
        int rosterCount = Kernel.RosterOwners(x.B, owners);
        for (int position = 0; position < rosterCount; position++)
        {
            byte enemy = owners[position];
            if (!Kernel.IsHittable(x.B, enemy))
                continue;
            Kernel.ApplyPower(ref x, enemy, doomId, amount, Limits.PlayerOwner);
            if (x.NeedsChoice)
                return;
            // ApplyEndOfDays drains the pending amount changes before its Doom threshold check,
            // so ShroudPower's block gain lands while the combat is still running.
            Kernel.ResolvePowerAmountChanges(ref x);
            if (x.NeedsChoice)
                return;
            Kernel.ApplyEnemyDeathPowers(ref x);
            if (x.NeedsChoice)
                return;
        }
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte enemy = Kernel.EnemyOwner(x.B.Roster[position]);
            if (!Kernel.IsHittable(x.B, enemy))
                continue;
            int doom = Kernel.GetAmount(x.B, enemy, doomId);
            if (doom > 0 && x.B.Creature(enemy).CurrentHp <= doom)
                doomed[count++] = enemy;
        }
        if (count > 0)
            Kernel.DoomKill(ref x, doomed[..count]);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyEnemyDeathPowers(ref x);
    }
}

/// <summary>
/// <c>Sacrifice</c>: block for three times Osty's max HP, then kill the pet with force
/// (<c>BespokeCardMirrors.SacrificeOnPlay</c>). Nothing happens while Osty is missing.
/// </summary>
[ContentId("SACRIFICE")]
public sealed class Sacrifice : CardDef
{
    public override void OnPlay(ref Exec x)
    {
        if (!NecrobinderContent.HasOsty(x.B))
            return;
        decimal block = x.B.Osty.MaxHp * 3;
        Span<byte> osty = stackalloc byte[1];
        osty[0] = Limits.OstyOwner;
        Kernel.Kill(ref x, osty, force: true);
        if (x.NeedsChoice)
            return;
        Kernel.GainBlock(ref x, Limits.PlayerOwner, block, ValueProps.Move, fromCard: true);
    }
}

/// <summary>
/// <c>Seance</c>: choose Cards cards from the draw pile and transform each into <c>Soul</c>
/// (<c>CardChoiceSupport</c>'s Seance spec, <c>CardChoiceResolution</c>'s TransformSelectedCards).
/// The replacement is the level-0 canonical Soul; the upgrade only reduces Seance's cost.
/// </summary>
[ContentId("SEANCE")]
public sealed class Seance : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];

    // TransformSelectedCards creates the level-0 canonical Soul; without this declaration the rule
    // table has no Soul stats and the first transform reports an unsupported kernel boundary.
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SOUL", 0)];

    public override void OnPlay(ref Exec x)
    {
        if (x.B.DrawCount == 0)
            return;
        if (!Registry.TryCard("SOUL", out ushort soul))
            throw new UnsupportedException("Soul 没有 Sts2Solver 定义。");
        int count = Math.Min((int)CardEffects.Var(x, 0), x.B.DrawCount);
        if (count <= 0)
            return;
        Span<int> options = stackalloc int[x.B.DrawCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[count];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.Transform, Pile.Draw, options, count, selected,
                out int chosen))
        {
            return;
        }
        Kernel.TransformCards(ref x, Pile.Draw, selected[..chosen],
            Kernel.CreateCard(x.R, soul), upgrade: 0);
    }
}

/// <summary><c>Reanimate</c>: summon Osty for the Summon variable (<c>CardOnPlaySupport.Batch043</c>).</summary>
[ContentId("REANIMATE")]
public sealed class Reanimate : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Summon"];

    public override void OnPlay(ref Exec x) => Kernel.SummonOsty(ref x, (int)NecrobinderContent.Var(ref x, 0));
}

/// <summary>
/// <c>SharedFate</c>: lose PlayerStrengthLoss Strength, then apply EnemyStrengthLoss to the target
/// (<c>CorePowerSupport</c>'s SharedFate case).
/// </summary>
[ContentId("SHARED_FATE")]
public sealed class SharedFate : CardDef
{
    public override IReadOnlyList<string> VarNames => ["EnemyStrengthLoss", "PlayerStrengthLoss"];

    public override void OnPlay(ref Exec x)
    {
        ushort strength = Registry.PowerId<Powers.StrengthPower>();
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, strength,
            -(int)NecrobinderContent.Var(ref x, 1), Limits.PlayerOwner);
        if (x.NeedsChoice || x.Target == Limits.NoOwner)
            return;
        Kernel.ApplyPower(ref x, x.Target, strength, -(int)NecrobinderContent.Var(ref x, 0),
            Limits.PlayerOwner);
    }
}

/// <summary><c>Oblivion</c>: apply OblivionPower to the target for its Doom variable (<c>CardOnPlaySupport</c>).</summary>
[ContentId("OBLIVION")]
public sealed class Oblivion : CardDef
{
    public override IReadOnlyList<string> VarNames => ["DoomPower"];

    public override void OnPlay(ref Exec x)
    {
        if (x.Target == Limits.NoOwner)
            return;
        Kernel.ApplyPower(ref x, x.Target, Registry.PowerId<Powers.OblivionPower>(),
            (int)NecrobinderContent.Var(ref x, 0), Limits.PlayerOwner);
    }
}

/// <summary>
/// <c>Undeath</c>: player block, then a clone of this card into the discard pile
/// (<c>CardEffectSpecRegistry</c>'s Undeath completion + <c>CardModel.CreateClone</c>).
/// </summary>
[ContentId("UNDEATH")]
public sealed class Undeath : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("UNDEATH", 0), ("UNDEATH", 1)];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice)
            return;
        Card clone = Kernel.CloneCard(x.Card);
        Kernel.AddGeneratedCard(ref x, clone, Pile.Discard);
    }
}

/// <summary>
/// <c>Transfigure</c>: add one this-combat cost to a selected hand card and increase its base replay
/// count (<c>CardChoiceResolution.ModifySelectedCard</c>).
/// </summary>
[ContentId("TRANSFIGURE")]
public sealed class Transfigure : CardDef
{
    public override void OnPlay(ref Exec x)
    {
    }

    public override void OwnChoice(ref Exec x)
    {
        Span<int> options = stackalloc int[x.B.HandCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.Modify, Pile.Hand, options, 1, selected,
                out int chosen))
        {
            return;
        }
        if (chosen == 0)
            return;
        ref Card card = ref Piles.At(ref x.B, Pile.Hand, selected[0]);
        int cost = Kernel.EnergyCostWithLocalModifiers(x.B, card);
        if ((card.Flags & CardFlags.CostsX) == 0 && cost >= 0)
        {
            Kernel.AddEnergyCostModifier(ref card, 1, CostType.Relative, CostExpiration.EndOfCombat,
                reduceOnly: false);
        }
        card.ReplayCount = checked((byte)(card.ReplayCount + 1));
    }
}

/// <summary>
/// <c>Eidolon</c>: auto-play every Ethereal, playable card in the exhaust pile in pile order
/// (<c>CardOnPlaySupport.Batch043</c>'s ApplyEidolon). The snapshot follows the existing engine's
/// <c>ExhaustPile.Cards.Where(...).ToArray()</c>: positions are taken before the loop and shifted only by
/// the removals of earlier plays, so a card that exhausts back into the pile is not played again.
/// </summary>
[ContentId("EIDOLON")]
public sealed class Eidolon : CardDef
{
    public override void OnPlay(ref Exec x)
    {
        Span<int> positions = stackalloc int[x.B.ExhaustCount];
        int count = 0;
        for (int offset = 0; offset < x.B.ExhaustCount; offset++)
        {
            Card card = Piles.At(ref x.B, Pile.Exhaust, offset);
            if (Kernel.HasKeyword(x.B, card, Keywords.Ethereal)
                && !Kernel.HasKeyword(x.B, card, Keywords.Unplayable))
            {
                positions[count++] = offset;
            }
        }
        for (int index = 0; index < count; index++)
        {
            int position = positions[index];
            // A blocked auto-play still moves this card and the native loop attempts the rest.
            Kernel.AutoPlay(ref x, Pile.Exhaust, position);
            if (x.NeedsChoice || Kernel.IsOverOrEnding(x.B))
                return;
            for (int later = index + 1; later < count; later++)
            {
                if (positions[later] > position)
                    positions[later]--;
            }
        }
    }
}
