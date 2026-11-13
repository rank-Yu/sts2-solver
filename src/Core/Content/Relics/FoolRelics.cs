// 对照游戏 0.113.0 (16ac0fe3) 的同名 RelicModel；状态使用 Battle 的遗物计数器。
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Content.Powers;
using Sts2Solver.Content.Cards.Fool;
namespace Sts2Solver.Content.Relics;

// SongBow applies Aim on room entry, before the imported boundary.
[ContentId("SONG_BOW")]
public sealed class SongBow : RelicDef;

[ContentId("BIZARRE_LAMP")]
public sealed class BizarreLamp : RelicDef
{
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("JOY", 0), ("SADNESS", 0)];
    public override void BeforeHandDraw(ref Exec x, Listener self)
    {
        if (x.B.PlayerTurn > 1) return;
        Kernel.AddGeneratedCard(ref x, Kernel.CreateCard(x.R, Registry.CardId<Joy>()), Pile.Hand);
        if (!x.NeedsChoice)
            Kernel.AddGeneratedCard(ref x, Kernel.CreateCard(x.R, Registry.CardId<Sadness>()), Pile.Hand);
    }
}

[ContentId("EXERCISE_MAT")]
public sealed class ExerciseMat : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["StretchPower"];
    public override IReadOnlyList<string> AppliedPowers => ["STRETCH_POWER"];
    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side == Side.Player && x.B.PlayerTurn <= 1)
            Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<StretchPower>(),
                (int)x.R.RelicVar(Id, 0), Limits.PlayerOwner);
    }
}

[ContentId("FERTILIZER")]
public sealed class Fertilizer : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["StrengthPower"];
    public override IReadOnlyList<string> AppliedPowers => ["STRENGTH_POWER"];
    public override void AfterCardGeneratedForCombat(ref Exec x, Listener self, int position, byte creator)
    {
        if (creator == Limits.PlayerOwner && x.B.Cards[position].Id == Registry.CardId<Sadness>())
            Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<StrengthPower>(),
                (int)x.R.RelicVar(Id, 0), Limits.PlayerOwner);
    }
}

[ContentId("TWINE")]
public sealed class Twine : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];
    public override void AfterArrowDislodged(ref Exec x, Listener self, byte target, int arrow,
        bool hasCardSource, in Card cardSource)
        => Kernel.GainBlock(ref x, Limits.PlayerOwner, x.R.RelicVar(Id, 0), ValueProps.Unpowered, false);
}

[ContentId("WHITTLING_KNIFE")]
public sealed class WhittlingKnife : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["AimPower"];
    public override IReadOnlyList<string> AppliedPowers => ["AIM_POWER"];
    public override void AfterShuffle(ref Exec x, Listener self, byte shuffler)
    {
        if (shuffler == Limits.PlayerOwner)
            Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<AimPower>(),
                (int)x.R.RelicVar(Id, 0), Limits.PlayerOwner);
    }
}

[ContentId("CONDUCTORS_BATON")]
public sealed class ConductorsBaton : RelicDef
{
    public override bool HasCombatState => true;

    public override IReadOnlyList<string> AppliedPowers => ["CONDUCTORS_BATON_POWER"];
    public override void BeforeSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side == Side.Player) x.B.RelicState(x.R, self.Index).Counter = 0;
    }
    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (x.B.RelicState(x.R, self.Index).Counter != 0 || Kernel.MelodiesThisTurn(x.B) < 3
            || Kernel.IsOverOrEnding(x.B)) return;
        x.B.RelicState(x.R, self.Index).Counter = 1;
        Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<ConductorsBatonPower>(), 1, Limits.PlayerOwner);
    }
}

[ContentId("OCARINA")]
public sealed class Ocarina : RelicDef
{
    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.Melodies];
    public override void BeforeHandDraw(ref Exec x, Listener self)
    {
        if (x.B.PlayerTurn > 1) return;
        Span<Card> cards = stackalloc Card[1];
        int count = Kernel.SampleCardPool(ref x.B, x.R, CardPools.Melodies, 1, true,
            RngStream.CombatCardGeneration, cards);
        if (count > 0) Kernel.AddGeneratedCard(ref x, cards[0], Pile.Hand);
    }
}
