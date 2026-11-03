// Ported from CombatSolver src/Search/CombatBeamSolver.Expansion.Replay.cs at commit 5c773caa
// (MIT, Copyright (c) 2026 Torch1230 and contributors): BuildTurnSetupRoots / ReplayTurnSetup /
// ResolveTurnSetupChoices. The state definition, choice enumeration and effect application are
// Sts2Solver's kernel; only the replay-from-Start-prefix recursion and the budget shape follow the
// original.
using System.Runtime.CompilerServices;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat;

internal enum TurnSetupEnumerationStatus
{
    /// <summary>The setup required no choice; <see cref="TurnSetupEnumeration.Branches"/> has the one Play root.</summary>
    NoChoice,

    /// <summary>At least one choice was enumerated and at least one Play root was produced.</summary>
    Completed,

    /// <summary>Every enumerated outcome ended the combat during setup; there is no Play root.</summary>
    Terminal,

    /// <summary>The setup cannot be represented: an extra turn, or more choices than a PlayerAction holds.</summary>
    Unsupported,
}

/// <summary>
/// One completed setup outcome: the Play root (or the terminal battle), the selections in request
/// order (including implicit ones) and whether the combat ended during setup.
/// </summary>
internal sealed record TurnSetupBranch(Battle Root, PlannedChoice[] Choices, bool Terminal);

/// <summary>Result of <see cref="TurnSetupRootEnumerator.Enumerate"/>.</summary>
internal sealed class TurnSetupEnumeration
{
    internal required TurnSetupEnumerationStatus Status { get; init; }
    internal required TurnSetupBranch[] Branches { get; init; }

    /// <summary>
    /// Selection branches dropped once the deduplicated root budget was exhausted. The count is the
    /// pending immediate choice branches at every unfinished level (not their descendants), matching
    /// the original engine's <c>RecordChoiceBranchesDroppedByBudget</c> granularity.
    /// </summary>
    internal required int DroppedByBudget { get; init; }

    internal string? Reason { get; init; }

    internal bool HasPlayRoots
    {
        get
        {
            foreach (TurnSetupBranch branch in Branches)
            {
                if (!branch.Terminal)
                    return true;
            }
            return false;
        }
    }
}

