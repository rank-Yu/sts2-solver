using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Ai.Combat.Quality;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

internal static class PotionStageContracts
{
    private static void Assert(bool value, string message)
    { if (!value) throw new InvalidDataException("potion-stage: " + message); }

    internal static void Run(SearchCase fixture, SearchPlan plan)
    {
        Assert(Registry.TryPotion("FIRE_POTION", out ushort fire), "fire potion");
        BeamOptions options = fixture.Options with
        {
            AuditPotionPolicy = true,
            Potions = new(new(PotionUseMode.Smart, []), false),
        };
        BeamSolver quality = new(fixture.Rules, options);
        BeamSolver.Candidate free = new(0, default(BeamSolver.RouteKey) with
        {
            Won = true, Survives = true, StrategicHpDeficit = 20, CombatEndedTurn = 5,
        }, 0, 0);
        BeamSolver.Candidate paid = new(1, free.Key with
        {
            StrategicHpDeficit = 5, PotionHpCost = 8, OptionalPotionPrice = 8, OptionalPotionCount = 1,
        }, 1, 0);
        BeamSolver.Candidate expensive = paid with { Key = paid.Key with { PotionHpCost = 18 } };
        Assert(quality.IsPolicyEligible(paid) && quality.IsPolicyEligible(expensive),
            "legal potion routes do not need a potion-free baseline or minimum savings");
        Assert(quality.ComparePolicyResults(paid, free) < 0
            && quality.ComparePolicyResults(free, paid) > 0
            && quality.ComparePolicyResults(expensive, free) > 0,
            "potion cost chooses the winner in either discovery order");
        foreach (int deficit in new[] { 0, 100 })
        {
            BeamSolver legacy = new(fixture.Rules, options with
            {
                PotionFreeBaseline = new(true, deficit, 75 - deficit, 1),
            });
            Assert(legacy.IsPolicyEligible(paid)
                && legacy.ComparePolicyResults(paid, free) < 0,
                "legacy serialized baselines do not influence eligibility or ordering");
        }
        BeamSolver disabled = new(fixture.Rules, options with
        {
            Potions = options.Potions with { ForceAllDisabled = true },
        });
        Assert(disabled.IsPolicyEligible(free) && !disabled.IsPolicyEligible(paid),
            "potion-free searches still reject explicit potion use");
        PotionPolicy protectedBottle = new(new(PotionUseMode.Smart,
            [new(0, "FIRE_POTION", PotionDirective.Disabled)]), false);
        Assert(options.Potions.AllowsExplicitUse(0, fire)
            && !protectedBottle.AllowsExplicitUse(0, fire)
            && !(options.Potions with { ForceAllDisabled = true }).AllowsExplicitUse(0, fire),
            "per-bottle protection and potion-free constraints still control expansion");
        SearchIncumbent? incumbent = null;
        Assert(IncumbentPolicy.TryTightenIncumbent(
            true, true, quality.IsPolicyEligible(paid), paid.Key.EffectiveHpDeficit,
            paid.Key.CombatEndedTurn, ref incumbent, PotionUseMode.Smart)
            && incumbent is { EffectiveHpDeficit: 13 },
            "a legal potion victory still sets the shared effective-cost pruning bound");
        Assert(IncumbentPolicy.ShouldPruneByIncumbent(
            14, 1, incumbent!.Value)
            && !IncumbentPolicy.ShouldPruneByIncumbent(
                12, 1, incumbent!.Value),
            "the bound prunes worse branches and preserves cheaper possibilities");

        SearchMember[] powered = PortfolioBuilder.Build(plan, power: true);
        SearchMember[] ordinary = PortfolioBuilder.Build(plan, power: false);
        Assert(powered.Length == 10 && ordinary.Length == 8,
            "compact portfolio sizes");
        Assert(powered.Select(m => (m.Retention, m.Evaluation, m.Potions)).SequenceEqual(new[]
            {
                (SearchRetention.Power, SearchEvaluation.Standard, SearchPotionLimit.None),
                (SearchRetention.Power, SearchEvaluation.Standard, SearchPotionLimit.Allowed),
                (SearchRetention.Standard, SearchEvaluation.Standard, SearchPotionLimit.None),
                (SearchRetention.Standard, SearchEvaluation.Standard, SearchPotionLimit.None),
                (SearchRetention.Standard, SearchEvaluation.Standard, SearchPotionLimit.Allowed),
                (SearchRetention.SecondBand, SearchEvaluation.Standard, SearchPotionLimit.None),
                (SearchRetention.Standard, SearchEvaluation.Base, SearchPotionLimit.None),
                (SearchRetention.Standard, SearchEvaluation.Base, SearchPotionLimit.Allowed),
                (SearchRetention.Standard, SearchEvaluation.Offensive, SearchPotionLimit.None),
                (SearchRetention.Standard, SearchEvaluation.Offensive, SearchPotionLimit.Allowed),
            }), "main method order pairs None/Allowed and keeps offensive last");
        Assert(ordinary.SequenceEqual(powered.Skip(2))
            && powered[2].Width == plan.NarrowWidth
            && powered[3].Width == plan.WideWidth && powered[4].Width == plan.WideWidth,
            "without power only its pair disappears; ordinary stays narrow and wide stays wide");
        Console.WriteLine("POTION_STAGE unified-cost/protection/incumbent/portfolios=pass");
    }
}
