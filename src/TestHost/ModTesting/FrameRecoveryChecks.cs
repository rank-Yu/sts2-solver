using Sts2Solver.Ai.Combat;

namespace Sts2Solver;

/// <summary>验证正常低帧率、后台限帧和无头运行不会触发恢复等待，前台长帧仍会触发。</summary>
internal static class FrameRecoveryChecks
{
    internal static void Run()
    {
        SearchFrameMonitor monitor = new();
        for (int i = 0; i < 31; i++)
            monitor.ObserveFrame(1000d / 30d, searchActive: false, recoveryAllowed: true);
        monitor.BeginSearch(recoveryEnabled: true, measureFrames: true);
        Expect(Math.Abs(monitor.Signal.PressureFrameGapMilliseconds - 50d) < 0.001,
            "30 FPS 的空闲基线应将压力阈值设为 50ms");
        for (int i = 0; i < 120; i++)
            monitor.ObserveFrame(1000d / 30d, searchActive: true, recoveryAllowed: true);
        int observed = 0;
        Expect(!monitor.Signal.WaitForRecovery(ref observed) && monitor.LongFrames == 0,
            "稳定 30 FPS 不应触发恢复等待");

        monitor.ObserveFrame(80d, searchActive: true, recoveryAllowed: true);
        Expect(monitor.Signal.PressureEpochForTesting == 1 && monitor.LongFrames == 1,
            "前台 80ms 长帧仍须触发压力");
        // Worker 已错过长帧、窗口转入后台时，不应补付旧压力的等待。
        monitor.ObserveFrame(100d, searchActive: true, recoveryAllowed: false);
        Expect(!monitor.Signal.WaitForRecovery(ref observed) && observed == 1,
            "窗口失焦后应消费旧压力且不等待");
        for (int i = 0; i < 20; i++)
            monitor.ObserveFrame(100d, searchActive: true, recoveryAllowed: false);
        Expect(monitor.Signal.PressureEpochForTesting == 1,
            "后台 10 FPS 限帧不应制造新压力");
        monitor.ObserveFrame(80d, searchActive: true, recoveryAllowed: true);
        Expect(monitor.Signal.PressureEpochForTesting == 2,
            "重新聚焦后应恢复长帧保护");

        // 同一组合的后续成员不能丢掉空闲基线，也不能把搜索慢帧学成基线。
        monitor.BeginSearch(recoveryEnabled: true, measureFrames: true);
        Expect(Math.Abs(monitor.Signal.PressureFrameGapMilliseconds - 50d) < 0.001
            && monitor.Signal.BaselineSampleCount == 31 && monitor.LongFrames == 0,
            "跨成员保留空闲基线并重置计数");

        monitor.BeginSearch(recoveryEnabled: false, measureFrames: true);
        monitor.ObserveFrame(100d, searchActive: true, recoveryAllowed: false);
        observed = 0;
        Expect(!monitor.Signal.WaitForRecovery(ref observed) && monitor.LongFrames == 1,
            "无头运行仍记录慢帧，但不阻塞搜索");

        SearchFrameMonitor fast = new();
        for (int i = 0; i < 31; i++)
            fast.ObserveFrame(1000d / 60d, searchActive: false, recoveryAllowed: true);
        fast.BeginSearch(recoveryEnabled: true, measureFrames: true);
        fast.ObserveFrame(40d, searchActive: true, recoveryAllowed: true);
        Expect(fast.Signal.PressureFrameGapMilliseconds == 33d
            && fast.Signal.PressureEpochForTesting == 1,
            "正常 60 FPS 下 40ms 长帧仍触发保护");

        // 对照旧宿主：它没有输入空闲样本，也没有传入窗口焦点。
        SearchFramePressureSignal old = new();
        for (int i = 0; i < 120; i++)
            old.ObserveFrame(1000d / 30d, searchActive: true);
        Expect(old.PressureEpochForTesting == 120,
            "旧宿主在正常 30 FPS 下每帧都错误触发压力");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
