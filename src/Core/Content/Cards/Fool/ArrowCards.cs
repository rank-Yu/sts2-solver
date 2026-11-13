// Native 0.113.0 (16ac0fe3); compared with CombatSolver
// src/Engine/InCombat/Mirrors/Cards/OnPlay/FoolArrowCardMirrors.cs at
// f48b69c590cb8b689ec7fdf033c2f62e625e0f1e (MIT, Copyright (c) 2026 Torch1230 and contributors).
using Sts2Solver.Simulation.Combat;
namespace Sts2Solver.Content.Cards.Fool;

[ContentId("STRIKE_FOOL")]
public sealed class StrikeFool : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];
    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);
}

[ContentId("DEFEND_FOOL")]
public sealed class DefendFool : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];
    public override void OnPlay(ref Exec x) => CardEffects.GeneralBlock(ref x, 0);
}

public abstract class SingleArrowCard : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];
    public override IReadOnlyList<string> AppliedPowers => ["IMPALED_POWER"];
    public override void OnPlay(ref Exec x)
        => Kernel.ShootArrow(ref x, CardEffects.Var(x.B, x.R, x.Card, 0));
}

[ContentId("FOOLISH_SHOT")] public sealed class FoolishShot : SingleArrowCard;
[ContentId("DIVINE_SHOT")] public sealed class DivineShot : SingleArrowCard;
[ContentId("QUICK_SHOT")] public sealed class QuickShot : SingleArrowCard;
[ContentId("SKY_SHOT")] public sealed class SkyShot : SingleArrowCard;

[ContentId("RAPID_SHOT")]
public sealed class RapidShot : SingleArrowCard
{
    public override IReadOnlyList<string> VarNames => ["Damage", "HitCount"];
    public override void OnPlay(ref Exec x)
        => Kernel.ShootArrow(ref x, CardEffects.Var(x.B, x.R, x.Card, 0),
            (int)CardEffects.Var(x.B, x.R, x.Card, 1));
}

[ContentId("REPEAT_SHOT")]
public sealed class RepeatShot : SingleArrowCard
{
    public override void OnPlay(ref Exec x)
    {
        int hits = Kernel.ResolveEnergyXValue(ref x);
        if (hits > 0) Kernel.ShootArrow(ref x, CardEffects.Var(x.B, x.R, x.Card, 0), hits);
    }
}

[ContentId("HEAVY_SHOT")]
public sealed class HeavyShot : SingleArrowCard
{
    public override IReadOnlyList<string> VarNames => ["Damage", "AimMultiplier"];
}

[ContentId("NOCK")]
public sealed class Nock : CardDef
{
    public override IReadOnlyList<string> VarNames => ["AimPower"];
    public override void OnPlay(ref Exec x)
        => Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.AimPower>(),
            (int)CardEffects.Var(x.B, x.R, x.Card, 0), Limits.PlayerOwner);
}

[ContentId("QUIVER")]
public sealed class Quiver : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Repeat"];
    public override void OnPlay(ref Exec x)
    {
        for (int i = 0, count = (int)CardEffects.Var(x.B, x.R, x.Card, 0); i < count; i++)
            Kernel.GainArrow(ref x.B);
    }
}
