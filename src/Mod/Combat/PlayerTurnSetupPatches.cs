// Entry patches for the visible turn-setup takeover. The interception gate and replacement tasks
// follow CombatSolver src/Runtime/PlayerTurnSetupPatches.cs at commit 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors). SetupPlayerTurn and
// RunAutoPrePlayPhase share one page session and one plan.
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using STS2RitsuLib.Patching.Models;

namespace Sts2Solver;

internal sealed class PlayerTurnSetupPatch : IPatchMethod
{
    public static string PatchId => "sts2solver_player_turn_setup";

    public static string Description => "首张可见选牌页出现后搜索并驱动回合准备选择";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CombatManager), "SetupPlayerTurn",
            [typeof(CombatTurnState), typeof(Player), typeof(HookPlayerChoiceContext)]),
    ];

    // The test-only root capture patch uses Priority.First and must observe the Start state first.
    [HarmonyPriority(Priority.High)]
    public static bool Prefix(CombatManager __instance, CombatTurnState turnState, Player player,
        HookPlayerChoiceContext playerChoiceContext, ref Task __result)
    {
        if (!PlayerTurnSetupRuntime.TryInterceptSetup(
                __instance, turnState, player, playerChoiceContext, out Task? task))
        {
            return true;
        }
        __result = task!;
        return false;
    }
}

internal sealed class PlayerTurnAutoPrePlayPatch : IPatchMethod
{
    public static string PatchId => "sts2solver_player_turn_auto_pre_play";

    public static string Description => "回合准备之后自动出牌阶段的选牌继续使用同一页面会话和计划";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CombatManager), "RunAutoPrePlayPhase",
            [typeof(CombatTurnState), typeof(HookPlayerChoiceContext), typeof(Task), typeof(Player)]),
    ];

    // The test-only root capture patch uses Priority.First and must observe the Start state first.
    [HarmonyPriority(Priority.High)]
    public static bool Prefix(CombatManager __instance, CombatTurnState turnState,
        HookPlayerChoiceContext playerChoiceContext, Task setupPlayerTurnTask, Player player,
        ref Task __result)
    {
        if (!PlayerTurnSetupRuntime.TryInterceptAutoPrePlay(
                __instance, turnState, playerChoiceContext, setupPlayerTurnTask, player,
                out Task? task))
        {
            return true;
        }
        __result = task!;
        return false;
    }
}

internal sealed class PlayerTurnSetupSceneExitPatch : IPatchMethod
{
    public static string PatchId => "sts2solver_player_turn_setup_scene_exit";

    public static string Description => "返回主菜单前释放回合准备页面的输入遮挡";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(NGame), nameof(NGame.ReturnToMainMenu), Type.EmptyTypes),
    ];

    [HarmonyPriority(Priority.High)]
    public static void Prefix() => PlayerTurnSetupRuntime.PrepareForSceneExit();
}
