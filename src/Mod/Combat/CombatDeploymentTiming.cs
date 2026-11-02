// Deployment speed and inter-action wait, translated from CombatSolver src/Runtime/SolverController.cs
// (ResolveDeploymentFastMode, DeployCurrentTurn and WaitForDeploymentDelayAsync) at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace Sts2Solver;

/// <summary>Maps the persisted deployment speed to the game's own fast-mode value.</summary>
internal static class CombatDeploymentTiming
{
    internal static FastModeType? ResolveFastMode(SolverDeploymentSpeed speed) => speed switch
    {
        SolverDeploymentSpeed.FollowGame => null,
        SolverDeploymentSpeed.Normal => FastModeType.Normal,
        SolverDeploymentSpeed.Fast => FastModeType.Fast,
        _ => throw new ArgumentOutOfRangeException(nameof(speed), speed, "未知自动出牌速度。"),
    };

    internal static FastModeType CaptureFastMode() => SaveManager.Instance.PrefsSave.FastMode;

    internal static void ApplyFastMode(FastModeType? mode)
    {
        if (mode is { } requested)
            SaveManager.Instance.PrefsSave.FastMode = requested;
    }

    internal static void RestoreFastMode(FastModeType original)
        => SaveManager.Instance.PrefsSave.FastMode = original;

    internal static async Task WaitAsync(NGame host, double seconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (seconds <= 0d)
            return;
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnTimeout() => completion.TrySetResult();
        using CancellationTokenRegistration registration = token.Register(
            static state => ((TaskCompletionSource)state!).TrySetCanceled(), completion);
        SceneTreeTimer timer = host.GetTree().CreateTimer(
            seconds,
            processAlways: false,
            processInPhysics: false,
            ignoreTimeScale: false);
        timer.Timeout += OnTimeout;
        try
        {
            await completion.Task;
        }
        finally
        {
            timer.Timeout -= OnTimeout;
        }
        token.ThrowIfCancellationRequested();
    }
}
