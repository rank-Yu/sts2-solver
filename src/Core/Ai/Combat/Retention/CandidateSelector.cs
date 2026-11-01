// Candidate ranking, representative protection and quota selection extracted from BeamSolver.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Ai.Combat.Scoring;

namespace Sts2Solver.Ai.Combat;

internal sealed partial class BeamSolver
{
    private sealed partial class Pool
    {
        /// <summary>
        /// Keeps the best <paramref name="width"/> states by rank. On top of the score leaders the
        /// existing search's required representatives (per potion count, global defensive/offensive/setup
        /// leaders and the strongest root-action lineages) replace the last non-required score seat. The
        /// total width is unchanged: a representative takes a seat, it does not widen the beam.
        /// </summary>
        public void CopyTopTo(Pool target, int width, bool turnBoundary = false)
        {
            List<int> selected = SelectTop(width, turnBoundary);
            // Every production target is empty: the source is already deduplicated, so copying its
            // hash and metadata cannot conflict and needs neither a rehash nor an equality probe.
            if (target.Count == 0 && !ReferenceEquals(target, this))
            {
                for (int index = 0; index < selected.Count; index++)
                    target.AddCopiedFrom(this, selected[index]);
                return;
            }
            for (int index = 0; index < selected.Count; index++)
            {
                int source = selected[index];
                target.Add(_states[source], _nodes[source], _scores[source], _ranks[source],
                    _potionCosts[source], _facts[source], _lineages[source],
                    _commitment[source], _labels[source]);
            }
        }

        /// <summary>
        /// Compacts the pool to <paramref name="keep"/> states with the same selection rule as
        /// <see cref="CopyTopTo"/> (rank seats and every required representative) applied at the
        /// compaction width. The kept entries are moved to the front in pool order and
        /// the dedup table is rebuilt; the dropped count is returned.
        /// </summary>
        public int Compact(int keep)
        {
            int count = Count;
            if (keep >= count)
                return 0;
            List<int> selected = SelectTop(keep, turnBoundary: true);
            if (_trace != null)
            {
                for (int index = 0; index < count; index++)
                {
                    int node = _nodes[index];
                    if (_trace.IsWatched(node) && !selected.Contains(index))
                        _trace.NodePruned(node, TracePhase, "ended-compaction");
                }
            }
            // SelectTop orders by rank position; compaction needs ascending source indexes so a move
            // never clobbers a later source. Destination slots (0..count-1) are always below the
            // current source, and selected sources are distinct.
            CollectionsMarshal.AsSpan(selected).Sort();
            for (int index = 0; index < selected.Count; index++)
            {
                int source = selected[index];
                if (source != index)
                    Move(source, index);
            }
            int dropped = count - selected.Count;
            for (int index = selected.Count; index < count; index++) _labels[index] = null;
            Count = selected.Count;
            _states.Truncate(selected.Count);
            RebuildTable();
            return dropped;
        }

        /// <summary>
        /// The shared selection rule of <see cref="CopyTopTo"/> and <see cref="Compact"/>: rank
        /// leaders, then every required representative replacing the last ordinary seat. Returns the
        /// selected source indexes in rank order.
        /// </summary>
        private List<int> SelectTop(int width, bool turnBoundary)
        {
            int count = Count;
            EnsureScratch(count);
            List<int> selected = _selected;
            selected.Clear();
            if (count == 0)
                return selected;
            for (int index = 0; index < count; index++)
            {
                _order[index] = index;
                _taken[index] = false;
                _required[index] = false;
            }
            _retentionObserver?.Invoke();
            _checkpoint?.Invoke();
            _cancellationToken.ThrowIfCancellationRequested();
            SortByRank(count);
            _checkpoint?.Invoke();
            _cancellationToken.ThrowIfCancellationRequested();
            int seats = Math.Min(width, count);
            // BeamRetentionPolicy.RankBest: a band member rotates the score-leading width to the tail
            // before the ordinary seats are taken, so ranks W+1..2W win (with the original order as
            // backfill when fewer than 2W candidates exist). Required representatives, the power
            // commitment seats and the plan seats are collected/added below and are unaffected.
            if (_secondRankBand && count > seats)
                RotateLeadingBandToTail(count, Math.Clamp(_secondRankBandWidth, 1, seats));
            for (int index = 0; index < seats; index++)
            {
                int source = _order[index];
                selected.Add(source);
                _taken[source] = true;
            }
            List<int> required = _requiredScratch;
            required.Clear();
            // When a required leader is not in the top seats it replaces the last non-required seat,
            // so the score leaders and then the required set are preserved. The collection limits
            // scale with the requested width exactly as before.
            CollectRepresentatives(required, count, width, turnBoundary);
            _checkpoint?.Invoke();
            _cancellationToken.ThrowIfCancellationRequested();
            // BeamRetentionPolicy.RankBest: every required candidate not already ranked replaces the
            // last ordinary seat, so all required members survive without widening the layer.
            foreach (int source in required)
            {
                if (_taken[source])
                    continue;
                int replaceIndex = -1;
                for (int index = selected.Count - 1; index >= 0; index--)
                {
                    if (_required[selected[index]])
                        continue;
                    replaceIndex = index;
                    break;
                }
                if (replaceIndex < 0)
                    break;
                _taken[selected[replaceIndex]] = false;
                selected[replaceIndex] = source;
                _taken[source] = true;
            }
            if (_trackPowerCommitments && _hasCommitments)
                AdmitPowerCommitmentSeats(selected, required, width);
            if (_scorer.QueenPolicy is { PreserveKillTiming: true } queen)
                AdmitQueenPhaseSeats(selected, width, queen);
            if (_checkPotionQuota)
                ApplyPotionUseQuota(selected, width);
            CollectionsMarshal.AsSpan(selected).Sort(new RankPositionComparer(_rankPosition));
            if (_trace != null)
            {
                for (int index = 0; index < count; index++)
                {
                    int node = _nodes[index];
                    if (!_trace.IsWatched(node))
                        continue;
                    int selectedPosition = selected.IndexOf(index);
                    bool isSelected = selectedPosition >= 0;
                    string lanes = isSelected ? "" : TraceLeadingLanes(index);
                    _trace.NodeRetained(node, TracePhase, isSelected, _required[index],
                        _rankPosition[index], count, width, lanes);
                }
            }
            return selected;
        }

        private void AdmitQueenPhaseSeats(List<int> selected, int width, QueenSearchPolicy queen)
        {
            bool aliveSeen = false, deadSeen = false;
            for (int i = 0; i < Count && !(aliveSeen && deadSeen); i++)
                if (_states[i].Enemies[queen.TorchSlot].Creature.IsAlive) aliveSeen = true;
                else deadSeen = true;
            if (!(aliveSeen && deadSeen)) return;
            int quota = Math.Max(1, width / 8);
            for (int phase = 0; phase < 2; phase++)
            {
                int retained = 0;
                for (int position = 0; position < Count && retained < quota; position++)
                {
                    int candidate = _rankOrder[position];
                    bool alive = _states[candidate].Enemies[queen.TorchSlot].Creature.IsAlive;
                    if (alive != (phase == 0)) continue;
                    int at = selected.IndexOf(candidate);
                    if (at < 0)
                    {
                        for (int j = selected.Count - 1; j >= 0; j--)
                            if (!_required[selected[j]])
                            {
                                _taken[selected[j]] = false;
                                selected[j] = candidate;
                                _taken[candidate] = true;
                                at = j;
                                break;
                            }
                    }
                    if (at >= 0) { _required[candidate] = true; retained++; }
                }
            }
        }

        /// <summary>
        /// Trace-only classification of a pool state that lost the selection: the lanes and grouping
        /// representatives whose leader comparison over this pool the state would win. It reads the
        /// same comparators as <see cref="CollectRepresentatives"/> but never writes selection state.
        /// </summary>
        private string TraceLeadingLanes(int target)
        {
            List<string> labels = [];
            RetentionFacts tf = _facts[target];
            int bd = -1, bo = -1, bs = -1, bsc = -1, bc = -1, bcmp = -1, bud = -1;
            int blt = -1, bhp = -1, bres = -1;
            for (int position = 0; position < Count; position++)
            {
                int source = _rankOrder[position];
                RetentionFacts facts = _facts[source];
                if (bd < 0 || IsBetterDefensive(facts, _facts[bd])) bd = source;
                if (bo < 0 || IsBetterOffensive(facts, _facts[bo])) bo = source;
                if (bs < 0 || IsBetterSetup(facts, _facts[bs])) bs = source;
                if (bsc < 0 || IsBetterSetupCombo(facts, _facts[bsc])) bsc = source;
                if (bc < 0 || IsBetterDeckCuration(facts, _facts[bc])) bc = source;
                if (bcmp < 0 || IsBetterCompressedDeck(facts, _facts[bcmp])) bcmp = source;
                if (facts.Traits != SearchRouteTraits.None
                    && (bud < 0 || IsBetterUtilityDefensive(facts, _facts[bud]))) bud = source;
                if ((facts.Traits & SearchRouteTraits.LongTermResource) != 0
                    && (blt < 0 || IsBetterLane(facts, _facts[blt], SearchRouteTraits.LongTermResource)))
                {
                    blt = source;
                }
                if ((facts.Traits & SearchRouteTraits.HpInvestment) != 0
                    && (bhp < 0 || IsBetterLane(facts, _facts[bhp], SearchRouteTraits.HpInvestment)))
                {
                    bhp = source;
                }
                if (_preserveTheft
                    && (bres < 0 || IsBetterResourcePreserving(facts, _facts[bres]))) bres = source;
            }
            if (target == bd) labels.Add("global.defensive");
            if (target == bo) labels.Add("global.offensive");
            if (target == bs) labels.Add("global.setup");
            if (target == bsc) labels.Add("global.setup-combo");
            if (target == bc) labels.Add("global.curation");
            if (target == bcmp) labels.Add("global.compressed");
            if (target == bud) labels.Add("global.utility-defensive");
            if (target == blt) labels.Add("lane.LongTermResource");
            if (target == bhp) labels.Add("lane.HpInvestment");
            if (target == bres) labels.Add("lane.ResourcePreserving");
            for (int lane = 0; lane < RetainedLanes.Length; lane++)
            {
                SearchRouteTraits trait = RetainedLanes[lane];
                int leader = -1;
                for (int position = 0; position < Count; position++)
                {
                    int source = _rankOrder[position];
                    RetentionFacts facts = _facts[source];
                    if ((facts.Traits & trait) == 0)
                        continue;
                    if (leader < 0 || IsBetterLane(facts, _facts[leader], trait))
                        leader = source;
                }
                if (target == leader)
                    labels.Add($"lane.{trait}");
            }
            // Per potion-count group: the same leaders RankBest keeps for the target's group.
            int pd = -1, po = -1, ps = -1, psc = -1, pc = -1, pcomp = -1,
                pstr = -1, pweak = -1, ptac = -1, ptb = -1;
            for (int position = 0; position < Count; position++)
            {
                int source = _rankOrder[position];
                RetentionFacts facts = _facts[source];
                if (facts.PotionCount != tf.PotionCount)
                    continue;
                if (pd < 0 || IsBetterDefensive(facts, _facts[pd])) pd = source;
                if (po < 0 || IsBetterOffensive(facts, _facts[po])) po = source;
                if (ps < 0 || IsBetterSetup(facts, _facts[ps])) ps = source;
                if (psc < 0 || IsBetterSetupCombo(facts, _facts[psc])) psc = source;
                if (pc < 0 || IsBetterDeckCuration(facts, _facts[pc])) pc = source;
                if (pcomp < 0 || IsBetterCompressedDeck(facts, _facts[pcomp])) pcomp = source;
                if (pstr < 0 || IsBetterEnemyStrengthControl(facts, _facts[pstr])) pstr = source;
                if (pweak < 0 || IsBetterEnemyWeakControl(facts, _facts[pweak])) pweak = source;
                if (ptac < 0 || IsBetterTacticalEnabler(facts, _facts[ptac])) ptac = source;
                if (ptb < 0 || IsBetterTurnBoundaryHand(facts, _facts[ptb])) ptb = source;
            }
            if (target == pd) labels.Add("potion.defensive");
            if (target == po) labels.Add("potion.offensive");
            if (target == ps) labels.Add("potion.setup");
            if (target == psc) labels.Add("potion.setup-combo");
            if (target == pc) labels.Add("potion.curation");
            if (target == pcomp) labels.Add("potion.compressed");
            if (target == pstr) labels.Add("potion.strength-control");
            if (target == pweak) labels.Add("potion.weak-control");
            if (target == ptac) labels.Add("potion.tactical");
            if (target == ptb) labels.Add("potion.turn-boundary");
            // Root-action lineage group leaders.
            ulong lineage = _lineages[target];
            int ld = -1, lo = -1, ls = -1, lsc = -1, lc = -1, lcomp = -1, ltac = -1, ltp = -1;
            for (int position = 0; position < Count; position++)
            {
                int source = _rankOrder[position];
                if (_lineages[source] != lineage)
                    continue;
                RetentionFacts facts = _facts[source];
                if (ld < 0 || IsBetterDefensive(facts, _facts[ld])) ld = source;
                if (lo < 0 || IsBetterOffensive(facts, _facts[lo])) lo = source;
                if (ls < 0 || IsBetterSetup(facts, _facts[ls])) ls = source;
                if (lsc < 0 || IsBetterSetupCombo(facts, _facts[lsc])) lsc = source;
                if (lc < 0 || IsBetterDeckCuration(facts, _facts[lc])) lc = source;
                if (lcomp < 0 || IsBetterCompressedDeck(facts, _facts[lcomp])) lcomp = source;
                if (ltac < 0 || IsBetterTacticalEnabler(facts, _facts[ltac])) ltac = source;
                if (ltp < 0 || IsBetterTargetPressure(facts, _facts[ltp])) ltp = source;
            }
            if (target == ld) labels.Add("lineage.defensive");
            if (target == lo) labels.Add("lineage.offensive");
            if (target == ls) labels.Add("lineage.setup");
            if (target == lsc) labels.Add("lineage.setup-combo");
            if (target == lc) labels.Add("lineage.curation");
            if (target == lcomp) labels.Add("lineage.compressed");
            if (target == ltac) labels.Add("lineage.tactical");
            if (target == ltp) labels.Add("lineage.target-pressure");
            // Persistent setup trait group (defensive/setup) and focus target group.
            if (tf.SetupTraits != PersistentSetupTraits.None)
            {
                int tdef = -1, tsetup = -1;
                for (int position = 0; position < Count; position++)
                {
                    int source = _rankOrder[position];
                    RetentionFacts facts = _facts[source];
                    if (facts.PotionCount != tf.PotionCount || facts.SetupTraits != tf.SetupTraits)
                        continue;
                    if (tdef < 0 || IsBetterDefensive(facts, _facts[tdef])) tdef = source;
                    if (tsetup < 0 || IsBetterSetup(facts, _facts[tsetup])) tsetup = source;
                }
                if (target == tdef) labels.Add("trait.defensive");
                if (target == tsetup) labels.Add("trait.setup");
            }
            if (tf.FocusTargetCombatId is { } focus)
            {
                int fpressure = -1, fsetup = -1;
                for (int position = 0; position < Count; position++)
                {
                    int source = _rankOrder[position];
                    RetentionFacts facts = _facts[source];
                    if (facts.PotionCount != tf.PotionCount || facts.FocusTargetCombatId != focus)
                        continue;
                    if (fpressure < 0 || IsBetterTargetPressure(facts, _facts[fpressure]))
                        fpressure = source;
                    if (fsetup < 0 || IsBetterTargetSetup(facts, _facts[fsetup])) fsetup = source;
                }
                if (target == fpressure) labels.Add("focus.pressure");
                if (target == fsetup) labels.Add("focus.setup");
            }
            return labels.Count == 0 ? "-" : string.Join(",", labels);
        }

