// Native 0.113.0 (16ac0fe3), compared with CombatSolver's
// src/Engine/InCombat/Mirrors/Cards/OnPlay/FoolCardMirrors.cs at
// f48b69c590cb8b689ec7fdf033c2f62e625e0f1e (MIT, Copyright (c) 2026 Torch1230 and contributors).
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Content.Powers;
namespace Sts2Solver.Content.Cards.Fool;

internal static class FoolEffects
{
    internal static decimal Var(ref Exec x, int i) => CardEffects.Var(x.B, x.R, x.Card, i);
    internal static int Int(ref Exec x, int i) => (int)Var(ref x, i);
    internal static void Apply<T>(ref Exec x, int amount, bool enemy = false) where T : PowerDef
        => Kernel.ApplyPower(ref x, enemy ? x.Target : Limits.PlayerOwner, Registry.PowerId<T>(), amount, Limits.PlayerOwner);
    internal static void Generate<T>(ref Exec x, int count) where T : CardDef
    {
        for (int i = 0; i < count && !x.NeedsChoice; i++)
            Kernel.AddGeneratedCard(ref x, Kernel.CreateCard(x.R, Registry.CardId<T>()), Pile.Hand);
    }
    internal static void Attack(ref Exec x, int variable = 0, int hits = 1, bool all = false)
        => Kernel.CardAttack(ref x, Var(ref x, variable), hits,
            all ? AttackTargeting.AllOpponents : AttackTargeting.Single, x.Target);
    internal static void Block(ref Exec x, int variable = 0)
        => Kernel.GainBlock(ref x, Limits.PlayerOwner, Var(ref x, variable), ValueProps.Move, true);
    internal static bool Impaled(ref Exec x) => Kernel.ImpaledPowerIndex(x.B, x.Target) >= 0;
}
[ContentId("SADNESS")]
public sealed class Sadness : CardDef
{
    public override void OnPlay(ref Exec x)
    {

    }
}

[ContentId("JOY")]
public sealed class Joy : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Energy", "Cards"];
    public override void OnPlay(ref Exec x)
    {
        Kernel.GainEnergy(ref x, FoolEffects.Var(ref x, 0));
        if (!x.NeedsChoice) Kernel.Draw(ref x, FoolEffects.Var(ref x, 1), false);
    }
}

[ContentId("BRIGHTEN_UP")]
public sealed class BrightenUp : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Sadness", "Joy"];
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SADNESS", 0), ("JOY", 0)];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Generate<Sadness>(ref x, FoolEffects.Int(ref x, 0));
        if (!x.NeedsChoice) FoolEffects.Generate<Joy>(ref x, FoolEffects.Int(ref x, 1));
    }
}

[ContentId("BRACE")]
public sealed class Brace : CardDef
{
    public override IReadOnlyList<string> VarNames => ["AimPower"];
    public override IReadOnlyList<string> AppliedPowers => ["AIM_POWER"];
    public override void OnPlay(ref Exec x)
    {
        for (int i = 0, count = Kernel.ResolveEnergyXValue(ref x); i < count && !x.NeedsChoice; i++)
            FoolEffects.Apply<AimPower>(ref x, FoolEffects.Int(ref x, 0));
    }
}

[ContentId("CHO_FIRM")]
public sealed class ChoFirm : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];
    public override void OnPlay(ref Exec x)
    {
        int count = Kernel.AvailableArrows(x.B) > 0 ? 2 : 1;
        for (int i = 0; i < count && !x.NeedsChoice; i++) FoolEffects.Block(ref x);
    }
}

[ContentId("CHO_KI")]
public sealed class ChoKi : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block"];
    public override IReadOnlyList<string> AppliedPowers => ["STRETCH_POWER"];
    public override void OnPlay(ref Exec x)
    {
        int stretch = Kernel.GetAmount(x.B, Limits.PlayerOwner, Registry.PowerId<StretchPower>());
        FoolEffects.Block(ref x);
        if (!x.NeedsChoice && stretch > 0) FoolEffects.Apply<StretchPower>(ref x, stretch);
    }
}

[ContentId("CHO_READY")]
public sealed class ChoReady : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block", "AimPower"];
    public override IReadOnlyList<string> AppliedPowers => ["AIM_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Block(ref x);
        if (!x.NeedsChoice) FoolEffects.Apply<AimPower>(ref x, FoolEffects.Int(ref x, 1));
    }
}

