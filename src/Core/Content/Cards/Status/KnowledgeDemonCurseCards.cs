using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Status;

/// <summary>Native <c>Disintegration</c>: choosing it applies 6 + the demon's curse counter as DisintegrationPower.</summary>
[ContentId("DISINTEGRATION")]
public sealed class Disintegration : CardDef
{
    public override void OnPlay(ref Exec x)
        => throw new UnsupportedException("知识恶魔的诅咒卡只作为选择选项。");
}

/// <summary>Native <c>MindRot</c>: choosing it applies MindRotPower 1.</summary>
[ContentId("MIND_ROT")]
public sealed class MindRot : CardDef
{
    public override void OnPlay(ref Exec x)
        => throw new UnsupportedException("知识恶魔的诅咒卡只作为选择选项。");
}

/// <summary>Native <c>Sloth</c>: choosing it applies SlothPower 3.</summary>
[ContentId("SLOTH")]
public sealed class Sloth : CardDef
{
    public override IReadOnlyList<string> VarNames => ["SlothPower"];
    public override void OnPlay(ref Exec x)
        => throw new UnsupportedException("知识恶魔的诅咒卡只作为选择选项。");
}

/// <summary>Native <c>WasteAway</c>: choosing it applies WasteAwayPower 1.</summary>
[ContentId("WASTE_AWAY")]
public sealed class WasteAway : CardDef
{
    public override void OnPlay(ref Exec x)
        => throw new UnsupportedException("知识恶魔的诅咒卡只作为选择选项。");
}


// 0.113 KnowledgeDemonDominion 的领域选项，只通过 OnChosen 生效。
[ContentId("AMNESIA")]
public sealed class Amnesia : CardDef
{
    public override IReadOnlyList<string> VarNames => ["AmnesiaPower"];
    public override void OnPlay(ref Exec x) => throw new UnsupportedException("失忆只作为领域选择选项。");
}
