// Seeded random legal-action policy for the in-process combat boundary comparison. The policy only
// proposes action shapes; the kernel produces the authoritative child state and the frozen choice
// plan that the native executor replays.
using Sts2Solver.Ai.Combat;
using Sts2Solver.Simulation.Combat;

namespace Differential;

internal sealed class CombatActionCoverage
{
    private readonly Dictionary<string, int> _actions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _effects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _cards = new(StringComparer.Ordinal);

    internal void CountAction(PlayerActionKind kind)
        => _actions[kind.ToString()] = _actions.GetValueOrDefault(kind.ToString()) + 1;

    internal void CountCard(string id)
        => _cards[id] = _cards.GetValueOrDefault(id) + 1;

    internal IEnumerable<(string Key, int Count)> Cards
        => _cards.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => (pair.Key, pair.Value));

    internal void CountChoice(in ChoiceRequest request)
    {
        if (request.Kind == ChoiceKind.None)
            return;
        string key = $"{request.Kind}/{request.Effect}";
        _effects[key] = _effects.GetValueOrDefault(key) + 1;
    }

    internal IEnumerable<(string Key, int Count)> Actions
        => _actions.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => (pair.Key, pair.Value));

    internal IEnumerable<(string Key, int Count)> Effects
        => _effects.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => (pair.Key, pair.Value));
}

internal static class RandomCombatPolicy
{
    /// <summary>
    /// Builds one legal action with a valid random selection for every choice it requests. Returns
    /// null when the battle is over.
    /// </summary>
    internal static ReplayedAction? Choose(in Battle root, Rules rules, Random random,
        CombatActionCoverage coverage)
    {
        if (root.Outcome != Outcome.None)
            return null;
        PlayerAction[] legal = new PlayerAction[Kernel.MaxLegalActions];
        int count = Kernel.LegalActions(root, rules, legal);
        if (count == 0)
            return null;
        List<int> plays = [];
        List<int> potions = [];
        int endTurn = -1;
        for (int index = 0; index < count; index++)
        {
            switch (legal[index].Kind)
            {
                case PlayerActionKind.PlayCard:
                    plays.Add(index);
                    break;
                case PlayerActionKind.UsePotion:
                    potions.Add(index);
                    break;
                case PlayerActionKind.EndTurn:
                    endTurn = index;
                    break;
                default:
                    throw new InvalidDataException($"未知合法动作 {legal[index].Kind}。");
            }
        }
        int selected;
        double roll = random.NextDouble();
        if (plays.Count != 0 && (potions.Count == 0 || roll < 0.72))
            selected = plays[random.Next(plays.Count)];
        else if (potions.Count != 0 && (roll < 0.88 || plays.Count == 0))
            selected = potions[random.Next(potions.Count)];
        else
            selected = endTurn >= 0 ? endTurn : 0;
        PlayerAction action = CompleteChoices(root, rules, legal[selected], random, coverage);
        Battle after = Replay(root, rules, action);
        return ReplayedRoute.Build(root, rules, [action], after.Outcome == Outcome.Victory).Actions[0];
    }

    /// <summary>Ends the turn when that action is legal; used to let a theft enemy act.</summary>
    internal static ReplayedAction? ChooseEndTurn(in Battle root, Rules rules,
        CombatActionCoverage coverage)
    {
        if (root.Outcome != Outcome.None)
            return null;
        PlayerAction[] legal = new PlayerAction[Kernel.MaxLegalActions];
        int count = Kernel.LegalActions(root, rules, legal);
        for (int index = 0; index < count; index++)
        {
            if (legal[index].Kind != PlayerActionKind.EndTurn)
                continue;
            PlayerAction action = CompleteChoices(root, rules, legal[index],
                new Random(0), coverage);
            Battle after = Replay(root, rules, action);
            return ReplayedRoute.Build(root, rules, [action],
                after.Outcome == Outcome.Victory).Actions[0];
        }
        return null;
    }

    private static PlayerAction CompleteChoices(in Battle root, Rules rules, PlayerAction action,
        Random random, CombatActionCoverage coverage)
    {
        PlayerAction current = action;
        for (int depth = 0; depth <= Limits.MaxChoices; depth++)
        {
            Battle state = root;
            StepResult step = Kernel.Step(ref state, current, rules, out ChoiceRequest request);
            if (step == StepResult.Completed)
                return current;
            if (current.ChoiceCount >= Limits.MaxChoices)
                throw new InvalidDataException("动作的选择数超过内核上限。");
            coverage.CountChoice(in request);
            current = current.WithChoice(RandomSelection(in request, random));
        }
        throw new InvalidDataException("动作没有在最大选择深度内完成。");
    }

    private static Battle Replay(in Battle root, Rules rules, PlayerAction action)
    {
        Battle state = root;
        StepResult step = Kernel.Step(ref state, action, rules, out _);
        if (step != StepResult.Completed)
            throw new InvalidDataException("带完整选择集的动作为何仍未完成。");
        return state;
    }

    private static ulong RandomSelection(in ChoiceRequest request, Random random)
    {
        int options = request.OptionCount;
        int min = Math.Min((int)request.MinCount, options);
        int max = Math.Min((int)request.MaxCount, options);
        if (min > max)
            throw new InvalidDataException($"选择边界 {min}..{max} 无效（options={options}）。");
        if (options == 0)
            return 0;
        int take = random.Next(min, max + 1);
        Span<int> pool = stackalloc int[options];
        for (int index = 0; index < options; index++)
            pool[index] = index;
        ulong selection = 0;
        for (int index = 0; index < take; index++)
        {
            int swap = random.Next(index, options);
            (pool[index], pool[swap]) = (pool[swap], pool[index]);
            selection |= 1UL << pool[index];
        }
        return selection;
    }
}