        /// <summary>
        /// <c>BeamRetentionPolicy.FeasiblePotionUseQuotas</c> and the two quota passes after the
        /// ordinary/required seats are chosen. The quota only swaps seats; it never widens the beam.
        /// </summary>
        private void ApplyPotionUseQuota(List<int> selected, int limit)
        {
            bool anyUsed = false, anyUnused = false;
            for (int position = 0; position < Count; position++)
            {
                if (UsesPotion(_rankOrder[position]))
                    anyUsed = true;
                else
                    anyUnused = true;
                if (anyUsed && anyUnused)
                    break;
            }
            if (!anyUsed || !anyUnused)
                return;
            (int usedQuota, int unusedQuota) = FeasiblePotionUseQuotas(_policyWidth);
            _potionReservations.Clear();
            for (int index = 0; index < _requiredScratch.Count; index++)
                _potionReservations.Add(_requiredScratch[index]);
            ReservePotionQuotaLeaders(usesPotion: true, usedQuota);
            ReservePotionQuotaLeaders(usesPotion: false, unusedQuota);
            EnforcePotionUseQuota(selected, usesPotion: true, usedQuota);
            EnforcePotionUseQuota(selected, usesPotion: false, unusedQuota);
        }

        private static (int Used, int Unused) FeasiblePotionUseQuotas(int limit)
        {
            if (limit < 1)
                throw new ArgumentOutOfRangeException(nameof(limit));
            int used = limit < 4 ? 1 : Math.Max(2, limit / 3);
            return (used, limit - used);
        }

        private bool UsesPotion(int index) => _facts[index].PotionCount > 0;

        private void ReservePotionQuotaLeaders(bool usesPotion, int quota)
        {
            if (quota <= 0)
                return;
            int reserved = 0;
            for (int position = 0; position < Count; position++)
            {
                int candidate = _rankOrder[position];
                if (UsesPotion(candidate) != usesPotion)
                    continue;
                _potionReservations.Add(candidate);
                reserved++;
                if (reserved >= quota)
                    return;
            }
        }

        private void EnforcePotionUseQuota(List<int> selected, bool usesPotion, int quota)
        {
            int retained = 0;
            for (int index = 0; index < selected.Count; index++)
            {
                if (UsesPotion(selected[index]) == usesPotion)
                    retained++;
            }
            if (retained >= quota)
                return;
            for (int position = 0; position < Count; position++)
            {
                if (retained >= quota)
                    return;
                int candidate = _rankOrder[position];
                if (UsesPotion(candidate) != usesPotion || selected.Contains(candidate))
                    continue;
                int replaceIndex = -1;
                for (int index = selected.Count - 1; index >= 0; index--)
                {
                    int node = selected[index];
                    if (UsesPotion(node) != usesPotion && !_potionReservations.Contains(node))
                    {
                        replaceIndex = index;
                        break;
                    }
                }
                if (replaceIndex < 0)
                    return;
                selected[replaceIndex] = candidate;
                retained++;
            }
        }

        /// <summary>Left-rotate <c>_order[0..count)</c> by <paramref name="bandWidth"/> in place.</summary>
        private void RotateLeadingBandToTail(int count, int bandWidth)
        {
            Span<int> order = _order.AsSpan(0, count);
            order.Reverse();
            order[..(count - bandWidth)].Reverse();
            order[(count - bandWidth)..].Reverse();
        }

        /// <summary>
        /// <c>BeamRetentionPolicy.AdmitPowerCommitmentRepresentatives</c>. The pool is the existing
        /// <c>quotaPool</c> (score order); the quota is half the beam for aggressive members and
        /// <c>clamp(W/12, 2, 12)</c> otherwise. Representatives replace the last non-required,
        /// non-representative seat and are marked required. The beam width is unchanged.
        /// </summary>
        private void AdmitPowerCommitmentSeats(List<int> selected, List<int> required, int width)
        {
            int quota = PowerCommitmentSeatPolicy.SeatQuota(width, _aggressivePowerCommitment);
            if (quota <= 0)
                return;
            _commitmentGroups.Clear();
            _commitmentGroupOrder.Clear();
            _powerRepresentatives.Clear();
            // Existing RankRepresentatives receives the score-ordered quota pool; walk the same
            // rank order so a first-occurrence tie in the group keys matches.
            for (int position = 0; position < Count; position++)
            {
                int index = _rankOrder[position];
                int commitmentIndex = _commitment[index];
                if (commitmentIndex <= 0)
                    continue;
                PowerCommitmentState commitment = _commitmentStates[commitmentIndex - 1];
                RetentionFacts facts = _facts[index];
                CommitmentGroupKey key = new(
                    (int)commitment.Family,
                    facts.PotionCount,
                    facts.Turn,
                    commitment.CardsLow,
                    commitment.CardsHigh);
                if (_commitmentGroups.TryGetValue(key, out CommitmentGroupLeader leader))
                {
                    if (IsBetterPowerRepresentative(index, leader.Winner))
                        _commitmentGroups[key] = leader with { Winner = index };
                }
                else
                {
                    // The existing GroupBy keeps the first occurrence order; record it for the stable
                    // subgroup ordering after the primary keys tie.
                    _commitmentGroups.Add(key, new CommitmentGroupLeader(index, position));
                    _commitmentGroupOrder.Add(key);
                }
            }
            if (_commitmentGroupOrder.Count == 0)
                return;
            _commitmentGroupOrder.Sort(new CommitmentGroupComparer(this));
            int take = Math.Min(quota, _commitmentGroupOrder.Count);
            for (int index = 0; index < take; index++)
                _powerRepresentatives.Add(_commitmentGroups[_commitmentGroupOrder[index]].Winner);
            for (int index = 0; index < _powerRepresentatives.Count; index++)
                _representative[_powerRepresentatives[index]] = true;
            foreach (int candidate in _powerRepresentatives)
            {
                if (selected.Contains(candidate))
                {
                    AddRequired(required, candidate, width);
                    continue;
                }
                int replaceIndex = -1;
                for (int index = selected.Count - 1; index >= 0; index--)
                {
                    int node = selected[index];
                    if (!_required[node] && !_representative[node])
                    {
                        replaceIndex = index;
                        break;
                    }
                }
                if (replaceIndex < 0)
                    continue;
                _taken[selected[replaceIndex]] = false;
                selected[replaceIndex] = candidate;
                _taken[candidate] = true;
                AddRequired(required, candidate, width);
            }
            foreach (int candidate in _powerRepresentatives)
                _representative[candidate] = false;
        }

        /// <summary><c>PowerCommitmentRetention.RankRepresentatives</c> in-group order.</summary>
        private bool IsBetterPowerRepresentative(int candidate, int current)
        {
            PowerCommitmentState a = _commitmentStates[_commitment[candidate] - 1];
            PowerCommitmentState b = _commitmentStates[_commitment[current] - 1];
            if (a.RealizedEvidence != b.RealizedEvidence)
                return a.RealizedEvidence > b.RealizedEvidence;
            if (a.Priority != b.Priority)
                return a.Priority > b.Priority;
            if (a.ProgressEvidence != b.ProgressEvidence)
                return a.ProgressEvidence > b.ProgressEvidence;
            if (a.NetUnrealizedValue != b.NetUnrealizedValue)
                return a.NetUnrealizedValue > b.NetUnrealizedValue;
            if (a.OpenedActionCount != b.OpenedActionCount)
                return a.OpenedActionCount < b.OpenedActionCount;
            if (a.RoundTransitions != b.RoundTransitions)
                return a.RoundTransitions < b.RoundTransitions;
            if (_facts[candidate].ProjectedPlayerHp != _facts[current].ProjectedPlayerHp)
                return _facts[candidate].ProjectedPlayerHp > _facts[current].ProjectedPlayerHp;
            if (_facts[candidate].OffensiveProgressValue != _facts[current].OffensiveProgressValue)
                return _facts[candidate].OffensiveProgressValue > _facts[current].OffensiveProgressValue;
            if (_scores[candidate] != _scores[current])
                return _scores[candidate] > _scores[current];
            return _facts[candidate].Depth < _facts[current].Depth;
        }

        private readonly record struct CommitmentGroupKey(
            int Family, int PotionCount, int Turn, ulong CardsLow, ulong CardsHigh);

        private readonly record struct CommitmentGroupLeader(int Winner, int Position);

        /// <summary>The existing representative ordering: descending keys, then ascending family.</summary>
        private readonly struct CommitmentGroupComparer : IComparer<CommitmentGroupKey>
        {
            private readonly Pool _pool;

            public CommitmentGroupComparer(Pool pool) => _pool = pool;

            public int Compare(CommitmentGroupKey left, CommitmentGroupKey right)
            {
                int leftWinner = _pool._commitmentGroups[left].Winner;
                int rightWinner = _pool._commitmentGroups[right].Winner;
                PowerCommitmentState a = _pool._commitmentStates[_pool._commitment[leftWinner] - 1];
                PowerCommitmentState b = _pool._commitmentStates[_pool._commitment[rightWinner] - 1];
                int comparison = b.RealizedEvidence.CompareTo(a.RealizedEvidence);
                if (comparison != 0) return comparison;
                comparison = ((int)b.Priority).CompareTo((int)a.Priority);
                if (comparison != 0) return comparison;
                comparison = b.ProgressEvidence.CompareTo(a.ProgressEvidence);
                if (comparison != 0) return comparison;
                comparison = b.NetUnrealizedValue.CompareTo(a.NetUnrealizedValue);
                if (comparison != 0) return comparison;
                comparison = a.OpenedActionCount.CompareTo(b.OpenedActionCount);
                if (comparison != 0) return comparison;
                comparison = a.RoundTransitions.CompareTo(b.RoundTransitions);
                if (comparison != 0) return comparison;
                comparison = _pool._facts[rightWinner].ProjectedPlayerHp
                    .CompareTo(_pool._facts[leftWinner].ProjectedPlayerHp);
                if (comparison != 0) return comparison;
                comparison = _pool._scores[rightWinner].CompareTo(_pool._scores[leftWinner]);
                if (comparison != 0) return comparison;
                comparison = left.Family.CompareTo(right.Family);
                return comparison != 0
                    ? comparison
                    : _pool._commitmentGroups[left].Position
                        .CompareTo(_pool._commitmentGroups[right].Position);
            }
        }

        private void AddRequired(List<int> required, int source, int width)
        {
            if (source < 0 || required.Count >= width || _required[source])
                return;
            _required[source] = true;
            required.Add(source);
        }

