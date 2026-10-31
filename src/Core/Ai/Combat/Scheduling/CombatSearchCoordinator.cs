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
            || plan.ContinuousSetupStrategies && !plan.SetupStrategies
            || plan.EndedKeepWidths < 2 || plan.EndedTriggerWidths <= plan.EndedKeepWidths)
            throw new ArgumentOutOfRangeException(nameof(request), "搜索参数无效。");
        SearchClock budget = new(clock);
        SearchCase initial = request.Case;
        if (initial.Battles.Length == 0) throw new InvalidDataException("搜索根为空。");
        if (initial.Options.Scorer is not { } scorer) throw new InvalidDataException("缺少评分配置。");
        bool canUsePotions = PotionSearchSchedule.HasAllowedUse(initial.Battles, initial.Rules, initial.Options);
        bool requiredPlan = initial.Options.EffectivePotionPolicy == PotionUseMode.RequireAtLeastOne;
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
        PotionPolicy[] policies = new PotionPolicy[members.Length];
        string?[] skips = new string?[members.Length];
        HashSet<(int Width, SearchEvaluation Evaluation, SearchRetention Retention, int? Limit)> seenMembers = [];
        for (int i = 0; i < members.Length; i++)
        {
            SearchMember item = members[i];
            PotionPolicy policy = PotionSearchSchedule.Limit(initial.Options.Potions, item.Potions,
                initial.Options.InitialExplicitPotionUses);
            policies[i] = policy;
            bool potionPhase = !requiredPlan && item.Potions != SearchPotionLimit.None;
            if (potionPhase && !canUsePotions)
                skips[i] = "no-allowed-potions";
            else if (potionPhase && policy.MaximumUses is { } limit
                && limit <= initial.Options.InitialExplicitPotionUses)
                skips[i] = "limit-already-used";
            else if (potionPhase && !seenMembers.Add(
                (item.Width, item.Evaluation, item.Retention, policy.MaximumUses)))
                skips[i] = "duplicate-limit";
        }
        BeamSolver quality = new(initial.Rules, initial.Options with { AuditPotionPolicy = true });
        List<SearchMemberResult> logs = new(members.Length);
        BeamSolver.Candidate? free = null, selected = null;
        BeamResult? best = null;
        Outcome bestOutcome = Outcome.None;
        int bestIndex = -1;
        long expanded = 0;
        string? error = null;
        bool cancelled = false;
        bool goalReached = false;
        SortedDictionary<string, int>? capacityDrops = null;
        for (int index = 0; index < members.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            SearchMember member = members[index];
            bool potionPhase = !requiredPlan && member.Potions != SearchPotionLimit.None;
            bool stageStart = index == 0 || members[index - 1].Potions != member.Potions;
            if (potionPhase && stageStart && !requiredPlan
                && PotionStageGate.CanSkip(initial.Battles, initial.Rules, initial.Options, selected, free))
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
                DeadlineExpired = DeadlineExpired,
                SoftTimeBudget = TimeSpan.FromMilliseconds(searchMs),
                Scorer = new Scorer(scorer.RulesForCodec,
                    baseOnly: member.Evaluation is SearchEvaluation.Base,
                    offensive: member.Evaluation == SearchEvaluation.Offensive,
                    queen: member.Retention == SearchRetention.QueenKillTiming ? timing : null,
                    setup: member.Evaluation == SearchEvaluation.Setup ? setupPolicy
                        : member.Evaluation == SearchEvaluation.ContinuousSetup ? continuousSetup : null),
                SecondRankBand = member.Retention == SearchRetention.SecondBand,
                SecondRankBandWidth = member.Width,
                AggressivePowerCommitment = member.Retention == SearchRetention.Power,
                Potions = policies[index],
                AuditPotionPolicy = potionPhase || requiredPlan
                    || initial.Options.InitialExplicitPotionUses > 0,
                PotionFreeBaseline = free is { } freeCandidate ? BeamSolver.PotionBaseline(freeCandidate) : null,
            };
            options = SearchBudgetPolicy.Apply(options, plan.EndedKeepWidths,
                plan.EndedTriggerWidths, plan.MemoryBytes);
            SearchMemberMeasurement measurement;
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
            bool adopted = false;
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
                    }
                }
                catch (TimeoutException) { stop = "deadline"; }
                catch (InvalidDataException exception) { error = exception.Message; stop = "invalid"; }
                catch (CapacityException exception) { error = exception.Message; stop = "invalid"; }
            }
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
            });
            if (error != null || cancelled || stop == "memory" || goalReached) break;
        }
        while (logs.Count < members.Length)
            logs.Add(new SearchMemberResult(members[logs.Count], false, error != null ? "invalid" : cancelled ? "cancelled" : goalReached ? "target-reached" : "memory", 0, 0, "skipped", false, null, null, false, 0, 0));
        if (bestIndex >= 0)
            for (int i = 0; i < logs.Count; i++) logs[i] = logs[i] with { Adopted = i == bestIndex };
        bool capacityBounded = !cancelled && error == null && capacityDrops != null
            && (best == null || bestOutcome != Outcome.Victory);
        if (capacityBounded)
            error = "成员丢弃超出状态容量的分支后前沿耗尽，且没有胜利路线（"
                + string.Join(", ", capacityDrops!.Select(d => $"{d.Key}={d.Value}")) + "）。";
        CombatSearchStatus status = cancelled ? CombatSearchStatus.Cancelled
            : capacityBounded ? CombatSearchStatus.CapacityBounded
            : error != null ? CombatSearchStatus.Invalid
            : best == null ? CombatSearchStatus.Incomplete
            : bestOutcome == Outcome.Victory ? CombatSearchStatus.Victory
            : bestOutcome is Outcome.Defeat or Outcome.EventDefeat ? CombatSearchStatus.Defeat
            : CombatSearchStatus.Incomplete;
        return new(status, error == null && !cancelled ? best : null, bestIndex, budget.Elapsed,
            expanded, [.. logs], error);
    }
}
