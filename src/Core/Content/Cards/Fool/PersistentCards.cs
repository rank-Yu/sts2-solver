// 对照游戏 0.113.0 (16ac0fe3) 的同名 CardModel。
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Content.Powers;
namespace Sts2Solver.Content.Cards.Fool;

[ContentId("YAH")]
public sealed class Yah : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Energy"];
    public override IReadOnlyList<string> AppliedPowers => ["YAH_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<YahPower>(ref x, FoolEffects.Int(ref x, 0));
    }
}

[ContentId("EXPLOSIVE_ARROWS")]
public sealed class ExplosiveArrows : CardDef
{
    public override IReadOnlyList<string> VarNames => ["ExplosiveArrowsPower"];
    public override IReadOnlyList<string> AppliedPowers => ["EXPLOSIVE_ARROWS_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<ExplosiveArrowsPower>(ref x, FoolEffects.Int(ref x, 0));
    }
}

[ContentId("KI_VERSE")]
public sealed class KiVerse : CardDef
{
    public override IReadOnlyList<string> VarNames => ["KiVersePower"];
    public override IReadOnlyList<string> AppliedPowers => ["KI_VERSE_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<KiVersePower>(ref x, FoolEffects.Int(ref x, 0));
    }
}

[ContentId("ENNUI")]
public sealed class Ennui : CardDef
{
    public override IReadOnlyList<string> VarNames => ["EnnuiPower"];
    public override IReadOnlyList<string> AppliedPowers => ["ENNUI_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<EnnuiPower>(ref x, FoolEffects.Int(ref x, 0));
        if (x.NeedsChoice) return;
        int power = Kernel.FindPower(x.B, Limits.PlayerOwner, Registry.PowerId<EnnuiPower>());
        if (power >= 0) x.B.Powers[power].Aux0++;
    }
}

[ContentId("LULLABY")]
public sealed class Lullaby : CardDef
{
    public override IReadOnlyList<string> VarNames => ["LullabyPower"];
    public override IReadOnlyList<string> AppliedPowers => ["LULLABY_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<LullabyPower>(ref x, FoolEffects.Int(ref x, 0));
        if (x.NeedsChoice) return;
        int power = Kernel.FindPower(x.B, Limits.PlayerOwner, Registry.PowerId<LullabyPower>());
        if (power >= 0) x.B.Powers[power].Aux0 |= Kernel.MelodiesThisTurn(x.B) == 3 ? 1 : 0;
    }
}

[ContentId("ECSTASY")]
public sealed class Ecstasy : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];
    public override IReadOnlyList<string> AppliedPowers => ["ECSTASY_POWER"];
    public override void OnPlay(ref Exec x) => FoolEffects.Apply<EcstasyPower>(ref x, FoolEffects.Int(ref x, 0));
}

[ContentId("SIMPLETON")]
public sealed class Simpleton : CardDef
{
    public override IReadOnlyList<string> VarNames => ["SimpletonPower"];
    public override IReadOnlyList<string> AppliedPowers => ["SIMPLETON_POWER"];
    public override void OnPlay(ref Exec x) => FoolEffects.Apply<SimpletonPower>(ref x, FoolEffects.Int(ref x, 0));
}

[ContentId("QUIETUS")]
public sealed class Quietus : CardDef
{
    public override IReadOnlyList<string> VarNames => ["QuietusPower"];
    public override IReadOnlyList<string> AppliedPowers => ["QUIETUS_POWER"];
    public override void OnPlay(ref Exec x) => FoolEffects.Apply<QuietusPower>(ref x, FoolEffects.Int(ref x, 0));
}

[ContentId("ENCORE")]
public sealed class Encore : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Energy", "Cards", "EncorePower"];
    public override IReadOnlyList<string> AppliedPowers => ["ENCORE_POWER"];
    public override void OnPlay(ref Exec x)
    {
        Kernel.GainEnergy(ref x, FoolEffects.Var(ref x, 0));
        if (!x.NeedsChoice) Kernel.Draw(ref x, FoolEffects.Var(ref x, 1), false);
        if (!x.NeedsChoice) FoolEffects.Apply<EncorePower>(ref x, FoolEffects.Int(ref x, 2));
    }
}

[ContentId("BLOT_OUT_THE_SKY")]
public sealed class BlotOutTheSky : CardDef
{
    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.CharacterShots];
    public override void OnPlay(ref Exec x)
    {
        Span<Card> cards = stackalloc Card[1];
        int count = Kernel.SampleCardPool(ref x.B, x.R, CardPools.CharacterShots, 1, true,
            RngStream.CombatCardGeneration, cards);
        if (count == 0) return;
        Kernel.MakeFreeThisTurn(x.R, ref cards[0]);
        Kernel.AddGeneratedCard(ref x, cards[0], Pile.Hand);
    }
}

[ContentId("MYTHIC_FORM")]
public sealed class MythicForm : CardDef
{
    public override IReadOnlyList<string> AppliedPowers => ["MYTHIC_FORM_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<MythicFormPower>(ref x, 1);
        if (x.NeedsChoice || x.Card.Upgrade == 0) return;
        int power = Kernel.FindPower(x.B, Limits.PlayerOwner, Registry.PowerId<MythicFormPower>());
        if (power >= 0) x.B.Powers[power].Aux0++;
    }
}
