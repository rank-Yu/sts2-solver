using System.IO.Compression;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

/// <summary>The forced-use semantics ported from the existing search's potion strategy and final ordering.</summary>
internal static class ForcedPotionContracts
{
    private const string PotionRoot = "3aa9116ebf3c";

    private static void Assert(bool value, string message)
    { if (!value) throw new InvalidDataException("forced-potion: " + message); }

    internal static void Run()
    {
        StrategySemantics();
        SmartThresholds();
        ForcedRouteUsesPotion();
        UnusableForcedPotionFails();
        Console.WriteLine("FORCED_POTION filter/thresholds/route/unsatisfied=pass");
    }

    private static void StrategySemantics()
    {
        PotionStrategySnapshot strategy = new(PotionUseMode.Smart,
        [
            new PotionSlotDirective(0, "FIRE_POTION", PotionDirective.Force),
            new PotionSlotDirective(1, "BLOCK_POTION", PotionDirective.Disabled),
            new PotionSlotDirective(2, "AMBERGRIS", PotionDirective.Force),
            new PotionSlotDirective(3, "STRENGTH_POTION", PotionDirective.Smart),
        ]);
        Assert(strategy.HasForcedDirectives, "forced directives detected");
        Assert(strategy.ForcedDirectiveCount == 2, "forced directive count");
        Assert(strategy.DescribeForcedUses() == "FIRE_POTION@0, AMBERGRIS@2",
            strategy.DescribeForcedUses());
        Assert(strategy.AllowsExplicitUse(0, "FIRE_POTION", PotionUseMode.Smart, false),
            "forced potion allowed");
        Assert(!strategy.AllowsExplicitUse(1, "BLOCK_POTION", PotionUseMode.Smart, false),
            "disabled potion rejected");
        Assert(strategy.AllowsExplicitUse(3, "STRENGTH_POTION", PotionUseMode.Smart, false),
            "smart potion allowed");
        PotionStrategySnapshot forced = strategy.ForForcedBaseline();
        Assert(forced.AllowsExplicitUse(0, "FIRE_POTION", PotionUseMode.Smart, false),
            "forced baseline allows the forced potion");
        Assert(!forced.AllowsExplicitUse(3, "STRENGTH_POTION", PotionUseMode.Smart, false),
            "forced baseline rejects optional potions");

        ForcedPotionUseEvaluation both = strategy.EvaluateForcedUses(
        [
            new PotionUse(0, "FIRE_POTION", 9),
            new PotionUse(3, "STRENGTH_POTION", 9),
            new PotionUse(2, "AMBERGRIS", 14),
        ]);
        Assert(both.AllForcedUsesSatisfied, "all forced uses satisfied");
        Assert(both.ForcedUseCount == 2, "forced use count");
        Assert(both.ForcedStrategicHpCost == 23, both.ForcedStrategicHpCost.ToString());
        Assert(both.ForcedAmbergrisCount == 1, "forced ambergris count");
        ForcedPotionUseEvaluation missing = strategy.EvaluateForcedUses(
        [
            new PotionUse(0, "FIRE_POTION", 9),
        ]);
        Assert(!missing.AllForcedUsesSatisfied, "missing forced use is unsatisfied");
        Assert(missing.ForcedUseCount == 1, "missing forced use count");
        Assert(missing.ForcedStrategicHpCost == 9, "missing forced strategic cost");
        ForcedPotionUseEvaluation wrongSlot = strategy.EvaluateForcedUses(
        [
            new PotionUse(1, "FIRE_POTION", 9),
            new PotionUse(2, "AMBERGRIS", 14),
        ]);
        Assert(!wrongSlot.AllForcedUsesSatisfied && wrongSlot.ForcedUseCount == 1,
            "forced use matches the slot and identity");
    }

    private static void SmartThresholds()
    {
        foreach ((int cost, int required) in new[] { (9, 9), (14, 14), (18, 18) })
        {
            Assert(PotionUsePolicy.SmartRequiredHpSaved(cost) == required,
                $"smart threshold {cost}");
            Assert(PotionUsePolicy.IsEligible(PotionUseMode.Smart, 1, cost,
                potionFreeWon: true, potionFreeHpDeficit: 100, anyRouteWon: true,
                potionRouteWon: true, potionRouteHpDeficit: 100 - required),
                $"saving {required} justifies cost {cost}");
            Assert(!PotionUsePolicy.IsEligible(PotionUseMode.Smart, 1, cost,
                potionFreeWon: true, potionFreeHpDeficit: 100, anyRouteWon: true,
                potionRouteWon: true, potionRouteHpDeficit: 100 - required + 1),
                $"saving {required - 1} cannot justify cost {cost}");
        }
        Assert(PotionUsePolicy.SmartRequiredHpSaved(9, BossHpRelief.ActClearHeal) == 45,
            "act-ending HP is worth five times as much");
        Assert(PotionUsePolicy.IsEligible(PotionUseMode.Smart, 1,
            PotionUsePolicy.SmartRequiredHpSaved(9, BossHpRelief.ActClearHeal),
            potionFreeWon: true, potionFreeHpDeficit: 100, anyRouteWon: true,
            potionRouteWon: true, potionRouteHpDeficit: 100 - 45),
            "act-ending boss requires the scaled saved HP");
    }

