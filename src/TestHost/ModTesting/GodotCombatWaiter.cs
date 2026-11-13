using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using Sts2Solver.TestHost;

namespace Sts2Solver;

/// <summary>在真实游戏主线程上建局并等原生战斗进入本地玩家的 Play 阶段。</summary>
internal sealed class GodotCombatWaiter(NGame host) : ICombatHost
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);

    public async Task<RunState> StartRunAsync(CharacterModel character, int ascension, string seed, MegaCrit.Sts2.Core.Runs.GameMode gameMode = MegaCrit.Sts2.Core.Runs.GameMode.Standard)
    {
        await host.GameStartupComplete;
        TestHostLog.Trace("run.startup_complete");
        if (gameMode == GameMode.Blitz)
        {
            // The seed profile predates Neow's new epoch. Blitz requires its starting ancient,
            // even in a fixture which immediately enters a specified combat.
            var saved = MegaCrit.Sts2.Core.Saves.SaveManager.Instance.GenerateUnlockStateFromProgress();
            var unlocks = new MegaCrit.Sts2.Core.Unlocks.UnlockState([
                saved,
                new MegaCrit.Sts2.Core.Unlocks.UnlockState([
                    MegaCrit.Sts2.Core.Timeline.EpochModel.GetId<MegaCrit.Sts2.Core.Timeline.Epochs.NeowEpoch>()
                ], [], 0)
            ]);
            RunState run = RunState.CreateForNewRun(
                [Player.CreateForNewRun(character, unlocks, 1uL)],
                ActModel.GetDefaultList().Select(act => act.ToMutable()).ToList(),
                [], gameMode, false, ascension, seed);
            RunManager.Instance.SetUpNewSingleplayer(run, shouldSave: false);
            using (RunManager.Instance.CardSelectorStack.UseLocalSelector(new FirstChoiceCardSelector()))
                await host.StartRun(run);
            return run;
        }
        return await host.StartNewSingleplayerRun(
            Player.CreateForNewRun(character,
                MegaCrit.Sts2.Core.Saves.SaveManager.Instance.GenerateUnlockStateFromProgress(), 1uL),
            shouldSave: false,
            ActModel.GetDefaultList(),
            [],
            seed,
            gameMode,
            isCustomGame: false,
            ascension);
    }

    public async Task<CombatState> WaitForPlayableCombatAsync()
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
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
            await CombatFrameWaiter.NextFrameAsync(host);
        }
    }

    public async Task<CombatState> WaitForTurnSetupPageAsync()
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            CombatState? state = CombatManager.Instance.DebugOnlyGetState();
            Player? player = state == null ? null : LocalContext.GetMe(state);
            if (state != null
                && CombatManager.Instance.IsInProgress
                && state.CurrentSide == CombatSide.Player)
            {
                PlayerTurnPhase? phase = player?.PlayerCombatState?.Phase;
                if (phase == PlayerTurnPhase.Play
                    || phase == PlayerTurnPhase.Start
                    && PlayerTurnSetupRuntime.TestPageVisible)
                {
                    return state;
                }
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    "等回合准备页面或玩家出牌阶段超时。" +
                    $"in_progress={CombatManager.Instance.IsInProgress} " +
                    $"side={state?.CurrentSide.ToString() ?? "-"} " +
                    $"phase={player?.PlayerCombatState?.Phase.ToString() ?? "-"} " +
                    $"setup_page={PlayerTurnSetupRuntime.TestPageVisible}");
            }
            await CombatFrameWaiter.NextFrameAsync(host);
        }
    }

}