        /// <summary>
        /// The existing RankBest required set, restricted to the mechanisms whose inputs Sts2Solver already
        /// evaluates exactly: one leader set per potion count, the global leaders, the strongest
        /// root-action lineages (clamp(width / 8, 4, 16)), the turn-boundary hand leader, the setup
        /// trait and focus target groups, and the eight lane leaders.
        /// </summary>
        private void CollectRepresentatives(List<int> required, int count, int width, bool turnBoundary)
        {
            _lineageLeaders.Clear();
            _potionLeaders.Clear();
            foreach (List<int> group in _standPatGroups.Values)
                group.Clear();
            _traitLeaders.Clear();
            _traitOrder.Clear();
            _focusLeaders.Clear();
            _focusOrder.Clear();
            int bestDefensive = -1, bestOffensive = -1, bestSetup = -1, bestUtilityDefensive = -1;
            int bestSetupCombo = -1, bestCuration = -1, bestCompressed = -1;
            int bestPotionFree = -1, bestPotionFreeDefensive = -1, bestPotionFreeOffensive = -1;
            int bestPotionFreeSetup = -1, bestPotionFreeSetupCombo = -1, bestPotionFreeUtilityDefensive = -1;
            int bestPotion = -1, bestPotionDefensive = -1, bestPotionOffensive = -1, bestPotionSetup = -1;
            int bestPotionSetupCombo = -1;
            int bestLongTermResource = -1, bestHpInvestment = -1, bestResourcePreserving = -1;
            bool wideLanes = _policyWidth >= 18;
            for (int position = 0; position < count; position++)
            {
                int source = _rankOrder[position];
                RetentionFacts facts = _facts[source];
                int potionCount = facts.PotionCount;
                if (!_potionLeaders.TryGetValue(potionCount, out PotionLeaders? potion))
                {
                    potion = new PotionLeaders { Leader = source };
                    _potionLeaders.Add(potionCount, potion);
                }
                if (wideLanes)
                {
                    if (!_standPatGroups.TryGetValue(potionCount, out List<int>? standPat))
                    {
                        standPat = [];
                        _standPatGroups.Add(potionCount, standPat);
                    }
                    standPat.Add(source);
                }
                if (potion.Defensive < 0 || IsBetterDefensive(facts, _facts[potion.Defensive]))
                    potion.Defensive = source;
                if (potion.Offensive < 0 || IsBetterOffensive(facts, _facts[potion.Offensive]))
                    potion.Offensive = source;
                if (potion.Setup < 0 || IsBetterSetup(facts, _facts[potion.Setup]))
                    potion.Setup = source;
                if (potion.SetupCombo < 0 || IsBetterSetupCombo(facts, _facts[potion.SetupCombo]))
                    potion.SetupCombo = source;
                if (potion.Curation < 0 || IsBetterDeckCuration(facts, _facts[potion.Curation]))
                    potion.Curation = source;
                if (potion.Compressed < 0 || IsBetterCompressedDeck(facts, _facts[potion.Compressed]))
                    potion.Compressed = source;
                if (potion.Strength < 0 || IsBetterEnemyStrengthControl(facts, _facts[potion.Strength]))
                    potion.Strength = source;
                if (potion.Weak < 0 || IsBetterEnemyWeakControl(facts, _facts[potion.Weak]))
                    potion.Weak = source;
                if (potion.Tactical < 0 || IsBetterTacticalEnabler(facts, _facts[potion.Tactical]))
                    potion.Tactical = source;
                if (potion.TurnBoundary < 0
                    || IsBetterTurnBoundaryHand(facts, _facts[potion.TurnBoundary]))
                {
                    potion.TurnBoundary = source;
                }
                if (_preserveTheft
                    && (potion.Resource < 0 || IsBetterResourcePreserving(facts, _facts[potion.Resource])))
                {
                    potion.Resource = source;
                }
                for (int lane = 0; wideLanes && lane < RetainedLanes.Length; lane++)
                {
                    // BeamRetentionPolicy.Ranking.FindBestLane: only nodes whose accumulated route
                    // traits contain the lane compete.
                    if ((facts.Traits & RetainedLanes[lane]) == 0)
                        continue;
                    if (potion.Lanes[lane] < 0
                        || IsBetterLane(facts, _facts[potion.Lanes[lane]], RetainedLanes[lane]))
                    {
                        potion.Lanes[lane] = source;
                    }
                }

                // BeamRetentionPolicy.RankBest: per potion group and persistent setup trait, keep the
                // defensive and setup leaders of each signature (limit max(4, width / 6) groups).
                if (facts.SetupTraits != PersistentSetupTraits.None)
                {
                    (int, PersistentSetupTraits) traitKey = (potionCount, facts.SetupTraits);
                    if (!_traitLeaders.TryGetValue(traitKey, out TraitLeaders? trait))
                    {
                        trait = new TraitLeaders { Position = position, MaxRank = _ranks[source] };
                        _traitLeaders.Add(traitKey, trait);
                    }
                    else if (position < trait.Position)
                    {
                        trait.Position = position;
                    }
                    if (_ranks[source] > trait.MaxRank)
                        trait.MaxRank = _ranks[source];
                    if (trait.Defensive < 0 || IsBetterDefensive(facts, _facts[trait.Defensive]))
                        trait.Defensive = source;
                    if (trait.Setup < 0 || IsBetterSetup(facts, _facts[trait.Setup]))
                        trait.Setup = source;
                }

                // BeamRetentionPolicy.RankBest: per potion group and focus target, keep the pressure
                // and target-setup leaders (limit clamp(width / 10, 2, 4) targets).
                if (facts.FocusTargetCombatId is { } focus)
                {
                    (int, uint?) focusKey = (potionCount, focus);
                    if (!_focusLeaders.TryGetValue(focusKey, out FocusLeaders? leader))
                    {
                        leader = new FocusLeaders { Position = position };
                        _focusLeaders.Add(focusKey, leader);
                    }
                    else if (position < leader.Position)
                    {
                        leader.Position = position;
                    }
                    if (facts.FocusTargetPressure > leader.MaxPressure)
                        leader.MaxPressure = facts.FocusTargetPressure;
                    if (leader.Pressure < 0 || IsBetterTargetPressure(facts, _facts[leader.Pressure]))
                        leader.Pressure = source;
                    if (leader.Setup < 0 || IsBetterTargetSetup(facts, _facts[leader.Setup]))
                        leader.Setup = source;
                }

                ulong lineage = _lineages[source];
                if (!_lineageLeaders.TryGetValue(lineage, out LineageLeaders? line))
                {
                    line = new LineageLeaders { Leader = source };
                    _lineageLeaders.Add(lineage, line);
                }
                if (line.Defensive < 0 || IsBetterDefensive(facts, _facts[line.Defensive]))
                    line.Defensive = source;
                if (line.Offensive < 0 || IsBetterOffensive(facts, _facts[line.Offensive]))
                    line.Offensive = source;
                if (line.Setup < 0 || IsBetterSetup(facts, _facts[line.Setup]))
                    line.Setup = source;
                if (line.SetupCombo < 0 || IsBetterSetupCombo(facts, _facts[line.SetupCombo]))
                    line.SetupCombo = source;
                if (line.Curation < 0 || IsBetterDeckCuration(facts, _facts[line.Curation]))
                    line.Curation = source;
                if (line.Compressed < 0 || IsBetterCompressedDeck(facts, _facts[line.Compressed]))
                    line.Compressed = source;
                if (line.Tactical < 0 || IsBetterTacticalEnabler(facts, _facts[line.Tactical]))
                    line.Tactical = source;
                if (line.TargetPressure < 0
                    || IsBetterTargetPressure(facts, _facts[line.TargetPressure]))
                {
                    line.TargetPressure = source;
                }

                if (bestDefensive < 0 || IsBetterDefensive(facts, _facts[bestDefensive]))
                    bestDefensive = source;
                if (bestOffensive < 0 || IsBetterOffensive(facts, _facts[bestOffensive]))
                    bestOffensive = source;
                if (bestSetup < 0 || IsBetterSetup(facts, _facts[bestSetup]))
                    bestSetup = source;
                if (bestSetupCombo < 0 || IsBetterSetupCombo(facts, _facts[bestSetupCombo]))
                    bestSetupCombo = source;
                if (bestCuration < 0 || IsBetterDeckCuration(facts, _facts[bestCuration]))
                    bestCuration = source;
                if (bestCompressed < 0 || IsBetterCompressedDeck(facts, _facts[bestCompressed]))
                    bestCompressed = source;
                if ((facts.Traits & SearchRouteTraits.LongTermResource) != 0
                    && (bestLongTermResource < 0
                        || IsBetterLane(facts, _facts[bestLongTermResource],
                            SearchRouteTraits.LongTermResource)))
                {
                    bestLongTermResource = source;
                }
                if ((facts.Traits & SearchRouteTraits.HpInvestment) != 0
                    && (bestHpInvestment < 0
                        || IsBetterLane(facts, _facts[bestHpInvestment], SearchRouteTraits.HpInvestment)))
                {
                    bestHpInvestment = source;
                }
                if (facts.Traits != SearchRouteTraits.None
                    && (bestUtilityDefensive < 0
                        || IsBetterUtilityDefensive(facts, _facts[bestUtilityDefensive])))
                {
                    bestUtilityDefensive = source;
                }
                if (_preserveTheft
                    && (bestResourcePreserving < 0
                        || IsBetterResourcePreserving(facts, _facts[bestResourcePreserving])))
                {
                    bestResourcePreserving = source;
                }
                if (potionCount == 0)
                {
                    if (bestPotionFree < 0)
                        bestPotionFree = source;
                    if (bestPotionFreeDefensive < 0
                        || IsBetterDefensive(facts, _facts[bestPotionFreeDefensive]))
                    {
                        bestPotionFreeDefensive = source;
                    }
                    if (bestPotionFreeOffensive < 0
                        || IsBetterOffensive(facts, _facts[bestPotionFreeOffensive]))
                    {
                        bestPotionFreeOffensive = source;
                    }
                    if (bestPotionFreeSetup < 0
                        || IsBetterSetup(facts, _facts[bestPotionFreeSetup]))
                    {
                        bestPotionFreeSetup = source;
                    }
                    if (bestPotionFreeSetupCombo < 0
                        || IsBetterSetupCombo(facts, _facts[bestPotionFreeSetupCombo]))
                    {
                        bestPotionFreeSetupCombo = source;
                    }
                    if (facts.Traits != SearchRouteTraits.None
                        && (bestPotionFreeUtilityDefensive < 0
                            || IsBetterUtilityDefensive(facts, _facts[bestPotionFreeUtilityDefensive])))
                    {
                        bestPotionFreeUtilityDefensive = source;
                    }
                }
                else
                {
                    if (bestPotion < 0)
                        bestPotion = source;
                    if (bestPotionDefensive < 0
                        || IsBetterDefensive(facts, _facts[bestPotionDefensive]))
                    {
                        bestPotionDefensive = source;
                    }
                    if (bestPotionOffensive < 0
                        || IsBetterOffensive(facts, _facts[bestPotionOffensive]))
                    {
                        bestPotionOffensive = source;
                    }
                    if (bestPotionSetup < 0 || IsBetterSetup(facts, _facts[bestPotionSetup]))
                        bestPotionSetup = source;
                    if (bestPotionSetupCombo < 0
                        || IsBetterSetupCombo(facts, _facts[bestPotionSetupCombo]))
                    {
                        bestPotionSetupCombo = source;
                    }
                }
            }

            // BeamRetentionPolicy.RankBest groups by the node's actual potion-use count, not by the
            // slot capacity: a search that generates and consumes additional potions can exceed
            // Limits.PotionSlots uses.
            _potionCountOrder.Clear();
            foreach (int potionCount in _potionLeaders.Keys)
                _potionCountOrder.Add(potionCount);
            CollectionsMarshal.AsSpan(_potionCountOrder).Sort();

            AddRoutingChoiceRepresentatives(required, width);
            AddPotionLineageRepresentatives(required, width);

            for (int potionIndex = 0; potionIndex < _potionCountOrder.Count; potionIndex++)
            {
                int potionCount = _potionCountOrder[potionIndex];
                PotionLeaders potion = _potionLeaders[potionCount];
                AddRequired(required, potion.Leader, width);
                AddRequired(required, potion.Defensive, width);
                AddRequired(required, potion.Offensive, width);
                AddRequired(required, potion.Strength, width);
                AddRequired(required, potion.Weak, width);
                AddRequired(required, potion.Setup, width);
                AddRequired(required, potion.SetupCombo, width);
                AddRequired(required, potion.Tactical, width);
                AddRequired(required, potion.Curation, width);
                AddRequired(required, potion.Compressed, width);
                if (turnBoundary)
                    AddRequired(required, potion.TurnBoundary, width);
                if (_preserveTheft)
                    AddRequired(required, potion.Resource, width);
                for (int lane = 0; lane < potion.Lanes.Length; lane++)
                    AddRequired(required, potion.Lanes[lane], width);
            }

            if (_policyWidth >= 18)
            {
                for (int potionIndex = 0; potionIndex < _potionCountOrder.Count; potionIndex++)
                {
                    int potionCount = _potionCountOrder[potionIndex];
                    if (!_standPatGroups.TryGetValue(potionCount, out List<int>? standPat)
                        || standPat.Count == 0)
                    {
                        continue;
                    }
                    AddRequired(required, FindBestDamagingContinuation(standPat), width);
                    AddRequired(required, FindBestFreshResourceStandPat(standPat), width);
                    AddRequired(required, FindBestStandPat(standPat, SearchRouteTraits.Scaling), width);
                    AddRequired(required, FindBestStandPat(standPat, SearchRouteTraits.Resource), width);
                    AddRequired(required, FindBestStandPat(standPat, SearchRouteTraits.Control), width);
                }
            }

            // BeamRetentionPolicy.RankBest: persistent setup trait groups (Focus, Thunder, OrbEngine,
            // …) keep their defensive and setup leaders; the group limit is max(4, width / 6).
            foreach ((int Potion, PersistentSetupTraits Traits) key in _traitLeaders.Keys)
                _traitOrder.Add(key);
            CollectionsMarshal.AsSpan(_traitOrder).Sort(new TraitOrderComparer(_traitLeaders));
            int traitLimit = Math.Max(4, _policyWidth / 6);
            int traitPotion = -1, traitTaken = 0;
            for (int index = 0; index < _traitOrder.Count; index++)
            {
                (int Potion, PersistentSetupTraits Traits) key = _traitOrder[index];
                if (key.Potion != traitPotion)
                {
                    traitPotion = key.Potion;
                    traitTaken = 0;
                }
                if (traitTaken >= traitLimit)
                    continue;
                TraitLeaders trait = _traitLeaders[key];
                AddRequired(required, trait.Defensive, width);
                AddRequired(required, trait.Setup, width);
                traitTaken++;
            }

            // BeamRetentionPolicy.RankBest: focus target groups (clamp(width / 10, 2, 4) targets per
            // potion group) keep their pressure and target-setup leaders.
            foreach ((int Potion, uint? Target) key in _focusLeaders.Keys)
                _focusOrder.Add(key);
            CollectionsMarshal.AsSpan(_focusOrder).Sort(new FocusOrderComparer(_focusLeaders));
            int focusLimit = Math.Clamp(_policyWidth / 10, 2, 4);
            int focusPotion = -1, focusTaken = 0;
            for (int index = 0; index < _focusOrder.Count; index++)
            {
                (int Potion, uint? Target) key = _focusOrder[index];
                if (key.Potion != focusPotion)
                {
                    focusPotion = key.Potion;
                    focusTaken = 0;
                }
                if (focusTaken >= focusLimit)
                    continue;
                FocusLeaders leader = _focusLeaders[key];
                AddRequired(required, leader.Pressure, width);
                AddRequired(required, leader.Setup, width);
                focusTaken++;
            }

            AddRequired(required, bestPotionFree, width);
            AddRequired(required, bestPotionFreeDefensive, width);
            AddRequired(required, bestPotionFreeOffensive, width);
            AddRequired(required, bestPotionFreeSetup, width);
            AddRequired(required, bestPotionFreeSetupCombo, width);
            AddRequired(required, bestPotionFreeUtilityDefensive, width);
            AddRequired(required, bestPotion, width);
            AddRequired(required, bestPotionDefensive, width);
            AddRequired(required, bestPotionOffensive, width);
            AddRequired(required, bestPotionSetup, width);
            AddRequired(required, bestPotionSetupCombo, width);
            AddRequired(required, bestDefensive, width);
            AddRequired(required, bestUtilityDefensive, width);
            AddRequired(required, bestOffensive, width);
            AddRequired(required, bestSetup, width);
            AddRequired(required, bestSetupCombo, width);
            AddRequired(required, bestCuration, width);
            AddRequired(required, bestCompressed, width);
            AddRequired(required, bestLongTermResource, width);
            AddRequired(required, bestHpInvestment, width);
            if (_preserveTheft)
                AddRequired(required, bestResourcePreserving, width);

            _lineageOrder.Clear();
            foreach (ulong key in _lineageLeaders.Keys)
                _lineageOrder.Add(key);
            CollectionsMarshal.AsSpan(_lineageOrder).Sort(new LineageOrderComparer(_lineageLeaders, _rankPosition));
            int lineageLimit = Math.Clamp(width / 8, 4, 16);
            for (int index = 0; index < _lineageOrder.Count && index < lineageLimit; index++)
            {
                LineageLeaders lineage = _lineageLeaders[_lineageOrder[index]];
                AddRequired(required, lineage.Leader, width);
                AddRequired(required, lineage.Defensive, width);
                AddRequired(required, lineage.Offensive, width);
                AddRequired(required, lineage.Setup, width);
                AddRequired(required, lineage.SetupCombo, width);
                AddRequired(required, lineage.Tactical, width);
                AddRequired(required, lineage.Curation, width);
                AddRequired(required, lineage.Compressed, width);
                AddRequired(required, lineage.TargetPressure, width);
                if (_preserveReplayAllocatorOpening)
                {
                    List<int> candidates = new();
                    foreach (int slot in _rankOrder)
                    {
                        if (_lineages[slot] == _lineageOrder[index])
                            candidates.Add(slot);
                    }
                    AddRequired(required, FindBestCuratedTurnBoundaryHand(candidates), width);
                    int shuffleLeader = -1;
                    for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
                    {
                        int slot = candidates[candidateIndex];
                        if (shuffleLeader < 0
                            || _facts[slot].ProjectedShuffleOrderValue
                                > _facts[shuffleLeader].ProjectedShuffleOrderValue
                            || _facts[slot].ProjectedShuffleOrderValue
                                == _facts[shuffleLeader].ProjectedShuffleOrderValue
                                && _ranks[slot] > _ranks[shuffleLeader])
                        {
                            shuffleLeader = slot;
                        }
                    }
                    AddRequired(required, shuffleLeader, width);
                }
            }
            AddOrderedPileRepresentatives(required, width, turnBoundary);
            AddParetoRepresentatives(required, width);
            if (_trace != null)
            {
                for (int slot = 0; slot < Count; slot++)
                {
                    int targetNode = _nodes[slot];
                    if (!_trace.IsWatched(targetNode))
                        continue;
                    TraceSetupLeader target = SetupLeaderInfo(slot);
                    if (_potionLeaders.TryGetValue(_facts[slot].PotionCount, out PotionLeaders? potion))
                    {
                        if (potion.Setup >= 0)
                        {
                            _trace.SetupLeader("potion.Setup", target, SetupLeaderInfo(potion.Setup),
                                IsBetterSetup(_facts[slot], _facts[potion.Setup]),
                                "SetupValue>RetainedAttackValue>ProjectedHp>Score");
                        }
                        if (potion.SetupCombo >= 0)
                        {
                            _trace.SetupLeader("potion.SetupCombo", target,
                                SetupLeaderInfo(potion.SetupCombo),
                                IsBetterSetupCombo(_facts[slot], _facts[potion.SetupCombo]),
                                "SetupComboValue>RetainedAttackValue>ProjectedHp>Score");
                        }
                    }
                    if (bestSetup >= 0)
                    {
                        _trace.SetupLeader("global.Setup", target, SetupLeaderInfo(bestSetup),
                            IsBetterSetup(_facts[slot], _facts[bestSetup]),
                            "SetupValue>RetainedAttackValue>ProjectedHp>Score");
                    }
                    if (bestSetupCombo >= 0)
                    {
                        _trace.SetupLeader("global.SetupCombo", target,
                            SetupLeaderInfo(bestSetupCombo),
                            IsBetterSetupCombo(_facts[slot], _facts[bestSetupCombo]),
                            "SetupComboValue>RetainedAttackValue>ProjectedHp>Score");
                    }
                    break;
                }
            }
        }

