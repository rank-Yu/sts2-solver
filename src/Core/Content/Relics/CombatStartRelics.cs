// Opening-combat effects of native models. A play-phase root is captured after them and imports their
// results (docs/import.md). A turn 1 setup root is captured at the native SetupPlayerTurn entry, so the
// turn-1-only hooks from the energy reset onward are implemented here and run in the replayed preamble.
// Values are the native canonical bases; these relic variables have no runtime writer.
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Relics;

public sealed partial class Anchor : RelicDef
{
}

public sealed partial class FakeAnchor : RelicDef
{
}

public sealed partial class BronzeScales : RelicDef
{
}

public sealed partial class DataDisk : RelicDef
{
}

public sealed partial class Gorget : RelicDef
{
}

public sealed partial class OddlySmoothStone : RelicDef
{
}

public sealed partial class Vajra : RelicDef
{
}

public sealed partial class SwordOfJade : RelicDef
{
}

public sealed partial class SlingOfCourage : RelicDef
{
}

public sealed partial class DivineRight : RelicDef
{
}

public sealed partial class PhilosophersStone : RelicDef
{
}

public sealed partial class RedSkull : RelicDef
{

}

public sealed partial class BeltBuckle : RelicDef
{

}

public sealed partial class SneckoEye : RelicDef
{
}

public sealed partial class FakeSneckoEye : RelicDef
{
}

public sealed partial class BoundPhylactery : RelicDef
{
}

public sealed partial class PhylacteryUnbound : RelicDef
{
}

public sealed partial class BagOfMarbles : RelicDef
{
}

public sealed partial class RedMask : RelicDef
{
}

public sealed partial class BagOfPreparation : RelicDef
{
    // BagOfPreparation.cs: CardsVar(2), turn number <= 1.
    public override decimal ModifyHandDraw(ref Exec x, Listener self, decimal amount)
        => x.B.PlayerTurn <= 1 ? amount + 2m : amount;
}

public sealed partial class BloodVial : RelicDef
{
    // BloodVial.cs: HealVar(2), AfterPlayerTurnStartLate, turn number <= 1.
    public override void AfterPlayerTurnStartLate(ref Exec x, Listener self)
    {
        if (x.B.PlayerTurn <= 1)
            Kernel.Heal(ref x, Limits.PlayerOwner, 2m);
    }
}

public sealed partial class Lantern : RelicDef
{
    // Lantern.cs: EnergyVar(1), turn number <= 1.
    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side == Side.Player && x.B.PlayerTurn <= 1)
            Kernel.GainEnergy(ref x, 1m);
    }
}

public sealed partial class VeryHotCocoa : RelicDef
{
    // VeryHotCocoa.cs: EnergyVar(4), turn number <= 1.
    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side == Side.Player && x.B.PlayerTurn <= 1)
            Kernel.GainEnergy(ref x, 4m);
    }
}

public sealed partial class FurCoat : RelicDef
{
}

public sealed partial class GhostSeed : RelicDef
{
}

public sealed partial class Pantograph : RelicDef
{
}

public sealed partial class StoneCracker : RelicDef
{

    private static int CompareDrawCards(in Battle battle, int left, int right)
    {
        int result = battle.Cards[left].Id.CompareTo(battle.Cards[right].Id);
        if (result != 0)
            return result;
        result = battle.Cards[left].Upgrade.CompareTo(battle.Cards[right].Upgrade);
        if (result != 0)
            return result;
        return battle.Cards[left].EnchantmentId.CompareTo(battle.Cards[right].EnchantmentId);
    }
}

public sealed partial class Bellows : RelicDef
{
    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
        if (x.B.PlayerTurn > 1)
            return;
        int start = Piles.Start(x.B, Pile.Hand);
        for (int index = 0; index < x.B.HandCount; index++)
        {
            ref Card card = ref x.B.Cards[start + index];
            int max = x.R.Card(card.Id, card.Upgrade).MaxUpgradeLevel;
            if (card.Upgrade < max)
            {
                card.Upgrade = checked((byte)max);
                CardStats upgraded = x.R.Card(card.Id, card.Upgrade);
                card.BaseCost = checked((sbyte)upgraded.EnergyCost);
                card.BaseStarCost = checked((sbyte)upgraded.StarCost);
            }
        }
    }
}

public sealed partial class PetrifiedToad : RelicDef
{
}

public sealed partial class Metronome : RelicDef
{
}

public sealed partial class Bread : RelicDef
{
    // Bread.cs: EnergyVar("LoseEnergy", 2), turn number == 1.
    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side == Side.Player && x.B.PlayerTurn == 1)
            Kernel.LoseEnergy(ref x, 2m);
    }
}

public sealed partial class TwistedFunnel : RelicDef
{
}

public sealed partial class EmberTea : RelicDef
{
}

public sealed partial class FuneraryMask : RelicDef
{
}

public sealed partial class FencingManual : RelicDef
{
    // The forge creates a Sovereign Blade when the player has none.
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [(Kernel.SovereignBladeId, 0)];

    // FencingManual.cs: ForgeVar(10), turn number <= 1.
    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side == Side.Player && x.B.PlayerTurn <= 1)
            Kernel.Forge(ref x, 10);
    }
}

public sealed partial class Akabeko : RelicDef
{
    // Akabeko.cs: PowerVar<VigorPower>(8), turn number <= 1, applied by the owner.
    public override void AfterSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side == Side.Player && x.B.PlayerTurn <= 1)
            Kernel.ApplyPower(ref x, Limits.PlayerOwner, Registry.PowerId<Powers.VigorPower>(), 8,
                Limits.PlayerOwner);
    }
}

public sealed partial class FestivePopper : RelicDef
{
    // FestivePopper.cs: DamageVar(9, Unpowered) to every hittable enemy, turn number == 1.
    public override void AfterPlayerTurnStart(ref Exec x, Listener self)
    {
        if (x.B.PlayerTurn == 1)
            CardEffects.DamageAllEnemies(ref x, 9m, ValueProps.Unpowered, Limits.PlayerOwner);
    }
}

public sealed partial class BurningSticks : RelicDef
{
}

public sealed partial class ThrowingAxe : RelicDef
{
}

public sealed partial class UnsettlingLamp : RelicDef
{
}

public sealed partial class Vambrace : RelicDef
{
}

public sealed partial class MeatOnTheBone : RelicDef
{

}
