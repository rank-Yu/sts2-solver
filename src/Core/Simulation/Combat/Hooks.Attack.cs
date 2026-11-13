// Checked against game 0.113.0 / 16ac0fe3: Hook.ModifyAttackHitCount and AttackCommand.Execute.
namespace Sts2Solver.Simulation.Combat;

public static partial class HookDispatch
{
    /// <summary>
    /// Thumbscrew is the native game's only hit-count listener. Iterate the root's active relic
    /// list in native order, without scanning powers/cards or applying the combat-ending gate.
    /// All card sources in the single-player kernel belong to the player, including Osty attacks.
    /// </summary>
    internal static int ModifyAttackHitCount(ref Exec x, int hitCount)
    {
        if ((x.B.Flags & BattleFlags.PlayerHooksActive) == 0)
            return hitCount;
        foreach (ushort index in x.R.Relics.Listeners(HookMaskExt.ModifyAttackHitCount))
        {
            Listener listener = new(ListenerKind.Relic, index, Limits.PlayerOwner);
            hitCount = Registry.Relic(x.R.Relics.Id(index))
                .ModifyAttackHitCount(ref x, listener, hitCount);
        }
        return hitCount;
    }
}
