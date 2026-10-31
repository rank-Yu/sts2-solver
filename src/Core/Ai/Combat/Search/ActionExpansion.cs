// Action and choice expansion extracted from BeamSolver.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Ai.Combat.Quality;
using Sts2Solver.Ai.Combat.Scoring;

namespace Sts2Solver.Ai.Combat;

internal sealed partial class BeamSolver
{
    /// <summary>
    /// Finds the lowest-index play action with the same hand-card content and target. The action list
    /// is ordered by hand index, so the first match is the representative. Exact means that every
    /// hand position between the two copies is content-equal: playing either leaves the same hand
    /// sequence. Any other content-equal copy is an approximate sibling in the A/B sense.
    /// </summary>
    private static bool TryFindEarlierEquivalentPlay(in Battle parent,
        scoped ReadOnlySpan<PlayerAction> actions, int actionIndex, out int representative,
        out bool exact)
    {
        representative = -1;
        exact = false;
        ref readonly PlayerAction action = ref actions[actionIndex];
        if (action.Kind != PlayerActionKind.PlayCard || action.HandIndex >= parent.HandCount)
            return false;
        Card card = parent.Cards[action.HandIndex];
        for (int prior = 0; prior < actionIndex; prior++)
        {
            ref readonly PlayerAction candidate = ref actions[prior];
            if (candidate.Kind != PlayerActionKind.PlayCard || candidate.Target != action.Target
                || candidate.HandIndex >= parent.HandCount)
                continue;
            if (!CardEquivalence.ContentEquals(in card, in parent.Cards[candidate.HandIndex]))
                continue;
            representative = prior;
            exact = CardEquivalence.SameHandRun(in parent, action.HandIndex, candidate.HandIndex);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Runs the skipped play action and its representative from the same parent with one deterministic
    /// choice policy, then compares the completed battles. An exact prediction must match; an
    /// approximate one only contributes to the equality count.
    /// </summary>
    private void VerifySkippedPlay(in Battle parent, in PlayerAction representative,
        in PlayerAction skipped, bool exact)
    {
        if (!TryExecuteForVerification(in parent, representative, out Battle expected)
            || !TryExecuteForVerification(in parent, skipped, out Battle actual))
        {
            _playVerifiedUnresolved++;
            return;
        }
        bool equal = Fingerprint.Equal(expected, actual);
        if (exact)
        {
            _playVerifiedExact++;
            if (!equal)
            {
                throw new InvalidOperationException(
                    $"同名出牌完全等价校验失败：representative=hand{representative.HandIndex} "
                    + $"skipped=hand{skipped.HandIndex} target={skipped.Target}。");
            }
            return;
        }
        _playVerifiedApprox++;
        if (equal)
            _playVerifiedApproxEqual++;
    }

    /// <summary>
    /// For every enumerated class vector, checks one distinct physical selection (the last occurrence
    /// of each class) against the vector's representative. A class vector is exact when the selected
    /// content can only leave the same source-pile order; generated options are always exact.
    /// </summary>
    private void VerifyChoiceAlternatives(in Battle parent, in Battle choiceState,
        in PlayerAction action, in ChoiceRequest request, scoped ReadOnlySpan<Card> cards,
        scoped ReadOnlySpan<ulong> selections)
    {
        int checkedAlternatives = 0;
        for (int index = 0; index < selections.Length
            && checkedAlternatives < MaxChoiceAlternativesVerifiedPerRequest; index++)
        {
            if (!ChoiceEnumerator.TryBuildLastOccurrenceSelection(request, cards, selections[index],
                    out ulong alternative))
                continue;
            checkedAlternatives++;
            bool exact = ChoiceEnumerator.IsExactByClass(in choiceState, request, cards, selections[index]);
            bool expectedComplete, actualComplete;
            Battle expected, actual;
            try
            {
                expectedComplete = TryExecuteForVerification(in parent,
                    action.WithChoice(selections[index]), out expected);
                actualComplete = TryExecuteForVerification(in parent,
                    action.WithChoice(alternative), out actual);
            }
            catch (CapacityException)
            {
                _choiceVerifiedUnresolved++;
                continue;
            }
            if (!expectedComplete || !actualComplete)
            {
                _choiceVerifiedUnresolved++;
                continue;
            }
            bool equal = Fingerprint.Equal(expected, actual);
            if (exact)
            {
                _choiceVerifiedExact++;
                if (!equal)
                {
                    System.Text.StringBuilder optionsText = new();
                    for (int option = 0; option < request.OptionCount; option++)
                    {
                        if (option != 0)
                            optionsText.Append(',');
                        optionsText.Append(option).Append(':')
                            .Append(cards[option].Id).Append('+').Append(cards[option].Upgrade)
                            .Append('@')
                            .Append(request.OptionsAreGenerated ? -1 : request.Options[option]);
                    }
                    throw new InvalidOperationException(
                        $"选牌等价类完全等价校验失败：effect={request.Effect} "
                        + $"source={request.Source} options={request.OptionCount} "
                        + $"choices={action.ChoiceCount} representative=0x{selections[index]:x} "
                        + $"alternative=0x{alternative:x} cards=[{optionsText}] "
                        + string.Join("; ", BattleDump.Diff(expected, actual, "representative", "alternative")));
                }
                continue;
            }
            _choiceVerifiedApprox++;
            if (equal)
                _choiceVerifiedApproxEqual++;
        }
    }

    /// <summary>
    /// Executes one action from a parent with the same deterministic continuation for both sides of a
    /// comparison: after every <see cref="StepResult.NeedsChoice"/> it reruns the action with the
    /// first class-vector selection appended. Returns false when the action hits a child capacity
    /// boundary or exceeds the choice depth; those comparisons are reported as unresolved.
    /// </summary>
    private bool TryExecuteForVerification(in Battle parent, in PlayerAction action, out Battle result)
    {
        Span<Card> optionCards = stackalloc Card[Limits.MaxChoiceOptions];
        Span<ulong> selections = stackalloc ulong[MaxChoiceBranches];
        PlayerAction current = action;
        for (int depth = 0; depth <= Limits.MaxChoices; depth++)
        {
            Battle state = parent;
            StepResult step;
            ChoiceRequest request;
            try
            {
                step = Kernel.Step(ref state, current, _rules, out request);
            }
            catch (CapacityException)
            {
                result = default;
                return false;
            }
            if (step == StepResult.Completed)
            {
                result = state;
                return true;
            }
            if (current.ChoiceCount >= Limits.MaxChoices)
                break;
            for (int option = 0; option < request.OptionCount; option++)
            {
                if (request.OptionsAreGenerated)
                {
                    optionCards[option] = request.GeneratedOptions[option];
                    continue;
                }
                int position = request.Options[option];
                if ((uint)position >= (uint)state.CardCount)
                {
                    result = default;
                    return false;
                }
                optionCards[option] = state.Cards[position];
            }
            int count = ChoiceEnumerator.EnumerateByClass(request, optionCards[..request.OptionCount],
                selections);
            if (count == 0)
                break;
            try
            {
                current = current.WithChoice(selections[0]);
            }
            catch (CapacityException)
            {
                result = default;
                return false;
            }
        }
        result = default;
        return false;
    }

    private SearchEquivalenceDiagnostics EquivalenceDiagnostics()
        => new(_playDeduped, _playVerifiedExact, _playVerifiedApprox, _playVerifiedApproxEqual,
            _playVerifiedUnresolved, _choiceByClassRequests, _choiceVerifiedExact,
            _choiceVerifiedApprox, _choiceVerifiedApproxEqual, _choiceVerifiedUnresolved);

    private void ExpandAction(in Battle parent, int parentNode, PlayerAction action, Pool next,
        Pool ended, TerminalSink terminal, in RetentionFacts parentFacts, int parentRank,
        double parentScore)
        => ExpandAction(in parent, parentNode, action, next, ended, terminal, parentFacts,
            parentRank, parentScore, null, default, null, allowFetchedPowerCommit: true);

    private void ExpandAction(in Battle parent, int parentNode, PlayerAction action, Pool next, Pool ended,
        TerminalSink terminal, in RetentionFacts parentFacts, int parentRank, double parentScore,
        ChoiceEffect? primaryEffect, scoped ReadOnlySpan<Card> selectedPowers,
        RoutingChoice? routingChoice, bool allowFetchedPowerCommit)
    {
        ushort usedPotion = ushort.MaxValue;
        if (action.Kind == PlayerActionKind.UsePotion
            && (uint)action.PotionSlot < (uint)parent.PotionSlotCount
            && parent.Potions[action.PotionSlot] != 0)
        {
            usedPotion = (ushort)(parent.Potions[action.PotionSlot] - 1);
        }
        // A capacity overflow is a child boundary, not a simulation error: the one child that needs
        // more cards, powers, enemies, choice slots or action slots than Battle holds is dropped
        // and the rest of the search continues. Every adopted route is still verified step by step
        // against the existing engine. The catch covers the whole construction of this child
        // (reserve, step, score and commit); the per-selection catch below keeps one overflowing
        // choice branch from cancelling its siblings.
        try
        {
            // The child is built in next's first free slot and stepped in place: one copy of the parent per
            // transition. It is committed to next, copied to ended, or dropped as a terminal; the slot is not
            // touched by anything else until then (a choice re-expansion reuses it from the start).
            ref Battle child = ref next.Reserve();
            Battle.CopyPacked(in parent, ref child);
            _transitions++;
            StepResult stepResult;
            ChoiceRequest request;
            bool historyRisk;
            if (action.Kind == PlayerActionKind.EndTurn && CanReuseEndTurnPrefix(parentNode))
            {
                _endTurnPrefixReusedLeaves++;
                stepResult = Kernel.StepEndTurnFromPrefix(ref child, action, _rules, in _endTurnPrefix,
                    out request, out historyRisk);
                if (_verifySearchEquivalence)
                    VerifyPreparedEndTurn(in parent, action, ref child, stepResult, in request, historyRisk);
            }
            else if (action.Kind == PlayerActionKind.EndTurn && IsFirstEndTurnLeaf(parentNode))
            {
                stepResult = Kernel.StepEndTurnSharingPrefix(ref child, action, _rules, out request,
                    out historyRisk, out EndTurnPrefix prefix, out EndTurnPrefixStatus prefixStatus);
                StoreEndTurnPrefix(in prefix, prefixStatus, parentNode);
            }
            else
            {
                stepResult = Kernel.Step(ref child, action, _rules, out request, out historyRisk);
            }
            if (stepResult == StepResult.NeedsChoice)
            {
                // The branch loop below expands every selection through the same pool. A recursive
                // ExpandAction reserves and overwrites next's current slot, so `child` is no longer a
                // stable view of the partial state once the first branch runs. Capture the option card
                // values now: they are the only child state the loop and its routing metadata read.
                Span<Card> optionCards = stackalloc Card[request.OptionCount];
                for (int option = 0; option < request.OptionCount; option++)
                {
                    if (request.OptionsAreGenerated)
                    {
                        optionCards[option] = request.GeneratedOptions[option];
                        continue;
                    }
                    int position = request.Options[option];
                    if ((uint)position >= (uint)child.CardCount)
                        throw new InvalidOperationException(
                            $"选择选项 {option} 的卡牌位置 {position} 超出当前牌数 {child.CardCount}。");
                    optionCards[option] = child.Cards[position];
                }
                // Every executed selection is exact; the cap only bounds how many are explored. With
                // class enumeration each slot is a different content multiset.
                Span<ulong> selections = stackalloc ulong[MaxChoiceBranches];
                int count;
                if (!_dedupeChoicesByClass)
                {
                    count = ChoiceEnumerator.Enumerate(request, selections);
                }
                else if (_choiceFillVariants)
                {
                    bool truncated = ChoiceEnumerator.Count(request, MaxChoiceBranches + 1)
                        > MaxChoiceBranches;
                    count = truncated
                        ? ChoiceEnumerator.EnumerateByClassFilled(request, optionCards, selections)
                        : ChoiceEnumerator.Enumerate(request, selections);
                }
                else
                {
                    count = ChoiceEnumerator.EnumerateByClass(request, optionCards, selections);
                }
                if (_dedupeChoicesByClass)
                {
                    _choiceByClassRequests++;
                    if (_verifySearchEquivalence)
                        VerifyChoiceAlternatives(in parent, in child, action, request, optionCards,
                            selections[..count]);
                }
                bool captureFetchedPowers = allowFetchedPowerCommit && primaryEffect is null
                    && request.Timing == ChoiceTiming.Action
                    && IsPrimaryMoveToHandAction(parent, action)
                    && request.Effect == ChoiceEffect.MoveToHand
                    && !request.OptionsAreGenerated;
                Span<bool> optionIsPower = stackalloc bool[captureFetchedPowers ? request.OptionCount : 0];
                for (int option = 0; option < optionIsPower.Length; option++)
                    optionIsPower[option] =
                        Kernel.ResolveCardType(child, _rules, optionCards[option]) == CardType.Power;
                Span<Card> capturedPowers = stackalloc Card[Limits.MaxChoiceOptions];
                for (int index = 0; index < count; index++)
                {
                    ChoiceEffect? effect = primaryEffect;
                    scoped ReadOnlySpan<Card> chosenPowers = selectedPowers;
                    if (captureFetchedPowers)
                    {
                        effect = request.Effect;
                        int captured = 0;
                        ulong bits = selections[index];
                        for (int option = 0; option < request.OptionCount; option++)
                        {
                            if ((bits & (1UL << option)) == 0 || !optionIsPower[option])
                                continue;
                            if (captured >= capturedPowers.Length)
                                throw new CapacityException("FetchedPowers", capturedPowers.Length);
                            capturedPowers[captured++] = optionCards[option];
                        }
                        chosenPowers = capturedPowers[..captured];
                    }
                    try
                    {
                        RoutingChoice? answered = routingChoice;
                        if (TryBuildRoutingChoice(parent, action, request, optionCards, selections[index],
                                parentNode, parentFacts.Turn, parentRank, parentScore, parentFacts,
                                out RoutingChoice choiceFacts))
                        {
                            answered = choiceFacts;
                        }
                        ExpandAction(in parent, parentNode, action.WithChoice(selections[index]), next, ended,
                            terminal, parentFacts, parentRank, parentScore, effect, chosenPowers, answered,
                            allowFetchedPowerCommit);
                    }
                    catch (CapacityException ex)
                    {
                        RecordCapacityDrop(ex);
                    }
                }
                return;
            }
            int depth = _nodes[parentNode].Depth + 1;
            bool isTerminal = !Kernel.IsInProgress(child);
            int traceStep = _trace?.Match(parentNode, action) ?? 0;
            (double score, double rank, double pureRank, int potionCost, ScoreFeatures features) =
                Score(child, depth, isTerminal, action.Kind == PlayerActionKind.EndTurn);
            // Route traits are accumulated from the K4-checked parent/child feature values. They are
            // node metadata only: the state bytes, fingerprint and key are untouched.
            bool nonPure = !IsPureTransition(parent, child, action, _rules, historyRisk);
            SearchRouteTraits childTraits = ClassifyRouteTraits(parentFacts, features, action,
                DeclinedExtraTurn(parent), nonPure);
            PocketwatchState(child, out int childPocketwatchThreshold, out int childPocketwatchPlayed,
                out int childPocketwatchLastTurn);
            // Power-commitment metadata is route history; it is computed before the node is published
            // so the pool can store the 1-based commitment index with the seat. Terminal children
            // carry none.
            int commitment = isTerminal
                ? 0
                : AttachPowerCommitment(parentNode, parent, child, action, parentFacts, features, depth);
            int node = _nodes.Count;
            ulong lineage = NextLineage(parentNode, parent, action, node,
                out ulong rootActionKey, out ushort firstCardId, out int firstCardTarget);
            RouteNode parentRoute = _nodes[parentNode];
            int routingSignature = routingChoice?.SignatureId ?? 0;
            RoutingMeta routingMeta;
            int routingMetaId;
            if (routingSignature != 0)
            {
                routingMeta = new RoutingMeta(routingSignature, routingChoice!.Value.Turn,
                    routingChoice.Value.ChoiceDepth, routingChoice.Value.ParentRank,
                    routingChoice.Value.ParentScore, routingChoice.Value.ParentEnergy);
                routingMetaId = _routingMetas.Add(routingMeta);
            }
            else
            {
                RoutingMeta parentMeta = _routingMetas[parentRoute.RoutingMetaId];
                if (parentMeta.SignatureId == 0)
                {
                    routingMeta = parentMeta;
                    routingMetaId = parentRoute.RoutingMetaId;
                }
                else
                {
                    // Descendants keep the inherited context fields but the answered signature is no
                    // longer current; one shared entry serves the rest of that route segment.
                    routingMeta = parentMeta with { SignatureId = 0 };
                    routingMetaId = _routingMetas.Add(routingMeta);
                }
            }
            int actionCardId = action.Kind == PlayerActionKind.PlayCard && action.HandIndex < parent.HandCount
                ? parent.Cards[action.HandIndex].Id : 0;
            _nodes.Add(new RouteNode(parentNode, depth, action, rootActionKey, lineage,
                usedPotion, firstCardId, firstCardTarget, commitment, actionCardId,
                TargetCombatId(parent, action), routingMeta.SignatureId, routingMeta.Turn,
                routingMeta.ChoiceDepth, routingMeta.ParentRank, routingMeta.ParentScore,
                routingMeta.ParentEnergy, parentFacts.Turn, routingMetaId), routingMetaId);
            if (traceStep > 0)
                _trace!.NodeCreated(node, traceStep);
            if (isTerminal)
            {
                terminal(child, node, score);
                return;
            }
            if (allowFetchedPowerCommit
                && _options.Act3BossStrategy
                && child.PlayerTurn == parent.PlayerTurn
                && primaryEffect == ChoiceEffect.MoveToHand
                && HasPlayableFetchedPower(child, selectedPowers)
                && CommitmentBudgetAvailable())
            {
                // Keep the ordinary fetched state, and also rank the direct play of the fetched
                // power without pruning the intermediate state first. The helper snapshots the
                // reserved slot before any pool mutation can overwrite it.
                ExpandFetchedPowerSuccessors(ref child, node, next, ended, terminal, childTraits,
                    childPocketwatchThreshold, childPocketwatchPlayed, childPocketwatchLastTurn,
                    parentRank, parentScore, parentFacts.EnemyBlock, parentFacts.EnemyHp,
                    parentFacts.FutureResourceValue, parentFacts.StrategicResourcePotential,
                    selectedPowers, score, rank, potionCost);
                return;
            }
            // The retention-only hand values are computed by the pool only when the state is actually
            // stored (a duplicate keeps its existing values); a terminal state never needs them.
            bool added = action.Kind == PlayerActionKind.EndTurn || child.PlayerTurn != parent.PlayerTurn
                ? AddEndTurnChild(ended, child, node, score, rank, potionCost, features, depth,
                    lineage, commitment, childTraits, childPocketwatchThreshold,
                    childPocketwatchPlayed, childPocketwatchLastTurn, parentFacts.EnemyBlock, parentFacts.EnemyHp,
                    parentFacts.FutureResourceValue, parentFacts.StrategicResourcePotential)
                : next.CommitReserved(node, score, rank, potionCost, features, depth, lineage,
                    commitment, childTraits, childPocketwatchThreshold,
                    childPocketwatchPlayed, childPocketwatchLastTurn, parentFacts.EnemyBlock, parentFacts.EnemyHp,
                    parentFacts.FutureResourceValue, parentFacts.StrategicResourcePotential);
            if (!added)
                _duplicates++;
        }
        catch (CapacityException ex)
        {
            RecordCapacityDrop(ex);
        }
    }

    /// <summary>
    /// True when this is the first end-turn leaf of the current parent. The first leaf executes the
    /// complete step and captures the prefix as a by-product; recursive selection leaves reuse it.
    /// </summary>
    private bool IsFirstEndTurnLeaf(int parentNode)
    {
        if (_endTurnPrefixAttemptGeneration == _expandGeneration
            && _endTurnPrefixAttemptNode == parentNode)
            return false;
        _endTurnPrefixAttemptGeneration = _expandGeneration;
        _endTurnPrefixAttemptNode = parentNode;
        return true;
    }

    private bool CanReuseEndTurnPrefix(int parentNode)
        => _endTurnPrefixValid
            && _endTurnPrefixGeneration == _expandGeneration
            && _endTurnPrefixNode == parentNode;

    private void StoreEndTurnPrefix(in EndTurnPrefix prefix, EndTurnPrefixStatus status, int parentNode)
    {
        if (prefix.IsPrepared)
        {
            _endTurnPrefix = prefix;
            _endTurnPrefixValid = true;
            _endTurnPrefixGeneration = _expandGeneration;
            _endTurnPrefixNode = parentNode;
            _endTurnPrefixShared++;
            return;
        }
        _endTurnPrefixValid = false;
        _endTurnPrefixUnshared++;
        switch (status)
        {
            case EndTurnPrefixStatus.ChoiceBeforePlayerStart:
                _endTurnPrefixChoiceUnshared++;
                break;
            case EndTurnPrefixStatus.ExtraTurn:
                _endTurnPrefixExtraTurnUnshared++;
                break;
            case EndTurnPrefixStatus.NotInProgress:
                _endTurnPrefixNotInProgress++;
                break;
        }
    }

    /// <summary>
    /// Default-off differential check: runs the complete step from the parent and compares every
    /// observable result of the prefix continuation against it. Only enabled by
    /// <see cref="SearchVerificationHooks.VerifySearchEquivalence"/>.
    /// </summary>
    private void VerifyPreparedEndTurn(in Battle parent, in PlayerAction action, ref Battle actual,
        StepResult actualResult, in ChoiceRequest actualRequest, bool actualHistoryRisk)
    {
        Unsafe.SkipInit(out Battle expected);
        Battle.CopyPacked(in parent, ref expected);
        StepResult expectedResult = Kernel.Step(ref expected, action, _rules, out ChoiceRequest expectedRequest,
            out bool expectedHistoryRisk);
        if (expectedResult == actualResult && expectedHistoryRisk == actualHistoryRisk
            && BattleBytesEqual(expected, actual) && RequestEquals(expectedRequest, actualRequest))
        {
            _endTurnPrefixVerified++;
            return;
        }
        string? requestDifference = RequestDifference(expectedRequest, actualRequest);
        List<string> battleDifference = BattleBytesEqual(expected, actual)
            ? []
            : BattleDump.Diff(expected, actual, "complete", "prefix");
        throw new InvalidOperationException(
            $"EndTurn 前缀等价校验失败：kind={action.Kind} hand={action.HandIndex} "
            + $"target={action.Target} choices={action.ChoiceCount} "
            + $"result complete={expectedResult} prefix={actualResult}；"
            + $"historyRisk complete={expectedHistoryRisk} prefix={actualHistoryRisk}；"
            + $"request={(requestDifference ?? "same")}；battle="
            + (battleDifference.Count == 0 ? "same" : string.Join(" | ", battleDifference)));
    }

    private static bool BattleBytesEqual(in Battle expected, in Battle actual)
        => Fingerprint.Equal(in expected, in actual);

    private static bool RequestEquals(in ChoiceRequest expected, in ChoiceRequest actual)
        => RequestDifference(expected, actual) is null;

    private static string? RequestDifference(in ChoiceRequest expected, in ChoiceRequest actual)
    {
        if (expected.Kind != actual.Kind)
            return $"Kind {expected.Kind}/{actual.Kind}";
        if (expected.Effect != actual.Effect)
            return $"Effect {expected.Effect}/{actual.Effect}";
        if (expected.Timing != actual.Timing)
            return $"Timing {expected.Timing}/{actual.Timing}";
        if (expected.Source != actual.Source)
            return $"Source {expected.Source}/{actual.Source}";
        if (expected.SourceIsNone != actual.SourceIsNone)
            return $"SourceIsNone {expected.SourceIsNone}/{actual.SourceIsNone}";
        if (expected.OptionsAreGenerated != actual.OptionsAreGenerated)
            return $"OptionsAreGenerated {expected.OptionsAreGenerated}/{actual.OptionsAreGenerated}";
        if (expected.OptionCount != actual.OptionCount || expected.MinCount != actual.MinCount
            || expected.MaxCount != actual.MaxCount)
            return $"counts {expected.OptionCount}/{actual.OptionCount} {expected.MinCount}/{actual.MinCount} "
                + $"{expected.MaxCount}/{actual.MaxCount}";
        for (int index = 0; index < expected.OptionCount; index++)
        {
            if (expected.OptionsAreGenerated)
            {
                if (!CardBytesEqual(expected.GeneratedOptions[index], actual.GeneratedOptions[index]))
                    return $"GeneratedOptions[{index}]";
            }
            else if (expected.Options[index] != actual.Options[index])
            {
                return $"Options[{index}] {expected.Options[index]}/{actual.Options[index]}";
            }
        }
        return null;
    }

    private static bool CardBytesEqual(in Card left, in Card right)
    {
        ReadOnlySpan<byte> leftBytes = MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in left), 1));
        ReadOnlySpan<byte> rightBytes = MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in right), 1));
        return leftBytes.SequenceEqual(rightBytes);
    }

    /// <summary>
    /// Builds the routing-choice signature of one answered selection request. The existing
    /// <c>RoutingChoiceSignature</c> also carries source/context ids and three enemy/pile distribution
    /// fingerprints; Sts2Solver has no string source ids or distribution keys on the route node, so those
    /// dimensions are not part of the key (choices from different sources of the same turn with the same
    /// effect and option merge). The supported effect set is the non-mutation routing set; persistent
    /// ordered-mutation effects are not part of this key (Sts2Solver does not generate that lineage).
    /// </summary>
    private bool TryBuildRoutingChoice(in Battle parent, in PlayerAction action,
        in ChoiceRequest request, scoped ReadOnlySpan<Card> optionCards, ulong selection,
        int parentNode, int parentTurn, int parentRank, double parentScore, in RetentionFacts parentFacts,
        out RoutingChoice choice)
    {
        choice = default;
        if (request.Effect is not (ChoiceEffect.MoveToHand or ChoiceEffect.MoveToDrawTop
            or ChoiceEffect.Discard or ChoiceEffect.GenerateToHand))
        {
            return false;
        }
        int count = System.Numerics.BitOperations.PopCount(selection);
        if (count <= 0)
            return false;
        int cardId = 0, upgrade = 0, stateKey = 0, occurrence = 0, cardCount = 1;
        if (count > 1)
        {
            cardCount = count;
            occurrence = count;
        }
        else
        {
            int option = System.Numerics.BitOperations.TrailingZeroCount(selection);
            Card card = optionCards[option];
            cardId = card.Id;
            upgrade = card.Upgrade;
            stateKey = CardStateKey(card);
            occurrence = option;
        }
        // The reference engine's PlanCardChoice.SourceId is the requesting card's entry (the potion's entry
        // for potion choices, the relic's for turn-start relic choices). Sts2Solver has no string on the
        // request, so the currently played card or potion identifies the source; a card/potion id is
        // prefixed with its kind so the two id spaces cannot merge.
        int sourceId = action.Kind switch
        {
            PlayerActionKind.PlayCard when action.HandIndex < parent.HandCount
                => (1 << 16) | parent.Cards[action.HandIndex].Id,
            PlayerActionKind.UsePotion when action.PotionSlot < parent.PotionSlotCount
                => (2 << 16) | parent.Potions[action.PotionSlot],
            _ => 0,
        };
        int turn = action.Kind == PlayerActionKind.EndTurn
            && request.Timing == ChoiceTiming.PlayerTurnStart
                ? parentTurn + 1
                : parentTurn;
        RoutingSignature signature = new(turn, sourceId, request.Effect,
            request.SourceIsNone ? -1 : (int)request.Source, cardId, upgrade, stateKey, occurrence,
            cardCount, ContextId: 0, parentFacts.EnemyCombatDistributionFirst,
            parentFacts.EnemyCombatDistributionSecond, parentFacts.EnemyControlDistributionFirst,
            parentFacts.EnemyControlDistributionSecond, parentFacts.UnorderedPileKeyFirst,
            parentFacts.UnorderedPileKeySecond);
        if (!_routingSignatureIds.TryGetValue(signature, out int id))
        {
            _routingSignatures.Add(signature);
            id = _routingSignatures.Count;
            _routingSignatureIds.Add(signature, id);
            // The chunk itself is charged by ChunkedList; this covers the dictionary entry and its
            // bucket headroom, the only signature allocation outside the chunk store.
            _storage.Memory.Charge(RoutingSignatureDictionaryBytes, "routing-signature");
        }
        choice = new RoutingChoice(id, turn, _nodes[parentNode].Depth + 1, parentRank, parentScore,
            parentFacts.Energy);
        return true;
    }

    /// <summary>A 32-bit state key for the selected option card: FNV-1a over the card value. It stands
    /// in for the existing <c>PlanCardToken.StateKey</c>, which distinguishes equal-id card variants.</summary>
    private static int CardStateKey(in Card card)
    {
        ulong hash = 1469598103934665603UL;
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in card), 1));
        for (int index = 0; index < bytes.Length; index++)
            hash = (hash ^ bytes[index]) * 1099511628211UL;
        return (int)(hash ^ (hash >> 32));
    }

    /// <summary>
    /// The existing <c>BeamRetentionPolicy.BuildRootActionLineageSignature</c> over Sts2Solver actions.
    /// The root action's kind/card/potion/target is frozen on the first step; when the root has an
    /// unused card replay allocator the route's first played card is part of the signature too. The
    /// signature is a stable value, so equal first actions from different turn-setup roots group
    /// together exactly as the existing search groups them.
    /// </summary>
    private ulong NextLineage(int parentNode, in Battle parent, in PlayerAction action, int node,
        out ulong rootActionKey, out ushort firstCardId, out int firstCardTarget)
    {
        RouteNode entry = _nodes[parentNode];
        rootActionKey = entry.RootActionKey;
        firstCardId = entry.FirstCardId;
        firstCardTarget = entry.FirstCardTarget;
        if (entry.Parent >= 0)
        {
            if (_preserveReplayAllocatorOpening && firstCardId == ushort.MaxValue
                && action.Kind == PlayerActionKind.PlayCard && action.HandIndex < parent.HandCount)
            {
                firstCardId = parent.Cards[action.HandIndex].Id;
                firstCardTarget = TargetCombatId(parent, action);
                return CombineLineage(rootActionKey, firstCardId, firstCardTarget);
            }
            return entry.Lineage;
        }
        rootActionKey = RootActionSignature(parent, action);
        firstCardId = ushort.MaxValue;
        firstCardTarget = int.MinValue;
        if (_preserveReplayAllocatorOpening && action.Kind == PlayerActionKind.PlayCard
            && action.HandIndex < parent.HandCount)
        {
            firstCardId = parent.Cards[action.HandIndex].Id;
            firstCardTarget = TargetCombatId(parent, action);
        }
        // With one root every first action keeps its own lineage. Signature grouping is only applied
        // when multiple turn-setup roots must join the same opening. A previous 120-root measurement
        // showed signature grouping regresses the aggregate, so it is not kept.
        return _singleRoot ? (ulong)node : CombineLineage(rootActionKey, firstCardId, firstCardTarget);
    }

    private static ulong RootActionSignature(in Battle parent, in PlayerAction action)
    {
        ulong kind = action.Kind switch
        {
            PlayerActionKind.PlayCard => 1UL,
            PlayerActionKind.UsePotion => 2UL,
            _ => 3UL,
        };
        ulong cardId = 0;
        ulong potionId = 0;
        if (action.Kind == PlayerActionKind.PlayCard && action.HandIndex < parent.HandCount)
            cardId = parent.Cards[action.HandIndex].Id;
        if (action.Kind == PlayerActionKind.UsePotion
            && (uint)action.PotionSlot < (uint)parent.PotionSlotCount
            && parent.Potions[action.PotionSlot] != 0)
        {
            potionId = (ushort)(parent.Potions[action.PotionSlot] - 1);
        }
        int target = TargetCombatId(parent, action);
        ulong hash = 1469598103934665603UL;
        hash = MixLineage(hash, kind);
        hash = MixLineage(hash, cardId);
        hash = MixLineage(hash, potionId);
        hash = MixLineage(hash, target == int.MinValue ? 0UL : (ulong)(uint)target + 1UL);
        return hash;
    }

    private static ulong CombineLineage(ulong rootActionKey, ushort firstCardId, int firstCardTarget)
    {
        ulong hash = MixLineage(rootActionKey, firstCardId == ushort.MaxValue ? 0UL : (ulong)firstCardId + 1UL);
        return MixLineage(hash, firstCardTarget == int.MinValue ? 0UL : (ulong)(uint)firstCardTarget + 1UL);
    }

    private static ulong MixLineage(ulong hash, ulong value) => (hash ^ value) * 1099511628211UL;

    /// <summary>The existing <c>PlanAction.TargetCombatId</c> for a Sts2Solver target owner.</summary>
    private static int TargetCombatId(in Battle parent, in PlayerAction action)
    {
        if (action.Target == Limits.NoOwner)
            return int.MinValue;
        int enemy = action.Target - Limits.FirstEnemyOwner;
        if ((uint)enemy >= (uint)parent.EnemyCount)
            return int.MinValue;
        return (int)parent.Enemies[enemy].CombatId;
    }

    /// <summary>
    /// The cumulative end-turn layer is the only pool that can reach its bound (the per-action-layer
    /// pool is cleared each layer and receives at most <c>width * MaxLegalActions</c> children). When
    /// a unique end-turn child no longer fits, compact the pool with the same retention rule the
    /// turn boundary uses, then add the child. Duplicates never reach the bound and stay free.
    /// </summary>
    private bool AddEndTurnChild(Pool ended, in Battle child, int node, double score, double rank,
        int potionCost, in ScoreFeatures features, int depth, ulong lineage,
        int commitment, SearchRouteTraits traits, int pocketwatchThreshold,
        int pocketwatchPlayed, int pocketwatchLastTurn, int parentEnemyBlock, int parentEnemyHp,
        int parentFutureResourceValue, int parentStrategicResourcePotential)
    {
        try
        {
            return ended.AddScored(child, node, score, rank, potionCost, features, depth,
                lineage, commitment, traits, pocketwatchThreshold, pocketwatchPlayed,
                pocketwatchLastTurn, parentEnemyBlock, parentEnemyHp, parentFutureResourceValue,
                parentStrategicResourcePotential);
        }
        catch (CapacityException) when (ended.IsFull)
        {
            _tracePhase = "ended-compact";
            ended.TracePhase = _tracePhase;
            int dropped = ended.Compact(_endedCompactionTarget);
            _endedCompactions++;
            _endedCompressedStates += dropped;
            return ended.AddScored(child, node, score, rank, potionCost, features, depth,
                lineage, commitment, traits, pocketwatchThreshold, pocketwatchPlayed,
                pocketwatchLastTurn, parentEnemyBlock, parentEnemyHp, parentFutureResourceValue,
                parentStrategicResourcePotential);
        }
    }

    private void RecordCapacityDrop(CapacityException ex)
    {
        _capacityPruned++;
        CollectionsMarshal.GetValueRefOrAddDefault(_capacityDropped, ex.Region, out _)++;
    }

    private bool CommitmentBudgetAvailable()
        => NodeBudgetAvailable
            && !_memoryBudgetExceeded
            && (_solveWatch is not { } watch || watch.Elapsed < _options.SoftTimeBudget);

    /// <summary>
    /// Preserve the normal fetched state, then add only legal plays of the fetched powers as
    /// direct two-action continuations. This extends the upstream first-turn commitment without
    /// removing ordinary choices such as drinking a potion before playing the power.
    /// </summary>
    private void ExpandFetchedPowerSuccessors(ref Battle commitment, int commitmentNode,
        Pool next, Pool ended, TerminalSink terminal, SearchRouteTraits traits,
        int pocketwatchThreshold, int pocketwatchPlayed, int pocketwatchLastTurn,
        int parentRank, double parentScore, int parentEnemyBlock, int parentEnemyHp,
        int parentFutureResourceValue, int parentStrategicResourcePotential,
        scoped ReadOnlySpan<Card> selectedPowers, double score, double rank, int potionCost)
    {
        // CommitReserved and recursive ExpandAction both reuse pool storage. Keep an independent
        // snapshot even when deduplication rejects the ordinary fetched state.
        Unsafe.SkipInit(out Battle state);
        Battle.CopyPacked(in commitment, ref state);
        // Both the ordinary state and its direct successors use the same retention context.
        int commitmentDepth = Depth(commitmentNode);
        ScoreFeatures commitmentFeatures =
            _scorer.Features(state, commitmentDepth, includeRetentionOnly: true);
        (int commitmentReachable, int commitmentZeroCost, _) = _scorer.ReachableHandPotential(state);
        RetentionFacts commitmentFacts = RetentionFacts.From(commitmentFeatures, commitmentDepth,
            commitmentReachable, commitmentZeroCost, traits, pocketwatchThreshold, pocketwatchPlayed,
            _scorer.InitialRetainedAttackValue, parentEnemyBlock, parentEnemyHp,
            parentFutureResourceValue, parentStrategicResourcePotential);
        RouteNode route = _nodes[commitmentNode];
        if (!next.CommitReserved(commitmentNode, score, rank, potionCost,
                commitmentFeatures, commitmentDepth, route.Lineage,
                route.Commitment, traits, pocketwatchThreshold, pocketwatchPlayed, pocketwatchLastTurn,
                parentEnemyBlock, parentEnemyHp, parentFutureResourceValue, parentStrategicResourcePotential))
            _duplicates++;
        if (!CommitmentBudgetAvailable())
            return;
        CountExpanded();
        _expandGeneration++;
        int count = Kernel.LegalActions(state, _rules, _commitmentActions);
        for (int index = 0; index < count; index++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            PlayerAction action = _commitmentActions[index];
            if (action.Kind != PlayerActionKind.PlayCard
                || !IsSelectedFetchedPower(state, action.HandIndex, selectedPowers))
                continue;
            // ExpandAction handles a capacity overflow for this successor as its own child; a
            // sibling's overflow never cancels the rest of the commitment batch.
            ExpandAction(in state, commitmentNode, action, next, ended, terminal, commitmentFacts,
                parentRank, parentScore, null, default, null, allowFetchedPowerCommit: false);
            if (!CommitmentBudgetAvailable())
                break;
        }
    }

    /// <summary>
    /// <c>the existing search's HasPlayableFetchedPower</c>: the choice moved a card to hand and a matching
    /// Power from that selection is now playable in hand.
    /// </summary>
    private bool HasPlayableFetchedPower(in Battle battle, scoped ReadOnlySpan<Card> selectedPowers)
    {
        if (selectedPowers.Length == 0)
            return false;
        for (int index = 0; index < battle.HandCount; index++)
            if (IsSelectedFetchedPower(battle, index, selectedPowers)
                && Kernel.CanPlayCard(battle, _rules, index))
                return true;
        return false;
    }

    private bool IsSelectedFetchedPower(in Battle battle, int handIndex,
        scoped ReadOnlySpan<Card> selectedPowers)
    {
        if ((uint)handIndex >= (uint)battle.HandCount)
            return false;
        ref readonly Card card = ref battle.Cards[handIndex];
        if (Kernel.ResolveCardType(battle, _rules, card) != CardType.Power)
            return false;
        for (int selected = 0; selected < selectedPowers.Length; selected++)
            if (SameCardIdentity(card, selectedPowers[selected]))
                return true;
        return false;
    }

    /// <summary>
    /// The played card's primary <c>CardChoiceSupport</c> spec is <c>MoveToHand</c>, or the potion is
    /// Droplet of Precognition (its <c>PotionChoiceSupport</c> spec is also <c>MoveToHand</c>).
    /// </summary>
    private bool IsPrimaryMoveToHandAction(in Battle parent, in PlayerAction action)
    {
        if (action.Kind == PlayerActionKind.PlayCard)
        {
            if (action.HandIndex >= parent.HandCount)
                return false;
            ref readonly Card card = ref parent.Cards[action.HandIndex];
            return _scorer.IsPrimaryMoveToHandCard(card.Id, card.Upgrade);
        }
        if (action.Kind != PlayerActionKind.UsePotion
            || _scorer.DropletOfPrecognitionPotionId == ushort.MaxValue
            || action.PotionSlot >= parent.PotionSlotCount)
        {
            return false;
        }
        return parent.Potions[action.PotionSlot]
            == (ushort)(_scorer.DropletOfPrecognitionPotionId + 1);
    }

    private static bool SameCardIdentity(in Card left, in Card right)
        => left.Id == right.Id
            && left.Upgrade == right.Upgrade
            && left.EnchantmentId == right.EnchantmentId
            && left.EnchantmentAmount == right.EnchantmentAmount
            && left.AfflictionId == right.AfflictionId
            && left.AfflictionAmount == right.AfflictionAmount;

}
