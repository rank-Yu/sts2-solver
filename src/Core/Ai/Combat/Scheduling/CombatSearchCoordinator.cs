// Adapted from CombatSolver src/Search/CombatSearchCoordinator.BeamPortfolio.cs and
// CombatSearchCoordinator.PotionChain.cs at a62f1850 (MIT License, Copyright (c) 2026 Torch1230
// and contributors). Every member has an independent deadline.
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat;

internal sealed record SearchMemberMeasurement(BeamResult? Beam, string Status, string? Error,
    long ElapsedMilliseconds, long ManagedHeapPeakBytes = 0, long WorkingSetPeakBytes = 0);

internal static class CombatSearchCoordinator
{
    internal static async ValueTask<CombatSearchResult> RunAsync(CombatSearchRequest request,
        SearchStorage storage,
        Func<SearchCase, SearchStorage, CancellationToken, ValueTask<SearchMemberMeasurement>> run,
        CancellationToken token, Func<long>? clock = null)
    {
        SearchPlan plan = request.Plan;
        if (plan.BaselineWidth <= 0 || plan.NarrowWidth <= 0 || plan.WideWidth <= 0
            || plan.OffensiveWidth <= 0 || plan.MillisecondsPerWidth <= 0
            || plan.MemoryBytes <= 0
            || plan.ContinuousSetupStrategies && !plan.SetupStrategies)
            throw new ArgumentOutOfRangeException(nameof(request), "搜索参数无效。");
        SearchClock budget = new(clock);
        SearchCase initial = request.Case;
        if (initial.Battles.Length == 0) throw new InvalidDataException("搜索根为空。");
        if (initial.Options.Scorer is not { } scorer) throw new InvalidDataException("缺少评分配置。");
        // The existing search tries a forced-only baseline first when the Smart policy has forced
        // directives, then falls back to optional potions when that baseline has no usable route.
        bool forcedGradient = initial.Options.EffectivePotionPolicy == PotionUseMode.Smart
            && initial.Options.Potions.Strategy.HasForcedDirectives;
        int passCount = forcedGradient ? 2 : 1;
        SearchMember[] members = PortfolioBuilder.Build(plan,
            initial.Options.PowerCommitments is { Rules.Length: > 0 });
        QueenSearchPolicy? queen = plan.QueenStrategies && initial.Battles.Length == 1
            ? QueenSearchPolicy.Capture(initial.Battles[0], initial.Rules) : null;
        bool setup = plan.SetupStrategies && initial.Battles.Length == 1;
        SetupSearchPolicy? setupPolicy = setup ? SetupSearchPolicy.Capture(initial.Battles[0], initial.Rules) : null;
        SetupSearchPolicy? continuousSetup = setup && plan.ContinuousSetupStrategies
            ? SetupSearchPolicy.Capture(initial.Battles[0], initial.Rules, continuous: true) : null;
        if (setup || queen is not null && plan.QueenStrategies)
            members = SupplementPortfolio.AppendToStages(members, plan, queen is not null && plan.QueenStrategies);
        QueenSearchPolicy? timing = queen is null ? null : queen with { PreserveKillTiming = true };
        // Resolve all limits before dividing time so skipped and duplicate layers receive no share.
        PotionPolicy[] ResolvePolicies(PotionStrategySnapshot strategy, out string?[] skips)
        {
            BeamOptions passOptions = initial.Options with
            {
                Potions = initial.Options.Potions with { Strategy = strategy },
            };
            bool canUsePotions = PotionSearchSchedule.HasAllowedUse(
                initial.Battles, initial.Rules, passOptions);
            PotionPolicy[] policies = new PotionPolicy[members.Length];
            skips = new string?[members.Length];
            HashSet<(int Width, SearchEvaluation Evaluation, SearchRetention Retention, int? Limit)>
                seenMembers = [];
            for (int i = 0; i < members.Length; i++)
            {
                SearchMember item = members[i];
                PotionPolicy policy = PotionSearchSchedule.Limit(initial.Options.Potions, item.Potions);
                policy = policy with { Strategy = strategy };
                policies[i] = policy;
                bool potionPhase = item.Potions != SearchPotionLimit.None;
                if (potionPhase && !canUsePotions)
                    skips[i] = "no-allowed-potions";
                else if (potionPhase && !seenMembers.Add(
                    (item.Width, item.Evaluation, item.Retention, policy.MaximumUses)))
                    skips[i] = "duplicate-limit";
            }
            return policies;
        }
        SearchProgressChannel? progress = request.Progress;
        SearchResumeState? resume = request.Resume;
        if (resume != null
            && (resume.Pass < 0 || resume.Pass >= passCount
                || resume.NextMemberIndex < 0 || resume.NextMemberIndex > members.Length
                || resume.Members.Length != resume.NextMemberIndex
                || resume.StoppedIndex < -1 || resume.StoppedIndex >= resume.Members.Length))
        {
            throw new InvalidDataException("接续状态与当前搜索计划不一致。");
        }
        long plannedMilliseconds = resume == null
            ? RemainingPlannedMilliseconds(0, 0)
            : RemainingPlannedMilliseconds(resume.Pass, resume.NextMemberIndex);
        if (progress != null)
            progress.PlannedBudgetMilliseconds = plannedMilliseconds;
        BeamSolver quality = new(initial.Rules, initial.Options with { AuditPotionPolicy = true });
        List<SearchMemberResult> logs = resume != null ? [.. resume.Members] : new(members.Length);
        BeamSolver.Candidate? free = resume?.Free, selected = resume?.Selected;
        BeamResult? best = resume?.Best;
        Outcome bestOutcome = resume?.BestOutcome ?? Outcome.None;
        int bestIndex = resume?.BestIndex ?? -1;
        long expanded = resume?.ExpandedNodes ?? 0L;
        string? error = null;
        bool cancelled = false;
        bool goalReached = false;
        SortedDictionary<string, int>? capacityDrops = resume?.CapacityDrops is { } drops
            ? new SortedDictionary<string, int>(drops, StringComparer.Ordinal)
            : null;
        SearchInterimResult? bestComplete = resume?.BestComplete;
        int firstPass = resume?.Pass ?? 0;
        int logsPass = firstPass;
        for (int pass = firstPass; pass < passCount; pass++)
        {
            logsPass = pass;
            bool forcedPass = forcedGradient && pass == 0;
            PotionStrategySnapshot strategy = forcedPass
                ? initial.Options.Potions.Strategy.ForForcedBaseline()
                : initial.Options.Potions.Strategy;
            BeamOptions passOptions = initial.Options with
            {
                Potions = initial.Options.Potions with { Strategy = strategy },
            };
            PotionPolicy[] policies = ResolvePolicies(strategy, out string?[] skips);
            bool resumedPass = resume != null && pass == resume.Pass;
            if (!resumedPass)
            {
                free = null;
                selected = null;
                best = null;
                bestOutcome = Outcome.None;
                bestIndex = -1;
                goalReached = false;
                capacityDrops = null;
                bestComplete = null;
                logs = new List<SearchMemberResult>(members.Length);
            }
            for (int index = resumedPass ? resume!.NextMemberIndex : 0; index < members.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                SearchMember member = members[index];
                bool potionPhase = member.Potions != SearchPotionLimit.None;
                bool stageStart = index == 0 || members[index - 1].Potions != member.Potions;
                if (potionPhase && stageStart
                    && PotionStageGate.CanSkip(initial.Battles, initial.Rules, passOptions, selected, free))
                {
                    for (int j = index; j < members.Length && members[j].Potions == member.Potions; j++)
                        if (PotionStageGate.HasRunCombination(logs, members[j]))
                            skips[j] ??= "stage-economy";
                }
                if (skips[index] is { } reason)
                {
                    logs.Add(new SearchMemberResult(member, false, reason, 0, 0, "skipped", false, null, null, false, 0, 0));
                    continue;
                }
                long configuredMs = checked((long)member.Width * plan.MillisecondsPerWidth);
                long searchMs = SearchClock.MemberSearchMilliseconds(configuredMs);
                long memberStarted = budget.Now;
                long memberDeadline = checked(memberStarted + searchMs);
                long validationDeadline = checked(memberStarted + configuredMs);
                bool DeadlineExpired() => budget.Now >= memberDeadline;
                bool ValidationExpired() => budget.Now >= validationDeadline;
                BeamOptions options = initial.Options with
                {
                    BeamWidth = member.Width,
                    MaxTurns = int.MaxValue,
                    UnlimitedNodes = true,
                    SearchThreads = initial.Options.SearchThreads,
                    DeadlineExpired = DeadlineExpired,
                    SoftTimeBudget = TimeSpan.FromMilliseconds(searchMs),
                    Scorer = new Scorer(scorer.RulesForCodec,
                        baseOnly: member.Evaluation is SearchEvaluation.Base,
                        offensive: member.Evaluation == SearchEvaluation.Offensive,
                        queen: member.Retention == SearchRetention.QueenKillTiming ? timing : null,
                        setup: member.Evaluation == SearchEvaluation.Setup ? setupPolicy
                            : member.Evaluation == SearchEvaluation.ContinuousSetup ? continuousSetup : null),
                    SecondRankBand = member.Retention == SearchRetention.SecondBand,
                    AggressivePowerCommitment = member.Retention == SearchRetention.Power,
                    Potions = policies[index],
                    AuditPotionPolicy = potionPhase,
                    PotionFreeBaseline = free is { } freeCandidate ? BeamSolver.PotionBaseline(freeCandidate) : null,
                    Progress = progress,
                };
                options = SearchBudgetPolicy.Apply(options, SearchBudgetPolicy.EndedKeepWidths,
                    SearchBudgetPolicy.EndedTriggerWidths, plan.MemoryBytes);
                SearchMemberMeasurement measurement;
                progress?.BeginMember(index, member, bestComplete);
                try
                {
                    measurement = await run(initial with { Options = options }, storage, token);
                }
                catch (SearchMemoryBudgetException exception)
                {
                    measurement = new(null, "memory", exception.Message, Math.Max(0, budget.Now - memberStarted));
                }
                token.ThrowIfCancellationRequested();
                BeamResult? beam = measurement.Beam;
                if (beam == null && measurement.Status == "cancelled") cancelled = true;
                else if (beam == null && measurement.Status is not ("ok" or "none" or "memory"))
                    error = measurement.Error ?? measurement.Status;
                expanded += beam?.ExpandedNodesTotal ?? 0;
                if (beam is { StopWithoutCandidate: true })
                {
                    logs.Add(new SearchMemberResult(member, true, null,
                        Math.Max(0, budget.Now - memberStarted), beam.ExpandedNodesTotal, "stopped",
                        false, null, null, false, 0, 0)
                    {
                        TurnLoopGuarded = beam.TurnLoopGuarded,
                    });
                    // The stopped member published no candidate of its own. Keep the route this
                    // request already owns instead of cancelling the whole decision.
                    if (resume?.StoppedResult is { } earlier)
                    {
                        BeamResult kept = earlier;
                        int keptIndex = resume.StoppedIndex;
                        bool earlierPass = resume.Pass != pass;
                        SearchAdoptionInfo keptAdoption = resume.Adoption;
                        bool keptOnlyDeath = resume.OnlyDeathRoutes;
                        if (best is { } completed
                            && quality.TryCompareResultQuality(initial.Battles, completed, earlier,
                                out int comparison, out int completedThroughTurn, out _)
                            && comparison < 0)
                        {
                            kept = completed;
                            keptIndex = bestIndex;
                            earlierPass = false;
                            keptOnlyDeath = selected is { } completedCandidate
                                && !completedCandidate.Key.Survives;
                            keptAdoption = new SearchAdoptionInfo(0, completedThroughTurn,
                                SearchAdoptionKind.Stopped, bestOutcome)
                            {
                                OnlyDeathRoutes = keptOnlyDeath,
                            };
                        }
                        return StopResult(kept, earlierPass ? -1 : keptIndex, keptAdoption,
                            keptOnlyDeath, pass);
                    }
                    if (best is { } bestCompleted)
                    {
                        bool onlyDeath = selected is { } bestCandidate
                            && !bestCandidate.Key.Survives;
                        int throughTurn = quality.TryGetResultThroughTurn(initial.Battles,
                            bestCompleted, out int completedTurn) ? completedTurn : 0;
                        SearchAdoptionInfo completedAdoption = new(0, throughTurn,
                            SearchAdoptionKind.Stopped, bestOutcome)
                        {
                            OnlyDeathRoutes = onlyDeath,
                        };
                        return StopResult(bestCompleted, bestIndex, completedAdoption, onlyDeath, pass);
                    }
                    return new CombatSearchResult(CombatSearchStatus.Cancelled, null, -1,
                        budget.Elapsed, expanded, [.. logs], null)
                    {
                        Completion = SearchCompletionKind.Cancelled,
                        SearchThreads = initial.Options.SearchThreads,
                        PlannedBudgetMilliseconds = plannedMilliseconds,
                    };
                }
                if (beam is { Adoption: { } adoption })
                {
                    BeamResult chosen = beam;
                    int chosenIndex = index;
                    bool chosenOnlyDeath = adoption.OnlyDeathRoutes;
                    if (adoption.Kind == SearchAdoptionKind.Stopped && best is { } completed
                        && quality.TryCompareResultQuality(initial.Battles, completed, beam,
                            out int comparison, out int completedThroughTurn, out _)
                        && comparison < 0)
                    {
                        chosen = completed;
                        chosenIndex = bestIndex;
                        chosenOnlyDeath = selected is { } selectedCandidate
                            && !selectedCandidate.Key.Survives;
                        adoption = adoption with
                        {
                            CandidateVersion = 0,
                            ThroughTurn = completedThroughTurn,
                            Outcome = bestOutcome,
                            OnlyDeathRoutes = chosenOnlyDeath,
                        };
                    }
                    bool chosenFromEarlierPass = false;
                    if (adoption.Kind == SearchAdoptionKind.Stopped
                        && resume?.StoppedResult is { } stoppedEarlier
                        && quality.TryCompareResultQuality(initial.Battles, stoppedEarlier, chosen,
                            out int earlierComparison, out _, out _)
                        && earlierComparison < 0)
                    {
                        // A second stop during a resumed request keeps the best route of both.
                        chosen = stoppedEarlier;
                        chosenIndex = resume.StoppedIndex;
                        chosenFromEarlierPass = resume.Pass != pass;
                        chosenOnlyDeath = resume.OnlyDeathRoutes;
                        adoption = resume.Adoption;
                    }
                    logs.Add(new SearchMemberResult(member, true, null,
                        Math.Max(0, budget.Now - memberStarted), beam.ExpandedNodesTotal,
                        adoption.Kind == SearchAdoptionKind.Stopped ? "stopped" : "adopted",
                        chosen.BestIsVictory, null, null, chosenIndex == index, chosen.SearchMemoryPeakBytes,
                        chosen.EndedCompactions)
                    {
                        MemoryUsedBytes = chosen.SearchMemoryUsedBytes,
                        MemoryBudgetExceeded = chosen.SearchMemoryBudgetExceeded,
                        MemoryExceededAtBytes = chosen.SearchMemoryExceededAtBytes,
                        RouteNodeStorageBytes = chosen.RouteNodeStorageBytes,
                        RouteNodePeakCount = chosen.RouteNodePeakCount,
                        ManagedHeapPeakBytes = measurement.ManagedHeapPeakBytes,
                        WorkingSetPeakBytes = measurement.WorkingSetPeakBytes,
                        WorkspaceOverflowParents = chosen.WorkspaceOverflowParents,
                        SearchWorkspacePeakBytes = chosen.SearchWorkspacePeakBytes,
                        SearchWorkspaceGrowablePeakBytes = chosen.SearchWorkspaceGrowablePeakBytes,
                        TurnLoopGuarded = beam.TurnLoopGuarded,
                    });
                    if (adoption.Kind == SearchAdoptionKind.Stopped)
                        return StopResult(chosen, chosenFromEarlierPass ? -1 : chosenIndex,
                            adoption, chosenOnlyDeath, pass);
                    for (int i = 0; i < logs.Count; i++)
                        logs[i] = logs[i] with { Adopted = i == chosenIndex };
                    CombatSearchStatus adoptionStatus = adoption.Outcome == Outcome.Victory
                        ? CombatSearchStatus.Victory
                        : adoption.Outcome is Outcome.Defeat or Outcome.EventDefeat
                            ? CombatSearchStatus.Defeat
                            : CombatSearchStatus.Incomplete;
                    return new CombatSearchResult(adoptionStatus, chosen, chosenIndex, budget.Elapsed,
                        expanded, [.. logs], null)
                    {
                        Completion = SearchCompletionKind.Adopted,
                        Adoption = adoption,
                        OnlyDeathRoutes = chosenOnlyDeath,
                        SearchThreads = initial.Options.SearchThreads,
                        PlannedBudgetMilliseconds = plannedMilliseconds,
                        CapacityDroppedRegions = SnapshotCapacityDrops(capacityDrops),
                    };
                }
                bool adopted = false;
                SearchInterimResult? completeVictory = null;
                int? hpLost = null;
                int? potionCount = null;
                CombatSearchStatus? memberStatus = null;
                string stop = beam == null ? measurement.Status
                    : beam.SearchMemoryBudgetExceeded ? "memory"
                    : beam.SoftTimeBudgetReached ? "time" : beam.StopReason;
                if (beam != null && stop == "frontier-empty" && beam.CapacityPruned > 0)
                {
                    capacityDrops ??= new(StringComparer.Ordinal);
                    foreach ((string region, int count) in beam.CapacityDroppedChildren)
                        capacityDrops[region] = capacityDrops.GetValueOrDefault(region) + count;
                }
                if (beam != null && beam.PotionPolicySatisfied)
                {
                    try
                    {
                        BeamSolver.Candidate candidate = quality.AssessResult(initial.Battles, beam,
                            token, ValidationExpired, out Outcome outcome, out int rawHpLost, out bool meetsGoal);
                        hpLost = rawHpLost;
                        potionCount = candidate.ExplicitPotionUseCount;
                        memberStatus = outcome == Outcome.Victory ? CombatSearchStatus.Victory
                            : outcome is Outcome.Defeat or Outcome.EventDefeat ? CombatSearchStatus.Defeat
                            : CombatSearchStatus.Incomplete;
                        if (member.Potions == SearchPotionLimit.None
                            && candidate.ExplicitPotionUseCount == 0
                            && (free is null || quality.ComparePolicyResults(candidate, free.Value) < 0))
                            free = candidate;
                        bool policyEligible = quality.IsPolicyEligible(candidate, free);
                        if (policyEligible
                            && (selected is null || quality.ComparePolicyResults(candidate, selected.Value) < 0))
                        {
                            selected = candidate;
                            best = beam;
                            bestOutcome = outcome;
                            bestIndex = index;
                            adopted = true;
                            goalReached = meetsGoal;
                            if (candidate.Key.Won)
                            {
                                completeVictory = new SearchInterimResult(index, true, rawHpLost,
                                    candidate.ExplicitPotionUseCount, beam.BestRoute.Length,
                                    candidate.Key.Score, candidate.Key.CombatEndedTurn);
                            }
                        }
                    }
                    catch (TimeoutException) { stop = "deadline"; }
                    catch (InvalidDataException exception) { error = exception.Message; stop = "invalid"; }
                    catch (CapacityException exception) { error = exception.Message; stop = "invalid"; }
                }
                if (completeVictory != null)
                    bestComplete = completeVictory;
                logs.Add(new(member, true, null, Math.Max(0, budget.Now - memberStarted),
                    beam?.ExpandedNodesTotal ?? 0, stop, beam?.BestIsVictory ?? false, hpLost, potionCount,
                    adopted, beam?.SearchMemoryPeakBytes ?? storage.Memory.PeakBytes,
                    beam?.EndedCompactions ?? 0)
                {
                    MemoryUsedBytes = beam?.SearchMemoryUsedBytes ?? storage.Memory.UsedBytes,
                    MemoryBudgetExceeded = beam?.SearchMemoryBudgetExceeded ?? stop == "memory",
                    MemoryExceededAtBytes = beam?.SearchMemoryExceededAtBytes ?? 0,
                    PoolCapacityDropped = beam != null && beam.CapacityDroppedChildren.TryGetValue("Beam.Pool", out int dropped) ? dropped : 0,
                    RouteNodeStorageBytes = beam?.RouteNodeStorageBytes ?? 0,
                    RouteNodePeakCount = beam?.RouteNodePeakCount ?? 0,
                    ManagedHeapPeakBytes = measurement.ManagedHeapPeakBytes,
                    WorkingSetPeakBytes = measurement.WorkingSetPeakBytes,
                    WorkspaceOverflowParents = beam?.WorkspaceOverflowParents ?? 0,
                    SearchWorkspacePeakBytes = beam?.SearchWorkspacePeakBytes ?? 0,
                    SearchWorkspaceGrowablePeakBytes = beam?.SearchWorkspaceGrowablePeakBytes ?? 0,
                    TurnLoopGuarded = beam?.TurnLoopGuarded ?? 0,
                });
                if (error != null || cancelled || stop == "memory" || goalReached) break;
            }
            if (forcedPass && selected == null && error == null && !cancelled)
                continue;
            break;
        }
        while (logs.Count < members.Length)
            logs.Add(new SearchMemberResult(members[logs.Count], false, error != null ? "invalid" : cancelled ? "cancelled" : goalReached ? "target-reached" : "memory", 0, 0, "skipped", false, null, null, false, 0, 0));
        SearchResumeMerge? resumeMerge = null;
        int selectedMember = bestIndex;
        if (resume?.StoppedResult is { } stoppedResult && !cancelled && error == null)
        {
            // The stop-time route participates in the final choice: the best of the members that
            // ran after the stop is compared with it by the same final ordering the stop used.
            if (best is null)
            {
                best = stoppedResult;
                bestIndex = resume.StoppedIndex;
                bestOutcome = resume.Adoption.Outcome;
                resumeMerge = new SearchResumeMerge(false, true, "no_remaining_result");
            }
            else if (quality.TryCompareResultQuality(initial.Battles, best, stoppedResult,
                out int comparison, out _, out _))
            {
                if (comparison > 0)
                {
                    best = stoppedResult;
                    bestIndex = resume.StoppedIndex;
                    bestOutcome = resume.Adoption.Outcome;
                    resumeMerge = new SearchResumeMerge(true, true, "stopped_not_worse");
                }
                else
                {
                    resumeMerge = new SearchResumeMerge(true, false, "completed_better");
                }
            }
            else
            {
                // An incomparable pair keeps the route the user already adopted.
                best = stoppedResult;
                bestIndex = resume.StoppedIndex;
                bestOutcome = resume.Adoption.Outcome;
                resumeMerge = new SearchResumeMerge(false, true, "result_not_comparable");
            }
            if (resumeMerge is { KeptStopped: true })
                selectedMember = resume.Pass == logsPass ? resume.StoppedIndex : -1;
        }
        if (selectedMember >= 0)
            for (int i = 0; i < logs.Count; i++) logs[i] = logs[i] with { Adopted = i == selectedMember };
        // A capacity-limited search that still has any route returns that route as a normal
        // Defeat/Incomplete result. CapacityBounded remains only for the no-route case, where
        // retrying the same root cannot produce a candidate.
        bool capacityBounded = !cancelled && error == null && capacityDrops != null && best == null;
        bool forcedPotionsUnsatisfied = false;
        if (capacityBounded)
            error = "成员丢弃超出状态容量的分支后前沿耗尽，且没有可执行路线（"
                + string.Join(", ", capacityDrops!.Select(d => $"{d.Key}={d.Value}")) + "）。";
        else if (best == null && error == null && !cancelled
            && initial.Options.Potions.Strategy.HasForcedDirectives)
        {
            forcedPotionsUnsatisfied = true;
            error = "指定药水必须使用，但搜索没有找到可执行路线："
                + initial.Options.Potions.Strategy.DescribeForcedUses() + "。";
        }
        CombatSearchStatus status = cancelled ? CombatSearchStatus.Cancelled
            : capacityBounded ? CombatSearchStatus.CapacityBounded
            : error != null ? CombatSearchStatus.Invalid
            : best == null ? CombatSearchStatus.Incomplete
            : bestOutcome == Outcome.Victory ? CombatSearchStatus.Victory
            : bestOutcome is Outcome.Defeat or Outcome.EventDefeat ? CombatSearchStatus.Defeat
            : CombatSearchStatus.Incomplete;
        return new(status, error == null && !cancelled ? best : null, selectedMember,
            budget.Elapsed, expanded, [.. logs], error)
        {
            Completion = cancelled ? SearchCompletionKind.Cancelled
                : error != null || capacityBounded ? SearchCompletionKind.Failed
                : SearchCompletionKind.Completed,
            SearchThreads = initial.Options.SearchThreads,
            PlannedBudgetMilliseconds = plannedMilliseconds,
            ForcedPotionsUnsatisfied = forcedPotionsUnsatisfied,
            ResumeMerge = resumeMerge,
            OnlyDeathRoutes = resumeMerge is { KeptStopped: true }
                && resume?.StoppedResult is not null && resume.OnlyDeathRoutes,
            CapacityDroppedRegions = SnapshotCapacityDrops(capacityDrops),
        };

        static IReadOnlyDictionary<string, int>? SnapshotCapacityDrops(
            SortedDictionary<string, int>? drops)
            => drops is { Count: > 0 }
                ? new SortedDictionary<string, int>(drops, StringComparer.Ordinal)
                : null;

        long RemainingPlannedMilliseconds(int firstPass, int firstMember)
        {
            long total = 0L;
            for (int pass = firstPass; pass < passCount; pass++)
            {
                PotionStrategySnapshot strategy = forcedGradient && pass == 0
                    ? initial.Options.Potions.Strategy.ForForcedBaseline()
                    : initial.Options.Potions.Strategy;
                _ = ResolvePolicies(strategy, out string?[] passSkips);
                for (int i = pass == firstPass ? firstMember : 0; i < members.Length; i++)
                    if (passSkips[i] is null)
                        total += checked((long)members[i].Width * plan.MillisecondsPerWidth);
            }
            return total;
        }

        CombatSearchResult StopResult(BeamResult chosen, int chosenIndex,
            SearchAdoptionInfo adoption, bool chosenOnlyDeath, int pass)
        {
            for (int i = 0; i < logs.Count; i++)
                logs[i] = logs[i] with { Adopted = i == chosenIndex };
            SearchResumeState? continuation = pass == passCount - 1 && logs.Count == members.Length
                ? null
                : new SearchResumeState(pass, logs.Count, chosen, chosenIndex, adoption)
                {
                    Members = [.. logs],
                    Free = free,
                    Selected = selected,
                    Best = best,
                    BestIndex = bestIndex,
                    BestOutcome = bestOutcome,
                    BestComplete = bestComplete,
                    CapacityDrops = capacityDrops,
                    ExpandedNodes = expanded,
                    OnlyDeathRoutes = chosenOnlyDeath,
                };
            CombatSearchStatus status = adoption.Outcome == Outcome.Victory
                ? CombatSearchStatus.Victory
                : adoption.Outcome is Outcome.Defeat or Outcome.EventDefeat
                    ? CombatSearchStatus.Defeat
                    : CombatSearchStatus.Incomplete;
            return new CombatSearchResult(status, chosen, chosenIndex, budget.Elapsed,
                expanded, [.. logs], null)
            {
                Completion = SearchCompletionKind.StoppedWithCandidate,
                Adoption = adoption,
                OnlyDeathRoutes = chosenOnlyDeath,
                SearchThreads = initial.Options.SearchThreads,
                PlannedBudgetMilliseconds = plannedMilliseconds,
                Resume = continuation,
                CapacityDroppedRegions = SnapshotCapacityDrops(capacityDrops),
            };
        }
    }
}
