// Builds every native option of the turn-setup page by rebuilding the same rooted scenario once per
// option. CombatSolver enumerated these branches on its shadow engine; the offline host runs the
// native setup page again for each choice, then imports each Play root with the shared rule table.
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Import;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace Differential;

internal sealed class SetupRootSet
{
    public required Rules Rules { get; init; }
    public required List<Battle> Roots { get; init; }
    public required List<string> Choices { get; init; }
    public required List<string> OptionIds { get; init; }
    public required HashSet<CardModel> StartCards { get; init; }
    public ScoreRules? Score { get; init; }
    public BoundFeatures? Bound { get; init; }
    public SearchPolicy? Policy { get; init; }

    public static SetupRootSet Build(MainLoopContext loop, OfflineRequest request, bool needConfig)
    {
        TurnSetupCardSelector firstSelector = new(0);
        TurnSetupRootCapture.BeginBuild();
        Task<CombatState> firstBuild = ScenarioBuilder.BuildAsync(new OfflineCombatWaiter(loop), request,
            skipInitialInjection: true, firstSelector);
        loop.RunUntilCompleted(firstBuild, TimeSpan.FromSeconds(600), "建局 setup[0]");
        CombatState firstCombat = firstBuild.GetAwaiter().GetResult();
        HashSet<CardModel> firstCards = TurnSetupRootCapture.StartCards
            ?? throw new InvalidDataException("回合准备 Start 快照没有捕获到卡牌集合。");
        Player player = LocalContext.GetMe(firstCombat)
            ?? throw new InvalidOperationException("找不到本地玩家。");
        if (firstCombat.RunState is not MegaCrit.Sts2.Core.Runs.RunState runState)
            throw new InvalidDataException("原生战斗的 RunState 不是具体 RunState。");
        Rules rules = RulesCompiler.Compile(firstCombat, player, runState);
        Battle first = BattleImporter.Import(firstCombat, player, rules,
            ImportOptions.Default with { TurnSetupRoot = true, RootCards = firstCards });
        int optionCount = firstSelector.LastOptions.Count;
        if (optionCount == 0)
            throw new InvalidDataException("回合准备页没有给出任何选项。");

        ScoreRules? score = null;
        BoundFeatures? bound = null;
        SearchPolicy? policy = null;
        if (needConfig)
        {
            policy = SearchPolicyOverrides.ApplyOverrides(
                SearchPolicyCapture.Capture(firstCombat, player), request, player);
            EvaluationRootFacts facts = EvaluationRootFacts.Default with
            {
                TurnSetupRoot = true,
            };
            score = ScoreCompiler.Compile(firstCombat, player, rules, first, policy, facts);
            bound = EvaluationConfigCompiler.CompileBound(firstCombat, player, first, policy, facts);
        }

        List<Battle> roots = [first];
        List<string> choices = [ChoiceKey(firstSelector.LastSelected)];
        List<string> optionIds = [.. firstSelector.LastOptions.Select(static option => option.CardId)];
        try
        {
            for (int index = 1; index < optionCount; index++)
            {
                RunManager.Instance.CleanUp(true);
                TurnSetupRootCapture.BeginBuild();
                TurnSetupCardSelector selector = new(index);
                Task<CombatState> build = ScenarioBuilder.BuildAsync(new OfflineCombatWaiter(loop), request,
                    skipInitialInjection: true, selector);
                loop.RunUntilCompleted(build, TimeSpan.FromSeconds(600), $"建局 setup[{index}]");
                CombatState combat = build.GetAwaiter().GetResult();
                Player variantPlayer = LocalContext.GetMe(combat)
                    ?? throw new InvalidOperationException("找不到本地玩家。");
                HashSet<CardModel> cards = TurnSetupRootCapture.StartCards
                    ?? throw new InvalidDataException($"回合准备 Start 快照 [{index}] 没有捕获到卡牌集合。");
                Battle battle = BattleImporter.Import(combat, variantPlayer, rules,
                    ImportOptions.Default with { TurnSetupRoot = true, RootCards = cards });
                roots.Add(battle);
                choices.Add(ChoiceKey(selector.LastSelected));
            }
        }
        finally
        {
            RunManager.Instance.CleanUp(true);
        }

        return new SetupRootSet
        {
            Rules = rules,
            Roots = roots,
            Choices = choices,
            OptionIds = optionIds,
            StartCards = firstCards,
            Score = score,
            Bound = bound,
            Policy = policy,
        };
    }

    private static string ChoiceKey((string CardId, int Upgrade)? selected)
        => selected is { } value ? $"{value.CardId}+{value.Upgrade}" : "";
}
