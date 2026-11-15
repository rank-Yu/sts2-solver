// Semantics audited against game v0.113.1 Models/Relics and Models/Powers.
using Sts2Solver.Content.Powers;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Relics;

[ContentId("BYRDPIP")]
public sealed class Byrdpip : RelicDef
{
    public override bool HasCombatState => true;
    public override IReadOnlyList<string> VarNames => ["Repeat"];
    public override void AfterCardPlayed(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (Registry.Card(play.Card.Id).NativeId != "BYRD_SWOOP") return;
        ref RelicState state = ref x.B.RelicState(x.R, self.Index);
        state.Counter = Math.Min(state.Counter + 1, (int)x.R.RelicVar(Id, 0));
    }
}

// These pets are non-interactable animation actors. Attacks and powers belong to the player.
[ContentId("BYRDINE")]
public sealed class Byrdine : RelicDef
{
    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [("GOD_BYRD", 0), ("GOD_BYRD", 1)];
}

[ContentId("WUPPY")]
public sealed class Wuppy : RelicDef;

[ContentId("BATH_ROBES")]
public sealed class BathRobes : RelicDef; // Boss-room healing finishes before combat root capture.

[ContentId("BATH_TOWEL")]
public sealed class BathTowel : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["Heal"];
    public override void AfterPlayerTurnStartLate(ref Exec x, Listener self)
    {
        if (x.B.PlayerTurn <= 1)
            Kernel.Heal(ref x, self.Owner, x.R.RelicVar(Id, 0));
    }
}

[ContentId("TORMENTOUS_COLLAR")]
public sealed class TormentousCollar : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["WeakPower"];
    public override void BeforeSideTurnStart(ref Exec x, Listener self, Side side)
    {
        if (side != Side.Player || x.B.PlayerTurn > 1 || x.R.Relics.Fixed(self.Index).Aux == 0) return;
        for (int i = 0; i < x.B.RosterCount; i++)
        {
            byte target = Kernel.EnemyOwner(x.B.Roster[i]);
            if (Kernel.IsHittable(x.B, target))
                Kernel.ApplyPower(ref x, target, Registry.PowerId<WeakPower>(),
                    (int)x.R.RelicVar(Id, 0), self.Owner);
        }
    }
}

[ContentId("MIGHTY_WINGS")]
public sealed class MightyWings : RelicDef
{
    public override IReadOnlyList<string> VarNames => ["DamageThreshold", "StrengthPower"];
    public override void AfterDamageGiven(ref Exec x, Listener self, in DamageResult result, byte dealer)
    {
        if (result.Props.IsPoweredAttack() && Kernel.IsOwnerOrOsty(self.Owner, dealer)
            && (result.BlockedDamage + result.UnblockedDamage) >= x.R.RelicVar(Id, 0))
            Kernel.ApplyPower(ref x, self.Owner, Registry.PowerId<StrengthPower>(),
                (int)x.R.RelicVar(Id, 1), Limits.NoOwner);
    }
}
