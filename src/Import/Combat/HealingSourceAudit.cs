// 0.113.1 native closure, reviewed against
// CombatSolver b2a5c474 src/Search/StrategicHpRecoveryBound.Components.cs,
// src/Prediction/PredictionModHookSubscriberCapture.cs and the installed native IL.
// MIT, Copyright (c) 2026 Torch1230 and contributors.
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Blitz;
using MegaCrit.Sts2.Core.Models.Dread;
using MegaCrit.Sts2.Core.Models.Dread.Dominions;

namespace Sts2Solver.Import;

/// <summary>只在根导入时检查已审计原版版本与游戏模式。</summary>
internal static class HealingSourceAudit
{
    // Game 0.113.1 / d09d48e5 has different module identities on each platform.
    private static readonly HashSet<Guid> NativeModules =
    [
        new("101ed96a-a88b-482f-9ea5-9b8f1ad4f4fe"), // Linux x64
        new("1a667627-d2cf-4b1d-ade2-39da43f6f1f0"), // macOS arm64
        new("b87f4ad8-cd63-47b8-a3f1-0b4a34df9309"), // macOS x64
        new("9d417b39-1d40-4fc7-b993-28acdc29e151"), // Windows x64
    ];

    internal static bool IsCurrentNativeAssembly
        => NativeModules.Contains(typeof(AbstractModel).Module.ModuleVersionId);

    internal static bool IsNonHealingModeSource(AbstractModel source)
        => source is DreadRunModel or BlitzRunModel
            or AeonglassDominion or CeremonialBeastDominion or InsatiableDominion
            or KaiserCrabDominion or KinDominion or KnowledgeDemonDominion
            or LagavulinMatriarchDominion or QueenDominion or SoulFyshDominion
            or TestSubjectDominion or VantomDominion or WaterfallGiantDominion;
}
