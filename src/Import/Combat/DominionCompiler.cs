// 对照游戏 0.113.0 (16ac0fe3) 的 Models.Dread.Dominions。
// 没有 DynamicVar 的领域常量在导入处冻结，内核只读取 Rules。
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models.Dread.Dominions;
using MegaCrit.Sts2.Core.Rooms;
using Sts2Solver.Simulation.Combat;
namespace Sts2Solver.Import;

public static partial class RulesCompiler
{
    private static DominionRules CompileDominion(CombatState state)
    {
        if (state.Dominion is null) return default;
        NativeAccess.RequireOriginal(state.Dominion);
        (DominionKind kind, string? power, int amount) = state.Dominion switch
        {
            AeonglassDominion => (DominionKind.Aeonglass, null, 0),
            CeremonialBeastDominion => (DominionKind.CeremonialBeast, "RITUAL_POWER", 1),
            InsatiableDominion => (DominionKind.Insatiable, null, 0),
            KaiserCrabDominion => (DominionKind.KaiserCrab, "PLATING_POWER", 7),
            KinDominion => (DominionKind.Kin, null, 0),
            KnowledgeDemonDominion => (DominionKind.KnowledgeDemon, null, 0),
            LagavulinMatriarchDominion => (DominionKind.LagavulinMatriarch, null, 0),
            QueenDominion => (DominionKind.Queen, null, 0),
            SoulFyshDominion => (DominionKind.SoulFysh, null, 0),
            TestSubjectDominion => (DominionKind.TestSubject, "STRENGTH_POWER", 3),
            VantomDominion => (DominionKind.Vantom, "SLIPPERY_POWER", 1),
            WaterfallGiantDominion => (DominionKind.WaterfallGiant, "EXPLOSIVE_POWER",
                state.Encounter!.MonstersWithSlots.Count > 1 ? 3 : state.Encounter.IsWeak ? 4
                    : state.Encounter.RoomType == RoomType.Elite ? 7 : 5),
            _ => throw new UnsupportedImportException(ImportReason.UnsupportedClosure,
                $"领域 {state.Dominion.Id.Entry} 尚未建模"),
        };
        ushort id = 0;
        if (power is not null && !Registry.TryPower(power, out id))
            throw new UnsupportedImportException(ImportReason.UnsupportedPower, power);
        return new(kind, id, amount);
    }
}