[ContentId("CHO_BARBS")]
public sealed class ChoBarbs : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block", "ChoBarbsPower"];
    public override IReadOnlyList<string> AppliedPowers => ["CHO_BARBS_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Block(ref x);
        if (!x.NeedsChoice) FoolEffects.Apply<ChoBarbsPower>(ref x, FoolEffects.Int(ref x, 1));
    }
}

[ContentId("DEEP_STRETCH")]
public sealed class DeepStretch : CardDef
{
    public override IReadOnlyList<string> VarNames => ["StretchPower", "Cards"];
    public override IReadOnlyList<string> AppliedPowers => ["STRETCH_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<StretchPower>(ref x, FoolEffects.Int(ref x, 0));
        if (!x.NeedsChoice) Kernel.Draw(ref x, FoolEffects.Var(ref x, 1), false);
    }
}

[ContentId("DISLIKE_YOU")]
public sealed class DislikeYou : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "DislikeYouPower"];
    public override IReadOnlyList<string> AppliedPowers => ["DISLIKE_YOU_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Attack(ref x);
        if (!x.NeedsChoice) FoolEffects.Apply<DislikeYouPower>(ref x, FoolEffects.Int(ref x, 1), true);
    }
}

[ContentId("KI_HOP")]
public sealed class KiHop : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Cards"];
    public override void OnPlay(ref Exec x)
    {
        bool impaled = FoolEffects.Impaled(ref x);
        FoolEffects.Attack(ref x);
        if (!x.NeedsChoice && impaled) Kernel.Draw(ref x, FoolEffects.Var(ref x, 1), false);
    }
}

[ContentId("BOW_SMACK")]
public sealed class BowSmack : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Energy"];
    public override void OnPlay(ref Exec x)
    {
        bool impaled = FoolEffects.Impaled(ref x);
        FoolEffects.Attack(ref x);
        if (!x.NeedsChoice && impaled) Kernel.GainEnergy(ref x, FoolEffects.Var(ref x, 1));
    }
}

[ContentId("KI_WHIP")]
public sealed class KiWhip : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Energy"];
    public override IReadOnlyList<string> AppliedPowers => ["ENERGY_NEXT_TURN_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Attack(ref x);
        if (!x.NeedsChoice) FoolEffects.Apply<EnergyNextTurnPower>(ref x, FoolEffects.Int(ref x, 1));
    }
}

[ContentId("WAH_KI")]
public sealed class WahKi : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "HitCount", "Energy", "Cards"];
    public override IReadOnlyList<string> AppliedPowers => ["ENERGY_NEXT_TURN_POWER", "DRAW_CARDS_NEXT_TURN_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Attack(ref x, hits: FoolEffects.Int(ref x, 1));
        if (x.NeedsChoice) return;
        FoolEffects.Apply<EnergyNextTurnPower>(ref x, FoolEffects.Int(ref x, 2));
        if (!x.NeedsChoice) FoolEffects.Apply<DrawCardsNextTurnPower>(ref x, FoolEffects.Int(ref x, 3));
    }
}

[ContentId("KI_REST")]
public sealed class KiRest : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Energy"];
    public override IReadOnlyList<string> AppliedPowers => ["ENERGY_NEXT_TURN_POWER", "RETAIN_HAND_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<EnergyNextTurnPower>(ref x, FoolEffects.Int(ref x, 0));
        if (!x.NeedsChoice && (Kernel.PlayedMelody(x.B, CardTag.Wah) || Kernel.PlayedMelody(x.B, CardTag.Cho)))
            FoolEffects.Apply<RetainHandPower>(ref x, 1);
    }
}

[ContentId("SORROWFUL_STRIKE")]
public sealed class SorrowfulStrike : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Cards"];
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SADNESS", 0)];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Attack(ref x);
        if (!x.NeedsChoice) FoolEffects.Generate<Sadness>(ref x, FoolEffects.Int(ref x, 1));
    }
}

[ContentId("WAH_CHO")]
public sealed class WahCho : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block", "Damage"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Block(ref x);
        if (!x.NeedsChoice) FoolEffects.Attack(ref x, 1);
    }
}