        /// <summary>The existing routing-context window; Art of War widens it to eight rounds.</summary>
        private const int PersistentRoutingContextRounds = 8;

        /// <summary>
        /// <c>BeamRetentionPolicy.TryGetRetainedRoutingChoice</c>: the last accumulated choice is
        /// current when it happened this turn, and stays current for up to eight turns while an
        /// Art of War relic can still trigger on the next turn.
        /// </summary>
        private bool IsRetainedRoutingChoice(int slot)
        {
            RouteNodeStore.RouteNodeView route = _routeNodes.View(_nodes[slot]);
            RetentionFacts facts = _facts[slot];
            if (route.RoutingSignatureId == 0 || route.RoutingChoiceTurn > facts.Turn)
                return false;
            return route.RoutingChoiceTurn == facts.Turn
                || facts.CanTriggerArtOfWarNextTurn
                    && facts.Turn - route.RoutingChoiceTurn <= PersistentRoutingContextRounds;
        }

        /// <summary>
        /// <c>BeamRetentionPolicy.RankBest</c> routing-choice quota and option-leader rotation. Every
        /// option context contributes the leader selected by its actions-since-choice rule (rank leader
        /// at 0, setup leader at 1, target-pressure leader otherwise); the family also contributes its
        /// target-pressure, deck-curation and setup leaders. The first
        /// <c>max(8, 2W/5)</c> distinct candidates become required seats. The Pareto context list and the
        /// direct current-turn extremes are collected after the family leaders.
        /// </summary>
        private void AddRoutingChoiceRepresentatives(List<int> required, int width)
        {
            _routingChoices.Clear();
            _routingGroups.Clear();
            _routingGroupOrder.Clear();
            _routingContextLists.Clear();
            _routingParetoLists.Clear();
            for (int position = 0; position < Count; position++)
            {
                int slot = _rankOrder[position];
                RouteNodeStore.RouteNodeView route = _routeNodes.View(_nodes[slot]);
                int signatureId = route.RoutingSignatureId;
                if (signatureId == 0 || !IsRetainedRoutingChoice(slot))
                    continue;
                if (!_routingGroups.TryGetValue(signatureId, out RoutingGroup? group))
                {
                    group = new RoutingGroup();
                    _routingGroups.Add(signatureId, group);
                    _routingGroupOrder.Add(signatureId);
                }
                group.Nodes.Add(slot);
                if (group.BestScore < 0 || _ranks[slot] > _ranks[group.BestScore])
                    group.BestScore = slot;
                if (group.BestOffense < 0
                    || IsBetterOffensive(_facts[slot], _facts[group.BestOffense]))
                {
                    group.BestOffense = slot;
                }
                if (group.BestDefense < 0
                    || IsBetterDefensive(_facts[slot], _facts[group.BestDefense]))
                {
                    group.BestDefense = slot;
                }
                if (group.BestSetup < 0 || IsBetterSetup(_facts[slot], _facts[group.BestSetup]))
                    group.BestSetup = slot;
                if (group.BestPileOrder < 0
                    || _facts[slot].ProjectedShuffleOrderValue
                        > _facts[group.BestPileOrder].ProjectedShuffleOrderValue
                    || _facts[slot].ProjectedShuffleOrderValue
                        == _facts[group.BestPileOrder].ProjectedShuffleOrderValue
                        && IsBetterSearchNode(_facts[slot], _facts[group.BestPileOrder]))
                {
                    group.BestPileOrder = slot;
                }
                group.MinParentRank = Math.Min(group.MinParentRank, route.RoutingParentRank);
                group.MaxParentScore = Math.Max(group.MaxParentScore, route.RoutingParentScore);
                group.MaxBeamRank = Math.Max(group.MaxBeamRank, _ranks[slot]);
            }
            if (_routingGroupOrder.Count == 0)
                return;

            _routingFamilies.Clear();
            _routingFamilyOrder.Clear();
            for (int index = 0; index < _routingGroupOrder.Count; index++)
            {
                int signatureId = _routingGroupOrder[index];
                RoutingSignature signature = _routingSignatures[signatureId - 1];
                RoutingFamilyKey key = new(signature.Turn, signature.SourceId,
                    (int)signature.Effect, signature.SourcePile);
                if (!_routingFamilies.TryGetValue(key, out RoutingBucket? family))
                {
                    family = new RoutingBucket();
                    _routingFamilies.Add(key, family);
                    _routingFamilyOrder.Add(key);
                }
                family.Groups.Add(signatureId);
                RoutingGroup group = _routingGroups[signatureId];
                family.MinParentRank = Math.Min(family.MinParentRank, group.MinParentRank);
                family.MaxParentScore = Math.Max(family.MaxParentScore, group.MaxParentScore);
                family.MaxBeamRank = Math.Max(family.MaxBeamRank, group.MaxBeamRank);
            }
            CollectionsMarshal.AsSpan(_routingFamilyOrder)
                .Sort(new RoutingBucketComparer<RoutingFamilyKey>(_routingFamilies));
            for (int familyIndex = 0; familyIndex < _routingFamilyOrder.Count; familyIndex++)
            {
                RoutingBucket family = _routingFamilies[_routingFamilyOrder[familyIndex]];
                _routingOptionOrder.Clear();
                _routingOptions.Clear();
                _routingFamilyNodes.Clear();
                for (int groupIndex = 0; groupIndex < family.Groups.Count; groupIndex++)
                {
                    int signatureId = family.Groups[groupIndex];
                    RoutingSignature signature = _routingSignatures[signatureId - 1];
                    RoutingOptionKey optionKey = new(signature.CardId, signature.Upgrade,
                        signature.CardStateKey, signature.CardCount);
                    if (!_routingOptions.TryGetValue(optionKey, out RoutingBucket? option))
                    {
                        option = new RoutingBucket();
                        _routingOptions.Add(optionKey, option);
                        _routingOptionOrder.Add(optionKey);
                    }
                    option.Groups.Add(signatureId);
                    RoutingGroup group = _routingGroups[signatureId];
                    option.MinParentRank = Math.Min(option.MinParentRank, group.MinParentRank);
                    option.MaxParentScore = Math.Max(option.MaxParentScore, group.MaxParentScore);
                    option.MaxBeamRank = Math.Max(option.MaxBeamRank, group.MaxBeamRank);
                    for (int nodeIndex = 0; nodeIndex < group.Nodes.Count; nodeIndex++)
                        _routingFamilyNodes.Add(group.Nodes[nodeIndex]);
                }
                AddRoutingCandidate(PreferMostVulnerableTargetVariant(_routingFamilyNodes,
                    FindBestTargetPressure(_routingFamilyNodes)));
                AddRoutingCandidate(FindBestDeckCuration(_routingFamilyNodes));
                int routingFamilySetup = FindBestSetup(_routingFamilyNodes);
                TraceSetupLeader("routingFamily.SetupCombo", routingFamilySetup);
                AddRoutingCandidate(routingFamilySetup);

                CollectionsMarshal.AsSpan(_routingOptionOrder)
                    .Sort(new RoutingBucketComparer<RoutingOptionKey>(_routingOptions));
                for (int optionIndex = 0; optionIndex < _routingOptionOrder.Count; optionIndex++)
                {
                    RoutingBucket option = _routingOptions[_routingOptionOrder[optionIndex]];
                    _routingOptionNodes.Clear();
                    int actionsSinceChoice = int.MaxValue;
                    for (int groupIndex = 0; groupIndex < option.Groups.Count; groupIndex++)
                    {
                        RoutingGroup group = _routingGroups[option.Groups[groupIndex]];
                        for (int nodeIndex = 0; nodeIndex < group.Nodes.Count; nodeIndex++)
                        {
                            int slot = group.Nodes[nodeIndex];
                            _routingOptionNodes.Add(slot);
                            RouteNodeStore.RouteNodeView route = _routeNodes.View(_nodes[slot]);
                            actionsSinceChoice = Math.Min(actionsSinceChoice,
                                _facts[slot].Depth - route.RoutingChoiceDepth);
                        }
                    }
                    int leader;
                    if (actionsSinceChoice == 0)
                    {
                        int contextId = option.Groups[0];
                        RoutingGroup context = _routingGroups[contextId];
                        for (int groupIndex = 1; groupIndex < option.Groups.Count; groupIndex++)
                        {
                            RoutingGroup other = _routingGroups[option.Groups[groupIndex]];
                            if (other.MinParentRank < context.MinParentRank
                                || other.MinParentRank == context.MinParentRank
                                    && other.MaxParentScore > context.MaxParentScore)
                            {
                                context = other;
                            }
                        }
                        leader = context.BestScore;
                    }
                    else if (actionsSinceChoice == 1)
                    {
                        leader = FindBestSetup(_routingOptionNodes);
                    }
                    else
                    {
                        leader = PreferMostVulnerableTargetVariant(_routingOptionNodes,
                            FindBestTargetPressure(_routingOptionNodes));
                    }
                    AddRoutingCandidate(leader);
                }
                _routingContextLists.Add(OrderRoutingFamilyContexts(family));
            }
            AppendDirectRoutingChoiceExtremes();
            AppendRoutingChoiceContextPareto();
            int quota = Math.Max(8, _policyWidth * 2 / 5);
            for (int index = 0; index < _routingChoices.Count && index < quota; index++)
                AddRequired(required, _routingChoices[index], width);

            // BeamRetentionPolicy.RankBest: per potion count, the defensive and setup leaders among
            // nodes whose Art of War window can still trigger next turn.
            for (int potionIndex = 0; potionIndex < _potionCountOrder.Count; potionIndex++)
            {
                int potionCount = _potionCountOrder[potionIndex];
                _standPatGroup.Clear();
                int defensive = -1, setup = -1;
                for (int position = 0; position < Count; position++)
                {
                    int slot = _rankOrder[position];
                    RetentionFacts facts = _facts[slot];
                    if (facts.PotionCount != potionCount || !facts.CanTriggerArtOfWarNextTurn)
                        continue;
                    _standPatGroup.Add(slot);
                    if (defensive < 0 || IsBetterDefensive(facts, _facts[defensive]))
                        defensive = slot;
                    if (setup < 0 || IsBetterSetupCombo(facts, _facts[setup]))
                        setup = slot;
                }
                AddRequired(required, defensive, width);
                AddRequired(required, setup, width);
            }
        }

        private int FindBestCuratedTurnBoundaryHand(List<int> nodes)
        {
            int best = -1;
            for (int index = 0; index < nodes.Count; index++)
            {
                int node = nodes[index];
                if (best < 0 || IsBetterCuratedTurnBoundaryHand(_facts[node], _facts[best]))
                    best = node;
            }
            return best;
        }

        /// <summary>
        /// <c>BeamRetentionPolicy.OrderRoutingChoiceEventContexts</c>: option groups ordered by
        /// (min parent rank, parent score, beam rank); contexts inside each option in the same order;
        /// blocks of at most eight contexts interleaved across options.
        /// </summary>
        private List<int> OrderRoutingFamilyContexts(RoutingBucket family)
        {
            _routingContextNodes.Clear();
            _routingOptions.Clear();
            _routingOptionOrder.Clear();
            for (int groupIndex = 0; groupIndex < family.Groups.Count; groupIndex++)
            {
                int signatureId = family.Groups[groupIndex];
                RoutingSignature signature = _routingSignatures[signatureId - 1];
                RoutingOptionKey optionKey = new(signature.CardId, signature.Upgrade,
                    signature.CardStateKey, signature.CardCount);
                if (!_routingOptions.TryGetValue(optionKey, out RoutingBucket? option))
                {
                    option = new RoutingBucket();
                    _routingOptions.Add(optionKey, option);
                    _routingOptionOrder.Add(optionKey);
                }
                option.Groups.Add(signatureId);
                RoutingGroup group = _routingGroups[signatureId];
                option.MinParentRank = Math.Min(option.MinParentRank, group.MinParentRank);
                option.MaxParentScore = Math.Max(option.MaxParentScore, group.MaxParentScore);
                option.MaxBeamRank = Math.Max(option.MaxBeamRank, group.MaxBeamRank);
            }
            CollectionsMarshal.AsSpan(_routingOptionOrder)
                .Sort(new RoutingBucketComparer<RoutingOptionKey>(_routingOptions));
            List<List<int>> optionGroups = new(_routingOptionOrder.Count);
            for (int optionIndex = 0; optionIndex < _routingOptionOrder.Count; optionIndex++)
            {
                RoutingBucket option = _routingOptions[_routingOptionOrder[optionIndex]];
                option.Groups.Sort(new RoutingGroupComparer(this));
                optionGroups.Add(option.Groups);
            }
            List<int> ordered = new(family.Groups.Count);
            for (int roundStart = 0; ; roundStart += PersistentRoutingContextRounds)
            {
                bool any = false;
                for (int optionIndex = 0; optionIndex < optionGroups.Count; optionIndex++)
                {
                    List<int> group = optionGroups[optionIndex];
                    int roundEnd = Math.Min(group.Count, roundStart + PersistentRoutingContextRounds);
                    for (int index = roundStart; index < roundEnd; index++)
                        ordered.Add(group[index]);
                    any |= roundEnd > roundStart;
                }
                if (!any)
                    break;
            }
            return ordered;
        }

        private readonly struct RoutingGroupComparer : IComparer<int>
        {
            private readonly Pool _pool;

            public RoutingGroupComparer(Pool pool) => _pool = pool;

            public int Compare(int left, int right)
            {
                RoutingGroup a = _pool._routingGroups[left];
                RoutingGroup b = _pool._routingGroups[right];
                int comparison = a.MinParentRank.CompareTo(b.MinParentRank);
                if (comparison != 0) return comparison;
                comparison = b.MaxParentScore.CompareTo(a.MaxParentScore);
                if (comparison != 0) return comparison;
                return b.MaxBeamRank.CompareTo(a.MaxBeamRank);
            }
        }

        /// <summary>
        /// <c>BeamRetentionPolicy.RankBest</c>'s routing-context Pareto: every ordered context keeps
        /// its safe discard continuation (discard contexts), its short/long-horizon leaders, the first
        /// 16 rank-ordered members and the context's score/offense/defense/ordered-pile leaders, minus
        /// dominated members. The per-context lists are interleaved into the routing quota.
        /// </summary>
        private void AppendRoutingChoiceContextPareto()
        {
            _routingParetoLists.Clear();
            int rounds = 0;
            for (int index = 0; index < _routingContextLists.Count; index++)
                rounds = Math.Max(rounds, _routingContextLists[index].Count);
            _routingContextOrder.Clear();
            for (int round = 0; round < rounds; round++)
            {
                for (int family = 0; family < _routingContextLists.Count; family++)
                {
                    List<int> contexts = _routingContextLists[family];
                    if (round < contexts.Count)
                        _routingContextOrder.Add(contexts[round]);
                }
            }
            for (int contextIndex = 0; contextIndex < _routingContextOrder.Count; contextIndex++)
                _routingParetoLists.Add(BuildRoutingContextPareto(_routingContextOrder[contextIndex]));
            int paretoRound = 0;
            while (_routingChoices.Count < RoutingChoiceLimit)
            {
                bool any = false;
                for (int listIndex = 0; listIndex < _routingParetoLists.Count; listIndex++)
                {
                    List<int> pareto = _routingParetoLists[listIndex];
                    if (paretoRound >= pareto.Count)
                        continue;
                    any = true;
                    AddRoutingCandidate(pareto[paretoRound]);
                    if (_routingChoices.Count >= RoutingChoiceLimit)
                        break;
                }
                if (!any)
                    break;
                paretoRound++;
            }
        }

