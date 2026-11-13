using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Solver;

internal static class CombatFrameWaiter
{
    /// <summary>
    /// 调用方先确认原版进入 Play 阶段且动作队列恢复，再等队列完成与主线程提交。
    /// 快速模式不等待遗物显示动画；导入器读取 TurnsSeen 等实际计数，而不是 DisplayAmount。
    /// </summary>
    internal static async Task SettleAsync(NGame host, SolverDeploymentSpeed speed,
        CancellationToken token)
    {
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions().WaitAsync(token);
        if (speed == SolverDeploymentSpeed.Fast)
            await NextFrameAsync(host);
        else
            await CombatDeploymentTiming.WaitAsync(host, 1.5, token);
        token.ThrowIfCancellationRequested();
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions().WaitAsync(token);
    }

    internal static async Task NextFrameAsync(NGame host)
        => await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
}
