// Test-only observer for the original combat terminal boundary. It installs Harmony prefixes on
// the two native end paths (victory: EndCombatInternal; defeat: ProcessPendingLoss) and imports the
// native battle before the game clears turn-scoped state. Nothing is written back.
using System.Reflection;
using System.Runtime.ExceptionServices;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;

namespace Differential;

internal enum TerminalCapturePath : byte
{
    None = 0,
    Victory = 1,
    Defeat = 2,
}

internal static class TerminalBattleCapture
{
    private static readonly HarmonyLib.Harmony Harmony = new("sts2solver.differential.terminal");
    private static readonly MethodInfo VictoryPrefixMethod = typeof(TerminalBattleCapture).GetMethod(
        nameof(VictoryPrefix), BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(TerminalBattleCapture).FullName,
            nameof(VictoryPrefix));
    private static readonly MethodInfo DefeatPrefixMethod = typeof(TerminalBattleCapture).GetMethod(
        nameof(DefeatPrefix), BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(TerminalBattleCapture).FullName,
            nameof(DefeatPrefix));

    private static Func<Battle?>? _capture;
    private static Battle? _value;
    private static Exception? _failure;
    private static TerminalCapturePath _path;
    private static bool _installed;

    internal static void Install()
    {
        if (_installed)
            return;
        _installed = true;
        foreach (MethodInfo method in typeof(CombatManager).GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.Name == "EndCombatInternal")
            {
                Harmony.Patch(method,
                    prefix: new HarmonyLib.HarmonyMethod(VictoryPrefixMethod));
            }
            else if (method.Name == "ProcessPendingLoss")
            {
                Harmony.Patch(method,
                    prefix: new HarmonyLib.HarmonyMethod(DefeatPrefixMethod));
            }
        }
    }

    internal static void Begin(Func<Battle?> capture)
    {
        _capture = capture;
        _value = null;
        _failure = null;
        _path = TerminalCapturePath.None;
    }

    internal static Battle? Value => _value;
    internal static TerminalCapturePath Path => _path;

    internal static void End()
    {
        _capture = null;
    }

    internal static string? Compare(in Battle original, Rules rules)
    {
        if (_failure is { } failure)
        {
            _failure = null;
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        if (_path == TerminalCapturePath.None)
            throw new OfflineDeploymentException("terminal_missing",
                "终局清理前没有捕获到原版结束路径，无法验证终局。");
        if (_value is not { } native)
            throw new OfflineDeploymentException("terminal_missing",
                "终局清理前没有捕获到原版 Battle，无法验证终局。");
        Battle predicted = original;
        ApplyTestTamper(ref predicted);
        return TerminalBattleProjection.Compare(in predicted, in native, _path, rules);
    }

    /// <summary>Negative-test hook: mutate only the predicted terminal copy.</summary>
    private static void ApplyTestTamper(ref Battle predicted)
    {
        string? tamper = Environment.GetEnvironmentVariable("STS2_SOLVER_PILOT_TAMPER");
        switch (tamper)
        {
            case "hp":
                predicted.Player.CurrentHp = predicted.Player.CurrentHp == 0
                    ? 1 : predicted.Player.CurrentHp + 1;
                break;
            case "outcome":
                predicted.Outcome = predicted.Outcome == Outcome.Defeat
                    ? Outcome.Victory : Outcome.Defeat;
                break;
            case "event":
                predicted.Outcome = Outcome.EventDefeat;
                break;
        }
    }

    private static void VictoryPrefix() => Capture(TerminalCapturePath.Victory);

    private static void DefeatPrefix() => Capture(TerminalCapturePath.Defeat);

    private static void Capture(TerminalCapturePath path)
    {
        if (_capture == null || _value != null || _failure != null)
            return;
        _path = path;
        try
        {
            _value = _capture();
        }
        catch (UnsupportedImportException error)
        {
            _failure = error;
        }
        catch (Sts2Solver.Simulation.Combat.UnsupportedException error)
        {
            _failure = error;
        }
        catch (CapacityException error)
        {
            _failure = error;
        }
        catch (InvalidDataException error)
        {
            _failure = error;
        }
    }

    internal static Battle? Import(CombatState? state, LiveCombatTracker tracker, Rules rules,
        in Battle expected, Player player)
    {
        if (state == null)
            return null;
        Player current = LocalContext.GetMe(state) ?? player;
        return tracker.ImportForComparison(state, current, rules, in expected,
            requirePlayPhase: false, allowDeadPlayer: true);
    }
}
