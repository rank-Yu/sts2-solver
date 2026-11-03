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
        in PlayerAction skipped, bool exact, ExpansionWorkspace workspace)
    {
        if (!TryExecuteForVerification(in parent, representative, out Battle expected)
            || !TryExecuteForVerification(in parent, skipped, out Battle actual))
        {
            workspace.PlayVerifiedUnresolved++;
            return;
        }
        bool equal = Fingerprint.Equal(expected, actual);
        if (exact)
        {
            workspace.PlayVerifiedExact++;
            if (!equal)
            {
                throw new InvalidOperationException(
                    $"同名出牌完全等价校验失败：representative=hand{representative.HandIndex} "
                    + $"skipped=hand{skipped.HandIndex} target={skipped.Target}。");
            }
            return;
        }
        workspace.PlayVerifiedApprox++;
        if (equal)
            workspace.PlayVerifiedApproxEqual++;
    }

    /// <summary>
    /// For every enumerated class vector, checks one distinct physical selection (the last occurrence
    /// of each class) against the vector's representative. A class vector is exact when the selected
    /// content can only leave the same source-pile order; generated options are always exact.
    /// </summary>
    private void VerifyChoiceAlternatives(in Battle parent, in Battle choiceState,
        in PlayerAction action, in ChoiceRequest request, scoped ReadOnlySpan<Card> cards,
        scoped ReadOnlySpan<ulong> selections, ExpansionWorkspace workspace)
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
                workspace.ChoiceVerifiedUnresolved++;
                continue;
            }
            if (!expectedComplete || !actualComplete)
            {
                workspace.ChoiceVerifiedUnresolved++;
                continue;
            }
            bool equal = Fingerprint.Equal(expected, actual);
            if (exact)
            {
                workspace.ChoiceVerifiedExact++;
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
            workspace.ChoiceVerifiedApprox++;
            if (equal)
                workspace.ChoiceVerifiedApproxEqual++;
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
        Unsafe.SkipInit(out result);
        Span<Card> optionCards = stackalloc Card[Limits.MaxChoiceOptions];
        Span<ulong> selections = stackalloc ulong[MaxChoiceBranches];
        PlayerAction current = action;
        for (int depth = 0; depth <= Limits.MaxChoices; depth++)
        {
            Unsafe.SkipInit(out Battle state);
            Battle.CopyPacked(in parent, ref state);
            StepResult step;
            ChoiceRequest request;
            try
            {
                step = Kernel.Step(ref state, current, _rules, out request);
            }
            catch (CapacityException)
            {
                return false;
            }
            if (step == StepResult.Completed)
            {
                Battle.CopyPacked(in state, ref result);
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
                    return false;
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
                return false;
            }
        }
        return false;
    }

    private SearchEquivalenceDiagnostics EquivalenceDiagnostics()
        => _workspaceEquivalence;

    /// <summary>
    /// Default-off differential check: runs the complete step from the parent and compares every
    /// observable result of the prefix continuation against it. Only enabled by
    /// <see cref="SearchVerificationHooks.VerifySearchEquivalence"/>.
    /// </summary>
    private void VerifyPreparedEndTurn(in Battle parent, in PlayerAction action, ref Battle actual,
        StepResult actualResult, in ChoiceRequest actualRequest, bool actualHistoryRisk,
        ExpansionWorkspace workspace)
    {
        Unsafe.SkipInit(out Battle expected);
        Battle.CopyPacked(in parent, ref expected);
        StepResult expectedResult = Kernel.Step(ref expected, action, _rules, out ChoiceRequest expectedRequest,
            out bool expectedHistoryRisk);
        if (expectedResult == actualResult && expectedHistoryRisk == actualHistoryRisk
            && BattleBytesEqual(expected, actual) && RequestEquals(expectedRequest, actualRequest))
        {
            workspace.EndTurnPrefixVerified++;
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
    private static bool TryBuildRoutingSignature(in Battle parent, in PlayerAction action,
        in ChoiceRequest request, scoped ReadOnlySpan<Card> optionCards, ulong selection,
        int choiceDepth, int parentTurn, int parentRank, double parentScore,
        in RetentionFacts parentFacts, out RoutingSignature signature, out int turn, out int depth,
        out int routingParentRank, out double routingParentScore, out int routingParentEnergy)
    {
        signature = default;
        turn = 0;
        depth = 0;
        routingParentRank = 0;
        routingParentScore = 0d;
        routingParentEnergy = 0;
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
        turn = action.Kind == PlayerActionKind.EndTurn
            && request.Timing == ChoiceTiming.PlayerTurnStart
                ? parentTurn + 1
                : parentTurn;
        signature = new RoutingSignature(turn, sourceId, request.Effect,
            request.SourceIsNone ? -1 : (int)request.Source, cardId, upgrade, stateKey, occurrence,
            cardCount, ContextId: 0, parentFacts.EnemyCombatDistributionFirst,
            parentFacts.EnemyCombatDistributionSecond, parentFacts.EnemyControlDistributionFirst,
            parentFacts.EnemyControlDistributionSecond, parentFacts.UnorderedPileKeyFirst,
            parentFacts.UnorderedPileKeySecond);
        depth = choiceDepth;
        routingParentRank = parentRank;
        routingParentScore = parentScore;
        routingParentEnergy = parentFacts.Energy;
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
        RouteNodeStore.RouteNodeView entry = _nodes.View(parentNode);
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
    /// The cumulative end-turn layer is the only pool that can reach its bound. When a unique end-turn
    /// child no longer fits, compact with the same retention rule the turn boundary uses, then add the
    /// child. Duplicates never reach the bound and stay free.
    /// </summary>
    private bool AddEndTurnChild(Pool ended, in Battle child, int node, double score, double rank,
        int potionCost, in ScoreFeatures features, int depth, ulong lineage,
        int commitment, SearchRouteTraits traits, int pocketwatchThreshold,
        int pocketwatchPlayed, int pocketwatchLastTurn, int parentEnemyBlock, int parentEnemyHp,
        int parentFutureResourceValue, int parentStrategicResourcePotential, ulong hash,
        in RetentionFacts facts)
    {
        try
        {
            return ended.AddScored(child, node, score, rank, potionCost, features, depth,
                lineage, commitment, traits, pocketwatchThreshold, pocketwatchPlayed,
                pocketwatchLastTurn, parentEnemyBlock, parentEnemyHp, parentFutureResourceValue,
                parentStrategicResourcePotential, hash, in facts);
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
                parentStrategicResourcePotential, hash, in facts);
        }
    }

    private void RecordCapacityDrop(CapacityException ex) => RecordCapacityDrop(ex.Region);

    private void RecordCapacityDrop(string region)
    {
        _capacityPruned++;
        CollectionsMarshal.GetValueRefOrAddDefault(_capacityDropped, region, out _)++;
    }

    private bool CommitmentBudgetAvailable()
        => NodeBudgetAvailable
            && !_memoryBudgetExceeded
            && (_solveWatch is not { } watch || watch.Elapsed < _options.SoftTimeBudget);

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