        private List<int> BuildRoutingContextPareto(int signatureId)
        {
            RoutingGroup group = _routingGroups[signatureId];
            List<int> nodes = group.Nodes;
            List<int> candidates = _routingParetoCandidates;
            candidates.Clear();
            RoutingSignature signature = _routingSignatures[signatureId - 1];
            if (signature.Effect == ChoiceEffect.Discard)
                AddContextCandidate(candidates, FindBestSafeDiscardContinuation(nodes));
            int actionsSince = int.MaxValue;
            for (int index = 0; index < nodes.Count; index++)
            {
                RouteNodeStore.RouteNodeView route = _routeNodes.View(_nodes[nodes[index]]);
                actionsSince = Math.Min(actionsSince, _facts[nodes[index]].Depth - route.RoutingChoiceDepth);
            }
            int bestTarget = PreferMostVulnerableTargetVariant(nodes, FindBestTargetPressure(nodes));
            if (actionsSince <= 1)
            {
                AddContextCandidate(candidates, group.BestSetup);
                AddContextCandidate(candidates, bestTarget);
            }
            else
            {
                AddContextCandidate(candidates, bestTarget);
                AddContextCandidate(candidates, FindBestDeckCuration(nodes));
                AddContextCandidate(candidates, group.BestSetup);
            }
            for (int index = 0; index < nodes.Count && index < 16; index++)
                AddContextCandidate(candidates, nodes[index]);
            AddContextCandidate(candidates, group.BestScore);
            AddContextCandidate(candidates, group.BestOffense);
            AddContextCandidate(candidates, group.BestDefense);
            AddContextCandidate(candidates, group.BestPileOrder);
            List<int> pareto = new(candidates.Count);
            for (int index = 0; index < candidates.Count; index++)
            {
                int candidate = candidates[index];
                bool dominated = false;
                for (int other = 0; other < candidates.Count; other++)
                {
                    int comparison = candidates[other];
                    if (comparison != candidate && ParetoDominates(comparison, candidate))
                    {
                        dominated = true;
                        break;
                    }
                }
                if (!dominated)
                    pareto.Add(candidate);
            }
            return pareto;
        }

        private static void AddContextCandidate(List<int> candidates, int candidate)
        {
            if (candidate < 0 || candidates.Contains(candidate))
                return;
            candidates.Add(candidate);
        }

        /// <summary>
        /// <c>BeamRetentionPolicy.BuildDirectRoutingChoiceExtremes</c>: a current-turn choice whose
        /// parent had no energy left. Within one parent state only the widest/hardest-hitting/strongest
        /// variant survives; families keep at most 96 parents and all families together at most 96
        /// candidates in (parent rank, parent beam score) order.
        /// </summary>
        private void AppendDirectRoutingChoiceExtremes()
        {
            _directByParent.Clear();
            _directFamilies.Clear();
            _directFamilyStats.Clear();
            _directOrder.Clear();
            for (int position = 0; position < Count; position++)
            {
                int slot = _rankOrder[position];
                RouteNodeStore.RouteNodeView route = _routeNodes.View(_nodes[slot]);
                RetentionFacts facts = _facts[slot];
                if (route.RoutingSignatureId == 0 || route.Parent < 0
                    || route.RoutingParentEnergy != 0
                    || route.RoutingChoiceDepth != facts.Depth
                    || route.RoutingChoiceTurn != facts.Turn)
                {
                    continue;
                }
                RoutingSignature signature = _routingSignatures[route.RoutingSignatureId - 1];
                RoutingFamilyKey family = new(signature.Turn, signature.SourceId,
                    (int)signature.Effect, signature.SourcePile);
                int parentKey = route.Parent;
                if (!_directByParent.TryGetValue(parentKey, out int current))
                    _directByParent.Add(parentKey, slot);
                else if (IsBetterDirectChoice(slot, current, signature))
                    _directByParent[parentKey] = slot;
                if (!_directFamilies.TryGetValue(family, out List<int>? familyNodes))
                {
                    familyNodes = new List<int>();
                    _directFamilies.Add(family, familyNodes);
                    _directOrder.Add(family);
                }
                familyNodes.Add(slot);
                if (_directFamilyStats.TryGetValue(family, out var stats))
                {
                    _directFamilyStats[family] = (Math.Min(stats.MinRank, route.RoutingParentRank),
                        Math.Max(stats.MaxScore, route.RoutingParentScore));
                }
                else
                {
                    _directFamilyStats.Add(family,
                        (route.RoutingParentRank, route.RoutingParentScore));
                }
            }
            if (_directByParent.Count == 0)
                return;
            CollectionsMarshal.AsSpan(_directOrder).Sort(new DirectFamilyComparer(_directFamilyStats));
            List<int> chosen = new(_directByParent.Count);
            for (int familyIndex = 0; familyIndex < _directOrder.Count; familyIndex++)
            {
                List<int> familyNodes = _directFamilies[_directOrder[familyIndex]];
                List<int> perParent = new();
                for (int index = 0; index < familyNodes.Count; index++)
                {
                    int slot = familyNodes[index];
                    if (_directByParent[_routeNodes.View(_nodes[slot]).Parent] == slot)
                        perParent.Add(slot);
                }
                perParent.Sort(new DirectParentComparer(this));
                for (int index = 0; index < perParent.Count && index < RoutingChoiceLimit; index++)
                    chosen.Add(perParent[index]);
            }
            chosen.Sort(new DirectCandidateComparer(this));
            for (int index = 0; index < chosen.Count && _routingChoices.Count < RoutingChoiceLimit; index++)
                AddRoutingCandidate(chosen[index]);
        }

        private bool IsBetterDirectChoice(int candidate, int current, in RoutingSignature signature)
        {
            int candidateCardinality = signature.CardCount > 1 ? signature.CardCount : 1;
            RoutingSignature currentSignature =
                _routingSignatures[_routeNodes.View(_nodes[current]).RoutingSignatureId - 1];
            int currentCardinality = currentSignature.CardCount > 1 ? currentSignature.CardCount : 1;
            if (candidateCardinality != currentCardinality)
                return candidateCardinality > currentCardinality;
            long candidateDensity = _facts[candidate].AttackDensity;
            long currentDensity = _facts[current].AttackDensity;
            if (candidateDensity != currentDensity)
                return candidateDensity > currentDensity;
            return _ranks[candidate] > _ranks[current];
        }

        private readonly struct DirectFamilyComparer : IComparer<RoutingFamilyKey>
        {
            private readonly Dictionary<RoutingFamilyKey, (int MinRank, double MaxScore)> _stats;

            public DirectFamilyComparer(
                Dictionary<RoutingFamilyKey, (int MinRank, double MaxScore)> stats) => _stats = stats;

            public int Compare(RoutingFamilyKey left, RoutingFamilyKey right)
            {
                (int MinRank, double MaxScore) a = _stats[left];
                (int MinRank, double MaxScore) b = _stats[right];
                int comparison = a.MinRank.CompareTo(b.MinRank);
                return comparison != 0 ? comparison : b.MaxScore.CompareTo(a.MaxScore);
            }
        }

        private readonly struct DirectParentComparer : IComparer<int>
        {
            private readonly Pool _pool;

            public DirectParentComparer(Pool pool) => _pool = pool;

            public int Compare(int left, int right)
            {
                RouteNodeStore.RouteNodeView a = _pool._routeNodes.View(_pool._nodes[left]);
                RouteNodeStore.RouteNodeView b = _pool._routeNodes.View(_pool._nodes[right]);
                int comparison = a.RoutingParentRank.CompareTo(b.RoutingParentRank);
                return comparison != 0 ? comparison : b.RoutingParentScore.CompareTo(a.RoutingParentScore);
            }
        }

        private readonly struct DirectCandidateComparer : IComparer<int>
        {
            private readonly Pool _pool;

            public DirectCandidateComparer(Pool pool) => _pool = pool;

            public int Compare(int left, int right)
            {
                RouteNodeStore.RouteNodeView a = _pool._routeNodes.View(_pool._nodes[left]);
                RouteNodeStore.RouteNodeView b = _pool._routeNodes.View(_pool._nodes[right]);
                int comparison = a.RoutingParentRank.CompareTo(b.RoutingParentRank);
                return comparison != 0 ? comparison : b.RoutingParentScore.CompareTo(a.RoutingParentScore);
            }
        }

        /// <summary>
        /// <c>BeamRetentionPolicy.FindBestSafeDiscardContinuation</c>: a non-terminal, non-turn-ending
        /// play after a retained discard context that keeps projected HP at least at the current HP,
        /// preferring projected HP, offensive progress, reachable hand value and the offensive
        /// comparator.
        /// </summary>
        private int FindBestSafeDiscardContinuation(List<int> nodes)
        {
            int best = -1;
            for (int index = 0; index < nodes.Count; index++)
            {
                int node = nodes[index];
                RetentionFacts facts = _facts[node];
                RouteNodeStore.RouteNodeView route = _routeNodes.View(_nodes[node]);
                if (facts.AllEnemiesDead || facts.PlayerDead || route.Parent < 0
                    || route.Action.Kind != PlayerActionKind.PlayCard
                    || facts.Turn != route.ParentTurn
                    || facts.Depth - route.RoutingChoiceDepth < 1
                    || facts.ProjectedPlayerHp < facts.PlayerHp)
                {
                    continue;
                }
                if (best < 0)
                {
                    best = node;
                    continue;
                }
                RetentionFacts current = _facts[best];
                if (facts.ProjectedPlayerHp > current.ProjectedPlayerHp
                    || facts.ProjectedPlayerHp == current.ProjectedPlayerHp
                        && (facts.OffensiveProgressValue > current.OffensiveProgressValue
                            || facts.OffensiveProgressValue == current.OffensiveProgressValue
                                && (facts.ReachableHandValue > current.ReachableHandValue
                                    || facts.ReachableHandValue == current.ReachableHandValue
                                        && IsBetterOffensive(facts, current))))
                {
                    best = node;
                }
            }
            return best;
        }

        private readonly List<int> _routingFamilyNodes = new();
        private readonly List<int> _routingOptionNodes = new();
        private readonly List<List<int>> _routingContextLists = new();
        private readonly List<List<int>> _routingParetoLists = new();
        private readonly List<int> _routingContextOrder = new();
        private readonly List<int> _routingParetoCandidates = new();
        private readonly List<RoutingFamilyKey> _directOrder = new();
        private readonly Dictionary<int, int> _directByParent = new();
        private readonly Dictionary<RoutingFamilyKey, List<int>> _directFamilies = new();
        private readonly Dictionary<RoutingFamilyKey, (int MinRank, double MaxScore)> _directFamilyStats = new();
        private readonly List<int> _routingContextNodes = new();

        private void AddRoutingCandidate(int candidate)
        {
            if (candidate < 0 || _routingChoices.Count >= RoutingChoiceLimit
                || _routingChoices.Contains(candidate))
            {
                return;
            }
            _routingChoices.Add(candidate);
        }

        private int FindBestTargetPressure(List<int> nodes)
        {
            int best = -1;
            for (int index = 0; index < nodes.Count; index++)
            {
                int node = nodes[index];
                if (best < 0 || IsBetterTargetPressure(_facts[node], _facts[best]))
                    best = node;
            }
            return best;
        }

        private int FindBestDeckCuration(List<int> nodes)
        {
            int best = -1;
            for (int index = 0; index < nodes.Count; index++)
            {
                int node = nodes[index];
                if (best < 0 || IsBetterDeckCuration(_facts[node], _facts[best]))
                    best = node;
            }
            return best;
        }

        private int FindBestSetup(List<int> nodes)
        {
            int best = -1;
            for (int index = 0; index < nodes.Count; index++)
            {
                int node = nodes[index];
                if (best < 0 || IsBetterSetupCombo(_facts[node], _facts[best]))
                    best = node;
            }
            return best;
        }

        private int PreferMostVulnerableTargetVariant(List<int> nodes, int candidate)
        {
            if (candidate < 0)
                return candidate;
            RouteNodeStore.RouteNodeView candidateRoute = _routeNodes.View(_nodes[candidate]);
            if (candidateRoute.TargetCombatId == int.MinValue)
                return candidate;
            int preferred = -1;
            for (int index = 0; index < nodes.Count; index++)
            {
                int node = nodes[index];
                RouteNodeStore.RouteNodeView route = _routeNodes.View(_nodes[node]);
                if (route.Action.Kind != candidateRoute.Action.Kind
                    || route.ActionCardId != candidateRoute.ActionCardId
                    || route.PotionId != candidateRoute.PotionId)
                {
                    continue;
                }
                if (_facts[node].MostVulnerableTargetCombatId != route.TargetCombatId)
                    continue;
                if (preferred < 0 || _ranks[node] > _ranks[preferred])
                    preferred = node;
            }
            return preferred >= 0 ? preferred : candidate;
        }

        private readonly struct RoutingBucketComparer<TKey> : IComparer<TKey>
            where TKey : notnull
        {
            private readonly Dictionary<TKey, RoutingBucket> _buckets;

            public RoutingBucketComparer(Dictionary<TKey, RoutingBucket> buckets) => _buckets = buckets;

            public int Compare(TKey? left, TKey? right)
            {
                RoutingBucket a = _buckets[left!];
                RoutingBucket b = _buckets[right!];
                int comparison = a.MinParentRank.CompareTo(b.MinParentRank);
                if (comparison != 0) return comparison;
                comparison = b.MaxParentScore.CompareTo(a.MaxParentScore);
                if (comparison != 0) return comparison;
                return b.MaxBeamRank.CompareTo(a.MaxBeamRank);
            }
        }

        /// <summary>
        /// <c>BeamRetentionPolicy.MultiObjectiveDominates</c> over the K4-checked retention facts.
        /// The original compares the three distribution fingerprints as a cohort gate; Sts2Solver has no
        /// fingerprint projection on the route node, so the cohort is the equivalent coarse tuple of
        /// the enemy combat/control and live-pile aggregates. All dominance dimensions are exact facts.
        /// </summary>
        private ParetoCohortKey BuildParetoCohort(int slot)
        {
            RetentionFacts f = _facts[slot];
            return new ParetoCohortKey(f.EnemyCombatDistributionFirst, f.EnemyCombatDistributionSecond,
                f.EnemyControlDistributionFirst, f.EnemyControlDistributionSecond,
                f.UnorderedPileKeyFirst, f.UnorderedPileKeySecond);
        }

