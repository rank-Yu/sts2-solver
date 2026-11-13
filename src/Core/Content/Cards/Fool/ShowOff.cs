// 对照游戏 0.113.0 (16ac0fe3) ShowOff；FoolIdentity 保留能力引用的卡牌对象身份。
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Content.Powers;
namespace Sts2Solver.Content.Cards.Fool;

[ContentId("SHOW_OFF")]
public sealed class ShowOff : CardDef
{
    public override bool IsIdentityContent => true;
    public override IReadOnlyList<string> VarNames => ["AimPower", "ShowOffPower", "BaseAim"];
    public override IReadOnlyList<string> AppliedPowers => ["AIM_POWER", "SHOW_OFF_POWER"];
    public override void OnPlay(ref Exec x)
    {
        FoolEffects.Apply<AimPower>(ref x, FoolEffects.Int(ref x, 0));
        if (x.NeedsChoice) return;
        ushort identity = Kernel.EnsureFoolCardIdentity(ref x);
        FoolEffects.Apply<ShowOffPower>(ref x, FoolEffects.Int(ref x, 1));
        if (x.NeedsChoice) return;
        ushort power = Registry.PowerId<ShowOffPower>();
        for (int i = x.B.PowerCount - 1; i >= 0; i--)
        {
            if (x.B.Powers[i].Id != power || x.B.Powers[i].Owner != Limits.PlayerOwner) continue;
            x.B.Powers[i].Aux0 = identity;
            x.B.Powers[i].Aux1 = (x.Card.Flags & CardFlags.DeckCard) != 0 ? 1 : 0;
            return;
        }
    }
    public override void ProjectPrivate(ref Card card) => card.Private = card.Var0;
    public override void AfterDowngraded(ref Battle battle, int position)
        => battle.Cards[position].Var0 = battle.Cards[position].Private;
}
