// Adapted from CombatSolver src/Search/PowerCardValuation/Projection and Cards at a62f1850
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat.Scoring;

namespace Sts2Solver.Ai.Combat;

/// <summary>
/// The existing solver's per-card power-commitment evidence over a Sts2Solver battle: trigger evidence,
/// opening-projection floors/potentials, and the Silent specialized progress/realized evidence. It
/// reads only the byte-exact Sts2Solver state (K1/K2/K4), the K4 feature values and the compiled card
/// score rules; no old-engine model or live value is touched.
/// </summary>
/// <summary>
/// The route-node commitment metadata: the ported <c>PowerCommitment</c> without its string list.
/// Registered cards are indexed by the power-commitment catalog, so the played set is an exact 128-bit mask.
/// </summary>
internal readonly record struct PowerCommitmentState(
    PowerCommitmentFamily Family,
    PowerRoutePriority Priority,
    ulong CardsLow,
    ulong CardsHigh,
    int RoundTransitions,
    int LastEvidenceTurn,
    int Investment,
    int ProvisionalPotential,
    int ProgressEvidence,
    int RealizedEvidence,
    int OpenedActionCount)
{
    public int NetUnrealizedValue => (int)Math.Clamp(
        (long)ProvisionalPotential - Investment, int.MinValue, int.MaxValue);
}