        private bool ParetoDominates(int left, int right)
        {
            if (left == right)
                return false;
            // BeamRetentionPolicy.MultiObjectiveDominates: different combat/control/pile cohorts are
            // never comparable.
            if (BuildParetoCohort(left) != BuildParetoCohort(right))
                return false;
            RetentionFacts a = _facts[left];
            RetentionFacts b = _facts[right];
            bool noWorse = a.ProjectedPlayerHp >= b.ProjectedPlayerHp
                && a.PlayerMaxHp >= b.PlayerMaxHp
                && a.CumulativePlayerHpLost <= b.CumulativePlayerHpLost
                && a.LongTermResourceValue >= b.LongTermResourceValue
                && a.StrategicHpCredit >= b.StrategicHpCredit
                && (a.RelicSatisfiedMask & b.RelicSatisfiedMask) == b.RelicSatisfiedMask
                && a.StrategyGoalCount >= b.StrategyGoalCount
                && a.AngerCopiesGenerated <= b.AngerCopiesGenerated
                && (!_preserveTheft || a.OutstandingStolenResource <= b.OutstandingStolenResource)
                && a.AliveEnemyCount <= b.AliveEnemyCount
                && a.EnemyHp <= b.EnemyHp
                && a.RawEnemyHp <= b.RawEnemyHp
                && a.MaxCurrentEnemyHp <= b.MaxCurrentEnemyHp
                && a.PersistentBuffValue >= b.PersistentBuffValue
                && a.LatentSetupValue >= b.LatentSetupValue
                && a.DelayedDamageValue >= b.DelayedDamageValue
                && a.ReactiveDamageValue >= b.ReactiveDamageValue
                && a.EnemyStrengthSuppression >= b.EnemyStrengthSuppression
                && a.EnemyWeakTurns >= b.EnemyWeakTurns
                && a.EnemyVulnerableTurns >= b.EnemyVulnerableTurns
                && a.FocusTargetVulnerableTurns >= b.FocusTargetVulnerableTurns
                && a.Energy >= b.Energy
                && a.Stars >= b.Stars
                && a.FutureResourceValue >= b.FutureResourceValue
                && a.OstyHp >= b.OstyHp
                && a.OstyMaxHp >= b.OstyMaxHp
                && a.RetainedAttackGrowth >= b.RetainedAttackGrowth
                && a.ReplayPotentialValue >= b.ReplayPotentialValue
                && a.FocusTargetPressure >= b.FocusTargetPressure
                && a.SandpitRemaining >= b.SandpitRemaining
                && a.LiveDeckClutter <= b.LiveDeckClutter
                && a.LiveDeckSize <= b.LiveDeckSize
                && a.PotionCount <= b.PotionCount
                && a.PotionStrategicCost <= b.PotionStrategicCost
                && a.FutureSoldHp <= b.FutureSoldHp
                && a.Depth <= b.Depth;
            if (!noWorse)
                return false;
            return a.ProjectedPlayerHp > b.ProjectedPlayerHp
                || a.PlayerMaxHp > b.PlayerMaxHp
                || a.CumulativePlayerHpLost < b.CumulativePlayerHpLost
                || a.LongTermResourceValue > b.LongTermResourceValue
                || a.AngerCopiesGenerated < b.AngerCopiesGenerated
                || _preserveTheft && a.OutstandingStolenResource < b.OutstandingStolenResource
                || a.AliveEnemyCount < b.AliveEnemyCount
                || a.EnemyHp < b.EnemyHp
                || a.RawEnemyHp < b.RawEnemyHp
                || a.MaxCurrentEnemyHp < b.MaxCurrentEnemyHp
                || a.PersistentBuffValue > b.PersistentBuffValue
                || a.LatentSetupValue > b.LatentSetupValue
                || a.DelayedDamageValue > b.DelayedDamageValue
                || a.ReactiveDamageValue > b.ReactiveDamageValue
                || a.EnemyStrengthSuppression > b.EnemyStrengthSuppression
                || a.EnemyWeakTurns > b.EnemyWeakTurns
                || a.EnemyVulnerableTurns > b.EnemyVulnerableTurns
                || a.FocusTargetVulnerableTurns > b.FocusTargetVulnerableTurns
                || a.Energy > b.Energy
                || a.Stars > b.Stars
                || a.FutureResourceValue > b.FutureResourceValue
                || a.OstyHp > b.OstyHp
                || a.OstyMaxHp > b.OstyMaxHp
                || a.RetainedAttackGrowth > b.RetainedAttackGrowth
                || a.ReplayPotentialValue > b.ReplayPotentialValue
                || a.FocusTargetPressure > b.FocusTargetPressure
                || a.SandpitRemaining > b.SandpitRemaining
                || a.LiveDeckClutter < b.LiveDeckClutter
                || a.LiveDeckSize < b.LiveDeckSize
                || a.PotionCount < b.PotionCount
                || a.PotionStrategicCost < b.PotionStrategicCost
                || a.FutureSoldHp < b.FutureSoldHp
                || a.Depth < b.Depth;
        }

        /// <summary>
        /// <c>CardChoiceSupport.BuildAutomaticPolicyChoice</c>: <c>min(MinCount, option count)</c>
        /// options ordered by card value (ascending for hand sources, descending otherwise), ties by
        /// (card id, upgrade, card-state hash) ordinal.
        /// </summary>
        private bool TryAutomaticTurnStartChoice(in Battle battle, in ChoiceRequest request,
            out ulong selection)
        {
            selection = 0;
            int count = Math.Min(request.MinCount, request.OptionCount);
            if (count <= 0)
                return true;
            Span<(double Value, int Id, int Upgrade, ulong Hash)> options =
                stackalloc (double, int, int, ulong)[request.OptionCount];
            for (int index = 0; index < request.OptionCount; index++)
            {
                Card card = request.OptionsAreGenerated
                    ? request.GeneratedOptions[index]
                    : battle.Cards[request.Options[index]];
                options[index] = (_scorer.AutomaticChoiceValue(battle, card), card.Id, card.Upgrade,
                    Fingerprint.HashCard(card));
            }
            bool ascending = !request.OptionsAreGenerated && request.Source == Pile.Hand;
            Span<bool> taken = stackalloc bool[request.OptionCount];
            // stackalloc memory is not zero-initialized; without this clear a stale true byte skips
            // every option and the probe appends an empty selection for a 1-of-5 request.
            taken.Clear();
            for (int picked = 0; picked < count; picked++)
            {
                int best = -1;
                for (int index = 0; index < request.OptionCount; index++)
                {
                    if (taken[index])
                        continue;
                    if (best < 0 || IsBetterAutomaticChoice(options[index], options[best], ascending))
                        best = index;
                }
                if (best < 0)
                    break;
                taken[best] = true;
                selection |= 1UL << best;
            }
            return true;
        }

        private static bool IsBetterAutomaticChoice(
            (double Value, int Id, int Upgrade, ulong Hash) candidate,
            (double Value, int Id, int Upgrade, ulong Hash) current, bool ascending)
        {
            int comparison = candidate.Value.CompareTo(current.Value);
            if (!ascending)
                comparison = -comparison;
            if (comparison != 0)
                return comparison < 0;
            if (candidate.Id != current.Id)
                return candidate.Id < current.Id;
            if (candidate.Upgrade != current.Upgrade)
                return candidate.Upgrade < current.Upgrade;
            return candidate.Hash < current.Hash;
        }

        /// <summary>
        /// The existing search's <c>ComputeStandPat</c> (<c>StandPatJobs</c>/<c>Retention</c>): replay EndTurn on the retained state and read the
        /// resulting state's resources. Sts2Solver has no simulator; the kernel step is the same end-turn
        /// semantics. A request for a turn-start choice makes the probe unavailable (default), which is
        /// the one boundary the existing roll-out resolves through its choice policy.
        /// </summary>
        private StandPatEvaluation EvaluateStandPat(int slot)
        {
            Unsafe.SkipInit(out Battle end);
            Battle.CopyPacked(in _states[slot], ref end);
            try
            {
                // The existing stand-pat roll-out answers an unprepared turn-start selection with the
                // CardChoiceSupport automatic policy (ascending hand values, descending draw/discard/
                // generated values), then continues the same EndTurn replay. The probe set is thus the
                // one the existing search sees instead of a boundary-only subset.
                PlayerAction action = PlayerAction.EndTurn();
                StepResult result;
                while (true)
                {
                    result = Kernel.Step(ref end, action, _scorer.RulesForRetention,
                        out ChoiceRequest request);
                    if (result != StepResult.NeedsChoice)
                        break;
                    if (request.Timing != ChoiceTiming.PlayerTurnStart
                        || action.ChoiceCount >= Limits.MaxChoices
                        || !TryAutomaticTurnStartChoice(end, request, out ulong automaticSelection))
                        return default;
                    action = action.WithChoice(automaticSelection);
                    Battle.CopyPacked(in _states[slot], ref end);
                }
                RetentionFacts before = _facts[slot];
                ScoreFeatures features = _scorer.Features(end, before.Depth + 1, includeRetentionOnly: true);
                return new StandPatEvaluation(
                    features.AllEnemiesDead,
                    Math.Max(0, before.EnemyHp - features.EnemyHp),
                    features.ProjectedPlayerHp,
                    features.Energy * 16
                        + features.Stars * 8
                        + features.HandCount
                        + features.ReachableHandValue
                        + features.FutureResourceValue
                        + features.OstyHp * 16
                        + features.OstyMaxHp * 4,
                    true);
            }
            catch (CapacityException)
            {
                return default;
            }
        }

        /// <summary><c>FindBestDamagingContinuation</c>: a shield-breaking playable step with a legal
        /// follow-up attack inside the frozen reachable hand value.</summary>
        private int FindBestDamagingContinuation(List<int> nodes)
        {
            int best = -1;
            for (int index = 0; index < nodes.Count; index++)
            {
                int node = nodes[index];
                RetentionFacts facts = _facts[node];
                RouteNodeStore.RouteNodeView route = _routeNodes.View(_nodes[node]);
                if (facts.AllEnemiesDead || facts.PlayerDead || route.Parent < 0
                    || route.Action.Kind != PlayerActionKind.PlayCard
                    || facts.EnemyBlock >= facts.ParentEnemyBlock
                    || facts.EnemyHp > facts.ReachableHandValue
                    || (long)facts.EnemyHp + facts.EnemyBlock
                        >= (long)facts.ParentEnemyHp + facts.ParentEnemyBlock
                    || facts.PlayableAttackCount <= 0)
                {
                    continue;
                }
                if (best < 0 || IsBetterOffensive(facts, _facts[best]))
                    best = node;
            }
            return best;
        }

        /// <summary><c>FindBestFreshResourceStandPat</c>: the first 64 rank-ordered fresh-resource
        /// candidates, evaluated with the same EndTurn stand-pat replay and comparator.</summary>
        private int FindBestFreshResourceStandPat(List<int> nodes)
        {
            const int probeLimit = 64;
            int best = -1;
            StandPatEvaluation bestEvaluation = default;
            int evaluated = 0;
            for (int index = 0; index < nodes.Count && evaluated < probeLimit; index++)
            {
                int node = nodes[index];
                RetentionFacts facts = _facts[node];
                RouteNodeStore.RouteNodeView route = _routeNodes.View(_nodes[node]);
                if (route.Parent < 0
                    || (facts.FutureResourceValue <= facts.ParentFutureResourceValue
                        && facts.StrategicResourcePotential <= facts.ParentStrategicResourcePotential))
                {
                    continue;
                }
                StandPatEvaluation evaluation = EvaluateStandPat(node);
                evaluated++;
                if (best < 0
                    || evaluation.AllEnemiesDead && !bestEvaluation.AllEnemiesDead
                    || evaluation.AllEnemiesDead == bestEvaluation.AllEnemiesDead
                        && (evaluation.ProjectedPlayerHp > bestEvaluation.ProjectedPlayerHp
                            || evaluation.ProjectedPlayerHp == bestEvaluation.ProjectedPlayerHp
                                && (evaluation.ResourceValue > bestEvaluation.ResourceValue
                                    || evaluation.ResourceValue == bestEvaluation.ResourceValue
                                        && facts.CumulativePlayerHpLost < _facts[best].CumulativePlayerHpLost
                                    || evaluation.ResourceValue == bestEvaluation.ResourceValue
                                        && facts.CumulativePlayerHpLost == _facts[best].CumulativePlayerHpLost
                                        && facts.Score > _facts[best].Score)))
                {
                    best = node;
                    bestEvaluation = evaluation;
                }
            }
            return best;
        }

        /// <summary><c>FindBestStandPat</c>: the same probe ordering and evaluation comparator.</summary>
        private int FindBestStandPat(List<int> nodes, SearchRouteTraits trait)
        {
            const int limit = 8;
            Span<int> probes = stackalloc int[limit + 1];
            int probeCount = 0;
            while (probeCount < limit)
            {
                int best = -1;
                for (int index = 0; index < nodes.Count; index++)
                {
                    int node = nodes[index];
                    RetentionFacts facts = _facts[node];
                    if ((facts.Traits & trait) == 0 || ContainsProbe(probes, probeCount, node))
                        continue;
                    if (best < 0 || IsBetterStandPatProbe(facts, _facts[best], trait))
                        best = node;
                }
                if (best < 0)
                    break;
                probes[probeCount++] = best;
            }
            if (trait == SearchRouteTraits.Control && probeCount > 0)
            {
                int delayedFinish = -1;
                for (int index = 0; index < nodes.Count; index++)
                {
                    int node = nodes[index];
                    RetentionFacts facts = _facts[node];
                    if ((facts.Traits & trait) == 0 || facts.AllEnemiesDead || facts.EnemyHp <= 0
                        || facts.DelayedDamageValue < facts.EnemyHp
                        || ContainsProbe(probes, probeCount, node))
                    {
                        continue;
                    }
                    if (delayedFinish < 0 || IsBetterDelayedFinish(facts, _facts[delayedFinish]))
                        delayedFinish = node;
                }
                if (delayedFinish >= 0)
                {
                    if (probeCount == limit)
                        probeCount--;
                    probes[probeCount++] = delayedFinish;
                }
            }
            if (_trace != null)
            {
                for (int index = 0; index < nodes.Count; index++)
                {
                    int node = nodes[index];
                    if (!_trace.IsWatched(node) || ContainsProbe(probes, probeCount, node))
                        continue;
                    RetentionFacts facts = _facts[node];
                    _trace.StandPatProbe(node, selected: false, available: true,
                        facts.AllEnemiesDead, facts.DelayedDamageValue, facts.ProjectedPlayerHp,
                        LaneValue(facts, SearchRouteTraits.Resource),
                        LaneValue(facts, trait), facts.Score, chosen: false);
                }
            }
            int bestNode = -1;
            StandPatEvaluation bestEvaluation = default;
            for (int index = 0; index < probeCount; index++)
            {
                int node = probes[index];
                StandPatEvaluation evaluation = EvaluateStandPat(node);
                int evaluationValue = trait == SearchRouteTraits.Resource
                    ? evaluation.ResourceValue
                    : evaluation.DelayedDamage;
                int bestValue = trait == SearchRouteTraits.Resource
                    ? bestEvaluation.ResourceValue
                    : bestEvaluation.DelayedDamage;
                if (bestNode < 0
                    || evaluation.AllEnemiesDead && !bestEvaluation.AllEnemiesDead
                    || evaluation.AllEnemiesDead == bestEvaluation.AllEnemiesDead
                        && (evaluation.ProjectedPlayerHp > bestEvaluation.ProjectedPlayerHp
                            || evaluation.ProjectedPlayerHp == bestEvaluation.ProjectedPlayerHp
                                && (evaluationValue > bestValue
                                    || evaluationValue == bestValue
                                        && _facts[node].Score > _facts[bestNode].Score)))
                {
                    bestNode = node;
                    bestEvaluation = evaluation;
                }
            }
            if (_trace != null)
            {
                for (int index = 0; index < probeCount; index++)
                {
                    int node = probes[index];
                    if (!_trace.IsWatched(node))
                        continue;
                    StandPatEvaluation evaluation = EvaluateStandPat(node);
                    _trace.StandPatProbe(node, selected: true, evaluation.Available,
                        evaluation.AllEnemiesDead, evaluation.DelayedDamage, evaluation.ProjectedPlayerHp,
                        evaluation.ResourceValue, LaneValue(_facts[node], trait), _facts[node].Score,
                        node == bestNode);
                }
            }
            return bestNode;
        }

