using System.Text.Json;
using System.Text.RegularExpressions;
using Sts2Solver.Autoplay;
using Sts2Solver.TestHost;

namespace Sts2Solver;

/// <summary>
/// Structured assertions over the snapshot history of one full combat. The output file uses stable
/// English keys and numeric/enum values; localized pause text is never asserted.
/// </summary>
internal static class UiSnapshotChecks
{
    internal static UiSnapshotCheckResult Run(string resultDirectory, CombatPilotResult result,
        CombatUiSnapshot[] history)
    {
        List<UiSnapshotCheck> checks = [];
        List<string> failures = [];
        void Check(string name, Func<bool> condition)
        {
            bool ok;
            string? error = null;
            try
            {
                ok = condition();
            }
            catch (InvalidDataException exception)
            {
                ok = false;
                error = exception.Message;
            }
            checks.Add(new UiSnapshotCheck(name, ok ? "ok" : "failed", error));
            if (!ok)
                failures.Add(error == null ? name : $"{name}:{error}");
        }
        void Require(string name, bool condition)
        {
            if (!condition)
                throw new InvalidDataException(name);
        }

        Check("history_nonempty", () => history.Length > 0);
        Check("phase_waiting", () => history.Any(static snapshot => snapshot.Phase == CombatUiPhase.WaitingTurn));
        Check("phase_searching", () => history.Any(static snapshot => snapshot.Phase == CombatUiPhase.Searching));
        Check("phase_deploying", () => history.Any(static snapshot => snapshot.Phase == CombatUiPhase.Deploying));
        Check("phase_terminal", () => history.Any(static snapshot =>
            snapshot.Phase is CombatUiPhase.Completed or CombatUiPhase.Paused
                or CombatUiPhase.Failed or CombatUiPhase.Cancelled));

        Check("search_start", () => history.Any(static snapshot =>
            snapshot.Search is { Phase: SearchUiPhase.Running }));
        Check("search_end", () => history.Any(static snapshot =>
            snapshot.Search is { Phase: SearchUiPhase.Completed }));

        if (result.SearchLog.Length > 0)
        {
            CombatPilotSearchEntry expected = result.SearchLog[0];
            SearchUiSnapshot? finished = history.Select(static snapshot => snapshot.Search)
                .LastOrDefault(static search => search is { Phase: SearchUiPhase.Completed });
            Check("search_stats", () =>
            {
                Require("search_missing", finished != null);
                Require("search_turn", finished!.Turn == expected.Turn);
                Require("search_status", finished.Status == "ok");
                Require("search_selected_member", finished.SelectedMember == expected.SelectedMember);
                Require("search_members", finished.Members.Length == expected.Members.Length);
                Require("search_members_run", finished.MembersRun == expected.MembersRun);
                Require("search_expanded", finished.ExpandedNodes == expected.ExpandedNodes);
                Require("search_route_actions", finished.RouteActions == expected.RouteActions);
                Require("search_member_runs", finished.MemberRuns.Length == expected.MembersRun);
                return true;
            });
        }

        RouteUiSnapshot? route = history.Select(static snapshot => snapshot.Route)
            .LastOrDefault(static value => value != null);
        Check("route_present", () => route != null);
        if (route != null)
        {
            Check("route_groups", () =>
            {
                Require("route_actions", route.Turns.Sum(static turn => turn.Actions.Length)
                    == route.ActionCount);
                Require("route_group_count", route.Turns.Length > 0);
                for (int index = 0; index < route.Turns.Length; index++)
                {
                    RouteTurnUi turn = route.Turns[index];
                    Require("route_turn_action_count", turn.Actions.Length > 0);
                    Require("route_turn_start", turn.StartActionIndex == turn.Actions[0].Index);
                    for (int action = 0; action < turn.Actions.Length; action++)
                    {
                        Require("route_action_index",
                            turn.Actions[action].Index == turn.StartActionIndex + action);
                    }
                    if (index > 0)
                    {
                        Require("route_turn_order", turn.Turn > route.Turns[index - 1].Turn);
                        Require("route_turn_contiguous",
                            turn.StartActionIndex == route.Turns[index - 1].StartActionIndex
                                + route.Turns[index - 1].Actions.Length);
                    }
                }
                return true;
            });
            Check("route_progress", () =>
            {
                foreach (RouteUiSnapshot snapshot in history.Select(static value => value.Route)
                             .Where(static value => value != null).Select(static value => value!))
                {
                    Require("route_progress_bounds",
                        snapshot.CurrentActionIndex >= 0 && snapshot.CurrentActionIndex <= snapshot.ActionCount
                        && snapshot.CompletedActions >= 0 && snapshot.CompletedActions <= snapshot.ActionCount);
                    Require("route_progress_order",
                        snapshot.CompletedActions == snapshot.CurrentActionIndex
                        || snapshot.CompletedActions == snapshot.CurrentActionIndex + 1);
                }
                Require("route_completed", route.CompletedActions == route.ActionCount);
                return true;
            });
            Check("route_reuse", () =>
            {
                if (result.ReusedTurns == 0)
                    return true;
                Require("route_reuse_flag", history.Any(static snapshot => snapshot.Route is { Reused: true }));
                Require("route_reuse_turns", route.Turns.Length >= 1);
                int reusedTurns = history.Select(static snapshot => snapshot.Route)
                    .Where(static value => value is { Reused: true })
                    .Select(static value => value!.StartTurn)
                    .Distinct()
                    .Count();
                Require("route_reuse_turn_advance", reusedTurns > 1);
                return true;
            });
        }

        Check("terminal_reason", () =>
        {
            CombatUiSnapshot? terminal = history.LastOrDefault(static snapshot =>
                snapshot.Phase is CombatUiPhase.Completed or CombatUiPhase.Paused
                    or CombatUiPhase.Failed or CombatUiPhase.Cancelled);
            Require("terminal_missing", terminal != null);
            Require("terminal_status", terminal!.EndReason == result.Status);
            Require("terminal_turn", terminal.Turn == result.Turns);
            Require("terminal_hp", terminal.PlayerHp == result.PlayerHp);
            Require("terminal_max_hp", terminal.PlayerMaxHp == result.PlayerMaxHp);
            Require("terminal_searches", terminal.Searches == result.Searches);
            Require("terminal_reused", terminal.ReusedTurns == result.ReusedTurns);
            Require("terminal_deviations", terminal.Deviations == result.Deviations);
            Require("terminal_deployment_failures",
                terminal.DeploymentFailures == result.DeploymentFailures);
            CombatUiPhase expectedPhase = result.Status switch
            {
                "victory" or "defeat" or "ended" => CombatUiPhase.Completed,
                "paused" => CombatUiPhase.Paused,
                "cancelled" => CombatUiPhase.Cancelled,
                _ => CombatUiPhase.Failed,
            };
            Require("terminal_phase", terminal.Phase == expectedPhase);
            return true;
        });
        Check("error_codes", () =>
        {
            Regex pattern = new("^[a-z0-9_]+$", RegexOptions.CultureInvariant);
            foreach (CombatUiSnapshot snapshot in history)
            {
                if (snapshot.ErrorCode is { } code)
                    Require("error_code_format", pattern.IsMatch(code));
                if (snapshot.Search?.ErrorCode is { } searchCode)
                    Require("search_error_code_format", pattern.IsMatch(searchCode));
            }
            return true;
        });
        Check("settings_memory", () =>
        {
            Require("history_empty", history.Length > 0);
            CombatUiSnapshot terminal = history[^1];
            Require("settings_budget", terminal.Memory.BudgetBytes > 0);
            return true;
        });

        UiSnapshotCheckResult output = new(1, failures.Count == 0 ? "ok" : "failed",
            [.. checks], [.. failures], history);
        string path = Path.Combine(resultDirectory, "ui-snapshots.json");
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(output, OfflineJson.Options));
        File.Move(temporary, path, overwrite: true);
        ModLog.Info(
            $"[Sts2Solver] UI_SNAPSHOTS status={output.Status} snapshots={history.Length} " +
            $"checks={checks.Count} failed={failures.Count}");
        if (failures.Count > 0)
            throw new InvalidDataException("ui_snapshot:" + string.Join(",", failures));
        return output;
    }

    internal sealed record UiSnapshotCheckResult(int SchemaVersion, string Status,
        UiSnapshotCheck[] Checks, string[] Failures, CombatUiSnapshot[] Snapshots);

    internal sealed record UiSnapshotCheck(string Name, string Status, string? Error);
}
