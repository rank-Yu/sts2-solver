// Targeted native regressions for CombatSolver 0.51.1–0.51.4 reports.
// Reference: Torch1230/CombatSolver bb0544d2, coverage/fixtures and docs/issues/reports-20261113-logic.md
// (MIT, Copyright (c) 2026 Torch1230 and contributors). Uses this repository's packed-state executor.
using System.Globalization;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using Sts2Solver.Import;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Autoplay;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.TestHost;
using NativePlayer = MegaCrit.Sts2.Core.Entities.Players.Player;

namespace Differential;

internal static class NativeEffectChecks
{
    internal static int Run(string path, string[] commands)
    {
        MainLoopContext loop = new();
        SynchronizationContext.SetSynchronizationContext(loop);
        GameBootstrap.ApplyGodotBypasses();
        GameBootstrap.SkipGodotNodeStaticConstructors();
        GameBootstrap.InitializeStaticState();
        TerminalBattleCapture.Install();
        OfflineRequest request = JsonSerializer.Deserialize<OfflineRequest>(
            File.ReadAllText(path), OfflineJson.Options)!;
        Task<CombatState> build = ScenarioBuilder.BuildAsync(new OfflineCombatWaiter(loop), request,
            turnSetupSelector: new TurnSetupCardSelector(0));
        loop.RunUntilCompleted(build, TimeSpan.FromSeconds(120), "native effect root");
        CombatState state = build.GetAwaiter().GetResult();
        NativePlayer player = LocalContext.GetMe(state)!;
        Rules rules = RulesCompiler.Compile(state, player, (RunState)state.RunState);
        LiveCombatTracker tracker = new(state);
        Battle current = tracker.ImportRoot(state, player, rules);
        tracker.Observe(current);
        Console.WriteLine($"NATIVE_EFFECT_ROOT scenario={request.ScenarioId} potion_slots={current.PotionSlotCount}");
        SearchCase? initialGoldRequest = null;
        foreach (string command in commands)
        {
            string[] fields = command.Split(':');
            if (fields is ["random", var seed, var steps])
            {
                Random random = new(int.Parse(seed, CultureInfo.InvariantCulture));
                int limit = int.Parse(steps, CultureInfo.InvariantCulture);
                if (limit <= 0) throw new InvalidDataException("Random walk must have positive steps.");
                CombatActionCoverage coverage = new();
                int executed = 0;
                while (executed < limit && current.Outcome == Outcome.None)
                {
                    ReplayedAction planned;
                    try
                    {
                        planned = RandomCombatPolicy.Choose(current, rules, random, coverage)
                            ?? throw new InvalidDataException("Ongoing battle has no legal action.");
                    }
                    catch (CapacityException exception)
                    {
                        // This action cannot be represented; retain the verified prefix and report the limit.
                        Console.WriteLine($"NATIVE_WALK_LIMIT step={executed} reason={exception.Message}");
                        break;
                    }
                    Console.WriteLine($"NATIVE_WALK_ACTION step={executed} action={CombatPilotAction.Describe(planned)} selections={string.Join(",", planned.Choices.Select(c => c.Selection.ToString("X", CultureInfo.InvariantCulture)))}");
                    Execute(planned);
                    coverage.CountAction(planned.Action.Kind);
                    if (planned.Action.Kind == PlayerActionKind.PlayCard)
                        coverage.CountCard(Registry.Card(planned.Before.Cards[planned.Action.HandIndex].Id).NativeId);
                    executed++;
                }
                foreach (var entry in coverage.Cards)
                    Console.WriteLine($"NATIVE_WALK_CARD {entry.Key}={entry.Count}");
                Console.WriteLine($"NATIVE_WALK_OK scenario={request.ScenarioId} seed={seed} actions={executed} outcome={current.Outcome}");
            }
            else if (fields is ["hp-loss-clamp"])
            {
                foreach (decimal amount in new[] { decimal.MinValue, -3.25m, -1m, -0.25m, 0m, 1.75m, 75m, decimal.MaxValue })
                {
                    var native = new MegaCrit.Sts2.Core.Entities.Creatures.Creature(player, 75, 100);
                    Creature simulated = new() { CurrentHp = 75, MaxHp = 100 };
                    var expected = native.LoseHpInternal(amount, MegaCrit.Sts2.Core.ValueProps.ValueProp.Move);
                    var actual = Kernel.LoseHp(ref simulated, Limits.PlayerOwner, amount, ValueProps.Move);
                    if (simulated.CurrentHp != native.CurrentHp || actual.WasTargetKilled != expected.WasTargetKilled
                        || actual.UnblockedDamage != expected.UnblockedDamage || actual.OverkillDamage != expected.OverkillDamage)
                        throw new InvalidDataException($"HP loss {amount}: native={native.CurrentHp}, solver={simulated.CurrentHp}.");
                }
            }
            else if (fields is ["gremlin-gold", var percent])
            {
                var fresh = Compile();
                initialGoldRequest ??= fresh;
                int nativePercent = (int)(100 * state.Encounter!.CalculateGoldProportion(state));
                bool lost = nativePercent < 100 && rules.Mode != CombatMode.Blitz;
                if (nativePercent != int.Parse(percent, CultureInfo.InvariantCulture)
                    || fresh.Options.Scorer!.Features(current, 0).LosesEncounterGold != lost
                    || initialGoldRequest.Options.Scorer!.Features(current, 0).LosesEncounterGold != lost)
                    throw new InvalidDataException($"Gremlin encounter gold: native={nativePercent}%, expected={percent}%.");
            }
            else if (fields is ["potion-win", var bottles])
            {
                var search = Compile();
                var options = SearchBudgetPolicy.ApplyRegression(search.Options) with { MaxTurns = 1, SearchThreads = 1 };
                var noPotions = options with { Potions = options.Potions with { ForceAllDisabled = true } };
                if (new BeamSolver(rules, noPotions).Solve([current], CancellationToken.None).BestIsVictory)
                    throw new InvalidDataException("Fixture can win the opening turn without a potion.");
                BeamResult result = new BeamSolver(rules, options).Solve([current], CancellationToken.None);
                if (!result.BestIsVictory) throw new InvalidDataException("No opening-turn potion victory.");
                var replay = ReplayedRoute.Build(current, rules, result.BestRoute, true);
                List<string> used = [];
                foreach (var step in replay.Actions)
                {
                    if (step.Action.Kind == PlayerActionKind.UsePotion)
                        used.Add(Registry.Potion((ushort)(step.Before.Potions[step.Action.PotionSlot] - 1)).NativeId);
                    Execute(step);
                }
                string names = string.Join(",", used);
                if (names != bottles)
                    throw new InvalidDataException($"Winning potions: {names}, expected {bottles}.");
            }
            else if (fields is ["recapture"])
            {
                rules = RulesCompiler.Compile(state, player, (RunState)state.RunState);
                tracker = new(state);
                current = tracker.ImportRoot(state, player, rules);
                tracker.Observe(current);
            }
            else
            {
                PlayerAction action = fields[0] switch
                {
                    "end" => PlayerAction.EndTurn(),
                    "potion" when fields.Length >= 3 =>
                        PlayerAction.UsePotion(int.Parse(fields[1]), byte.Parse(fields[2])),
                    "play" when fields.Length >= 3 =>
                        PlayerAction.PlayCard(int.Parse(fields[1]), byte.Parse(fields[2])),
                    _ => throw new InvalidDataException($"Unknown effect command: {command}"),
                };
                for (int index = fields[0] == "end" ? 1 : 3; index < fields.Length; index++)
                    action = action.WithChoice(ulong.Parse(fields[index], CultureInfo.InvariantCulture));
                Battle after = current;
                if (Kernel.Step(ref after, action, rules, out _) != StepResult.Completed)
                    throw new InvalidDataException("Effect command needs more choices: " + command);
                ReplayedAction planned = ReplayedRoute.Build(current, rules, [action],
                    after.Outcome == Outcome.Victory).Actions[0];
                Execute(planned);
            }
            Console.WriteLine($"NATIVE_EFFECT_OK scenario={request.ScenarioId} command={command}");
        }
        return 0;

        SearchPolicy Policy() => SearchPolicyOverrides.ApplyOverrides(SearchPolicyCapture.Capture(state, player), request, player);

        SearchCase Compile()
        {
            var search = SearchRequestCompiler.CompileRequest(state, player, rules, current, Policy(),
                EvaluationRootFacts.Default, SearchPerformanceProfile.For(SearchPerformanceTier.Medium),
                deterministic: true, searchMemoryBudgetBytes: 256 * 1024 * 1024);
            return SearchCodec.DecodeRequest(SearchCodec.EncodeRequest(search));
        }

        void Execute(ReplayedAction planned)
        {
            Battle actual = new NativeActionExecutor(loop, state, player, rules, tracker).Execute(planned);
            string? difference = planned.After.Outcome != Outcome.None
                ? TerminalBattleCapture.Compare(planned.After, rules)
                : CombatRouteDecision.ActionMatches(planned, actual) ? null
                    : string.Join("\n", CombatRouteDecision.BoundaryDiff(planned.After, actual));
            if (difference != null)
            {
                if (Environment.GetEnvironmentVariable("STS2_SOLVER_NATIVE_EVIDENCE") is { Length: > 0 } evidence)
                {
                    string directory = Path.Combine(evidence, request.ScenarioId);
                    Directory.CreateDirectory(directory);
                    File.WriteAllLines(Path.Combine(directory, "before.txt"), BattleDump.Lines(planned.Before));
                    File.WriteAllLines(Path.Combine(directory, "predicted.txt"), BattleDump.Lines(planned.After));
                    File.WriteAllLines(Path.Combine(directory, "actual.txt"), BattleDump.Lines(actual));
                    File.WriteAllBytes(Path.Combine(directory, "before.batl"), BattleCodec.Encode(planned.Before, rules.Relics));
                    File.WriteAllBytes(Path.Combine(directory, "rules.rule"), RulesCodec.Encode(rules));
                }
                throw new InvalidDataException(CombatPilotAction.Describe(planned) + ": " + difference);
            }
            current = planned.After;
            tracker.Observe(current);
        }
    }

}
