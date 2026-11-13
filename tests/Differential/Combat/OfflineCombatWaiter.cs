// Ported from CombatSolver/tools/search/OfflineSearchHarness/OfflineCombat.cs at commit a62f1850
// (MIT, Copyright (c) 2026 Torch1230 and contributors). The No-Godot run startup and playable-state
// wait remain here; all fixture construction is shared with the Mod.
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace Differential;

/// <summary>Drives the native run through the offline message loop, without Godot or a scene tree.</summary>
internal sealed class OfflineCombatWaiter(MainLoopContext loop) : ICombatHost
{
    public async Task<RunState> StartRunAsync(CharacterModel character, int ascension, string seed, MegaCrit.Sts2.Core.Runs.GameMode gameMode = MegaCrit.Sts2.Core.Runs.GameMode.Standard)
    {
        if (RunManager.Instance.IsInProgress)
            throw new InvalidOperationException("已经有进行中的跑局。");
        UnlockState unlockState = SaveManager.Instance.GenerateUnlockStateFromProgress();
        RunState runState = RunState.CreateForNewRun(
            [Player.CreateForNewRun(character, unlockState, 1uL)],
            ActModel.GetDefaultList().Select(act => act.ToMutable()).ToList(),
            [],
            gameMode,
            false,
            ascension,
            seed);
        RunManager.Instance.SetUpNewSingleplayer(runState, shouldSave: false);
        TestHostLog.Trace("run.setup");
        await RunManager.Instance.FinalizeStartingRelics();
        TestHostLog.Trace("run.relics");
        RunManager.Instance.Launch();
        TestHostLog.Trace("run.launch");
        // TestMode skips the ancient room setup. Blitz always enters that room in EnterAct;
        // the combat harness sets the native act directly, then ScenarioBuilder enters its fixture.
        if (gameMode == GameMode.Blitz)
        {
            await RunManager.Instance.SetActInternal(0);
            await MegaCrit.Sts2.Core.Hooks.Hook.AfterActEntered(runState);
        }
        else
            await RunManager.Instance.EnterAct(0, doTransition: false);
        TestHostLog.Trace("run.act0");
        return runState;
    }

    public Task<CombatState> WaitForPlayableCombatAsync() => Task.FromResult(Wait());

    private CombatState Wait()
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(120);
        while (true)
        {
            CombatState? state = CombatManager.Instance.DebugOnlyGetState();
            Player? player = state == null ? null : LocalContext.GetMe(state);
            if (state != null
                && CombatManager.Instance.IsInProgress
                && state.CurrentSide == CombatSide.Player
                && player?.PlayerCombatState?.Phase == PlayerTurnPhase.Play)
            {
                return state;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    "等玩家出牌阶段超时。" +
                    $"in_progress={CombatManager.Instance.IsInProgress} " +
                    $"side={state?.CurrentSide.ToString() ?? "-"} " +
                    $"phase={player?.PlayerCombatState?.Phase.ToString() ?? "-"}");
            }
            loop.Pump(TimeSpan.FromMilliseconds(30));
        }
    }
}
