namespace Sts2Solver.Simulation.Combat;

public static partial class Kernel
{
    private static readonly Lazy<ushort> EnergyNextTurnId = new(() => PowerIdOrMax("ENERGY_NEXT_TURN_POWER"));

    /// <summary>
    /// <c>SimulatedCombatState.ConsumeEnergyNextTurn</c>: the player's EnergyNextTurn amount is added
    /// to this turn's energy and cleared. The existing engine consumes it directly in the turn-start
    /// path instead of running the power's AfterEnergyReset.
    /// </summary>
    internal static int ConsumeEnergyNextTurn(ref Battle battle)
    {
        ushort id = EnergyNextTurnId.Value;
        if (id == ushort.MaxValue)
            return 0;
        int amount = GetAmount(battle, Limits.PlayerOwner, id);
        if (amount > 0)
        SetPowerAmount(ref battle, Limits.PlayerOwner, id, 0);
        return amount;
    }

}
