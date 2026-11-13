using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Relics;

[ContentId("CRACKED_CORE")]
public sealed partial class CrackedCore : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Lightning"];

    public override void BeforeSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player || x.B.PlayerTurn > 1)
            return;
        int count = (int)x.R.RelicVar(x.R.Relics.Id(self.Index), 0);
        Kernel.ChannelOrbs(ref x, Kernel.OrbId("LIGHTNING_ORB"), count);
    }
}

[ContentId("RUNIC_CAPACITOR")]
public sealed class RunicCapacitor : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Repeat"];

    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player || x.B.PlayerTurn > 1)
            return;
        Kernel.AddOrbSlots(ref x.B, (int)x.R.RelicVar(x.R.Relics.Id(self.Index), 0));
    }
}

[ContentId("SYMBIOTIC_VIRUS")]
public sealed class SymbioticVirus : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Dark"];

    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player || x.B.PlayerTurn > 1)
            return;
        Kernel.ChannelOrbs(ref x, Kernel.OrbId("DARK_ORB"), (int)x.R.RelicVar(x.R.Relics.Id(self.Index), 0));
    }
}

[ContentId("GOLD_PLATED_CABLES")]
public sealed class GoldPlatedCables : RelicDef
{
    public override int ModifyOrbPassiveTriggerCounts(in Battle battle, Rules rules, Listener self,
        in Orb orb, int orbIndex, int triggerCount)
        => orbIndex == 0 ? triggerCount + 1 : triggerCount;
}

[ContentId("POWER_CELL")]
public sealed class PowerCell : RelicDef
{
    // The existing engine's BeforeSideTurnStartMirrors.Vanilla registered PowerCell as ignored; the
    // definition exists so a deck containing it can be admitted, and it has no combat effect.
}

[ContentId("REPTILE_TRINKET")]
public sealed class ReptileTrinket : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["StrengthPower"];

    public override void AfterPotionUsed(ref Exec x, Listener self, ushort potionId)
    {
        if (x.B.Player.IsDead)
            return;
        Kernel.ApplyTemporaryStrength(ref x, Limits.PlayerOwner,
            Registry.PowerId<Powers.ReptileTrinketPower>(), (int)x.R.RelicVar(x.R.Relics.Id(self.Index), 0),
            Limits.PlayerOwner, sign: 1);
    }
}
