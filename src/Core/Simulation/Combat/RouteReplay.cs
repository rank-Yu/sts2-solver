// Route replay for deployment and reconciliation. The bit-set choices are resolved against the
// partial Battle the kernel is in when it asks, mirroring the existing search's replay order; this
// is a translation of the deployment design in CombatSolver src/Runtime/SolverController.cs at
// a62f1850 (MIT, Copyright (c) 2026 Torch1230 and contributors), not a copy of its engine.
namespace Sts2Solver.Simulation.Combat;

/// <summary>
/// One choice request observed while replaying an action, plus the selection the route carries and
/// the option card values frozen at request time. <see cref="Implicit"/> marks a native auto-select:
/// the game consumes all options without calling <c>ICardSelector</c>.
/// </summary>
internal sealed class PlannedChoice
{
    internal required ChoiceRequest Request { get; init; }
    internal required ulong Selection { get; init; }
    internal required bool Implicit { get; init; }
    internal required Card[] PileOptions { get; init; }
    internal required Card[] GeneratedOptions { get; init; }

    internal int OptionCount => Request.OptionCount;

    internal bool OptionsAreGenerated => Request.OptionsAreGenerated;

    internal bool SelectsAll()
    {
        if (Request.OptionCount == 0)
            return Selection == 0;
        ulong all = Request.OptionCount >= 64 ? ulong.MaxValue : (1UL << Request.OptionCount) - 1;
        return Selection == all;
    }

    internal IEnumerable<int> SelectedIndexes()
    {
        for (int index = 0; index < Request.OptionCount; index++)
        {
            if ((Selection & (1UL << index)) != 0)
                yield return index;
        }
    }
}

/// <summary>One route action with the state before it and the planned choices it consumes.</summary>
internal sealed class ReplayedAction
{
    internal required int Index { get; init; }
    internal required PlayerAction Action { get; init; }
    internal required Battle Before { get; init; }
    internal required Battle After { get; init; }
    internal required PlannedChoice[] Choices { get; init; }

    /// <summary>
    /// The action reached the enemy-side turn-end pass. The native turn counter is already one ahead
    /// of the kernel boundary when a kill there ends the fight.
    /// </summary>
    internal required bool EnemySideTurnEndReached { get; init; }
}

/// <summary>
/// Deterministic replay of a <c>BeamResult.BestRoute</c>. The replay is the deployment authority:
/// every action must reach <see cref="StepResult.Completed"/> with exactly its carried choices, and
/// the final state must agree with the search's victory flag.
/// </summary>
internal sealed class ReplayedRoute
{
    private ReplayedRoute(Battle root, ReplayedAction[] actions, bool victory, Rules rules)
    {
        Root = root;
        Actions = actions;
        Victory = victory;
        Rules = rules;
    }

    internal Battle Root { get; }

    internal ReplayedAction[] Actions { get; }

    internal bool Victory { get; }

    internal Rules Rules { get; }

    internal int Count => Actions.Length;

    internal ReplayedAction this[int index] => Actions[index];

    internal static ReplayedRoute Build(in Battle root, Rules rules, IReadOnlyList<PlayerAction> route,
        bool expectedVictory)
    {
        List<ReplayedAction> actions = new(route.Count);
        Battle current = root;
        for (int index = 0; index < route.Count; index++)
        {
            PlayerAction action = route[index];
            Battle before = current;
            List<PlannedChoice> choices = new(action.ChoiceCount);
            Battle after = default;
            bool completed = false;
            bool enemySideTurnEndReached = false;
            for (int prefix = 0; prefix <= action.ChoiceCount; prefix++)
            {
                Battle partial = before;
                PlayerAction partialAction = action;
                partialAction.ChoiceCount = checked((byte)prefix);
                StepResult step = Kernel.Step(ref partial, partialAction, rules,
                    out ChoiceRequest request, out _, out bool reachedEnemySideTurnEnd);
                if (prefix < action.ChoiceCount)
                {
                    if (step != StepResult.NeedsChoice)
                    {
                        throw new InvalidDataException(
                            $"路线动作 {index} 携带了多余的选择 {prefix + 1}/{action.ChoiceCount}。");
                    }
                    choices.Add(CaptureChoice(partial, request, action.Choices[prefix]));
                }
                else
                {
                    if (step != StepResult.Completed)
                    {
                        throw new InvalidDataException(
                            $"路线动作 {index} 在 {prefix} 个选择后仍请求额外选择。");
                    }
                    after = partial;
                    completed = true;
                    enemySideTurnEndReached = reachedEnemySideTurnEnd;
                }
            }
            if (!completed)
                throw new InvalidDataException($"路线动作 {index} 没有完成回放。");
            actions.Add(new ReplayedAction
            {
                Index = index,
                Action = action,
                Before = before,
                After = after,
                Choices = [.. choices],
                EnemySideTurnEndReached = enemySideTurnEndReached,
            });
            current = after;
        }
        bool victory = current.Outcome == Outcome.Victory;
        if (victory != expectedVictory)
        {
            throw new InvalidDataException(
                $"路线回放结局 {current.Outcome} 与搜索结果 victory={expectedVictory} 不符。");
        }
        return new ReplayedRoute(root, [.. actions], victory, rules);
    }

    /// <summary>
    /// Captures one request and the selection the route (or a turn-setup plan) made for it. The
    /// option cards are copied out of <paramref name="partial"/> because <see cref="ChoiceRequest"/>
    /// stores pile positions, not values. Shared by route replay and the turn-setup enumerator.
    /// </summary>
    internal static PlannedChoice CaptureChoice(in Battle partial, ChoiceRequest request, ulong selection)
    {
        Card[] generated = [];
        if (request.OptionsAreGenerated)
        {
            generated = new Card[request.OptionCount];
            for (int index = 0; index < request.OptionCount; index++)
                generated[index] = request.GeneratedOptions[index];
        }
        // Pile options are absolute positions in the partial battle the kernel was replaying. The
        // caller captured the request before the state was discarded, so the cards must be copied
        // here; ChoiceRequest itself carries only positions.
        Card[] pile = new Card[request.OptionCount];
        for (int index = 0; index < request.OptionCount; index++)
            pile[index] = partial.Cards[request.Options[index]];
        return new PlannedChoice
        {
            Request = request,
            Selection = selection,
            Implicit = IsImplicit(request, selection),
            PileOptions = pile,
            GeneratedOptions = generated,
        };
    }

    private static bool IsImplicit(ChoiceRequest request, ulong selection)
    {
        if (request.Kind != ChoiceKind.SelectCards || request.OptionsAreGenerated)
            return false;
        if (request.OptionCount == 0)
        {
            if (selection != 0)
                throw new InvalidDataException("空选择的路线位集不是零。");
            return true;
        }
        if (request.MinCount == request.MaxCount && request.OptionCount <= request.MinCount)
        {
            ulong all = request.OptionCount >= 64 ? ulong.MaxValue : (1UL << request.OptionCount) - 1;
            if (selection != all)
                throw new InvalidDataException("原生会自动选满的选项，路线位集却没有全选。");
            return true;
        }
        return false;
    }
}
