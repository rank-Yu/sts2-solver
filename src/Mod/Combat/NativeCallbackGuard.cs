// Last-resort containment for the callbacks and Harmony patches the game invokes from its own async
// combat flow. A solver bug must never fault the native turn loop: the failure is logged, the
// controller pauses automatic play, and control returns to the game. Business logic elsewhere still
// uses the narrow exception types; this boundary intentionally observes every failure because it is
// the only place that can stop an exception from entering the game's task continuation.
using MegaCrit.Sts2.Core.Combat;

namespace Sts2Solver;

internal static class NativeCallbackGuard
{
    internal static bool Try(string callback, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception error)
        {
            Report(callback, error);
            return false;
        }
    }

    internal static void Report(string callback, Exception error)
    {
        string detail = $"{callback}: {error.GetType().Name}: {error.Message}";
        ModLog.Error($"[Sts2Solver] NATIVE_CALLBACK_FAILED {detail}\n{error}");
        try
        {
            CombatSessionController.OnNativeCallbackFailure(callback, detail);
        }
        catch (Exception nested)
        {
            ModLog.Error(
                $"[Sts2Solver] NATIVE_CALLBACK_PAUSE_FAILED {callback}: " +
                $"{nested.GetType().Name}: {nested.Message}");
        }
    }

    internal static void OnCombatStarting(object? combatState)
        => Try("CombatStarting", () =>
        {
            SearchSession.OnCombatStarting();
            CombatSessionController.BeginCombat(combatState as CombatState);
        });

    internal static void OnCombatEnded()
        => Try("CombatEnded", () =>
        {
            SearchSession.OnCombatEnded();
            CombatSessionController.OnCombatEnded();
#if STS2_SOLVER_TESTING
            TestSession.OnCombatEnded();
#endif
        });

    internal static void OnTurnStarted(CombatState state)
        => Try("TurnStarted", () => CombatSessionController.OnTurnStarted(state));

    internal static void OnTreeExiting()
        => Try("TreeExiting", () =>
        {
            CombatSessionController.Reset("tree_exiting");
            SearchSession.Cancel("tree_exiting");
        });

    /// <summary>
    /// Returning to the main menu leaves the combat without CombatEnded. Release the setup page's
    /// input blocker first, then stop the search and execution as the game's exit does, so nothing
    /// keeps running in the menu until the next combat starts.
    /// </summary>
    internal static void OnReturnToMainMenu()
        => Try("ReturnToMainMenu", () =>
        {
            PlayerTurnSetupRuntime.PrepareForSceneExit();
            CombatSessionController.Reset("main_menu");
            SearchSession.Cancel("main_menu");
        });
}
