using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.KernelChecks;

internal static class PowerCapacityChecks
{
    internal static void Run()
    {
        Battle battle = default;
        battle.EnemyCount = 1;
        Registry.TryPower("STRENGTH_POWER", out ushort strength);
        Registry.TryPower("DEXTERITY_POWER", out ushort dexterity);
        for (int i = 0; i < Limits.Powers - 1; i++)
        {
            battle.Powers[i] = new Power
            {
                Id = strength, Owner = Limits.PlayerOwner, Amount = i + 1,
                AmountOnTurnStart = i * 7, Aux0 = -i, Aux1 = i * 13, Aux2 = (short)i,
                Applier = Limits.FirstEnemyOwner, Target = Limits.OstyOwner,
            };
        }
        battle.PowerCount = Limits.Powers - 1;
        Kernel.SetPowerAmount(ref battle, Limits.FirstEnemyOwner, strength, 4);
        Require(battle.PowerCount == Limits.Powers, "28th power accepted");
        Battle before = battle;
        bool rejected = false;
        try { Kernel.SetPowerAmount(ref battle, Limits.OstyOwner, dexterity, 1); }
        catch (CapacityException) { rejected = true; }
        Require(rejected && Fingerprint.Equal(before, battle), "29th power rejected without mutation");
        Kernel.SetPowerAmount(ref battle, Limits.FirstEnemyOwner, strength, 9);
        Require(battle.PowerCount == Limits.Powers && battle.Powers[Limits.Powers - 1].Amount == 9,
            "stacking existing power at capacity");

        Registry.TryRelic("HAPPY_FLOWER", out ushort flower);
        Relic[] rows = [new Relic { Id = flower, Counter = 123456 }];
        RelicRoster roster = RelicRoster.Create(rows);
        roster.Import(rows, ref battle);
        battle.Orbs[0].Aux = -12345;
        battle.Fool = default;
        battle.HistoryCourseCard.Private = 321;
        byte[] encoded = BattleCodec.Encode(battle, roster);
        Battle copy = BattleCodec.Decode(encoded, out _);
        Require(Fingerprint.Equal(battle, copy), "full power table and following fields round trip");
        Require(encoded.AsSpan().SequenceEqual(BattleCodec.Encode(copy, roster)), "current payload");
        encoded[BattleCodec.HeaderSize + 4 + 45] = 29;
        rejected = false;
        try { BattleCodec.Decode(encoded, out _); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected, "invalid power count rejected");

        Kernel.SetPowerAmountAt(ref battle, 0, 0);
        Require(battle.PowerCount == Limits.Powers - 1, "remove releases one slot");
        Kernel.SetPowerAmount(ref battle, Limits.OstyOwner, dexterity, 1);
        Require(battle.PowerCount == Limits.Powers && Kernel.GetAmount(battle, Limits.OstyOwner, dexterity) == 1,
            "released slot reusable by another owner");
        Console.WriteLine("POWER_CAPACITY28 insert/overflow/stack/remove/reuse/wire=pass");
    }

    private static void Require(bool ok, string reason)
    {
        if (!ok) throw new InvalidDataException("Power capacity: " + reason);
    }
}