internal static class PowerEvidence
{
    internal readonly ref struct View
    {
        private readonly ref readonly Battle _battle;
        private readonly ScoreFeatures _features;
        private readonly Rules _rules;
        private readonly Scorer _scorer;

        public View(in Battle battle, in ScoreFeatures features, Rules rules,
            Scorer scorer)
        {
            _battle = ref battle;
            _features = features;
            _rules = rules;
            _scorer = scorer;
        }

        public int LiveCount => _battle.HandCount + _battle.DrawCount + _battle.DiscardCount;
        public int HandCount => _battle.HandCount;
        public ScoreFeatures Features => _features;
        public Rules Rules => _rules;
        public ref readonly Battle Battle => ref _battle;

        public Card LiveCard(int index)
        {
            if ((uint)index >= (uint)LiveCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _battle.Cards[index];
        }

        public Card HandCard(int index) => _battle.Cards[index];
        public CardStats Stats(in Card card) => _rules.Card(card.Id, card.Upgrade);
        public CardScore Score(in Card card)
            => _scorer.CardScoreForEvidence(card.Id, card.Upgrade);
        public CardType Type(in Card card) => Kernel.ResolveCardType(_battle, _rules, card);
        public bool HasKeyword(in Card card, Keywords keyword)
            => Kernel.HasKeyword(_battle, card, keyword);
        public string NativeId(in Card card) => Registry.Card(card.Id).NativeId;
        public int CardValue(in Card card) => (int)Math.Round(_scorer.CardValueForEvidence(_battle, card));
        public int AttackHits(in Card card) => _scorer.AttackHitsForEvidence(_battle, card);
        public int EnemyPowerAmount(int enemy, ushort powerId)
        {
            byte owner = (byte)(Limits.FirstEnemyOwner + enemy);
            return Math.Max(0, Kernel.GetAmount(_battle, owner, powerId));
        }
        public int Var(in Card card, int index) => _scorer.VarForEvidence(_battle, card, index);
        public int Block(in Card card)
        {
            CardScore score = Score(card);
            return score.BlockVar >= 0 ? Math.Max(0, Var(card, score.BlockVar)) : 0;
        }

        public int Damage(in Card card)
        {
            CardScore score = Score(card);
            return score.DamageVar >= 0 ? Math.Max(0, Var(card, score.DamageVar)) : 0;
        }

        public int Repeat(in Card card)
        {
            CardScore score = Score(card);
            return score.RepeatVar >= 0 ? Math.Max(1, Var(card, score.RepeatVar)) : 1;
        }

        public bool HasVarFragment(in Card card, string fragment)
        {
            IReadOnlyList<string> names = Registry.Card(card.Id).VarNames;
            for (int index = 0; index < names.Count; index++)
            {
                if (names[index].Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public int CountType(CardType type)
        {
            int count = 0;
            for (int index = 0; index < LiveCount; index++)
            {
                Card card = LiveCard(index);
                if (Type(card) == type)
                    count++;
            }
            return count;
        }

        public int CountVar(string fragment)
        {
            int count = 0;
            for (int index = 0; index < LiveCount; index++)
            {
                if (HasVarFragment(LiveCard(index), fragment))
                    count++;
            }
            return count;
        }

        public bool AnyVar(string fragment)
        {
            for (int index = 0; index < LiveCount; index++)
            {
                if (HasVarFragment(LiveCard(index), fragment))
                    return true;
            }
            return false;
        }

        public bool AnyNativeIdContaining(string fragment)
        {
            for (int index = 0; index < LiveCount; index++)
            {
                if (NativeId(LiveCard(index)).Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public int CountNativeIdContaining(string fragment)
        {
            int count = 0;
            for (int index = 0; index < LiveCount; index++)
            {
                if (NativeId(LiveCard(index)).Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            return count;
        }

        public int MaxBlock()
        {
            int max = 0;
            for (int index = 0; index < LiveCount; index++)
                max = Math.Max(max, Block(LiveCard(index)));
            return max;
        }

        public int MaxDamage()
        {
            int max = 0;
            for (int index = 0; index < LiveCount; index++)
                max = Math.Max(max, Damage(LiveCard(index)));
            return max;
        }

        public int TotalAttackDamage()
        {
            long total = 0;
            for (int index = 0; index < LiveCount; index++)
            {
                Card card = LiveCard(index);
                if (Type(card) != CardType.Attack)
                    continue;
                total += (long)Damage(card) * Repeat(card);
            }
            return (int)Math.Min(int.MaxValue, total);
        }

        public int CountShiv()
        {
            int count = 0;
            for (int index = 0; index < LiveCount; index++)
            {
                Card card = LiveCard(index);
                if ((Stats(card).Tags & CardTag.Shiv) != 0
                    || _scorer.ImmediateShivSupplyForEvidence(_battle, card) > 0
                    || NativeId(card) is "INFINITE_BLADES" or "FAN_OF_KNIVES")
                {
                    count++;
                }
            }
            return count;
        }

        public bool EnemyHasPower(ushort powerId)
        {
            for (int enemy = 0; enemy < _battle.EnemyCount; enemy++)
            {
                byte owner = (byte)(Limits.FirstEnemyOwner + enemy);
                if (_battle.Enemies[enemy].Creature.IsAlive
                    && Kernel.GetAmount(_battle, owner, powerId) > 0)
                {
                    return true;
                }
            }
            return false;
        }

        public int PlayerPowerAmount(ushort powerId)
            => Math.Max(0, Kernel.GetAmount(_battle, Limits.PlayerOwner, powerId));

        /// <summary>Signed parent-to-child delta of the specific effect, not unrelated powers.</summary>
        public int PowerAmountGain(in Battle parent, ushort powerId)
        {
            int before = Kernel.GetAmount(parent, Limits.PlayerOwner, powerId);
            int after = Kernel.GetAmount(_battle, Limits.PlayerOwner, powerId);
            return (int)Math.Clamp((long)after - before, 0, int.MaxValue);
        }

        public int RemainingTurns()
        {
            int drawPerTurn = Math.Max(1, _scorer.ModifiedHandDrawForEvidence(_battle));
            int damagePerTurn = Math.Max(1,
                _features.RetainedAttackValue * Math.Max(1, drawPerTurn)
                / Math.Max(1, _features.LiveDeckSize));
            return Math.Clamp(
                (_features.EnemyHp + damagePerTurn - 1) / damagePerTurn,
                1,
                ScoreWeights.SetupValueHorizonTurns);
        }

        public int EnergyUnit()
        {
            if (LiveCount == 0)
                return 3;
            long total = 0;
            for (int index = 0; index < LiveCount; index++)
                total += Math.Max(0, CardValue(LiveCard(index)));
            return Math.Max(3, (int)(total / LiveCount));
        }

        public int DrawCount(in Card card)
        {
            CardScore score = Score(card);
            int cards = score.CardsVar >= 0 ? Math.Max(0, Var(card, score.CardsVar)) : 0;
            string id = NativeId(card);
            int draws = id switch
            {
                "DAGGER_THROW" or "ESCAPE_PLAN" => 1,
                "PREPARED" or "ADRENALINE" or "ACROBATICS" or "BACKFLIP"
                    or "EXPERTISE" or "REFLEX" => cards,
                "CALCULATED_GAMBLE" => Math.Max(0, HandCount - 1),
                _ => 0,
            };
            return Math.Max(0, draws);
        }

        public int ReachableCardPlays()
        {
            int remainingTurns = RemainingTurns();
            if (remainingTurns <= 0 || LiveCount == 0)
                return 0;
            int current = AffordableCount(currentTurn: true, energy: _battle.Energy,
                limit: HandCount);
            int futureTurns = Math.Max(0, remainingTurns - 1);
            if (futureTurns == 0)
                return current;
            int drawPerTurn = Math.Max(1, _scorer.ModifiedHandDrawForEvidence(_battle));
            int futureEnergy = Math.Max(0, _scorer.ModifiedMaxEnergyForEvidence(_battle));
            int drawIndex = 0;
            int future = 0;
            for (int turn = 0; turn < futureTurns && drawIndex < _battle.DrawCount; turn++)
            {
                int window = Math.Min(drawPerTurn, _battle.DrawCount - drawIndex);
                future = SaturatingAdd(future, AffordableCountInDraw(drawIndex, window, futureEnergy));
                drawIndex += window;
            }
            return SaturatingAdd(current, future);
        }

        private int AffordableCount(bool currentTurn, int energy, int limit)
        {
            Span<int> costs = stackalloc int[Math.Min(limit, 16)];
            int count = 0;
            for (int index = 0; index < limit && count < costs.Length; index++)
            {
                Card card = HandCard(index);
                if (HasKeyword(card, Keywords.Unplayable))
                    continue;
                int cost = currentTurn
                    ? Kernel.EnergyCostWithModifiers(_battle, _rules, card, Pile.Hand)
                    : Kernel.EnergyCostWithLocalModifiers(_battle, card);
                costs[count++] = Math.Max(0, cost);
            }
            return AffordableFromCosts(costs[..count], energy);
        }

        private int AffordableCountInDraw(int start, int window, int energy)
        {
            Span<int> costs = stackalloc int[Math.Min(window, 16)];
            int count = 0;
            for (int offset = 0; offset < window && count < costs.Length; offset++)
            {
                Card card = _battle.Cards[_battle.HandCount + start + offset];
                if (HasKeyword(card, Keywords.Unplayable))
                    continue;
                costs[count++] = Math.Max(0,
                    Kernel.EnergyCostWithLocalModifiers(_battle, card));
            }
            return AffordableFromCosts(costs[..count], energy);
        }

        private static int AffordableFromCosts(Span<int> costs, int energy)
        {
            costs.Sort();
            int count = 0;
            for (int index = 0; index < costs.Length; index++)
            {
                if (costs[index] > energy)
                    break;
                energy -= costs[index];
                count++;
            }
            return count;
        }

        public int CountKeyword(Keywords keyword)
        {
            int count = 0;
            for (int index = 0; index < LiveCount; index++)
            {
                if (HasKeyword(LiveCard(index), keyword))
                    count++;
            }
            return count;
        }

        public int CountTag(CardTag tag)
        {
            int count = 0;
            for (int index = 0; index < LiveCount; index++)
            {
                if ((Stats(LiveCard(index)).Tags & tag) != 0)
                    count++;
            }
            return count;
        }

        public int TotalDrawCount()
        {
            int total = 0;
            for (int index = 0; index < LiveCount; index++)
                total = SaturatingAdd(total, DrawCount(LiveCard(index)));
            return total;
        }

        public int IncomingDamage()
            => Math.Max(0, _features.PlayerHp - _features.ProjectedPlayerHp);

        /// <summary>
        /// Current attack intents or their known direct successor. The latter is a conservative,
        /// one-step approximation of the upstream forecast: no RNG, state mutation, random/conditional
        /// branch selection, forced move, revival or steam transition is predicted.
        /// </summary>
        public int IncomingAttackHits(bool nextTurn)
        {
            int hits = 0;
            for (int position = 0; position < _battle.RosterCount; position++)
            {
                int index = _battle.Roster[position];
                ref readonly Enemy enemy = ref _battle.Enemies[index];
                if (!enemy.Creature.IsAlive || (enemy.Flags & EnemyFlags.SkipNextMove) != 0)
                    continue;
                if (enemy.SteamPhase != SteamPhase.None)
                {
                    if (!nextTurn && enemy.SteamPhase == SteamPhase.Explode
                        && enemy.Ai[Kernel.SteamEruptionDamageRegister] > 0) hits++;
                    continue;
                }
                MonsterRules rules = _rules.MonsterRule(enemy.Rules);
                int move = enemy.Move;
                if (nextTurn)
                {
                    if (move == rules.StunnedNode || move == rules.ReviveNode
                        || (enemy.Flags & (EnemyFlags.NeedsInitialRoll | EnemyFlags.ForcedMustPerformAfterMove
                            | EnemyFlags.PerformedMoveThisRound | EnemyFlags.ActingThisRound)) != 0)
                        continue;
                    MonsterNode current = rules.Nodes[move];
                    int next = current.FollowUp;
                    if (current.Kind != MonsterNodeKind.Move || (uint)next >= (uint)rules.Nodes.Length
                        || next == rules.StunnedNode || next == rules.ReviveNode
                        || rules.Nodes[next].Kind != MonsterNodeKind.Move)
                        continue;
                    move = next;
                }
                MonsterNode target = rules.Nodes[move];
                for (int attack = 0; attack < target.Attacks.Length; attack++)
                {
                    MonsterAttack intent = target.Attacks[attack];
                    if (nextTurn)
                    {
                        // The direct successor is not simulated; its static intent is only a bounded
                        // estimate because its dynamic modifiers may change before the next turn.
                        if (intent.BaseDamage > 0)
                            hits = SaturatingAdd(hits, Math.Max(1, intent.Repeats));
                    }
                    else
                    {
                        byte owner = Kernel.EnemyOwner(index);
                        MonsterDef def = Registry.Monster(enemy.MonsterId);
                        int damage = def.ModifyAttackDamage(_battle, _rules, owner, move, attack, intent.BaseDamage);
                        if (damage > 0)
                            hits = SaturatingAdd(hits, Math.Max(1,
                                def.ModifyAttackRepeats(_battle, _rules, owner, move, attack, intent.Repeats)));
                    }
                }
            }
            return hits;
        }

        /// <summary>Bounded estimate from current pile order; future shuffle is not simulated.</summary>
        public int SeedTurn(int index, int drawPerTurn)
        {
            if (index < HandCount) return 0;
            int offset = index - HandCount;
            if (offset < _battle.DrawCount) return 1 + offset / drawPerTurn;
            offset -= _battle.DrawCount;
            return 1 + (int)(((long)_battle.DrawCount + drawPerTurn - 1) / drawPerTurn) + offset / drawPerTurn;
        }

        private bool AffordableForSeed(int index, in Card card, int seedTurn, int futureEnergy)
        {
            if (HasKeyword(card, Keywords.Unplayable)) return false;
            if (seedTurn == 0) return Kernel.CanPlayCard(_battle, _rules, index);
            int energy = futureEnergy;
            int cost = (card.Flags & CardFlags.CostsX) != 0
                ? energy : Kernel.EnergyCostWithLocalModifiers(_battle, card);
            if (cost < 0 || cost > energy) return false;
            int stars = Kernel.StarCostWithModifiers(_battle, _rules, card, Piles.PileOf(_battle, index, out _));
            return stars <= Math.Max(0, _battle.Stars);
        }

        public int PlayablePowerSources()
        {
            int count = 0, turns = RemainingTurns();
            int draw = _scorer.ModifiedHandDrawForEvidence(_battle);
            int futureEnergy = -1;
            for (int index = 0; index < LiveCount; index++)
            {
                Card card = LiveCard(index);
                if (Type(card) != CardType.Power || index >= HandCount && draw <= 0) continue;
                int seedTurn = SeedTurn(index, Math.Max(1, draw));
                if (seedTurn >= turns || seedTurn > 2) continue;
                if (seedTurn > 0 && futureEnergy < 0) futureEnergy = Math.Max(0, ModifiedMaxEnergy());
                if (AffordableForSeed(index, card, seedTurn, futureEnergy)) count++;
            }
            return count;
        }

        /// <summary>
        /// Conservative single-card lower estimate of the upstream multi-skill Master Planner DP.
        /// It scans bounded battle piles without allocating or deriving capacity from energy; future
        /// shuffle/retention is an estimate rather than an exact forecast.
        /// </summary>
        public int MasterPlannerPotential(in Battle parent)
        {
            if (PowerAmountGain(parent, PowerEvidenceIds.MasterPlanner) == 0) return 0;
            int turns = RemainingTurns(), draw = _scorer.ModifiedHandDrawForEvidence(_battle);
            if (turns <= 1 || draw <= 0 || LiveCount < 2) return 0;
            int futureEnergy = Math.Max(0, ModifiedMaxEnergy());
            int cycleTurns = Math.Max(1, (int)(((long)LiveCount + draw - 1) / draw));
            int shuffleTurn = 1 + (int)(((long)_battle.DrawCount + draw - 1) / draw);
            Span<int> windowCount = stackalloc int[ScoreWeights.SetupValueHorizonTurns + 1];
            Span<int> onlySource = stackalloc int[ScoreWeights.SetupValueHorizonTurns + 1];
            windowCount.Clear(); onlySource.Fill(-1);
            for (int index = 0; index < LiveCount; index++)
            {
                Card card = LiveCard(index);
                bool source = NativeId(card) switch
                {
                    "SURVIVOR" or "ACROBATICS" or "DAGGER_THROW" => true,
                    "PREPARED" or "HIDDEN_DAGGERS" => Score(card).CardsVar >= 0
                        && Var(card, Score(card).CardsVar) > 0,
                    "STORM_OF_STEEL" or "SHADOW_STEP" or "CALCULATED_GAMBLE" => true,
                    _ => false,
                };
                if (!source) continue;
                int seedTurn = SeedTurn(index, draw);
                if (seedTurn >= turns || seedTurn > 2 || !AffordableForSeed(index, card, seedTurn, futureEnergy)) continue;
                int firstWindow = seedTurn == 0 ? shuffleTurn + index / draw : seedTurn;
                for (int turn = firstWindow; turn < turns; turn += cycleTurns)
                {
                    windowCount[turn]++;
                    onlySource[turn] = index;
                }
            }
            bool automaticDiscard = PlayerPowerAmount(PowerEvidenceIds.ToolsOfTheTrade) > 0;
            int best = 0;
            for (int index = 0; index < LiveCount; index++)
            {
                Card card = LiveCard(index);
                if (Type(card) != CardType.Skill || Kernel.IsSlyThisTurn(_battle, card)) continue;
                int seedTurn = SeedTurn(index, draw);
                int payoffTurn = seedTurn == 0 ? shuffleTurn + index / draw : seedTurn + cycleTurns;
                if (seedTurn > 2 || payoffTurn >= turns || !AffordableForSeed(index, card, seedTurn, futureEnergy)) continue;
                bool hasPayoff = false;
                for (int turn = payoffTurn; turn < turns; turn += cycleTurns)
                {
                    if (automaticDiscard || windowCount[turn] > 1
                        || windowCount[turn] == 1 && onlySource[turn] != index)
                    { hasPayoff = true; break; }
                }
                if (hasPayoff)
                    best = Math.Max(best, Math.Max(1, Math.Max(1, CardValue(card)) / Math.Max(1, payoffTurn)));
            }
            return best;
        }

        public int DistinctOrbTypes()
        {
            ulong mask = 0;
            for (int index = 0; index < _battle.OrbCount; index++)
            {
                int id = _battle.Orbs[index].Id;
                if (id < 64)
                    mask |= 1UL << id;
            }
            return System.Numerics.BitOperations.PopCount(mask);
        }

        public int CountOrb(string fragment)
        {
            int count = 0;
            for (int index = 0; index < _battle.OrbCount; index++)
            {
                if (Registry.Orb(_battle.Orbs[index].Id).NativeId
                    .Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }
            return count;
        }

        public int CountZeroCostAttack()
        {
            int count = 0;
            for (int index = 0; index < LiveCount; index++)
            {
                Card card = LiveCard(index);
                if (Type(card) == CardType.Attack
                    && Kernel.EnergyCostWithModifiers(_battle, _rules, card, Pile.Hand) == 0)
                {
                    count++;
                }
            }
            return count;
        }

        public bool StatusOrCurseInHand()
        {
            for (int index = 0; index < HandCount; index++)
            {
                CardType type = Type(HandCard(index));
                if (type is CardType.Status or CardType.Curse)
                    return true;
            }
            return false;
        }

        public bool HasEnergyCostAtLeast(int minimum)
        {
            for (int index = 0; index < LiveCount; index++)
            {
                if (Kernel.EnergyCostWithModifiers(_battle, _rules, LiveCard(index), Pile.Hand)
                    >= minimum)
                {
                    return true;
                }
            }
            return false;
        }

        public int CountExhaustSources()
        {
            int count = 0;
            for (int index = 0; index < LiveCount; index++)
            {
                Card card = LiveCard(index);
                if (HasKeyword(card, Keywords.Exhaust) || HasVarFragment(card, "Exhaust"))
                    count++;
            }
            return count;
        }

        public bool HasCardGeneration() => CountCardGeneration() > 0;

        public int CountCardGeneration()
        {
            int count = 0;
            for (int index = 0; index < LiveCount; index++)
            {
                Card card = LiveCard(index);
                if (Type(card) != CardType.Attack
                    && (HasVarFragment(card, "Cards") || HasVarFragment(card, "Generated")))
                {
                    count++;
                }
            }
            return count;
        }

        public bool HasStarCost()
        {
            for (int index = 0; index < LiveCount; index++)
            {
                CardStats stats = Stats(LiveCard(index));
                if (stats.StarCost > 0 || stats.StarCostsX)
                    return true;
            }
            return false;
        }

        public bool HasVar(string fragment) => AnyVar(fragment);

        public bool HasForge()
        {
            for (int index = 0; index < LiveCount; index++)
            {
                Card card = LiveCard(index);
                if (NativeId(card) is "FURNACE" or "SEEKING_EDGE" || HasVarFragment(card, "Forge"))
                    return true;
            }
            return false;
        }

        public bool HasSovereignBladeSource()
        {
            for (int index = 0; index < LiveCount; index++)
            {
                Card card = LiveCard(index);
                if (NativeId(card) is "FURNACE" or "SEEKING_EDGE" or "SWORD_SAGE" or "PARRY"
                    || HasVarFragment(card, "Sovereign"))
                {
                    return true;
                }
            }
            return false;
        }

        public int CountSouls()
        {
            int count = 0;
            for (int index = 0; index < LiveCount; index++)
            {
                Card card = LiveCard(index);
                if (NativeId(card).Contains("SOUL", StringComparison.OrdinalIgnoreCase)
                    || HasVarFragment(card, "Soul"))
                {
                    count++;
                }
            }
            return count;
        }

        public bool HasDebuffSource()
        {
            for (int index = 0; index < LiveCount; index++)
            {
                if (Score(LiveCard(index)).HasDebuffDynamicVar)
                    return true;
            }
            return false;
        }

        public bool OstyPresent => _features.OstyHp > 0;

        public int StarSpendCapacity()
        {
            int stars = Math.Max(0, _battle.Stars);
            if (stars == 0)
                return 0;
            long spend = 0;
            for (int index = 0; index < HandCount; index++)
            {
                CardStats stats = Stats(HandCard(index));
                int cost = stats.StarCostsX ? stars : stats.StarCost;
                if (cost > 0)
                    spend += Math.Min(cost, stars);
            }
            return (int)Math.Min(stars, spend);
        }

        public int ModifiedMaxEnergy() => _scorer.ModifiedMaxEnergyForEvidence(_battle);
        public int ModifiedHandDraw() => Math.Max(1, _scorer.ModifiedHandDrawForEvidence(_battle));

        public bool WraithTiming(in Battle parent)
        {
            int intangible = PowerAmountGain(parent, PowerEvidenceIds.Intangible);
            return intangible > 0
                && ( _features.ProjectedPlayerHp <= 0
                    || RemainingTurns() <= intangible + 1);
        }
    }

    /// <summary><c>*PowerHasTriggerEvidence</c> over the child state.</summary>
    public static bool HasTriggerEvidence(ushort cardId, in Battle parent, scoped in View view)
    {
        string id = Registry.Card(cardId).NativeId;
        int remainingTurns = view.RemainingTurns();
        bool hasAttack = view.CountType(CardType.Attack) > 0;
        bool hasSkill = view.CountType(CardType.Skill) > 0;
        bool hasBlockSkill = view.MaxBlock() > 0;
        int attackCount = view.CountType(CardType.Attack);
        bool aliveEnemy = view.Features.AliveEnemyCount > 0;
        return id switch
        {
            // Ironclad.
            "AGGRESSION" => hasAttack && remainingTurns > 1,
            "BARRICADE" => hasBlockSkill,
            "CORRUPTION" => hasSkill,
            "CRIMSON_MANTLE" => remainingTurns > 1,
            "CRUELTY" => (view.CountVar("Vulnerable") > 0 || view.EnemyHasPower(PowerEvidenceIds.Vulnerable)) && hasAttack,
            "DARK_EMBRACE" => view.CountVar("Exhaust") + view.CountKeyword(Keywords.Exhaust) > 0
                && remainingTurns > 1,
            "DEMON_FORM" => hasAttack && remainingTurns > 1,
            "FEEL_NO_PAIN" => view.CountVar("Exhaust") + view.CountKeyword(Keywords.Exhaust) > 0,
            "HELLRAISER" => view.CountTag(CardTag.Strike) > 0,
            "INFERNO" => remainingTurns > 1 && aliveEnemy,
            "INFLAME" => hasAttack,
            "JUGGERNAUT" => hasBlockSkill && aliveEnemy,
            "JUGGLING" => attackCount >= 3 || hasAttack && remainingTurns > 1,
            "PYRE" => remainingTurns > 1,
            "RUPTURE" => view.CountVar("SelfDamage") + view.CountVar("HpLoss") + view.CountVar("LoseHp") > 0,
            "STAMPEDE" => hasAttack && remainingTurns > 1,
            "STONE_ARMOR" => remainingTurns > 1,
            "UNMOVABLE" => hasBlockSkill,
            "VICIOUS" => (view.CountVar("Vulnerable") > 0 || view.EnemyHasPower(PowerEvidenceIds.Vulnerable))
                && remainingTurns > 1,

            // Silent.
            "ABRASIVE" => hasBlockSkill || view.Features.ProjectedPlayerHp < view.Features.PlayerHp,
            "ACCELERANT" => view.CountVar("Poison") > 0 || view.EnemyHasPower(PowerEvidenceIds.Poison),
            "ACCURACY" => view.CountShiv() > 0,
            "AFTERIMAGE" => view.ReachableCardPlays() >= 5,
            "ENVENOM" => hasAttack && remainingTurns > 1,
            "FAN_OF_KNIVES" => aliveEnemy,
            "FOOTWORK" => hasBlockSkill,
            "INFINITE_BLADES" => remainingTurns > 1,
            "MASTER_PLANNER" => true,
            "NOXIOUS_FUMES" => remainingTurns > 1 && aliveEnemy,
            "PHANTOM_BLADES" => view.CountShiv() > 0,
            "SERPENT_FORM" => view.ReachableCardPlays() > 0,
            "SPEEDSTER" => view.TotalDrawCount() > 0,
            "TOOLS_OF_THE_TRADE" => remainingTurns > 1,
            "TRACKING" => (view.CountVar("Weak") > 0 || view.EnemyHasPower(PowerEvidenceIds.Weak)) && hasAttack,
            "WELL_LAID_PLANS" => remainingTurns > 1 && view.LiveCount > 0,
            "WRAITH_FORM" => view.WraithTiming(parent),

            // Defect.
            "BIASED_COGNITION" => view.Battle.OrbCount > 0 || view.CountVar("Orb") > 0,
            "BUFFER" => view.PowerAmountGain(parent, PowerEvidenceIds.Buffer) > 0
                && (view.IncomingDamage() > 0 || view.IncomingAttackHits(nextTurn: false) > 0
                    || view.IncomingAttackHits(nextTurn: true) > 0),
            "BULK_UP" => hasAttack && hasBlockSkill,
            "CAPACITOR" => view.Battle.OrbCount > 0 || view.CountVar("Orb") > 0,
            "CONSUMING_SHADOW" => view.Battle.OrbCount > 0,
            "COOLANT" => view.DistinctOrbTypes() > 0,
            "CREATIVE_AI" => remainingTurns > 1,
            "DEFRAGMENT" => view.Battle.OrbCount > 0 || view.CountVar("Orb") > 0,
            "ECHO_FORM" => remainingTurns > 1 && (hasAttack || hasBlockSkill),
            "FERAL" => view.CountZeroCostAttack() > 0,
            "HAILSTORM" => view.CountOrb("FROST") > 0,
            "ITERATION" => view.StatusOrCurseInHand() || view.CountVar("Status") > 0,
            "LOOP" => view.Battle.OrbCount > 0,
            "MACHINE_LEARNING" => remainingTurns > 1,
            "SMOKESTACK" => view.CountVar("Status") > 0,
            "SPINNER" => remainingTurns > 1,
            "STORM" => view.CountType(CardType.Power) > 0,
            "SUBROUTINE" => view.PowerAmountGain(parent, PowerEvidenceIds.Subroutine) > 0
                && view.PlayablePowerSources() > 0,
            "THUNDER" => view.CountOrb("LIGHTNING") > 0,
            "TRASH_TO_TREASURE" => view.CountVar("Status") > 0,

            // Regent.
            "ARSENAL" => view.HasCardGeneration() && remainingTurns > 1,
            "BLACK_HOLE" => (view.HasStarCost() || view.HasVar("Star")) && aliveEnemy,
            "CHILD_OF_THE_STARS" => view.HasStarCost(),
            "FURNACE" => view.HasForge() || remainingTurns > 1,
            "GENESIS" => remainingTurns > 1,
            "MONARCHS_GAZE" => hasAttack && aliveEnemy,
            "NEUTRON_AEGIS" => remainingTurns > 1,
            "ORBIT" => remainingTurns > 1,
            "PALE_BLUE_DOT" => remainingTurns > 1 && view.HandCount > 0,
            "PARRY" => view.HasSovereignBladeSource(),
            "PILLAR_OF_CREATION" => view.HasCardGeneration(),
            "SEEKING_EDGE" => view.HasForge() || remainingTurns > 1,
            "SPECTRUM_SHIFT" => remainingTurns > 1,
            "SWORD_SAGE" => view.HasSovereignBladeSource(),
            "THE_SEALED_THRONE" => remainingTurns > 1 && view.LiveCount > 0,
            "TYRANNY" => remainingTurns > 1 && view.HandCount > 0,
            "VOID_FORM" => remainingTurns > 1,

            // Necrobinder.
            "CALCIFY" => view.OstyPresent,
            "CALL_OF_THE_VOID" => remainingTurns > 1,
            "COUNTDOWN" => aliveEnemy && remainingTurns > 1,
            "DANSE_MACABRE" => remainingTurns > 1 && view.HasEnergyCostAtLeast(2),
            "DEMESNE" => remainingTurns > 1,
            "DEVOUR_LIFE" => view.CountSouls() > 0 || view.OstyPresent,
            "FRIENDSHIP" => remainingTurns > 1 && hasAttack,
            "HAUNT" => view.CountSouls() > 0,
            "LETHALITY" => hasAttack,
            "NECRO_MASTERY" => view.OstyPresent,
            "NEUROSURGE" => hasAttack || remainingTurns > 1,
            "PAGESTORM" => view.CountKeyword(Keywords.Ethereal) > 0,
            "REAPER_FORM" => hasAttack,
            "SENTRY_MODE" => view.OstyPresent || remainingTurns > 1,
            "SHROUD" => view.CountVar("Doom") > 0,
            "SLEIGHT_OF_FLESH" => hasAttack || view.HasDebuffSource(),
            "SPIRIT_OF_ASH" => view.CountKeyword(Keywords.Ethereal) > 0,

            // Colorless.
            "AUTOMATION" => remainingTurns > 1,
            "CALAMITY" => hasAttack,
            "ENTROPY" => remainingTurns > 1 && view.HandCount > 0,
            "ETERNAL_ARMOR" => remainingTurns > 1,
            "FASTEN" => view.CountTag(CardTag.Defend) > 0,
            "MAYHEM" => remainingTurns > 1 && view.Battle.DrawCount > 0,
            "NOSTALGIA" => hasAttack || hasSkill,
            "PANACHE" => view.HandCount > 0,
            "PREP_TIME" => hasAttack,
            "PROWESS" => hasAttack || hasBlockSkill,
            "ROLLING_BOULDER" => aliveEnemy && remainingTurns > 1,
            "STRATAGEM" => remainingTurns > 1,
            _ => false,
        };
    }

    /// <summary><c>*PowerTriggerProjectionFloor</c> over the child state.</summary>
    public static int TriggerProjectionFloor(ushort cardId, scoped in View view)
    {
        string id = Registry.Card(cardId).NativeId;
        int turns = Math.Max(1, view.RemainingTurns() - 1);
        int attacks = view.CountType(CardType.Attack);
        return id switch
        {
            "DEMON_FORM" => SaturatingMul(4, Math.Max(1, attacks)),
            "INFLAME" => SaturatingMul(3, Math.Max(1, attacks)),
            "FEEL_NO_PAIN" => SaturatingMul(4, Math.Max(1, view.CountExhaustSources())),
            "JUGGERNAUT" => SaturatingMul(8, Math.Max(1, view.CountVar("Block"))),
            "PYRE" => SaturatingMul(2, turns),
            "STONE_ARMOR" => SaturatingMul(6, turns),
            "BARRICADE" or "CORRUPTION" or "CRIMSON_MANTLE" or "DARK_EMBRACE"
                or "INFERNO" or "UNMOVABLE" => 1,
            "FAN_OF_KNIVES" => Math.Max(1, view.Features.AliveEnemyCount),
            "FOOTWORK" or "INFINITE_BLADES" or "SERPENT_FORM" or "SPEEDSTER"
                or "TOOLS_OF_THE_TRADE" or "WELL_LAID_PLANS" => 1,
            "NOXIOUS_FUMES" => Math.Max(1, view.Features.AliveEnemyCount),
            "BIASED_COGNITION" or "DEFRAGMENT" => SaturatingMul(
                Math.Max(1, view.PlayerPowerAmount(PowerEvidenceIds.Focus)),
                Math.Max(1, (int)view.Battle.OrbCount)),
            "BULK_UP" => SaturatingMul(Math.Max(1, attacks), 3),
            "CAPACITOR" or "CONSUMING_SHADOW" or "COOLANT" or "CREATIVE_AI"
                or "ECHO_FORM" or "LOOP" or "MACHINE_LEARNING" or "SPINNER"
                or "HAILSTORM" => 1,
            "FURNACE" => SaturatingMul(Math.Max(1, view.PlayerPowerAmount(PowerEvidenceIds.Furnace)), turns),
            "GENESIS" => SaturatingMul(Math.Max(1, view.PlayerPowerAmount(PowerEvidenceIds.Genesis)), turns),
            "NEUTRON_AEGIS" => Math.Max(1, view.PlayerPowerAmount(PowerEvidenceIds.Plating)),
            "ARSENAL" or "SEEKING_EDGE" or "SPECTRUM_SHIFT" or "TYRANNY"
                or "VOID_FORM" => 1,
            "COUNTDOWN" => SaturatingMul(Math.Max(1, view.PlayerPowerAmount(PowerEvidenceIds.Countdown)), turns),
            "LETHALITY" => SaturatingMul(Math.Max(1, view.MaxDamage()), 2),
            "CALL_OF_THE_VOID" or "DEMESNE" or "FRIENDSHIP" or "NECRO_MASTERY"
                or "NEUROSURGE" or "REAPER_FORM" or "SENTRY_MODE" => 1,
            "ETERNAL_ARMOR" => SaturatingMul(Math.Max(1, view.PlayerPowerAmount(PowerEvidenceIds.Plating)), turns),
            "MAYHEM" => 1,
            "PROWESS" => SaturatingMul(
                Math.Max(1, view.PlayerPowerAmount(PowerEvidenceIds.Strength)),
                Math.Max(1, attacks)),
            "ROLLING_BOULDER" => SaturatingMul(
                Math.Max(1, view.PlayerPowerAmount(PowerEvidenceIds.RollingBoulder)), turns),
            _ => 0,
        };
    }

    /// <summary><c>*PowerOpeningProjectionPotential</c>; the frontier-based terms use the bounded
    /// attack/block/turn estimate below.</summary>
    public static int ProjectionPotential(ushort cardId, scoped in View view, in Battle parent)
    {
        string id = Registry.Card(cardId).NativeId;
        int turns = Math.Max(0, view.RemainingTurns() - 1);
        int incoming = view.IncomingDamage();
        int attacks = view.CountType(CardType.Attack);
        int enemies = Math.Max(1, view.Features.AliveEnemyCount);
        int gain = ProjectionGain(id, view, parent);
        return id switch
        {
            "AGGRESSION" => PerTurnResource(Math.Max(1, view.MaxDamage()), turns),
            "BARRICADE" => PerTurnBlock(Math.Max(1, view.MaxBlock()), turns, incoming),
            "CORRUPTION" => PerTriggerResource(Math.Max(0, view.CountType(CardType.Skill)),
                Math.Max(1, view.EnergyUnit())),
            "CRIMSON_MANTLE" => PerTurnBlock(gain, turns, incoming),
            "CRUELTY" => PerTriggerDamage(gain, view.TotalAttackDamage() / 100),
            "DARK_EMBRACE" => PerTriggerResource(gain * Math.Max(1, attacks), view.CountExhaustSources()),
            "DEMON_FORM" => GrowthFrontier(view, damagePerAttack: gain),
            "FEEL_NO_PAIN" => PerTriggerBlock(gain, view.CountExhaustSources(), incoming),
            "HELLRAISER" => PerTurnDamage(Math.Max(1, view.MaxDamage()), turns, 1),
            "INFERNO" => PerTurnDamage(gain, turns, enemies),
            "INFLAME" => GrowthFrontier(view, damagePerAttack: gain),
            "JUGGERNAUT" => PerTriggerDamage(gain, Math.Max(1, view.CountVar("Block"))),
            "JUGGLING" => PerTurnResource(Math.Max(1, view.MaxDamage()), turns),
            "PYRE" => PerTurnResource(gain, turns),
            "RUPTURE" => GrowthFrontier(view, damagePerAttack: SaturatingMul(gain,
                Math.Max(1, view.CountVar("SelfDamage") + view.CountVar("HpLoss") + view.CountVar("LoseHp")))),
            "STAMPEDE" => PerTurnDamage(Math.Max(1, view.MaxDamage()), turns, 1),
            "STONE_ARMOR" => PerTurnBlock(gain, turns, incoming),
            "UNMOVABLE" => PerTurnBlock(Math.Max(1, view.MaxBlock()), 1, incoming),
            "VICIOUS" => PerTriggerResource(gain * Math.Max(1, view.EnergyUnit()),
                view.CountVar("Vulnerable")),
            _ => SimpleProjection(view, parent, id, turns, incoming, enemies, gain),
        };
    }

    private static int ProjectionGain(string id, scoped in View view, in Battle parent)
    {
        return id switch
        {
            "CRIMSON_MANTLE" => view.PowerAmountGain(parent, PowerEvidenceIds.CrimsonMantle),
            "CRUELTY" => view.PowerAmountGain(parent, PowerEvidenceIds.Cruelty),
            "DARK_EMBRACE" => view.PowerAmountGain(parent, PowerEvidenceIds.DarkEmbrace),
            "DEMON_FORM" => view.PowerAmountGain(parent, PowerEvidenceIds.DemonForm),
            "FEEL_NO_PAIN" => view.PowerAmountGain(parent, PowerEvidenceIds.FeelNoPain),
            "INFERNO" => view.PowerAmountGain(parent, PowerEvidenceIds.Inferno),
            "INFLAME" => view.PowerAmountGain(parent, PowerEvidenceIds.Strength),
            "JUGGERNAUT" => view.PowerAmountGain(parent, PowerEvidenceIds.Juggernaut),
            "PYRE" => view.PowerAmountGain(parent, PowerEvidenceIds.Pyre),
            "RUPTURE" => view.PowerAmountGain(parent, PowerEvidenceIds.Rupture),
            "STONE_ARMOR" => view.PowerAmountGain(parent, PowerEvidenceIds.Plating),
            "VICIOUS" => view.PowerAmountGain(parent, PowerEvidenceIds.Vicious),
            "NOXIOUS_FUMES" => view.PowerAmountGain(parent, PowerEvidenceIds.NoxiousFumes),
            "FURNACE" => view.PowerAmountGain(parent, PowerEvidenceIds.Furnace),
            "GENESIS" => view.PowerAmountGain(parent, PowerEvidenceIds.Genesis),
            "NEUTRON_AEGIS" => view.PowerAmountGain(parent, PowerEvidenceIds.Plating),
            "COUNTDOWN" => view.PowerAmountGain(parent, PowerEvidenceIds.Countdown),
            "SHROUD" => view.PowerAmountGain(parent, PowerEvidenceIds.Shroud),
            "ETERNAL_ARMOR" => view.PowerAmountGain(parent, PowerEvidenceIds.Plating),
            "ROLLING_BOULDER" => view.PowerAmountGain(parent, PowerEvidenceIds.RollingBoulder),
            "BIASED_COGNITION" => view.PowerAmountGain(parent, PowerEvidenceIds.Focus),
            "DEFRAGMENT" => view.PowerAmountGain(parent, PowerEvidenceIds.Focus),
            "CHILD_OF_THE_STARS" => view.PowerAmountGain(parent, PowerEvidenceIds.ChildOfTheStars),
            "BUFFER" => view.PowerAmountGain(parent, PowerEvidenceIds.Buffer),
            "MASTER_PLANNER" => view.PowerAmountGain(parent, PowerEvidenceIds.MasterPlanner),
            "SUBROUTINE" => view.PowerAmountGain(parent, PowerEvidenceIds.Subroutine),
            _ => 0,
        };
    }

    private static int SimpleProjection(scoped in View view, in Battle parent,
        string id, int turns, int incoming, int enemies, int gain)
    {
        int attacks = view.CountType(CardType.Attack);
        return id switch
        {
            "AFTERIMAGE" or "ABRASIVE" => PerTriggerBlock(Math.Max(1, view.MaxBlock()), 1, incoming),
            "ACCURACY" => PerTurnDamage(Math.Max(1, view.CountShiv()), turns, 1),
            "ENVENOM" => PerTriggerDamage(Math.Max(1, view.CountType(CardType.Attack)), enemies),
            "MASTER_PLANNER" => view.MasterPlannerPotential(parent),
            "NOXIOUS_FUMES" => PerTurnDamage(gain, turns, enemies),
            "PHANTOM_BLADES" => PerTurnResource(Math.Max(1, view.CountShiv()), turns),
            "SERPENT_FORM" => PerTurnResource(Math.Max(1, view.EnergyUnit()), turns),
            "SPEEDSTER" => PerTriggerDamage(2, view.TotalDrawCount()),
            "TOOLS_OF_THE_TRADE" => PerTurnResource(Math.Max(1, view.EnergyUnit()), turns),
            "WELL_LAID_PLANS" => PerTurnResource(Math.Max(1, (int)view.HandCount), turns),
            "TRACKING" => PerTurnDamage(Math.Max(1, view.MaxDamage()), turns, enemies),
            "BIASED_COGNITION" or "DEFRAGMENT" => PerTurnResource(
                gain, Math.Max(1, (int)view.Battle.OrbCount)),
            "BUFFER" => BufferPotential(gain, incoming, view.IncomingAttackHits(nextTurn: true)),
            "BULK_UP" => GrowthFrontier(view,
                blockPerSkillBonus: view.PowerAmountGain(parent, PowerEvidenceIds.Dexterity),
                damagePerAttack: view.PowerAmountGain(parent, PowerEvidenceIds.Strength)),
            "COOLANT" => PerTurnBlock(Math.Max(1, view.MaxBlock()), turns, incoming),
            "CREATIVE_AI" => PerTurnResource(Math.Max(1, view.EnergyUnit()), turns),
            "ECHO_FORM" => PerTurnDamage(Math.Max(1, view.MaxDamage()), turns, enemies),
            "FERAL" => PerTurnDamage(Math.Max(1, view.MaxDamage()), turns, 1),
            "HAILSTORM" => PerTurnDamage(Math.Max(1, view.CountOrb("FROST")), turns, enemies),
            "ITERATION" => PerTurnResource(Math.Max(1, (int)view.HandCount), turns),
            "LOOP" => PerTurnDamage(Math.Max(1, (int)view.Battle.OrbCount), turns, 1),
            "MACHINE_LEARNING" => PerTurnResource(Math.Max(1, view.EnergyUnit()), turns),
            "SMOKESTACK" => PerTurnDamage(Math.Max(1, view.CountVar("Status")), turns, enemies),
            "SPINNER" => PerTurnResource(Math.Max(1, view.EnergyUnit()), turns),
            "STORM" => PerTurnDamage(Math.Max(1, view.EnergyUnit()), turns, enemies),
            "SUBROUTINE" => PerTriggerResource(SaturatingMul(gain, view.EnergyUnit()), view.PlayablePowerSources()),
            "THUNDER" => PerTurnDamage(Math.Max(1, view.CountOrb("LIGHTNING")), turns, enemies),
            "TRASH_TO_TREASURE" => PerTurnResource(Math.Max(1, view.CountVar("Status")), turns),
            "ARSENAL" => PerTurnResource(Math.Max(1, view.CountCardGeneration()), turns),
            "BLACK_HOLE" => PerTurnResource(Math.Max(1, view.EnergyUnit()), turns),
            "CHILD_OF_THE_STARS" => PerTriggerBlock(
                gain, view.StarSpendCapacity(), incoming),
            "FURNACE" => PerTurnResource(gain, turns),
            "GENESIS" => PerTurnResource(gain, turns),
            "MONARCHS_GAZE" => PerTurnDamage(Math.Max(1, view.MaxDamage()), turns, enemies),
            "NEUTRON_AEGIS" => PerTurnBlock(gain, turns, incoming),
            "ORBIT" => PerTurnResource(Math.Max(1, view.ModifiedMaxEnergy()), turns),
            "PALE_BLUE_DOT" => PerTurnResource(Math.Max(1, view.EnergyUnit()), turns),
            "PARRY" => PerTurnBlock(Math.Max(1, view.MaxBlock()), turns, incoming),
            "PILLAR_OF_CREATION" => PerTurnResource(Math.Max(1, view.CountCardGeneration()), turns),
            "SEEKING_EDGE" => PerTurnResource(view.EnergyUnit(), turns),
            "SPECTRUM_SHIFT" => PerTurnResource(Math.Max(1, view.EnergyUnit()), turns),
            "SWORD_SAGE" => PerTurnDamage(Math.Max(1, view.MaxDamage()), turns, 1),
            "THE_SEALED_THRONE" => PerTurnResource(Math.Max(1, (int)view.HandCount), turns),
            "TYRANNY" => PerTurnResource(Math.Max(1, (int)view.HandCount), turns),
            "VOID_FORM" => PerTurnResource(Math.Max(1, view.EnergyUnit()), turns),
            "CALCIFY" => PerTurnBlock(Math.Max(1, view.MaxBlock()), turns, incoming),
            "CALL_OF_THE_VOID" => PerTurnResource(Math.Max(1, view.EnergyUnit()), turns),
            "COUNTDOWN" => PerTurnDamage(gain, turns, enemies),
            "DANSE_MACABRE" => PerTurnDamage(Math.Max(1, view.ModifiedMaxEnergy()), turns, enemies),
            "DEMESNE" => PerTurnResource(Math.Max(1, view.ModifiedMaxEnergy()), turns),
            "DEVOUR_LIFE" => PerTurnDamage(Math.Max(1, view.MaxDamage()), turns, 1),
            "FRIENDSHIP" => PerTurnBlock(Math.Max(1, view.MaxBlock()), turns, incoming),
            "HAUNT" => PerTurnDamage(Math.Max(1, view.CountSouls()), turns, enemies),
            "LETHALITY" => PerTurnDamage(Math.Max(1, view.MaxDamage()), turns, 1),
            "NECRO_MASTERY" => PerTurnDamage(Math.Max(1, view.MaxDamage()), turns, 1),
            "NEUROSURGE" => PerTurnDamage(Math.Max(1, view.MaxDamage()), turns, enemies),
            "PAGESTORM" => PerTurnResource(Math.Max(1, view.CountKeyword(Keywords.Ethereal)), turns),
            "REAPER_FORM" => PerTurnDamage(Math.Max(1, view.MaxDamage()), turns, enemies),
            "SENTRY_MODE" => PerTurnBlock(Math.Max(1, view.MaxBlock()), turns, incoming),
            "SHROUD" => PerTurnDamage(gain, turns, enemies),
            "SLEIGHT_OF_FLESH" => PerTurnDamage(Math.Max(1, view.MaxDamage()), turns, 1),
            "SPIRIT_OF_ASH" => PerTurnDamage(Math.Max(1, view.CountKeyword(Keywords.Ethereal)), turns, enemies),
            "AUTOMATION" => PerTurnResource(Math.Max(1, view.ModifiedHandDraw()), turns),
            "CALAMITY" => PerTurnDamage(Math.Max(1, view.MaxDamage()), turns, enemies),
            "ENTROPY" => PerTurnResource(Math.Max(1, view.EnergyUnit()), turns),
            "ETERNAL_ARMOR" => PerTurnBlock(gain, turns, incoming),
            "FASTEN" => PerTurnBlock(Math.Max(1, view.MaxBlock()), turns, incoming),
            "MAYHEM" => PerTurnDamage(Math.Max(1, view.MaxDamage()), turns, enemies),
            "NOSTALGIA" => PerTurnResource(Math.Max(1, view.EnergyUnit()), turns),
            "PANACHE" => PerTurnDamage(Math.Max(1, view.MaxDamage()), turns, enemies),
            "PREP_TIME" => PerTurnBlock(Math.Max(1, view.MaxBlock()), turns, incoming),
            "PROWESS" => GrowthFrontier(view,
                blockPerSkillBonus: view.PowerAmountGain(parent, PowerEvidenceIds.Dexterity),
                damagePerAttack: view.PowerAmountGain(parent, PowerEvidenceIds.Strength)),
            "ROLLING_BOULDER" => PerTurnDamage(gain, turns, enemies),
            "STRATAGEM" => PerTurnResource(Math.Max(1, view.EnergyUnit()), turns),
            _ => 0,
        };
    }

    // Port of the upstream BufferPrevention approximation: per-hit average, bounded by incoming HP loss.
    internal static int BufferPotential(int charges, int incoming, int incomingHits)
    {
        if (charges <= 0 || incoming <= 0) return 0;
        int perHit = Math.Max(1, incoming / Math.Max(1, incomingHits));
        int raw = (int)Math.Min(incoming, (long)charges * perHit);
        return BoundedPrevention(raw, incoming);
    }

    public static int GrowthFrontier(scoped in View view, int blockPerSkillBonus = 0,
        int blockPerCardBonus = 0, int damagePerDraw = 0, int damageTargets = 1,
        int damagePerShiv = 0, int firstShivDamageBonus = 0, int damagePerCard = 0,
        int damagePerUnblockedAttackHit = 0, int weakAttackBonusPercent = 0,
        int damagePerAttack = 0)
    {
        int incoming = view.IncomingDamage();
        PowerTurnCardOption[] baseline = BuildHandOptions(view);
        if (baseline.Length == 0)
            return 0;
        PowerTurnCardOption[] powered = weakAttackBonusPercent > 0
            ? BuildHandOptions(view, weakAttackBonusPercent)
            : baseline;
        int value = MarginalFrontierValue(
            PowerTurnFrontier.Build(view.Battle.Energy, incoming, baseline),
            PowerTurnFrontier.Build(
                view.Battle.Energy,
                incoming,
                powered,
                blockPerSkillBonus,
                blockPerCardBonus,
                damagePerDraw,
                Math.Max(1, damageTargets),
                damagePerShiv,
                firstShivDamageBonus,
                damagePerCard,
                damagePerUnblockedAttackHit,
                damagePerAttack));
        return Math.Min(view.Features.EnemyHp, value);
    }

    private static int MarginalFrontierValue(IReadOnlyList<PowerTurnFrontierState> baseline,
        IReadOnlyList<PowerTurnFrontierState> powered)
        => SaturatingAdd(
            PowerTurnFrontier.DefensiveDamageUplift(baseline, powered),
            SaturatingMul(PowerTurnFrontier.DefensiveHpUplift(baseline, powered), 8));

    private static PowerTurnCardOption[] BuildHandOptions(scoped in View view,
        int weakAttackBonusPercent = 0)
    {
        if (view.HandCount == 0)
            return [];
        int weakTargets = 0;
        int minimumEnemyBlock = int.MaxValue;
        for (int enemy = 0; enemy < view.Battle.EnemyCount; enemy++)
        {
            if (!view.Battle.Enemies[enemy].Creature.IsAlive)
                continue;
            if (view.EnemyPowerAmount(enemy, PowerEvidenceIds.Weak) > 0)
                weakTargets++;
            minimumEnemyBlock = Math.Min(minimumEnemyBlock,
                Math.Max(0, view.Battle.Enemies[enemy].Creature.Block));
        }
        if (minimumEnemyBlock == int.MaxValue)
            minimumEnemyBlock = 0;
        int shivTargets = view.PlayerPowerAmount(PowerEvidenceIds.FanOfKnives) > 0
            ? Math.Max(1, view.Features.AliveEnemyCount)
            : 1;
        List<PowerTurnCardOption> options = new(view.HandCount);
        for (int index = 0; index < view.HandCount; index++)
        {
            Card card = view.HandCard(index);
            if (view.HasKeyword(card, Keywords.Unplayable))
                continue;
            int cost = Math.Max(0,
                Kernel.EnergyCostWithModifiers(view.Battle, view.Rules, card, Pile.Hand));
            bool isShiv = (view.Stats(card).Tags & CardTag.Shiv) != 0;
            int baseDamage = view.Type(card) == CardType.Attack ? view.Damage(card) : 0;
            int damage = isShiv ? SaturatingMul(baseDamage, shivTargets) : baseDamage;
            if (baseDamage > 0 && weakTargets > 0 && weakAttackBonusPercent > 0)
            {
                int affected = isShiv ? weakTargets : 1;
                damage = SaturatingAdd(damage,
                    (int)Math.Min(int.MaxValue,
                        (long)baseDamage * affected * weakAttackBonusPercent / 100));
            }
            int block = view.Type(card) == CardType.Skill ? view.Block(card) : 0;
            int draws = view.DrawCount(card);
            int hits = baseDamage == 0 ? 0 : view.AttackHits(card);
            int unblockedHits = baseDamage == 0
                || (long)baseDamage * hits <= minimumEnemyBlock
                    ? 0
                    : Math.Max(1, hits - minimumEnemyBlock / baseDamage);
            if (damage > 0 || block > 0 || draws > 0)
            {
                options.Add(new PowerTurnCardOption(cost, damage, block,
                    CardAccess: draws, Draws: draws, IsShiv: isShiv,
                    UnblockedAttackHits: unblockedHits));
            }
        }
        return options.ToArray();
    }

    private static int PerTurnDamage(int damagePerTurn, int turns, int targets)
        => Math.Min(int.MaxValue, SaturatingMul(damagePerTurn, Math.Max(0, turns), Math.Max(1, targets)));

    private static int PerTurnBlock(int blockPerTurn, int turns, int incoming)
        => BoundedPrevention(SaturatingMul(blockPerTurn, Math.Max(0, turns)), incoming);

    private static int PerTriggerDamage(int perTrigger, int triggers)
        => SaturatingMul(perTrigger, Math.Max(0, triggers));

    private static int PerTriggerBlock(int perTrigger, int triggers, int incoming)
        => BoundedPrevention(SaturatingMul(perTrigger, Math.Max(0, triggers)), incoming);

    private static int PerTurnResource(int perTurn, int turns)
        => SaturatingMul(perTurn, Math.Max(0, turns));

    private static int PerTriggerResource(int perTrigger, int triggers)
        => SaturatingMul(perTrigger, Math.Max(0, triggers));

    private static int BoundedPrevention(int rawBlock, int incomingDamage)
    {
        int ceiling = Math.Max(Math.Max(0, incomingDamage), rawBlock / 2);
        return Math.Min(rawBlock, ceiling);
    }

    private static int SaturatingMul(int left, int right)
        => (int)Math.Min(int.MaxValue, (long)left * right);

    private static int SaturatingMul(int a, int b, int c)
        => (int)Math.Min(int.MaxValue, (long)a * b * c);

    private static int SaturatingAdd(int left, int right)
        => (int)Math.Min(int.MaxValue, (long)left + right);

    /// <summary><c>SilentPowerProgressEvidence</c>: Master Planner's Sly skill value delta.</summary>
    public static int SilentProgress(in PowerCommitmentState commitment, scoped in View parent,
        scoped in View child)
    {
        if (!CommitmentHasCard(commitment, "MASTER_PLANNER"))
            return 0;
        return Math.Max(0, SilentSlySkillValue(child) - SilentSlySkillValue(parent));
    }

    /// <summary><c>SilentPowerRealizedEvidence</c> over the K4-checked state deltas and counters.</summary>
    public static int SilentRealized(in PowerCommitmentState commitment,
        int parentPlayerBlock, int parentProjectedHp, int parentReachable, int parentTurn,
        int childPlayerBlock, int childProjectedHp, int childReachable, int childTurn,
        int childAliveEnemies, in Battle parentBattle, in Battle childBattle,
        ushort playedCardId, bool actionIsPlay, scoped in View childView, out bool handled)
    {
        long gain = 0;
        handled = false;
        if (CommitmentHasCard(commitment, "AFTERIMAGE"))
        {
            handled = true;
            if (actionIsPlay)
                gain += Math.Max(1, childPlayerBlock - parentPlayerBlock);
        }
        if (CommitmentHasCard(commitment, "FOOTWORK"))
        {
            handled = true;
            Card played = default;
            if (playedCardId != ushort.MaxValue)
            {
                for (int index = 0; index < childBattle.PlayCount; index++)
                {
                    int slot = Piles.Start(childBattle, Pile.Play) + index;
                    if (childBattle.Cards[slot].Id == playedCardId)
                    {
                        played = childBattle.Cards[slot];
                        break;
                    }
                }
            }
            if (played.Id == playedCardId && playedCardId != ushort.MaxValue
                && childView.Type(played) == CardType.Skill && childView.Block(played) > 0)
            {
                gain += Math.Max(1, childPlayerBlock - parentPlayerBlock);
                gain += Math.Max(0, childProjectedHp - parentProjectedHp);
            }
        }
        if (CommitmentHasCard(commitment, "WELL_LAID_PLANS"))
        {
            handled = true;
            if (childTurn > parentTurn)
                gain += Math.Max(1, childReachable);
        }
        if (CommitmentHasCard(commitment, "MASTER_PLANNER"))
        {
            handled = true;
            gain += SlyAutoPlayValue(parentBattle, childBattle, childView);
        }
        if (CommitmentHasCard(commitment, "SPEEDSTER"))
        {
            handled = true;
            int draws = Math.Max(0,
                childBattle.Counter(Counter.NonHandDrawsThisTurn)
                - parentBattle.Counter(Counter.NonHandDrawsThisTurn));
            gain += (long)draws * 2 * Math.Max(0, childAliveEnemies);
        }
        if (CommitmentHasCard(commitment, "TOOLS_OF_THE_TRADE"))
        {
            handled = true;
            if (childTurn > parentTurn)
                gain += Math.Max(1, childReachable);
        }
        return (int)Math.Min(int.MaxValue, gain);
    }

    private static int SilentSlySkillValue(scoped in View view)
    {
        long total = 0;
        for (int index = 0; index < view.LiveCount; index++)
        {
            Card card = view.LiveCard(index);
            if (view.Type(card) == CardType.Skill
                && Kernel.IsSlyThisTurn(view.Battle, card))
            {
                total += Math.Max(1, view.CardValue(card));
            }
        }
        return (int)Math.Min(int.MaxValue, total);
    }

    private static int SlyAutoPlayValue(in Battle parentBattle, in Battle childBattle,
        scoped in View childView)
    {
        int plays = childBattle.PlayCount - parentBattle.PlayCount;
        if (plays <= 0)
            return 0;
        int start = Piles.Start(childBattle, Pile.Play);
        int value = 0;
        for (int offset = 1; offset < plays; offset++)
        {
            Card card = childBattle.Cards[start + offset];
            if (childView.Type(card) != CardType.Skill
                || !Kernel.IsSlyThisTurn(childBattle, card))
            {
                continue;
            }
            value = SaturatingAdd(value, Math.Max(1, childView.CardValue(card)));
        }
        return value;
    }

    private static bool IsBlockSkill(scoped in View view)
    {
        for (int index = 0; index < view.LiveCount; index++)
        {
            Card card = view.LiveCard(index);
            if (view.Type(card) == CardType.Skill && view.Block(card) > 0)
                return true;
        }
        return false;
    }

    private static bool CommitmentHasCard(in PowerCommitmentState commitment, string nativeId)
    {
        if (!Registry.TryCard(nativeId, out ushort cardId))
            return false;
        return cardId < 64
            ? (commitment.CardsLow & (1UL << cardId)) != 0
            : cardId < 128 && (commitment.CardsHigh & (1UL << (cardId - 64))) != 0;
    }
}
