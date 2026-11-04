using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Ironclad;

[ContentId("SHRUG_IT_OFF")]
public sealed class ShrugItOff : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block", "Cards"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 0);
        Kernel.Draw(ref x, CardEffects.Var(x, 1), fromHandDraw: false);
    }
}

[ContentId("ARMAMENTS")]
public sealed class Armaments : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void OnPlay(ref Exec x)
    {
        // Inferred BlockVar recipe, then CorePowerSupport: the upgraded card upgrades every upgradable
        // hand card in hand order.
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice || x.Card.Upgrade == 0)
            return;
        for (int index = 0; index < x.B.HandCount; index++)
            Kernel.UpgradeCard(x.R, ref x.B.Cards[index]);
    }

    public override void OwnChoice(ref Exec x)
    {
        // GetSpec: Armaments when !IsUpgraded → Upgrade, Hand, 1, upgradable hand cards.
        if (x.Card.Upgrade != 0)
            return;
        Span<int> options = stackalloc int[x.B.HandCount];
        int count = 0;
        for (int index = 0; index < x.B.HandCount; index++)
        {
            ref readonly Card card = ref x.B.Cards[index];
            if (card.Upgrade < x.R.Card(card.Id, card.Upgrade).MaxUpgradeLevel)
                options[count++] = index;
        }
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.Upgrade, Pile.Hand, options[..count], 1, selected, out int chosen))
            return;
        Kernel.UpgradeSelected(ref x, Pile.Hand, selected[..chosen]);
    }
}

[ContentId("TRUE_GRIT")]
public sealed class TrueGrit : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];

    public override void OnPlay(ref Exec x)
    {
        // CardSelectionCardMirrors.TrueGritOnPlay: block, then the base card exhausts a random hand
        // card (Rng.CombatCardSelection.NextItem; no draw from an empty hand).
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice || x.Card.Upgrade != 0 || x.B.HandCount == 0)
            return;
        int pick = x.B.RngStream(RngStream.CombatCardSelection).NextInt(0, x.B.HandCount);
        Kernel.Exhaust(ref x, Pile.Hand, pick);
    }

    public override void OwnChoice(ref Exec x)
    {
        // GetSpec: TrueGrit when IsUpgraded → Exhaust, Hand, 1, every hand card.
        if (x.Card.Upgrade == 0)
            return;
        Span<int> options = stackalloc int[x.B.HandCount];
        for (int index = 0; index < options.Length; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.Exhaust, Pile.Hand, options, 1, selected, out int chosen))
            return;
        Kernel.ExhaustSelected(ref x, Pile.Hand, selected[..chosen]);
    }
}

[ContentId("HAVOC")]
public sealed class Havoc : CardDef
{
    public override void OnPlay(ref Exec x)
        => Kernel.AutoPlayFromDrawPile(ref x, 1, forceExhaust: true);
}

[ContentId("BLOOD_WALL")]
public sealed class BloodWall : CardDef
{
    public override IReadOnlyList<string> VarNames => ["HpLoss", "Block"];

    public override void OnPlay(ref Exec x)
    {
        // BloodWall.cs: lose HP first, with the card as the damage source, then gain block.
        CardEffects.LoseHpFromCard(ref x, CardEffects.Var(x, 0));
        if (x.NeedsChoice)
            return;
        CardEffects.GeneralBlock(ref x, 1);
    }
}

[ContentId("TAUNT")]
public sealed class Taunt : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block", "VulnerablePower"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralBlock(ref x, 0);
        if (x.NeedsChoice)
            return;
        CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.VulnerablePower>(), 1);
    }
}

[ContentId("TREMBLE")]
public sealed class Tremble : CardDef
{
    public override IReadOnlyList<string> VarNames => ["VulnerablePower"];

    public override void OnPlay(ref Exec x)
        => CardEffects.ApplyToTarget(ref x, Registry.PowerId<Powers.VulnerablePower>(), 0);
}
