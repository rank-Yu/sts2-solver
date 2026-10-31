// Opening-combat effects that native models run before the captured play-phase root. These are the
// solver-side counterparts of the pre-root imports documented in docs/import.md; the values
// are the native canonical bases (the same values the content table exports for the rules).
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
}

public sealed partial class BloodVial : RelicDef
{
}

public sealed partial class Lantern : RelicDef
{
}

public sealed partial class VeryHotCocoa : RelicDef
{
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
}

public sealed partial class Akabeko : RelicDef
{
}

public sealed partial class FestivePopper : RelicDef
{
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