        private static bool ContainsProbe(Span<int> probes, int count, int node)
        {
            for (int index = 0; index < count; index++)
            {
                if (probes[index] == node)
                    return true;
            }
            return false;
        }

        private static bool IsBetterStandPatProbe(in RetentionFacts candidate,
            in RetentionFacts current, SearchRouteTraits trait)
        {
            if (candidate.ProjectedPlayerHp != current.ProjectedPlayerHp)
                return candidate.ProjectedPlayerHp > current.ProjectedPlayerHp;
            int candidateLane = LaneValue(candidate, trait);
            int currentLane = LaneValue(current, trait);
            if (candidateLane != currentLane)
                return candidateLane > currentLane;
            return candidate.Score > current.Score;
        }

        private static bool IsBetterDelayedFinish(in RetentionFacts candidate,
            in RetentionFacts current)
        {
            if (candidate.CumulativePlayerHpLost != current.CumulativePlayerHpLost)
                return candidate.CumulativePlayerHpLost < current.CumulativePlayerHpLost;
            if (candidate.PlayerHp != current.PlayerHp)
                return candidate.PlayerHp > current.PlayerHp;
            if (candidate.EnemyHp != current.EnemyHp)
                return candidate.EnemyHp < current.EnemyHp;
            return candidate.Score > current.Score;
        }

        /// <summary>
        /// <c>OrderedMutationRetention.BuildOrderedPileTacticalKey</c> over the K4-checked facts. Sts2Solver
        /// has no per-node shuffle-crossed/boundary fields in the retention facts; both are constant
        /// for a ranked pool (a pool is one turn layer), so they add no grouping distinction here.
        /// </summary>
        private readonly record struct OrderedPileTacticalKey(
            int Turn, int PotionCount, int PotionCost, int FutureSoldHp, int PlayerHp,
            int ProjectedPlayerHp, int PlayerBlock, int EnemyHp, int RawEnemyHp, int MaxCurrentEnemyHp,
            ulong EnemyCombatFirst, ulong EnemyCombatSecond, ulong AliveEnemyMask, int RevivingEnemyCount,
            uint? FocusTarget, int PersistentBuffValue, PersistentSetupTraits StrategySetupTraits,
            int FutureResourceValue, int DelayedDamageValue, int EnemyStrengthSuppression,
            int EnemyWeakTurns, int EnemyVulnerableTurns, int FocusTargetVulnerableTurns,
            ulong EnemyControlFirst, ulong EnemyControlSecond, int SandpitRemaining, int LiveDeckClutter,
            int OutstandingStolenResource, int Energy, int Stars, int HandCount,
            int PocketwatchCardsPlayedThisTurn, int PocketwatchCardsPlayedLastTurn,
            int PocketwatchCardThreshold, ulong UnorderedPileFirst, ulong UnorderedPileSecond);

        private OrderedPileTacticalKey BuildOrderedPileTacticalKey(int slot)
        {
            RetentionFacts f = _facts[slot];
            return new OrderedPileTacticalKey(f.Turn, f.PotionCount, f.PotionStrategicCost,
                f.FutureSoldHp, f.PlayerHp, f.ProjectedPlayerHp, f.PlayerBlock, f.EnemyHp, f.RawEnemyHp,
                f.MaxCurrentEnemyHp, f.EnemyCombatDistributionFirst, f.EnemyCombatDistributionSecond,
                f.AliveEnemyMask, f.RevivingEnemyCount, f.FocusTargetCombatId, f.PersistentBuffValue,
                f.StrategySetupTraits, f.FutureResourceValue, f.DelayedDamageValue,
                f.EnemyStrengthSuppression, f.EnemyWeakTurns, f.EnemyVulnerableTurns,
                f.FocusTargetVulnerableTurns, f.EnemyControlDistributionFirst,
                f.EnemyControlDistributionSecond, f.SandpitRemaining, f.LiveDeckClutter,
                f.OutstandingStolenResource, f.Energy, f.Stars, f.HandCount,
                f.PocketwatchCardsPlayedThisTurn, f.PocketwatchCardsPlayedLastTurn,
                f.PocketwatchCardThreshold, f.UnorderedPileKeyFirst, f.UnorderedPileKeySecond);
        }

        private readonly record struct PocketwatchCadenceKey(
            int PotionCount, uint? FocusTarget, int RetainedAttackGrowth,
            ulong ControlFirst, ulong ControlSecond, bool WithinThreshold, bool CanStillTrigger);

        private PocketwatchCadenceKey BuildPocketwatchCadenceKey(int slot)
        {
            RetentionFacts f = _facts[slot];
            return new PocketwatchCadenceKey(f.PotionCount, f.FocusTargetCombatId, f.RetainedAttackGrowth,
                f.EnemyControlDistributionFirst, f.EnemyControlDistributionSecond,
                f.PocketwatchCardThreshold >= 0
                    && f.PocketwatchCardsPlayedLastTurn <= f.PocketwatchCardThreshold,
                f.PocketwatchCardThreshold >= 0
                    && f.PocketwatchCardsPlayedThisTurn <= f.PocketwatchCardThreshold);
        }

        private readonly record struct PocketwatchCadenceFamilyKey(
            int PotionCount, uint? FocusTarget, int RetainedAttackGrowth, bool WithinThreshold,
            bool CanStillTrigger);

        private PocketwatchCadenceFamilyKey BuildPocketwatchCadenceFamilyKey(int slot)
        {
            PocketwatchCadenceKey key = BuildPocketwatchCadenceKey(slot);
            return new PocketwatchCadenceFamilyKey(key.PotionCount, key.FocusTarget,
                key.RetainedAttackGrowth, key.WithinThreshold, key.CanStillTrigger);
        }

        private sealed class OrderedPileTacticalGroup
        {
            public readonly List<int> Nodes = new();
            public double MaxRank = double.NegativeInfinity;
            public int Position;
        }

        private sealed class PocketwatchCadenceBucket
        {
            public readonly List<OrderedPileTacticalGroup> Groups = new();
            public double MaxRank = double.NegativeInfinity;
            public int Position;
        }

        private sealed class PocketwatchCadenceFamily
        {
            public readonly List<PocketwatchCadenceBucket> Buckets = new();
            public double MaxRank = double.NegativeInfinity;
            public int Position;
        }

        private readonly List<List<int>> _orderedPileCohorts = new();
        private readonly List<int> _orderedPileBuffer = new();
        private readonly Dictionary<OrderedPileTacticalKey, OrderedPileTacticalGroup> _tacticalGroups = new();
        private readonly List<OrderedPileTacticalKey> _tacticalOrder = new();
        private readonly Dictionary<PocketwatchCadenceKey, PocketwatchCadenceBucket> _cadenceBuckets = new();
        private readonly List<PocketwatchCadenceKey> _cadenceOrder = new();
        private readonly Dictionary<PocketwatchCadenceFamilyKey, PocketwatchCadenceFamily> _cadenceFamilies = new();
        private readonly List<PocketwatchCadenceFamilyKey> _cadenceFamilyOrder = new();

        /// <summary>
        /// <c>BeamRetentionPolicy.RankBest</c>'s ordered-pile portfolio: tactical state groups by
        /// max rank, cadence buckets/families by pocketwatch state, Pareto representatives per
        /// cadence, then bounded exact projected-shuffle-order variants per selected tactical group.
        /// The representatives replace ordinary seats (the Sts2Solver beam width is unchanged).
        /// </summary>
        private void AddOrderedPileRepresentatives(List<int> required, int width, bool turnBoundary)
        {
            bool anyPocketwatch = false;
            for (int position = 0; position < Count; position++)
            {
                if (_facts[_rankOrder[position]].PocketwatchCardThreshold >= 0)
                {
                    anyPocketwatch = true;
                    break;
                }
            }
            if (!anyPocketwatch)
                return;
            _tacticalGroups.Clear();
            _tacticalOrder.Clear();
            for (int position = 0; position < Count; position++)
            {
                int slot = _rankOrder[position];
                if (_facts[slot].PocketwatchCardThreshold < 0)
                    continue;
                OrderedPileTacticalKey key = BuildOrderedPileTacticalKey(slot);
                if (!_tacticalGroups.TryGetValue(key, out OrderedPileTacticalGroup? group))
                {
                    group = new OrderedPileTacticalGroup { Position = _tacticalOrder.Count };
                    _tacticalGroups.Add(key, group);
                    _tacticalOrder.Add(key);
                }
                group.Nodes.Add(slot);
                group.MaxRank = Math.Max(group.MaxRank, _ranks[slot]);
            }
            if (_tacticalOrder.Count == 0)
                return;
            _tacticalOrder.Sort(new TacticalGroupComparer(_tacticalGroups));
            List<OrderedPileTacticalGroup> tacticalGroups = new(_tacticalOrder.Count);
            for (int index = 0; index < _tacticalOrder.Count; index++)
                tacticalGroups.Add(_tacticalGroups[_tacticalOrder[index]]);

            _cadenceBuckets.Clear();
            _cadenceOrder.Clear();
            for (int index = 0; index < tacticalGroups.Count; index++)
            {
                OrderedPileTacticalGroup group = tacticalGroups[index];
                PocketwatchCadenceKey key = BuildPocketwatchCadenceKey(group.Nodes[0]);
                if (!_cadenceBuckets.TryGetValue(key, out PocketwatchCadenceBucket? bucket))
                {
                    bucket = new PocketwatchCadenceBucket { Position = _cadenceOrder.Count };
                    _cadenceBuckets.Add(key, bucket);
                    _cadenceOrder.Add(key);
                }
                bucket.Groups.Add(group);
                bucket.MaxRank = Math.Max(bucket.MaxRank, group.MaxRank);
            }
            _cadenceOrder.Sort(new CadenceBucketComparer(_cadenceBuckets));
            List<PocketwatchCadenceBucket> buckets = new(_cadenceOrder.Count);
            for (int index = 0; index < _cadenceOrder.Count; index++)
                buckets.Add(_cadenceBuckets[_cadenceOrder[index]]);

            _cadenceFamilies.Clear();
            _cadenceFamilyOrder.Clear();
            for (int index = 0; index < buckets.Count; index++)
            {
                PocketwatchCadenceBucket bucket = buckets[index];
                PocketwatchCadenceFamilyKey key =
                    BuildPocketwatchCadenceFamilyKey(bucket.Groups[0].Nodes[0]);
                if (!_cadenceFamilies.TryGetValue(key, out PocketwatchCadenceFamily? family))
                {
                    family = new PocketwatchCadenceFamily { Position = _cadenceFamilyOrder.Count };
                    _cadenceFamilies.Add(key, family);
                    _cadenceFamilyOrder.Add(key);
                }
                family.Buckets.Add(bucket);
                family.MaxRank = Math.Max(family.MaxRank, bucket.MaxRank);
            }
            _cadenceFamilyOrder.Sort(new CadenceFamilyComparer(_cadenceFamilies));
            buckets.Clear();
            int familyRound = 0;
            while (true)
            {
                bool any = false;
                for (int index = 0; index < _cadenceFamilyOrder.Count; index++)
                {
                    PocketwatchCadenceFamily family = _cadenceFamilies[_cadenceFamilyOrder[index]];
                    if (familyRound < family.Buckets.Count)
                    {
                        buckets.Add(family.Buckets[familyRound]);
                        any = true;
                    }
                }
                if (!any)
                    break;
                familyRound++;
            }

            List<List<OrderedPileTacticalGroup>> paretoByCadence = new(buckets.Count);
            for (int index = 0; index < buckets.Count; index++)
                paretoByCadence.Add(BuildCadencePareto(buckets[index]));

            _orderedPileCohorts.Clear();
            int paretoRound = 0;
            while (true)
            {
                bool any = false;
                for (int index = 0; index < paretoByCadence.Count; index++)
                {
                    List<OrderedPileTacticalGroup> pareto = paretoByCadence[index];
                    if (paretoRound >= pareto.Count)
                        continue;
                    any = true;
                    BuildOrderedPileCohort(pareto[paretoRound]);
                }
                if (!any)
                    break;
                paretoRound++;
            }
            if (_orderedPileCohorts.Count == 0)
                return;

            int limit = Math.Max(1, _policyWidth);
            int orderedPileQuota = Math.Min(width, Math.Max(8, limit * 2 / 3));
            int orderedPileRounds = 0;
            for (int index = 0; index < _orderedPileCohorts.Count; index++)
                orderedPileRounds = Math.Max(orderedPileRounds, _orderedPileCohorts[index].Count);
            int quota = turnBoundary
                ? orderedPileQuota
                : Math.Min(width, limit + 1);
            int exactStateRounds = Math.Min(
                ScoreWeights.ExactStatesPerProjectedShuffleOrder, orderedPileRounds);
            for (int round = 0; round < exactStateRounds && required.Count < quota; round++)
            {
                for (int index = 0; index < _orderedPileCohorts.Count; index++)
                {
                    List<int> cohort = _orderedPileCohorts[index];
                    if (round < cohort.Count)
                        AddRequired(required, cohort[round], quota);
                }
            }
            for (int round = exactStateRounds; round < orderedPileRounds && required.Count < quota; round++)
            {
                for (int index = 0; index < _orderedPileCohorts.Count; index++)
                {
                    List<int> cohort = _orderedPileCohorts[index];
                    if (round < cohort.Count)
                        AddRequired(required, cohort[round], quota);
                }
            }
        }

        private List<OrderedPileTacticalGroup> BuildCadencePareto(PocketwatchCadenceBucket bucket)
        {
            List<OrderedPileTacticalGroup> candidates = new();
            AddTacticalGroup(candidates, bucket.Groups[0]);
            AddTacticalGroup(candidates, BestTacticalGroupBy(bucket,
                slot => _facts[slot].ProjectedPlayerHp, descending: true));
            AddTacticalGroup(candidates, BestTacticalGroupBy(bucket,
                slot => _facts[slot].EnemyHp, descending: false));
            AddTacticalGroup(candidates, BestTacticalGroupBy(bucket,
                slot => _facts[slot].AliveEnemyCount, descending: false));
            AddTacticalGroup(candidates, BestTacticalGroupBy(bucket,
                slot => LaneValue(_facts[slot], SearchRouteTraits.Control), descending: true));
            AddTacticalGroup(candidates, BestTacticalGroupBy(bucket,
                slot => LaneValue(_facts[slot], SearchRouteTraits.Resource), descending: true));
            AddTacticalGroup(candidates, BestTacticalGroupBy(bucket,
                slot => _facts[slot].ProjectedShuffleOrderValue, descending: true));
            for (int index = 0; index < bucket.Groups.Count
                && candidates.Count < ScoreWeights.PocketwatchParetoCandidatesPerCadence; index++)
            {
                AddTacticalGroup(candidates, bucket.Groups[index]);
            }
            List<OrderedPileTacticalGroup> pareto = new(candidates.Count);
            for (int index = 0; index < candidates.Count; index++)
            {
                OrderedPileTacticalGroup candidate = candidates[index];
                bool dominated = false;
                for (int other = 0; other < candidates.Count; other++)
                {
                    OrderedPileTacticalGroup comparison = candidates[other];
                    if (!ReferenceEquals(comparison, candidate)
                        && ParetoDominates(comparison.Nodes[0], candidate.Nodes[0]))
                    {
                        dominated = true;
                        break;
                    }
                }
                if (!dominated)
                    pareto.Add(candidate);
            }
            return pareto;
        }