/// <summary>
/// Enumerates the Play roots reachable from one native <c>SetupPlayerTurn</c> entry state.
/// <para>
/// This is the Sts2Solver equivalent of <c>CombatBeamSolver.BuildTurnSetupRoots</c>: replay the
/// turn-start preamble from the Start root, branch on every physical selection the kernel asks for,
/// and collect each branch that reaches <c>Phase = Play</c>. Deduplicated roots keep the first
/// plan in <see cref="ChoiceEnumerator.Enumerate"/> order. The budget is the search layer's
/// per-choice bound (<see cref="BeamSolver.MaxChoiceBranches"/>): the first 32 unique roots are kept
/// and the remaining selection branches are counted in
/// <see cref="TurnSetupEnumeration.DroppedByBudget"/>.
/// </para>
/// <para>
/// Multi-stage plans are supported (choices may occur in <c>BeforeHandDraw</c>,
/// <c>AfterPlayerTurnStart</c> and the auto-pre-play phase). A plan longer than
/// <see cref="Limits.MaxChoices"/> cannot fit in a <see cref="PlayerAction"/> and is reported as
/// <see cref="TurnSetupEnumerationStatus.Unsupported"/>, not truncated. Extra turns are likewise
/// unsupported in this batch.
/// </para>
/// </summary>
internal static class TurnSetupRootEnumerator
{
    internal static TurnSetupEnumeration Enumerate(in Battle start, Rules rules,
        bool takingExtraTurn = false, int maxRoots = BeamSolver.MaxChoiceBranches)
    {
        if (maxRoots <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRoots), maxRoots, "根预算必须为正。");
        ArgumentNullException.ThrowIfNull(rules);
        if (!Kernel.IsInProgress(start))
            throw new InvalidOperationException("回合准备枚举的 Start 根已经终局。");
        if (start.Phase != PlayerPhase.Start)
            throw new InvalidOperationException(
                $"回合准备枚举要求 Phase=Start，实际 {start.Phase}。");
        Visitor visitor = new(in start, rules, takingExtraTurn, maxRoots);
        visitor.Run();
        return visitor.Result;
    }

    private sealed class Visitor
    {
        private const string ExtraTurnReason = "回合准备前奏不支持额外回合。";
        private const string TooManyChoicesReason =
            "回合准备连续选择超过 PlayerAction 的 8 个选择位。";

        private readonly Battle _start;
        private readonly Rules _rules;
        private readonly bool _takingExtraTurn;
        private readonly int _maxRoots;
        private readonly List<TurnSetupBranch> _branches = new();
        private readonly List<PlannedChoice> _plan = new();
        private bool _sawChoice;
        private bool _stopped;
        private int _dropped;
        private string? _unsupported;

        internal Visitor(in Battle start, Rules rules, bool takingExtraTurn, int maxRoots)
        {
            _start = start;
            _rules = rules;
            _takingExtraTurn = takingExtraTurn;
            _maxRoots = maxRoots;
        }

        internal TurnSetupEnumeration Result
        {
            get
            {
                TurnSetupBranch[] branches = [.. _branches];
                if (_unsupported != null)
                {
                    return new TurnSetupEnumeration
                    {
                        Status = TurnSetupEnumerationStatus.Unsupported,
                        Branches = branches,
                        DroppedByBudget = _dropped,
                        Reason = _unsupported,
                    };
                }
                if (branches.Length == 0)
                    throw new InvalidDataException("回合准备枚举没有产生任何根。");
                bool hasPlayRoot = false;
                foreach (TurnSetupBranch branch in branches)
                {
                    if (!branch.Terminal)
                    {
                        hasPlayRoot = true;
                        break;
                    }
                }
                TurnSetupEnumerationStatus status = !hasPlayRoot
                    ? TurnSetupEnumerationStatus.Terminal
                    : _sawChoice
                        ? TurnSetupEnumerationStatus.Completed
                        : TurnSetupEnumerationStatus.NoChoice;
                return new TurnSetupEnumeration
                {
                    Status = status,
                    Branches = branches,
                    DroppedByBudget = _dropped,
                };
            }
        }

        internal void Run()
        {
            if (_takingExtraTurn)
            {
                _unsupported = ExtraTurnReason;
                return;
            }
            Visit([]);
        }

        private void Visit(ulong[] selections)
        {
            if (_stopped || _unsupported != null)
                return;
            Unsafe.SkipInit(out Battle partial);
            Battle.CopyPacked(in _start, ref partial);
            StepResult step = Kernel.PreparePlayerPlayPhase(ref partial, _rules, selections,
                out ChoiceRequest request);
            if (step == StepResult.Completed)
            {
                bool terminal = partial.Outcome != Outcome.None
                    || partial.Phase != PlayerPhase.Play;
                if (!terminal && partial.Phase != PlayerPhase.Play)
                {
                    throw new InvalidDataException(
                        $"回合准备完成后的根相位是 {partial.Phase}，既不是 Play 也不是终局。");
                }
                Add(partial, terminal);
                return;
            }

            _sawChoice = true;
            if (_plan.Count >= Limits.MaxChoices)
            {
                _unsupported = TooManyChoicesReason;
                return;
            }
            long total = ChoiceEnumerator.Count(request, int.MaxValue);
            if (total <= 0)
                throw new InvalidDataException("回合准备的选择请求没有任何合法选择。");
            Span<ulong> buffer = stackalloc ulong[BeamSolver.MaxChoiceBranches];
            int written = ChoiceEnumerator.Enumerate(request, buffer);
            if (written == 0)
                throw new InvalidDataException("回合准备的选择请求没有枚举出选择。");
            // Selections beyond the shared per-choice bound are dropped before replay.
            AddDropped(total - written);
            for (int index = 0; index < written; index++)
            {
                PlannedChoice choice = ReplayedRoute.CaptureChoice(in partial, request,
                    buffer[index]);
                _plan.Add(choice);
                ulong[] next = new ulong[selections.Length + 1];
                selections.CopyTo(next, 0);
                next[^1] = buffer[index];
                Visit(next);
                _plan.RemoveAt(_plan.Count - 1);
                if (_stopped || _unsupported != null)
                {
                    // Everything after this sibling would have been explored next.
                    AddDropped(written - index - 1);
                    return;
                }
            }
        }

        private void Add(in Battle root, bool terminal)
        {
            foreach (TurnSetupBranch existing in _branches)
            {
                if (Fingerprint.Equal(existing.Root, root))
                    return;
            }
            if (_branches.Count >= _maxRoots)
            {
                _stopped = true;
                return;
            }
            _branches.Add(new TurnSetupBranch(root, [.. _plan], terminal));
            // The budget is on deduplicated roots: once the last slot is used, stop replaying the
            // remaining tree and count its pending choice branches.
            if (_branches.Count >= _maxRoots)
                _stopped = true;
        }

        private void AddDropped(long count)
        {
            if (count <= 0)
                return;
            long total = (long)_dropped + count;
            _dropped = total >= int.MaxValue ? int.MaxValue : (int)total;
        }
    }
}
