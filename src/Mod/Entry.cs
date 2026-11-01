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
    public const string Version = "0.1.0";

    public static bool Enabled { get; private set; }

    public static bool TestMode { get; private set; }

    public static void Initialize()
    {
        // 与 CombatSolver 共存时完全不启用：不注册补丁、不订阅事件，只保留这一行日志。
        if (CombatSolverPresent())
        {
            MegaCrit.Sts2.Core.Logging.Log.Info(
                "[Sts2Solver] DISABLED reason=CombatSolver present (coexistence policy: no patches, no events)");
            return;
        }

        Enabled = true;
#if STS2_SOLVER_TESTING
        string? testDirectory = Environment.GetEnvironmentVariable(TestSession.DirectoryVariable);
        TestMode = !string.IsNullOrWhiteSpace(testDirectory);
        ModLog.Initialize(TestMode ? Path.Combine(testDirectory!, "mod.log") : null);
        if (TestMode)
        {
            ModPatcher patcher = RitsuLibFramework.CreatePatcher(ModId, "sts2solver", DisplayName);
            patcher.RegisterPatch<UnattendedTestIsolationPatch>();
            patcher.RegisterPatch<UnattendedNoDiscoveryOrderPatch>();
            patcher.RegisterPatch<UnattendedHeadlessFtuePatch>();
            patcher.RegisterPatch<TurnSetupRootCapturePatch>();
            RitsuLibFramework.ApplyRequiredPatcher(patcher, DisableMod);
            if (!Enabled)
                return;
        }
#else
        ModLog.Initialize(null);
#endif
        SolverSettings.Initialize();
        SolverText.Initialize();
        CombatSessionController.Initialize();
        if (NGame.IsMainThread())
        {
            SearchThreadCount.Resolve();
            CombatUiStore.Reset(CombatUiProjection.Idle(SearchMemoryBudget.Resolve()));
        }
        else
            ModLog.Warn("[Sts2Solver] UI_STORE_DEFERRED reason=not_main_thread");
        ModLog.Info($"[Sts2Solver] INIT id={ModId} version={Version} name={DisplayName} english={EnglishName} test_mode={TestMode}");

        // 战斗结束或退出时取消后台搜索和自动执行。
        RitsuLibFramework.SubscribeLifecycle<CombatStartingEvent>(static evt =>
        {
            SearchSession.OnCombatStarting();
            CombatSessionController.BeginCombat(evt.CombatState as CombatState);
        });
        RitsuLibFramework.SubscribeLifecycle<CombatEndedEvent>(static _ =>
        {
            SearchSession.OnCombatEnded();
            CombatSessionController.OnCombatEnded();
#if STS2_SOLVER_TESTING
            TestSession.OnCombatEnded();
#endif
        });
        if (CombatManager.Instance != null)
            CombatManager.Instance.TurnStarted += CombatSessionController.OnTurnStarted;
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
            host.AddChild(new SolverPanelNode());
            host.TreeExiting += static () =>
            {
                CombatSessionController.Reset("tree_exiting");
                SearchSession.Cancel("tree_exiting");
            };
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
