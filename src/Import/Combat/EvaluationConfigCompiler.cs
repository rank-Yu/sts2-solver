// Entry point for the M2a-4 evaluation configuration: assemble the same SREQ request the
// CombatSolver M0 harness wrote (rules + imported root + BeamOptions with Scorer/Bound).
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace Sts2Solver.Import;

/// <summary>Compiles the score table and bound configuration and assembles the frozen request.</summary>
internal static class EvaluationConfigCompiler
{
    public static Scorer CompileScorer(CombatState state, Player player, Rules rules,
        in Battle root, SearchPolicy policy, EvaluationRootFacts facts,
        Battle? rankBaselineRoot = null, Rules? rankBaselineRules = null)
        => new(ScoreCompiler.Compile(state, player, rules, root, policy, facts,
            rankBaselineRoot, rankBaselineRules));

    public static BoundFeatures CompileBound(CombatState state, Player player, in Battle root,
        SearchPolicy policy, EvaluationRootFacts facts)
        => new(BoundCompiler.Compile(state, player, root, policy, facts));

    public static SearchCase CompileRequest(CombatState state, Player player, Rules rules,
        Battle root, SearchPolicy policy, EvaluationRootFacts facts)
    {
        Scorer scorer = CompileScorer(state, player, rules, root, policy, facts);
        BoundFeatures bound = CompileBound(state, player, root, policy, facts);
        BeamOptions options = new(1, 1, 1, TimeSpan.Zero)
        {
            Scorer = scorer,
            Bound = bound,
        };
        return new SearchCase(rules, [root], options);
    }
}