[ContentId("WAH_CHO_KI")]
public sealed class WahChoKi : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block", "Damage", "Cards"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Block(ref x);
        if (!x.NeedsChoice) FoolEffects.Attack(ref x, 1);
        if (!x.NeedsChoice) Kernel.Draw(ref x, FoolEffects.Var(ref x, 2), false);
    }
}

[ContentId("WAH_SWEEP")]
public sealed class WahSweep : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Attack(ref x, all: true);
    }
}

[ContentId("WAH_BLAST")]
public sealed class WahBlast : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];
    public override void OnPlay(ref Exec x)
    {
        int hits = Kernel.PlayedMelody(x.B, CardTag.Cho) || Kernel.PlayedMelody(x.B, CardTag.Ki) ? 2 : 1;
        FoolEffects.Attack(ref x, hits: hits, all: true);
    }
}

[ContentId("WAH_TOUCH")]
public sealed class WahTouch : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "VulnerablePower"];
    public override IReadOnlyList<string> AppliedPowers => ["VULNERABLE_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Attack(ref x);
        if (!x.NeedsChoice) FoolEffects.Apply<VulnerablePower>(ref x, FoolEffects.Int(ref x, 1), true);
    }
}

[ContentId("TIMBER")]
public sealed class Timber : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "OnKillDamage"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Attack(ref x);
        if (!x.NeedsChoice && x.CardKilledTarget) FoolEffects.Attack(ref x, 1, all: true);
    }
}

[ContentId("JEST")]
public sealed class Jest : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Block", "Cards"];
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SADNESS", 0)];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Block(ref x);
        if (!x.NeedsChoice) FoolEffects.Generate<Sadness>(ref x, FoolEffects.Int(ref x, 1));
    }
}

[ContentId("NAIL")]
public sealed class Nail : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Power"];
    public override IReadOnlyList<string> AppliedPowers => ["WEAK_POWER", "VULNERABLE_POWER"];
    public override void OnPlay(ref Exec x)
    {
        if (!FoolEffects.Impaled(ref x)) return;
        FoolEffects.Attack(ref x);
        if (x.NeedsChoice) return;
        FoolEffects.Apply<WeakPower>(ref x, FoolEffects.Int(ref x, 1), true);
        if (!x.NeedsChoice) FoolEffects.Apply<VulnerablePower>(ref x, FoolEffects.Int(ref x, 1), true);
    }
}

[ContentId("RUMMAGE")]
public sealed class Rummage : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SADNESS", 0)];
    public override void OnPlay(ref Exec x)
    {
        Kernel.Draw(ref x, FoolEffects.Var(ref x, 0), false);
        if (!x.NeedsChoice) FoolEffects.Generate<Sadness>(ref x, 1);
    }
}

[ContentId("VITRIOL")]
public sealed class Vitriol : CardDef
{
    public override IReadOnlyList<string> VarNames => ["WeakPower"];
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("SADNESS", 0)];
    public override IReadOnlyList<string> AppliedPowers => ["WEAK_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<WeakPower>(ref x, FoolEffects.Int(ref x, 0), true);
        if (!x.NeedsChoice) FoolEffects.Generate<Sadness>(ref x, 1);
    }
}

[ContentId("UPROOT")]
public sealed class Uproot : CardDef
{
    public override IReadOnlyList<string> VarNames => ["VulnerablePower"];
    public override IReadOnlyList<string> AppliedPowers => ["VULNERABLE_POWER"];
    public override void OnPlay(ref Exec x)
    {
        if (!FoolEffects.Impaled(ref x)) return;
        Kernel.DislodgeAllArrows(ref x, x.Target);
        if (!x.NeedsChoice) FoolEffects.Apply<VulnerablePower>(ref x, FoolEffects.Int(ref x, 0), true);
    }
}

[ContentId("WREST")]
public sealed class Wrest : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];
    public override void OnPlay(ref Exec x)
    {
        if (!FoolEffects.Impaled(ref x)) return;
        Kernel.DislodgeAllArrows(ref x, x.Target);
        if (!x.NeedsChoice) Kernel.Draw(ref x, FoolEffects.Var(ref x, 0), false);
    }
}

[ContentId("STEADY_CADENCE")]
public sealed class SteadyCadence : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "CalculationBase", "CalculationExtra"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Attack(ref x);
        if (!x.NeedsChoice) Kernel.Draw(ref x, FoolEffects.Var(ref x, 1) + FoolEffects.Var(ref x, 2) * Kernel.MelodiesThisTurn(x.B), false);
    }
}