    private static (SearchCase Case, Battle Root) LoadForcedCase(string forcedPotionId)
    {
        using FileStream input = File.OpenRead(Directory.GetFiles(
            "tests/Golden/data/d2/requests", PotionRoot + "*.request.br").Single());
        using BrotliStream compressed = new(input, CompressionMode.Decompress);
        using MemoryStream bytes = new();
        compressed.CopyTo(bytes);
        SearchCase original = SearchCodec.DecodeRequest(bytes.ToArray());
        Battle root = original.Battles[0];
        PotionStrategySnapshot strategy = new(PotionUseMode.Smart,
        [
            new PotionSlotDirective(0, "POISON_POTION", PotionDirective.Disabled),
            new PotionSlotDirective(1, forcedPotionId, PotionDirective.Force),
        ]);
        PotionPolicy potions = original.Options.Potions with
        {
            Strategy = strategy,
            EffectivePolicy = PotionUseMode.Smart,
            ForceAllDisabled = false,
            EarliestPlayerTurn = int.MinValue,
        };
        BeamOptions options = original.Options with
        {
            BeamWidth = 12,
            MaxExpandedNodes = 100_000,
            MaxTurns = int.MaxValue,
            UnlimitedNodes = false,
            SoftTimeBudget = TimeSpan.MaxValue,
            SearchThreads = 1,
            DeadlineExpired = null,
            SecondRankBand = false,
            Potions = potions,
            EffectivePotionPolicy = PotionUseMode.Smart,
            PotionFreeBaseline = null,
            AuditPotionPolicy = true,
        };
        options = SearchBudgetPolicy.ApplyRegression(options);
        return (original with { Options = options }, root);
    }

    private static void ForcedRouteUsesPotion()
    {
        (SearchCase test, Battle root) = LoadForcedCase("FIRE_POTION");
        Assert(root.PotionSlotCount > 1 && root.Potions[1] != 0
            && Registry.Potion((ushort)(root.Potions[1] - 1)).NativeId == "FIRE_POTION",
            "fixture slot 1 holds FIRE_POTION");
        SearchCase decoded = SearchCodec.DecodeRequest(SearchCodec.EncodeRequest(test));
        Assert(decoded.Options.Potions.Strategy.Directives.Any(item =>
            item.Slot == 1 && item.PotionId == "FIRE_POTION"
            && item.Directive == PotionDirective.Force),
            "forced directive round trips through SREQ");
        BeamResult result = new BeamSolver(test.Rules, test.Options)
            .Solve(test.Battles, CancellationToken.None);
        Assert(result.PotionPolicySatisfied, "forced route found");
        Assert(result.BestRoute.Any(action => action.Kind == PlayerActionKind.UsePotion
            && action.PotionSlot == 1), "route uses the forced potion");
        Assert(!result.BestRoute.Any(action => action.Kind == PlayerActionKind.UsePotion
            && action.PotionSlot == 0), "route never uses the disabled potion");
    }

    private static void UnusableForcedPotionFails()
    {
        (SearchCase test, _) = LoadForcedCase("FAIRY_IN_A_BOTTLE");
        BeamResult direct = new BeamSolver(test.Rules, test.Options)
            .Solve(test.Battles, CancellationToken.None);
        Assert(!direct.PotionPolicySatisfied, "unusable forced potion has no eligible route");
        Assert(direct.BestRoute.Length == 0, "unusable forced potion returns no route");

        SearchPlan plan = new(8, 4, 8, 3, 100_000_000) { MillisecondsPerWidth = 25 };
        ValueTask<SearchMemberMeasurement> Run(SearchCase member, SearchStorage shared,
            CancellationToken token)
        {
            BeamResult result = new BeamSolver(member.Rules, member.Options)
                .Solve(member.Battles, shared, token);
            return ValueTask.FromResult(new SearchMemberMeasurement(result, "ok", null, 0));
        }
        CombatSearchResult outcome = CombatSearchCoordinator.RunAsync(
            new(test, plan), new SearchStorage(), Run, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        Assert(outcome.Status == CombatSearchStatus.Invalid, outcome.Status.ToString());
        Assert(outcome.Completion == SearchCompletionKind.Failed, outcome.Completion.ToString());
        Assert(outcome.Error?.Contains("指定药水必须使用", StringComparison.Ordinal) == true,
            outcome.Error ?? "-");
        Assert(outcome.Error?.Contains("FAIRY_IN_A_BOTTLE@1", StringComparison.Ordinal) == true,
            outcome.Error ?? "-");
        Assert(outcome.ForcedPotionsUnsatisfied, "forced failure is flagged for the host");

        // Without the Smart gradient a forced directive is still a hard requirement, so an
        // unusable one must fail explicitly instead of returning an unqualified incomplete route.
        SearchCase disabled = test with
        {
            Options = test.Options with
            {
                EffectivePotionPolicy = PotionUseMode.Disabled,
                Potions = test.Options.Potions with { EffectivePolicy = PotionUseMode.Disabled },
            },
        };
        CombatSearchResult disabledOutcome = CombatSearchCoordinator.RunAsync(
            new(disabled, plan), new SearchStorage(), Run, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        Assert(disabledOutcome.Status == CombatSearchStatus.Invalid,
            disabledOutcome.Status.ToString());
        Assert(disabledOutcome.Completion == SearchCompletionKind.Failed,
            disabledOutcome.Completion.ToString());
        Assert(disabledOutcome.Error?.Contains("指定药水必须使用", StringComparison.Ordinal) == true,
            disabledOutcome.Error ?? "-");
        Assert(disabledOutcome.ForcedPotionsUnsatisfied, "forced failure is flagged without the gradient");
    }
}
