namespace Sts2Solver.Simulation.Combat;

public enum CombatMode : byte { Standard, Dread, Blitz }

// 0.113 的领域在根处冻结；开场已结算的效果由普通牌、能力和怪物状态表示。
public enum DominionKind : byte
{
    None, Aeonglass, CeremonialBeast, Insatiable, KaiserCrab, Kin, KnowledgeDemon,
    LagavulinMatriarch, Queen, SoulFysh, TestSubject, Vantom, WaterfallGiant,
}

public readonly record struct DominionRules(DominionKind Kind, ushort SpawnPower, int SpawnAmount);