[ContentId("RHYTHM_STRIKE")]
public sealed class RhythmStrike : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "CalculationBase", "CalculationExtra"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Attack(ref x, hits: FoolEffects.Int(ref x, 1) + FoolEffects.Int(ref x, 2) * Kernel.MelodiesThisTurn(x.B));
    }
}

[ContentId("HARMONIC_SHIELD")]
public sealed class HarmonicShield : CardDef
{
    public override IReadOnlyList<string> VarNames => ["CalculationBase", "CalculationExtra"];
    public override void OnPlay(ref Exec x)
    {
        Kernel.GainBlock(ref x, Limits.PlayerOwner, FoolEffects.Var(ref x, 0) + FoolEffects.Var(ref x, 1) * Kernel.MelodiesThisTurn(x.B), ValueProps.Move, true);
    }
}

[ContentId("ALL_IS_WELL")]
public sealed class AllIsWell : CardDef
{
    public override IReadOnlyList<string> VarNames => ["AllIsWellPower"];
    public override IReadOnlyList<string> AppliedPowers => ["ALL_IS_WELL_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<AllIsWellPower>(ref x, FoolEffects.Int(ref x, 0));
    }
}

[ContentId("ARCHERY")]
public sealed class Archery : CardDef
{
    public override IReadOnlyList<string> VarNames => ["AimPower"];
    public override IReadOnlyList<string> AppliedPowers => ["ARCHERY_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<ArcheryPower>(ref x, FoolEffects.Int(ref x, 0));
    }
}

[ContentId("HIDDEN_ROOTS")]
public sealed class HiddenRoots : CardDef
{
    public override IReadOnlyList<string> VarNames => ["HiddenRootsPower"];
    public override IReadOnlyList<string> AppliedPowers => ["HIDDEN_ROOTS_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<HiddenRootsPower>(ref x, FoolEffects.Int(ref x, 0));
    }
}

[ContentId("HONING")]
public sealed class Honing : CardDef
{
    public override IReadOnlyList<string> VarNames => ["HoningPower"];
    public override IReadOnlyList<string> AppliedPowers => ["HONING_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<HoningPower>(ref x, FoolEffects.Int(ref x, 0));
    }
}

[ContentId("LOST_LUTE")]
public sealed class LostLute : CardDef
{
    public override IReadOnlyList<string> VarNames => ["LostLutePower"];
    public override IReadOnlyList<string> AppliedPowers => ["LOST_LUTE_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<LostLutePower>(ref x, FoolEffects.Int(ref x, 0));
    }
}

[ContentId("MOTIF")]
public sealed class Motif : CardDef
{
    public override IReadOnlyList<string> VarNames => ["MotifPower"];
    public override IReadOnlyList<string> AppliedPowers => ["MOTIF_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<MotifPower>(ref x, FoolEffects.Int(ref x, 0));
    }
}

[ContentId("SERRATED_ARROWS")]
public sealed class SerratedArrows : CardDef
{
    public override IReadOnlyList<string> VarNames => ["SerratedArrowsPower"];
    public override IReadOnlyList<string> AppliedPowers => ["SERRATED_ARROWS_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<SerratedArrowsPower>(ref x, FoolEffects.Int(ref x, 0));
    }
}

[ContentId("SONG_PATH")]
public sealed class SongPath : CardDef
{
    public override IReadOnlyList<string> VarNames => ["SongPathPower"];
    public override IReadOnlyList<string> AppliedPowers => ["SONG_PATH_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<SongPathPower>(ref x, FoolEffects.Int(ref x, 0));
    }
}

[ContentId("TUNE")]
public sealed class Tune : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Energy"];
    public override IReadOnlyList<string> AppliedPowers => ["TUNE_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<TunePower>(ref x, FoolEffects.Int(ref x, 0));
    }
}

[ContentId("VIOLENT_CHORD")]
public sealed class ViolentChord : CardDef
{
    public override IReadOnlyList<string> VarNames => ["ViolentChordPower"];
    public override IReadOnlyList<string> AppliedPowers => ["VIOLENT_CHORD_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<ViolentChordPower>(ref x, FoolEffects.Int(ref x, 0));
    }
}
