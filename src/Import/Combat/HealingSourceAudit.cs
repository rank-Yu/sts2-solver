// 0.111.1 native closure, reviewed against
// CombatSolver b2a5c474 src/Search/StrategicHpRecoveryBound.Components.cs,
// src/Prediction/PredictionModHookSubscriberCapture.cs and the installed native IL.
// MIT, Copyright (c) 2026 Torch1230 and contributors.
using MegaCrit.Sts2.Core.Models;

namespace Sts2Solver.Import;

/// <summary>只在根导入时检查已审计的原版程序集。</summary>
internal static class HealingSourceAudit
{
    internal static bool IsCurrentNativeAssembly
    {
        get
        {
            Guid id = typeof(AbstractModel).Module.ModuleVersionId;
            return id == new Guid("63a41e98-0a81-4966-bbe9-1b12a5075de5") // Linux
                || id == new Guid("a5742661-f95f-4ba1-8b21-18dedf86422b") // Windows
                || id == new Guid("4e00c689-5bff-4363-9263-83dc46c745fa") // macOS arm64
                || id == new Guid("c2396e62-a406-4c65-bd30-28862dad0ddc"); // macOS x64
        }
    }
}
