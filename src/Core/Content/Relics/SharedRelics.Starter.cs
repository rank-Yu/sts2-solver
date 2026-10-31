using Sts2Solver.Content.Powers;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Relics;

/// <summary>
/// <c>InfusedCore</c>: channels 3 Lightning at the first side turn start (before the root) and adds
/// its ExtraDamage to every Lightning orb value (<c>SimulatedCombatState.RelicTurnStart</c> case
/// InfusedCore; native <c>ModifyOrbValue</c> over the combat listeners).
/// </summary>
[ContentId("INFUSED_CORE")]
public sealed class InfusedCore : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Lightning", "ExtraDamage"];

    public override decimal ModifyOrbValue(in Battle battle, Rules rules, Listener self,
        in Orb orb, decimal value)
    {
        if (!Registry.TryOrb("LIGHTNING_ORB", out ushort lightning) || orb.Id != lightning)
            return value;
        return value + rules.RelicVar(Id, 1);
    }

}

/// <summary>
/// <c>PhylacteryUnbound</c>: summons Osty for its StartOfTurn amount at every player side turn start;
/// the StartOfCombat summon is already in the captured root (<c>SimulatedCombatState.RelicTurnStart</c>).
/// </summary>
[ContentId("PHYLACTERY_UNBOUND")]
public sealed partial class PhylacteryUnbound : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["StartOfCombat", "StartOfTurn"];

    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player)
            return;
        Kernel.SummonOsty(ref x, (int)x.R.RelicVar(Id, 1));
    }
}

/// <summary>
/// <c>DivineDestiny</c>: gains 7 stars at the first side turn start
/// (<c>AfterSideTurnStart</c> with <c>TurnNumber &lt;= 1</c>), before the captured root.
/// </summary>
[ContentId("DIVINE_DESTINY")]
public sealed class DivineDestiny : RelicDef
{
}

[ContentId("BLACK_BLOOD")] public sealed class BlackBlood : RelicDef; // AfterCombatVictory

/// <summary>
/// <c>RingOfTheDrake</c>: <c>ModifyHandDraw</c> adds Cards while the player turn number is at most
/// Turns (<c>PersistentPowerSupport</c> native relic contribution).
/// </summary>
[ContentId("RING_OF_THE_DRAKE")]
public sealed class RingOfTheDrake : RelicDef
{
    // RingOfTheDrake.cs: CardsVar(2), DynamicVar("Turns", 3m).
    public override IReadOnlyList<string> VarNames => ["Cards", "Turns"];

    public override decimal ModifyHandDraw(ref Exec x, Listener self, decimal amount)
        => x.B.PlayerTurn > (int)x.R.RelicVar(Id, 1) ? amount : amount + x.R.RelicVar(Id, 0);
}
