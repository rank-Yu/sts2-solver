using Sts2Solver.TestHost;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using STS2RitsuLib.Patching.Models;

namespace Sts2Solver;

/// <summary>
/// 测试模式在 CombatManager.SetupPlayerTurn 入口记录 Start 时刻的卡牌对象集合。
/// Ported from CombatSolver/src/Testing/Host/... turn-setup capture path at a62f1850
/// (MIT, Copyright (c) 2026 Torch1230 and contributors); only the card-object set is kept.
/// </summary>
internal sealed class TurnSetupRootCapturePatch : IPatchMethod
{
    public static string PatchId => "sts2solver_turn_setup_root_capture";

    public static string Description => "无头测试记录回合准备 Start 的卡牌对象集合";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CombatManager), "SetupPlayerTurn"),
    ];

    [HarmonyPriority(Priority.First)]
    public static bool Prefix(CombatManager __instance, CombatTurnState turnState)
        => TurnSetupRootCapture.Prefix(__instance, turnState);
}
