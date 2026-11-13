using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using STS2RitsuLib;
using STS2RitsuLib.Patching.Core;

namespace Sts2Solver;

[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    public const string ModId = "Sts2Solver";
    public const string DisplayName = "尖塔求解器";
    public const string EnglishName = "STS2 Solver";
    public static string Version => typeof(ModEntry).Assembly.GetName().Version!.ToString(3);

    public static bool Enabled { get; private set; }

    public static bool TestMode { get; private set; }

    /// <summary>
    /// CombatSolver is loaded (or waiting to load) in the same game. Both solvers stay enabled: the
    /// player may search with both but should let only one execute. The turn-setup page is taken over
    /// only during a 执行本场战斗 run; auto search alone searches it without taking it over
    /// (<see cref="CombatSessionController.TurnSetupHandlingNow"/>).
    /// </summary>
    public static bool CombatSolverLoaded { get; private set; }

#if STS2_SOLVER_TESTING
    /// <summary>Test-only: run a request as if CombatSolver were loaded beside this Mod.</summary>
    internal static void SetCombatSolverLoadedForTest() => CombatSolverLoaded = true;

    /// <summary>Test-only: back to what the mod list says.</summary>
    internal static void ResetCombatSolverLoadedForTest() => CombatSolverLoaded = CombatSolverPresent();
#endif

    public static void Initialize()
    {
        CombatSolverLoaded = CombatSolverPresent();
        if (CombatSolverLoaded)
        {
            MegaCrit.Sts2.Core.Logging.Log.Info(
                "[Sts2Solver] COEXIST CombatSolver present: both enabled; turn-setup takeover only while playing the combat, otherwise search only; let only one solver execute");
        }

        Enabled = true;
#if STS2_SOLVER_TESTING
        string? testDirectory = Environment.GetEnvironmentVariable(TestSession.DirectoryVariable);
        TestMode = !string.IsNullOrWhiteSpace(testDirectory);
        ModLog.Initialize(TestMode ? Path.Combine(testDirectory!, "mod.log") : null);
#else
        ModLog.Initialize(null);
#endif
        ModPatcher patcher = RitsuLibFramework.CreatePatcher(ModId, "sts2solver", DisplayName);
        patcher.RegisterPatch<PlayerTurnSetupPatch>();
        patcher.RegisterPatch<PlayerTurnAutoPrePlayPatch>();
        patcher.RegisterPatch<PlayerTurnSetupSceneExitPatch>();
        patcher.RegisterPatch<ChooseCardObservationPatch>();
        patcher.RegisterPatch<SimpleGridObservationPatch>();
        patcher.RegisterPatch<RewardGridObservationPatch>();
        patcher.RegisterPatch<CombatPileObservationPatch>();
        patcher.RegisterPatch<HandObservationPatch>();
        patcher.RegisterPatch<HandUpgradeObservationPatch>();
#if STS2_SOLVER_TESTING
        if (TestMode)
        {
            patcher.RegisterPatch<UnattendedTestIsolationPatch>();
            patcher.RegisterPatch<UnattendedNoDiscoveryOrderPatch>();
            patcher.RegisterPatch<UnattendedHeadlessFtuePatch>();
            patcher.RegisterPatch<TurnSetupRootCapturePatch>();
        }
#endif
        RitsuLibFramework.ApplyRequiredPatcher(patcher, DisableMod);
        if (!Enabled)
            return;
        SolverSettings.Initialize();
#if STS2_SOLVER_TESTING
        CombatLogRecorder.Initialize(TestMode ? Path.Combine(testDirectory!, "results", "combat-logs") : null);
#else
        CombatLogRecorder.Initialize(null);
#endif
        SolverText.Initialize();
        CombatSessionController.Initialize();
        if (NGame.IsMainThread())
        {
            CombatUiStore.Reset(CombatUiProjection.Idle(SearchMemoryBudget.Resolve()));
        }
        else
            ModLog.Warn("[Sts2Solver] UI_STORE_DEFERRED reason=not_main_thread");
        ModLog.Info($"[Sts2Solver] INIT id={ModId} version={Version} name={DisplayName} english={EnglishName} test_mode={TestMode}");
        ModLog.Info($"[Sts2Solver] TEST_MODE test_mode_is_on={MegaCrit.Sts2.Core.TestSupport.TestMode.IsOn}");

        // 战斗结束或退出时取消后台搜索和自动执行。
        RitsuLibFramework.SubscribeLifecycle<CombatStartingEvent>(
            static evt => NativeCallbackGuard.OnCombatStarting(evt.CombatState));
        RitsuLibFramework.SubscribeLifecycle<CombatEndedEvent>(
            static _ => NativeCallbackGuard.OnCombatEnded());
        if (CombatManager.Instance != null)
            CombatManager.Instance.TurnStarted += NativeCallbackGuard.OnTurnStarted;
        else
            ModLog.Error("[Sts2Solver] TURN_EVENT_UNAVAILABLE reason=CombatManager_missing");
#if STS2_SOLVER_TESTING
        if (TestMode)
            RitsuLibFramework.SubscribeLifecycle<GameReadyEvent>(static _ => TestSession.Start());
#endif

        NGame? host = NGame.Instance;
        if (host == null)
        {
            ModLog.Warn("[Sts2Solver] CONTROLLER_NODE_DISABLED reason=NGame_instance_missing");
        }
        else
        {
            host.AddChild(new CombatUiMemorySampler());
            host.AddChild(new CombatSessionNode());
            host.AddChild(new SolverOverlay());
            host.TreeExiting += NativeCallbackGuard.OnTreeExiting;
            ModLog.Info("[Sts2Solver] CONTROLLER_READY hotkey=none mode_switch=panel");
        }
    }

    private static bool CombatSolverPresent()
    {
        foreach (Mod mod in ModManager.Mods)
        {
            if (!string.Equals(mod.manifest?.id, "CombatSolver", StringComparison.Ordinal))
                continue;
            // None 表示已发现、尚未轮到加载；它仍会与本 Mod 同时出现在进程里。
            if (mod.state is ModLoadState.None or ModLoadState.Loaded)
                return true;
        }
        return false;
    }

    private static void DisableMod()
    {
        Enabled = false;
    }
}