        private OrderedPileTacticalGroup BestTacticalGroupBy(PocketwatchCadenceBucket bucket,
            Func<int, int> selector, bool descending)
        {
            OrderedPileTacticalGroup best = bucket.Groups[0];
            int bestValue = GroupExtreme(best, selector, descending);
            double bestRank = best.MaxRank;
            for (int index = 1; index < bucket.Groups.Count; index++)
            {
                OrderedPileTacticalGroup candidate = bucket.Groups[index];
                int value = GroupExtreme(candidate, selector, descending);
                bool better = descending ? value > bestValue : value < bestValue;
                if (better || value == bestValue && candidate.MaxRank > bestRank)
                {
                    best = candidate;
                    bestValue = value;
                    bestRank = candidate.MaxRank;
                }
            }
            return best;
        }

        private int GroupExtreme(OrderedPileTacticalGroup group, Func<int, int> selector,
            bool descending)
        {
            int value = selector(group.Nodes[0]);
            for (int index = 1; index < group.Nodes.Count; index++)
            {
                int candidate = selector(group.Nodes[index]);
                value = descending ? Math.Max(value, candidate) : Math.Min(value, candidate);
            }
            return value;
        }

        private static void AddTacticalGroup(List<OrderedPileTacticalGroup> groups,
            OrderedPileTacticalGroup group)
        {
            if (groups.Contains(group))
                return;
            groups.Add(group);
        }

        private void BuildOrderedPileCohort(OrderedPileTacticalGroup group)
        {
            _orderedPileBuffer.Clear();
            List<int> variants = new();
            List<(ulong First, ulong Second)> prefixes = new();
            for (int index = 0; index < group.Nodes.Count; index++)
            {
                int slot = group.Nodes[index];
                RetentionFacts facts = _facts[slot];
                (ulong First, ulong Second) key =
                    (facts.ProjectedShuffleOrderKeyFirst, facts.ProjectedShuffleOrderKeySecond);
                if (!prefixes.Contains(key))
                    prefixes.Add(key);
            }
            for (int prefix = 0; prefix < prefixes.Count; prefix++)
            {
                List<int> variantsForPrefix = new();
                for (int index = 0; index < group.Nodes.Count; index++)
                {
                    int slot = group.Nodes[index];
                    if ((_facts[slot].ProjectedShuffleOrderKeyFirst,
                            _facts[slot].ProjectedShuffleOrderKeySecond) != prefixes[prefix])
                    {
                        continue;
                    }
                    variantsForPrefix.Add(slot);
                }
                variantsForPrefix.Sort(new OrderedPileVariantComparer(this));
                for (int index = 0; index < variantsForPrefix.Count
                    && index < ScoreWeights.ExactStatesPerProjectedShuffleOrder; index++)
                {
                    variants.Add(variantsForPrefix[index]);
                }
            }
            variants.Sort(new OrderedPileVariantComparer(this));
            int take = Math.Min(ScoreWeights.OrderedPileVariantsPerTacticalState, variants.Count);
            List<int> cohort = new(take);
            for (int index = 0; index < take; index++)
                cohort.Add(variants[index]);
            _orderedPileCohorts.Add(cohort);
        }

        private readonly struct TacticalGroupComparer : IComparer<OrderedPileTacticalKey>
        {
            private readonly Dictionary<OrderedPileTacticalKey, OrderedPileTacticalGroup> _groups;

            public TacticalGroupComparer(
                Dictionary<OrderedPileTacticalKey, OrderedPileTacticalGroup> groups) => _groups = groups;

            public int Compare(OrderedPileTacticalKey left, OrderedPileTacticalKey right)
            {
                OrderedPileTacticalGroup a = _groups[left];
                OrderedPileTacticalGroup b = _groups[right];
                int comparison = b.MaxRank.CompareTo(a.MaxRank);
                return comparison != 0 ? comparison : a.Position.CompareTo(b.Position);
            }
        }

        private readonly struct CadenceBucketComparer : IComparer<PocketwatchCadenceKey>
        {
            private readonly Dictionary<PocketwatchCadenceKey, PocketwatchCadenceBucket> _buckets;

            public CadenceBucketComparer(
                Dictionary<PocketwatchCadenceKey, PocketwatchCadenceBucket> buckets) => _buckets = buckets;

            public int Compare(PocketwatchCadenceKey left, PocketwatchCadenceKey right)
            {
                PocketwatchCadenceBucket a = _buckets[left];
                PocketwatchCadenceBucket b = _buckets[right];
                int comparison = b.MaxRank.CompareTo(a.MaxRank);
                return comparison != 0 ? comparison : a.Position.CompareTo(b.Position);
            }
        }

        private readonly struct CadenceFamilyComparer : IComparer<PocketwatchCadenceFamilyKey>
        {
            private readonly Dictionary<PocketwatchCadenceFamilyKey, PocketwatchCadenceFamily> _families;

            public CadenceFamilyComparer(
                Dictionary<PocketwatchCadenceFamilyKey, PocketwatchCadenceFamily> families)
                => _families = families;

            public int Compare(PocketwatchCadenceFamilyKey left, PocketwatchCadenceFamilyKey right)
            {
                PocketwatchCadenceFamily a = _families[left];
                PocketwatchCadenceFamily b = _families[right];
                int comparison = b.MaxRank.CompareTo(a.MaxRank);
                return comparison != 0 ? comparison : a.Position.CompareTo(b.Position);
            }
        }

        private readonly struct OrderedPileVariantComparer : IComparer<int>
        {
            private readonly Pool _pool;

            public OrderedPileVariantComparer(Pool pool) => _pool = pool;

            public int Compare(int left, int right)
            {
                int comparison = _pool._facts[right].ProjectedShuffleOrderValue
                    .CompareTo(_pool._facts[left].ProjectedShuffleOrderValue);
                return comparison != 0 ? comparison : _pool._ranks[right].CompareTo(_pool._ranks[left]);
            }
        }

        private void AddParetoRepresentatives(List<int> required, int width)
        {
            if (_policyWidth < 18)
                return;
            _paretoCohorts.Clear();
            for (int position = 0; position < Count; position++)
            {
                int slot = _rankOrder[position];
                ParetoCohortKey key = BuildParetoCohort(slot);
                if (!_paretoCohorts.TryGetValue(key, out List<int>? cohort))
                {
                    cohort = new List<int>();
                    _paretoCohorts.Add(key, cohort);
                }
                cohort.Add(slot);
            }
            _pareto.Clear();
            for (int position = 0; position < Count; position++)
            {
                int slot = _rankOrder[position];
                List<int> cohort = _paretoCohorts[BuildParetoCohort(slot)];
                bool dominated = false;
                for (int index = 0; index < cohort.Count; index++)
                {
                    int other = cohort[index];
                    if (other != slot && ParetoDominates(other, slot))
                    {
                        dominated = true;
                        break;
                    }
                }
                if (dominated)
                    continue;
                int insert = 0;
                while (insert < _pareto.Count
                    && (_scores[_pareto[insert]] > _scores[slot]
                        || _scores[_pareto[insert]].Equals(_scores[slot])
                            && _facts[_pareto[insert]].Depth <= _facts[slot].Depth))
                {
                    insert++;
                }
                if (insert >= 3)
                    continue;
                _pareto.Insert(insert, slot);
                if (_pareto.Count > 3)
                    _pareto.RemoveAt(3);
            }
            for (int index = 0; index < _pareto.Count; index++)
                AddRequired(required, _pareto[index], width);
        }

        /// <summary>
        /// <c>BeamRetentionPolicy.RankBest</c> potion-use lineage groups: for every potion count, the
        /// best-ranked <c>clamp(width / 6, 2, 6)</c> distinct use-multisets each keep one representative.
        /// </summary>
        private void AddPotionLineageRepresentatives(List<int> required, int width)
        {
            if (!_checkPotionQuota)
                return;
            int potionLineageLimit = Math.Clamp(_policyWidth / 6, 2, 6);
            for (int potionIndex = 0; potionIndex < _potionCountOrder.Count; potionIndex++)
            {
                int potionCount = _potionCountOrder[potionIndex];
                if (potionCount == 0)
                    continue;
                _potionLineageGroups.Clear();
                _potionLineageOrder.Clear();
                for (int position = 0; position < Count; position++)
                {
                    int source = _rankOrder[position];
                    if (_facts[source].PotionCount != potionCount)
                        continue;
                    int key = PotionUseLineageKey(_nodes[source]);
                    if (!_potionLineageGroups.TryGetValue(key, out PotionLineageLeaders? leader))
                    {
                        leader = new PotionLineageLeaders { Position = position };
                        _potionLineageGroups.Add(key, leader);
                        _potionLineageOrder.Add(key);
                    }
                    leader.Nodes.Add(source);
                }
                if (_potionLineageOrder.Count == 0)
                    continue;
                foreach (int key in _potionLineageOrder)
                {
                    PotionLineageLeaders leader = _potionLineageGroups[key];
                    double maxRank = double.NegativeInfinity;
                    for (int index = 0; index < leader.Nodes.Count; index++)
                        maxRank = Math.Max(maxRank, _ranks[leader.Nodes[index]]);
                    leader.MaxRank = maxRank;
                }
                CollectionsMarshal.AsSpan(_potionLineageOrder)
                    .Sort(new PotionLineageOrderComparer(_potionLineageGroups));
                for (int index = 0;
                     index < _potionLineageOrder.Count && index < potionLineageLimit; index++)
                {
                    AddRequired(required,
                        FindBestPotionLineage(_potionLineageGroups[_potionLineageOrder[index]].Nodes), width);
                }
            }
        }

        private sealed class PotionLineageLeaders
        {
            public readonly List<int> Nodes = new();
            public int Position;
            public double MaxRank;
        }

        private readonly struct PotionLineageOrderComparer : IComparer<int>
        {
            private readonly Dictionary<int, PotionLineageLeaders> _groups;

            public PotionLineageOrderComparer(Dictionary<int, PotionLineageLeaders> groups)
                => _groups = groups;

            public int Compare(int left, int right)
            {
                PotionLineageLeaders a = _groups[left];
                PotionLineageLeaders b = _groups[right];
                int comparison = b.MaxRank.CompareTo(a.MaxRank);
                return comparison != 0 ? comparison : a.Position.CompareTo(b.Position);
            }
        }

        /// <summary><c>BeamRetentionPolicy.Ranking.PotionUseLineageKey</c>: the sorted explicit potion
        /// id multiset along the route, interned exactly when the node is created.</summary>
        private int PotionUseLineageKey(int node) => _routeNodes.PotionLineageKeyAt(node);

        /// <summary><c>BeamRetentionPolicy.Ranking.FindBestPotionLineage</c>.</summary>
        private int FindBestPotionLineage(List<int> nodes)
        {
            int best = -1;
            for (int index = 0; index < nodes.Count; index++)
            {
                int node = nodes[index];
                RetentionFacts facts = _facts[node];
                if (best < 0)
                {
                    best = node;
                    continue;
                }
                RetentionFacts current = _facts[best];
                if (facts.AllEnemiesDead && !current.AllEnemiesDead
                    || facts.AllEnemiesDead == current.AllEnemiesDead
                        && (facts.ProjectedPlayerHp > current.ProjectedPlayerHp
                            || facts.ProjectedPlayerHp == current.ProjectedPlayerHp
                                && (facts.EnemyHp < current.EnemyHp
                                    || facts.EnemyHp == current.EnemyHp
                                        && facts.Score > current.Score)))
                {
                    best = node;
                }
            }
            return best;
        }

        /// <summary>
        /// BeamRetentionPolicy.Ranking.IsBetterResourcePreserving: fewer outstanding stolen resources,
        /// then higher projected HP, then a higher score.
        /// </summary>
        private static bool IsBetterResourcePreserving(in RetentionFacts candidate, in RetentionFacts current)
            => candidate.OutstandingStolenResource < current.OutstandingStolenResource
                || candidate.OutstandingStolenResource == current.OutstandingStolenResource
                    && (candidate.ProjectedPlayerHp > current.ProjectedPlayerHp
                        || candidate.ProjectedPlayerHp == current.ProjectedPlayerHp
                            && candidate.Score > current.Score);

        private readonly struct RankOrderComparer : IComparer<int>
        {
            private readonly ChunkedArray<double> _ranks;
            private readonly ChunkedArray<RetentionFacts> _facts;

            public RankOrderComparer(ChunkedArray<double> ranks, ChunkedArray<RetentionFacts> facts)
            {
                _ranks = ranks;
                _facts = facts;
            }

            public int Compare(int left, int right)
            {
                // the existing search's CompareBeamRankOrder: rank, then fewer actions, then offensive progress.
                int comparison = _ranks[right].CompareTo(_ranks[left]);
                if (comparison != 0)
                    return comparison;
                comparison = _facts[left].Depth.CompareTo(_facts[right].Depth);
                if (comparison != 0)
                    return comparison;
                comparison = _facts[right].OffensiveProgressValue.CompareTo(_facts[left].OffensiveProgressValue);
                return comparison != 0 ? comparison : left.CompareTo(right);
            }
        }

        private readonly struct RankPositionComparer : IComparer<int>
        {
            private readonly int[] _rankPosition;

            public RankPositionComparer(int[] rankPosition) => _rankPosition = rankPosition;

            public int Compare(int left, int right) => _rankPosition[left].CompareTo(_rankPosition[right]);
        }

        private readonly struct TraitOrderComparer
            : IComparer<(int Potion, PersistentSetupTraits Traits)>
        {
            private readonly Dictionary<(int Potion, PersistentSetupTraits Traits), TraitLeaders> _leaders;

            public TraitOrderComparer(
                Dictionary<(int Potion, PersistentSetupTraits Traits), TraitLeaders> leaders)
                => _leaders = leaders;

            public int Compare((int Potion, PersistentSetupTraits Traits) left,
                (int Potion, PersistentSetupTraits Traits) right)
            {
                int comparison = left.Potion.CompareTo(right.Potion);
                if (comparison != 0)
                    return comparison;
                TraitLeaders a = _leaders[left];
                TraitLeaders b = _leaders[right];
                comparison = b.MaxRank.CompareTo(a.MaxRank);
                return comparison != 0 ? comparison : a.Position.CompareTo(b.Position);
            }
        }

        private readonly struct FocusOrderComparer : IComparer<(int Potion, uint? Target)>
        {
            private readonly Dictionary<(int Potion, uint? Target), FocusLeaders> _leaders;

            public FocusOrderComparer(Dictionary<(int Potion, uint? Target), FocusLeaders> leaders)
                => _leaders = leaders;

            public int Compare((int Potion, uint? Target) left, (int Potion, uint? Target) right)
            {
                FocusLeaders leftLeader = _leaders[left];
                FocusLeaders rightLeader = _leaders[right];
                int comparison = left.Potion.CompareTo(right.Potion);
                if (comparison != 0)
                    return comparison;
                comparison = rightLeader.MaxPressure.CompareTo(leftLeader.MaxPressure);
                return comparison != 0
                    ? comparison
                    : leftLeader.Position.CompareTo(rightLeader.Position);
            }
        }

        private readonly struct LineageOrderComparer : IComparer<ulong>
        {
            private readonly Dictionary<ulong, LineageLeaders> _leaders;
            private readonly int[] _rankPosition;

            public LineageOrderComparer(Dictionary<ulong, LineageLeaders> leaders, int[] rankPosition)
            {
                _leaders = leaders;
                _rankPosition = rankPosition;
            }

            public int Compare(ulong left, ulong right)
                => _rankPosition[_leaders[left].Leader]
                    .CompareTo(_rankPosition[_leaders[right].Leader]);
        }

        private void SortByRank(int count)
        {
            _order.AsSpan(0, count).Sort(new RankOrderComparer(_ranks, _facts));
            for (int position = 0; position < count; position++)
            {
                int source = _order[position];
                _rankOrder[position] = source;
                _rankPosition[source] = position;
            }
        }

    }
}
