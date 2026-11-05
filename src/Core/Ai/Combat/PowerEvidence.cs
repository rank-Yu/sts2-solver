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
        /// Attack damage of the known deterministic move <paramref name="turnOffset"/> rounds ahead.
        /// Current-turn damage comes from the scored projection; future rounds follow only a direct
        /// <c>Move</c> successor chain and return zero when any step is stunned, reviving, random or
        /// otherwise not predictable without simulating the enemy turn.
        /// </summary>
        public int ForecastIncomingDamage(int turnOffset)
        {
            if (turnOffset <= 0)
                return IncomingDamage();
            if (turnOffset > 2)
                return 0;
            long total = 0;
            for (int position = 0; position < _battle.RosterCount; position++)
            {
                int index = _battle.Roster[position];
                ref readonly Enemy enemy = ref _battle.Enemies[index];
                if (!enemy.Creature.IsAlive
                    || (enemy.Flags & EnemyFlags.SkipNextMove) != 0
                    || enemy.SteamPhase != SteamPhase.None)
                {
                    continue;
                }
                MonsterRules rules = _rules.MonsterRule(enemy.Rules);
                int move = enemy.Move;
                bool known = true;
                for (int step = 0; step < turnOffset; step++)
                {
                    if (step == 0
                        && (move == rules.StunnedNode || move == rules.ReviveNode
                            || (enemy.Flags & (EnemyFlags.NeedsInitialRoll
                                | EnemyFlags.ForcedMustPerformAfterMove
                                | EnemyFlags.PerformedMoveThisRound
                                | EnemyFlags.ActingThisRound)) != 0))
                    {
                        known = false;
                        break;
                    }
                    if ((uint)move >= (uint)rules.Nodes.Length)
                    {
                        known = false;
                        break;
                    }
                    MonsterNode current = rules.Nodes[move];
                    int next = current.FollowUp;
                    if (current.Kind != MonsterNodeKind.Move
                        || (uint)next >= (uint)rules.Nodes.Length
                        || next == rules.StunnedNode || next == rules.ReviveNode
                        || rules.Nodes[next].Kind != MonsterNodeKind.Move)
                    {
                        known = false;
                        break;
                    }
                    move = next;
                }
                if (!known)
                    continue;
                MonsterNode target = rules.Nodes[move];
                MonsterDef def = Registry.Monster(enemy.MonsterId);
                for (int attack = 0; attack < target.Attacks.Length; attack++)
                {
                    MonsterAttack intent = target.Attacks[attack];
                    int damage = def.ModifyAttackDamage(
                        _battle, _rules, Kernel.EnemyOwner(index), move, attack, intent.BaseDamage);
                    if (damage <= 0)
                        continue;
                    int repeats = def.ModifyAttackRepeats(
                        _battle, _rules, Kernel.EnemyOwner(index), move, attack, intent.Repeats);
                    total += (long)damage * Math.Max(1, repeats);
                }
            }
            return (int)Math.Min(int.MaxValue, total);
        }

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
        public int RawModifiedHandDraw() => Math.Max(0, _scorer.ModifiedHandDrawForEvidence(_battle));

        public bool RetainsThisTurn(in Card card)
            => RetainsThisTurn(_battle, card);

        public bool RetainsThisTurn(in Battle battle, in Card card)
            => Kernel.HasKeyword(battle, card, Keywords.Retain)
                || (card.Flags & CardFlags.RetainThisTurn) != 0;

        public int LocalEnergyCost(in Card card)
            => Math.Max(0, Kernel.EnergyCostWithLocalModifiers(_battle, card));

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
    public static int ProjectionPotential(ushort cardId, scoped in View view, in Battle parent,
        int parentProjectedPlayerHp)
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
            "ACCELERANT" => AccelerantProjectionPotential(view, parent),
            "ENVENOM" => EnvenomProjectionPotential(view, parent),
            "NOXIOUS_FUMES" => NoxiousFumesProjectionPotential(view, parent),
            "AFTERIMAGE" => AfterimageProjectionPotential(view, parent),
            "ABRASIVE" => FootworkProjectionPotential(view, parent, requireLongFight: false),
            "ACCURACY" => AccuracyProjectionPotential(view, parent),
            "FAN_OF_KNIVES" => 0,
            "FOOTWORK" => FootworkProjectionPotential(view, parent, requireLongFight: true),
            "INFINITE_BLADES" => 0,
            "MASTER_PLANNER" => MasterPlannerProjectionPotential(view, parent),
            "PHANTOM_BLADES" => PhantomBladesProjectionPotential(view, parent),
            "SERPENT_FORM" => PerTurnResource(Math.Max(1, view.EnergyUnit()), turns),
            "SPEEDSTER" => SpeedsterProjectionPotential(view, parent),
            "TOOLS_OF_THE_TRADE" => ToolsOfTheTradeProjectionPotential(view, parent),
            "TRACKING" => TrackingProjectionPotential(view, parent),
            "WELL_LAID_PLANS" => WellLaidPlansProjectionPotential(view, parent),
            "WRAITH_FORM" => WraithFormProjectionPotential(
                view, parent, parentProjectedPlayerHp),
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
            "ACCELERANT" => view.PowerAmountGain(parent, PowerEvidenceIds.Accelerant),
            "ENVENOM" => view.PowerAmountGain(parent, PowerEvidenceIds.Envenom),
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

    /// <summary>
    /// <c>EnvenomProjectionPotential</c>: the marginal current-hand frontier when every unblocked
    /// attack hit applies the Envenom amount as Poison. The old attack-count times enemy-count
    /// estimate stays as a lower bound: when all current attacks are blocked the frontier alone is
    /// observably lower and would drop a route the retained baseline already admitted.
    /// </summary>
    private static int EnvenomProjectionPotential(scoped in View view, in Battle parent)
    {
        int poisonPerHit = view.PowerAmountGain(parent, PowerEvidenceIds.Envenom);
        if (poisonPerHit == 0)
            return 0;
        PowerTurnCardOption[] options = BuildHandOptions(view);
        int incoming = view.IncomingDamage();
        int frontier = MarginalFrontierValue(
            PowerTurnFrontier.Build(view.Battle.Energy, incoming, options),
            PowerTurnFrontier.Build(view.Battle.Energy, incoming, options,
                damagePerUnblockedAttackHit: poisonPerHit));
        int previous = SaturatingMul(
            Math.Max(1, view.CountType(CardType.Attack)),
            Math.Max(1, view.Features.AliveEnemyCount));
        return Math.Max(frontier, previous);
    }

    /// <summary>
    /// <c>AccelerantProjectionPotential</c>: every additional Accelerant trigger resolves the
    /// enemy's current Poison once more, capped by that enemy's current HP.
    /// </summary>
    private static int AccelerantProjectionPotential(scoped in View view, in Battle parent)
    {
        int extraTriggers = view.PowerAmountGain(parent, PowerEvidenceIds.Accelerant);
        if (extraTriggers == 0)
            return 0;
        long damage = 0;
        for (int position = 0; position < view.Battle.RosterCount; position++)
        {
            int index = view.Battle.Roster[position];
            ref readonly Enemy enemy = ref view.Battle.Enemies[index];
            if (!enemy.Creature.IsAlive || (enemy.Flags & EnemyFlags.InCombat) == 0)
                continue;
            damage += PoisonStackProjection.ExtraTriggerDamage(
                view.EnemyPowerAmount(index, PowerEvidenceIds.Poison),
                extraTriggers,
                Math.Max(0, enemy.Creature.CurrentHp));
        }
        return (int)Math.Min(int.MaxValue, damage);
    }

    /// <summary>
    /// <c>NoxiousFumesProjectionPotential</c>: the power first ticks after the current turn, then
    /// each following turn adds its amount and resolves one less Poison. Every enemy is capped by
    /// its current HP.
    /// </summary>
    private static int NoxiousFumesProjectionPotential(scoped in View view, in Battle parent)
    {
        int poisonPerTurn = view.PowerAmountGain(parent, PowerEvidenceIds.NoxiousFumes);
        if (poisonPerTurn == 0)
            return 0;
        int futureTurns = Math.Max(0, view.RemainingTurns() - 1);
        long total = 0;
        for (int position = 0; position < view.Battle.RosterCount; position++)
        {
            int index = view.Battle.Roster[position];
            ref readonly Enemy enemy = ref view.Battle.Enemies[index];
            if (!enemy.Creature.IsAlive || (enemy.Flags & EnemyFlags.InCombat) == 0)
                continue;
            total += PoisonStackProjection.RecurringApplicationDamage(
                poisonPerTurn,
                futureTurns,
                Math.Max(0, enemy.Creature.CurrentHp));
        }
        return (int)Math.Min(int.MaxValue, total);
    }

    /// <summary>
    /// <c>AfterimageProjectionPotential</c>: the current-hand frontier marginal when every played
    /// card gains the Afterimage amount as block.
    /// </summary>
    private static int AfterimageProjectionPotential(scoped in View view, in Battle parent)
    {
        int blockPerCard = view.PowerAmountGain(parent, PowerEvidenceIds.Afterimage);
        if (blockPerCard == 0)
            return 0;
        PowerTurnCardOption[] options = BuildHandOptions(view);
        if (options.Length == 0)
            return 0;
        int incoming = view.IncomingDamage();
        return MarginalFrontierValue(
            PowerTurnFrontier.Build(view.Battle.Energy, incoming, options),
            PowerTurnFrontier.Build(view.Battle.Energy, incoming, options,
                blockPerCardBonus: blockPerCard));
    }

    /// <summary>
    /// Below this effective enemy HP the remaining fight is short enough that Footwork's two future
    /// draw windows are not credited; the trigger floor remains.
    /// </summary>
    private const int FootworkMinimumEnemyHp = 300;

    /// <summary>
    /// <c>FootworkProjectionPotential</c> (shared by Footwork and Abrasive's Dexterity half): the
    /// current-hand frontier plus the next two draw windows, using the solved Dexterity increment.
    /// </summary>
    private static int FootworkProjectionPotential(
        scoped in View view, in Battle parent, bool requireLongFight)
    {
        int dexterityGain = view.PowerAmountGain(parent, PowerEvidenceIds.Dexterity);
        if (dexterityGain == 0)
            return 0;
        // Two future draw windows only pay off when the fight is long enough to use the scaling;
        // at lower effective enemy HP the trigger floor is the conservative estimate.
        if (requireLongFight && view.Features.EnemyHp < FootworkMinimumEnemyHp)
            return 0;
        int potential = FootworkTurnPotential(
            view.Battle.Energy, BuildHandOptions(view), view.IncomingDamage(), dexterityGain);
        int drawPerTurn = view.ModifiedHandDraw();
        int futureEnergy = view.ModifiedMaxEnergy();
        for (int turnOffset = 1; turnOffset <= 2; turnOffset++)
        {
            PowerTurnCardOption[] options = BuildDrawOptions(
                view, (turnOffset - 1) * drawPerTurn, drawPerTurn, drawPerTurn);
            potential = SaturatingAdd(potential, FootworkTurnPotential(
                futureEnergy, options, view.ForecastIncomingDamage(turnOffset), dexterityGain));
        }
        return potential;
    }

    private static int FootworkTurnPotential(
        int energy,
        PowerTurnCardOption[] options,
        int incomingDamage,
        int dexterityGain)
    {
        if (options.Length == 0)
            return 0;
        return MarginalFrontierValue(
            PowerTurnFrontier.Build(energy, incomingDamage, options),
            PowerTurnFrontier.Build(energy, incomingDamage, options,
                blockPerSkillBonus: dexterityGain));
    }

    /// <summary>
    /// <c>AccuracyProjectionPotential</c>: each played Shiv gains the Accuracy increment on every
    /// target the current Fan of Knives state would hit.
    /// </summary>
    private static int AccuracyProjectionPotential(scoped in View view, in Battle parent)
    {
        int amount = view.PowerAmountGain(parent, PowerEvidenceIds.Accuracy);
        if (amount == 0)
            return 0;
        PowerTurnCardOption[] options = BuildHandOptions(view);
        if (options.Length == 0)
            return 0;
        int damagePerShiv = SaturatingMul(amount, CurrentShivTargets(view));
        int incoming = view.IncomingDamage();
        return MarginalFrontierValue(
            PowerTurnFrontier.Build(view.Battle.Energy, incoming, options),
            PowerTurnFrontier.Build(view.Battle.Energy, incoming, options,
                damagePerShiv: damagePerShiv));
    }

    /// <summary>
    /// <c>MasterPlannerProjectionPotential</c>: the multi-skill discard-window DP over the current
    /// hand, draw pile and discard pile, with the Tools of the Trade extra windows.
    /// </summary>
    private static int MasterPlannerProjectionPotential(scoped in View view, in Battle parent)
    {
        if (view.PowerAmountGain(parent, PowerEvidenceIds.MasterPlanner) == 0)
            return 0;
        int drawPerTurn = view.RawModifiedHandDraw();
        if (drawPerTurn == 0)
            return 0;
        int remainingTurns = view.RemainingTurns();
        int discardWindows = 0;
        for (int index = 0; index < view.LiveCount; index++)
        {
            Card card = view.LiveCard(index);
            CardScore score = view.Score(card);
            int selectedCards = score.CardsVar >= 0 ? Math.Max(0, view.Var(card, score.CardsVar)) : 0;
            discardWindows = SaturatingAdd(discardWindows, SilentDiscardWindowFacts.Capacity(
                view.NativeId(card), selectedCards, view.HandCount));
        }
        discardWindows = SaturatingAdd(discardWindows, SaturatingMul(
            view.PlayerPowerAmount(PowerEvidenceIds.ToolsOfTheTrade),
            Math.Max(0, remainingTurns - 1)));
        int futureEnergy = view.ModifiedMaxEnergy();
        int cycleTurns = Math.Max(1, (view.LiveCount + drawPerTurn - 1) / drawPerTurn);
        int turnsUntilShuffle = 1 + (view.Battle.DrawCount + drawPerTurn - 1) / drawPerTurn;
        List<MasterPlannerSkillFact> skills = new(view.LiveCount);
        for (int index = 0; index < view.HandCount; index++)
        {
            AddMasterPlannerSkill(skills, view, view.HandCard(index), 0,
                turnsUntilShuffle, futureEnergy);
        }
        for (int index = 0; index < view.Battle.DrawCount; index++)
        {
            int seedTurn = 1 + index / drawPerTurn;
            AddMasterPlannerSkill(skills, view, view.LiveCard(view.HandCount + index),
                seedTurn, seedTurn + cycleTurns, futureEnergy);
        }
        for (int index = 0; index < view.Battle.DiscardCount; index++)
        {
            int seedTurn = turnsUntilShuffle + index / drawPerTurn;
            AddMasterPlannerSkill(skills,
                view, view.LiveCard(view.HandCount + view.Battle.DrawCount + index),
                seedTurn, seedTurn + cycleTurns, futureEnergy);
        }
        return MasterPlannerProjection.Evaluate(
            view.Battle.Energy, futureEnergy, remainingTurns, discardWindows, skills.ToArray());
    }

    private static void AddMasterPlannerSkill(List<MasterPlannerSkillFact> skills,
        scoped in View view, in Card card, int seedTurn, int payoffTurn, int futureEnergy)
    {
        if (view.Type(card) != CardType.Skill
            || Kernel.IsSlyThisTurn(view.Battle, card)
            || view.HasKeyword(card, Keywords.Unplayable))
        {
            return;
        }
        int energyCost = seedTurn == 0
            ? Math.Max(0, Kernel.EnergyCostWithModifiers(
                view.Battle, view.Rules, card, Pile.Hand))
            : (card.Flags & CardFlags.CostsX) != 0
                ? futureEnergy
                : view.LocalEnergyCost(card);
        skills.Add(new MasterPlannerSkillFact(
            Math.Max(1, view.CardValue(card)), energyCost, seedTurn, payoffTurn));
    }

    /// <summary>
    /// <c>PhantomBladesProjectionPotential</c>: the first-Shiv damage uplift plus the value of the
    /// newly retained Shivs in the next-turn hand transition.
    /// </summary>
    private static int PhantomBladesProjectionPotential(scoped in View view, in Battle parent)
    {
        int amount = view.PowerAmountGain(parent, PowerEvidenceIds.PhantomBlades);
        if (amount == 0)
            return 0;
        PowerTurnCardOption[] options = BuildHandOptions(view);
        int damageUplift = options.Length == 0 ? 0 : MarginalFrontierValue(
            PowerTurnFrontier.Build(view.Battle.Energy, view.IncomingDamage(), options),
            PowerTurnFrontier.Build(view.Battle.Energy, view.IncomingDamage(), options,
                firstShivDamageBonus: SaturatingMul(amount, CurrentShivTargets(view))));
        return SaturatingAdd(damageUplift, PhantomBladesRetentionPotential(view, parent));
    }

    private static int PhantomBladesRetentionPotential(scoped in View view, in Battle parent)
    {
        Dictionary<int, int> parentRetainedShivs = [];
        for (int index = 0; index < parent.HandCount; index++)
        {
            Card card = parent.Cards[index];
            if ((view.Stats(card).Tags & CardTag.Shiv) == 0
                || !view.RetainsThisTurn(parent, card))
            {
                continue;
            }
            parentRetainedShivs[card.Upgrade] =
                parentRetainedShivs.GetValueOrDefault(card.Upgrade) + 1;
        }

        List<RetainedHandCardFact> poweredRetained = new(view.HandCount);
        List<RetainedHandCardFact> baselineRetained = new(view.HandCount);
        for (int index = 0; index < view.HandCount; index++)
        {
            Card card = view.HandCard(index);
            if (!view.RetainsThisTurn(card))
                continue;
            RetainedHandCardFact fact = new(
                Math.Max(0, view.CardValue(card)), view.LocalEnergyCost(card));
            poweredRetained.Add(fact);
            if ((view.Stats(card).Tags & CardTag.Shiv) == 0)
            {
                baselineRetained.Add(fact);
                continue;
            }
            if (parentRetainedShivs.TryGetValue(card.Upgrade, out int remaining) && remaining > 0)
            {
                parentRetainedShivs[card.Upgrade] = remaining - 1;
                baselineRetained.Add(fact);
            }
        }
        if (poweredRetained.Count == baselineRetained.Count)
            return 0;

        int drawPerTurn = view.ModifiedHandDraw();
        int[] nextDrawValues = DrawPileValues(view, drawPerTurn);
        int energy = view.ModifiedMaxEnergy();
        int handLimit = view.Battle.MaxHandSize;
        int baseline = RetainedHandTransition.Evaluate(
            energy, handLimit, drawPerTurn, baselineRetained.ToArray(), nextDrawValues).NetValue;
        int powered = RetainedHandTransition.Evaluate(
            energy, handLimit, drawPerTurn, poweredRetained.ToArray(), nextDrawValues).NetValue;
        return Math.Max(0, powered - baseline);
    }

    /// <summary>
    /// <c>SpeedsterProjectionPotential</c>: every non-hand draw in the current hand's draw options
    /// deals the solved Speedster amount to every living enemy.
    /// </summary>
    private static int SpeedsterProjectionPotential(scoped in View view, in Battle parent)
    {
        int damagePerDraw = view.PowerAmountGain(parent, PowerEvidenceIds.Speedster);
        if (damagePerDraw == 0)
            return 0;
        PowerTurnCardOption[] options = BuildHandOptions(view);
        if (options.Length == 0)
            return 0;
        int incoming = view.IncomingDamage();
        return Math.Min(
            view.Features.EnemyHp,
            MarginalFrontierValue(
                PowerTurnFrontier.Build(view.Battle.Energy, incoming, options),
                PowerTurnFrontier.Build(view.Battle.Energy, incoming, options,
                    damagePerDraw: damagePerDraw,
                    damageTargets: Math.Max(1, view.Features.AliveEnemyCount))));
    }

    /// <summary>
    /// <c>ToolsOfTheTradeProjectionPotential</c>: the next-turn draw/discard hand transition, with
    /// Sly cards credited when discarded.
    /// </summary>
    private static int ToolsOfTheTradeProjectionPotential(scoped in View view, in Battle parent)
    {
        if (view.PowerAmountGain(parent, PowerEvidenceIds.ToolsOfTheTrade) == 0)
            return 0;
        int remainingTurns = view.RemainingTurns();
        if (remainingTurns <= 1)
            return 0;
        int drawPerTurn = view.ModifiedHandDraw();
        List<DrawDiscardCardFact> retainedCards = new(view.HandCount);
        for (int index = 0; index < view.HandCount; index++)
        {
            Card card = view.HandCard(index);
            if (!view.RetainsThisTurn(card))
                continue;
            int value = Math.Max(0, view.CardValue(card));
            retainedCards.Add(new(value,
                Kernel.IsSlyThisTurn(view.Battle, card) ? Math.Max(1, value) : 0));
        }
        int nextCount = Math.Min(drawPerTurn + 1, view.Battle.DrawCount);
        List<DrawDiscardCardFact> nextDrawCards = new(nextCount);
        for (int index = 0; index < nextCount; index++)
        {
            Card card = view.LiveCard(view.HandCount + index);
            int value = Math.Max(0, view.CardValue(card));
            nextDrawCards.Add(new(value,
                Kernel.IsSlyThisTurn(view.Battle, card) ? Math.Max(1, value) : 0));
        }
        int transition = Math.Max(0, DrawDiscardTransition.Evaluate(
            view.Battle.MaxHandSize, drawPerTurn,
            retainedCards.ToArray(), nextDrawCards.ToArray()).NetValue);
        // The transition prices only the next draw/discard exchange; the old resource estimate
        // priced the whole remaining fight. Keep a conservative floor equal to that total spread
        // over the maximum horizon so a blocked next-turn exchange does not erase the recurring
        // draw engine value.
        int horizon = Math.Max(1, ScoreWeights.SetupValueHorizonTurns);
        int resourceFloor = SaturatingMul(
            Math.Max(1, view.EnergyUnit()), Math.Max(0, remainingTurns - 1)) / horizon;
        return Math.Max(transition, resourceFloor);
    }

    /// <summary>
    /// <c>TrackingProjectionPotential</c>: the current-hand frontier marginal of the solved Tracking
    /// percent on attacks that hit Weak enemies.
    /// </summary>
    private static int TrackingProjectionPotential(scoped in View view, in Battle parent)
    {
        int damagePercent = view.PowerAmountGain(parent, PowerEvidenceIds.Tracking);
        if (damagePercent == 0)
            return 0;
        PowerTurnCardOption[] options = BuildHandOptions(view);
        if (options.Length == 0)
            return 0;
        PowerTurnCardOption[] powered = BuildHandOptions(
            view, weakAttackBonusPercent: damagePercent);
        int incoming = view.IncomingDamage();
        return Math.Min(
            view.Features.EnemyHp,
            MarginalFrontierValue(
                PowerTurnFrontier.Build(view.Battle.Energy, incoming, options),
                PowerTurnFrontier.Build(view.Battle.Energy, incoming, powered)));
    }

    /// <summary>
    /// <c>WellLaidPlansProjectionPotential</c>: the next-turn hand transition when the whole current
    /// hand may be retained.
    /// </summary>
    private static int WellLaidPlansProjectionPotential(scoped in View view, in Battle parent)
    {
        if (view.PowerAmountGain(parent, PowerEvidenceIds.WellLaidPlans) == 0)
            return 0;
        int drawPerTurn = view.ModifiedHandDraw();
        List<RetainedHandCardFact> retainedCards = new(view.HandCount);
        for (int index = 0; index < view.HandCount; index++)
        {
            Card card = view.HandCard(index);
            retainedCards.Add(new(
                Math.Max(0, view.CardValue(card)), view.LocalEnergyCost(card)));
        }
        int[] nextDrawValues = DrawPileValues(view, drawPerTurn);
        return Math.Max(0, RetainedHandTransition.Evaluate(
            view.ModifiedMaxEnergy(), view.Battle.MaxHandSize, drawPerTurn,
            retainedCards.ToArray(), nextDrawValues).NetValue);
    }

    /// <summary>
    /// <c>WraithFormProjectionPotential</c>: eight times the projected HP the child state saves
    /// against the parent state. Intangible is already part of the scored threat projection.
    /// </summary>
    private static int WraithFormProjectionPotential(scoped in View view, in Battle parent,
        int parentProjectedPlayerHp)
    {
        int intangible = view.PowerAmountGain(parent, PowerEvidenceIds.Intangible);
        if (intangible == 0)
            return 0;
        long prevented = Math.Max(
            0, (long)view.Features.ProjectedPlayerHp - parentProjectedPlayerHp);
        return (int)Math.Min(int.MaxValue, prevented * 8);
    }

    private static int[] DrawPileValues(scoped in View view, int count)
    {
        int available = Math.Min(Math.Max(0, count), view.Battle.DrawCount);
        int[] values = new int[available];
        for (int index = 0; index < available; index++)
            values[index] = Math.Max(0, view.CardValue(view.LiveCard(view.HandCount + index)));
        return values;
    }

    private static int CurrentShivTargets(scoped in View view)
        => view.PlayerPowerAmount(PowerEvidenceIds.FanOfKnives) > 0
            ? Math.Max(1, view.Features.AliveEnemyCount)
            : 1;

    private static int SimpleProjection(scoped in View view, in Battle parent,
        string id, int turns, int incoming, int enemies, int gain)
    {
        return id switch
        {
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
        => BuildOptions(view, 0, view.HandCount, view.HandCount, currentTurn: true,
            weakAttackBonusPercent);

    private static PowerTurnCardOption[] BuildDrawOptions(scoped in View view,
        int drawOffset, int count, int projectedHandCount,
        int weakAttackBonusPercent = 0)
    {
        int available = Math.Min(
            Math.Max(0, count), Math.Max(0, view.Battle.DrawCount - drawOffset));
        if (available == 0)
            return [];
        return BuildOptions(view, view.HandCount + drawOffset, available, projectedHandCount,
            currentTurn: false, weakAttackBonusPercent);
    }

    private static PowerTurnCardOption[] BuildOptions(scoped in View view,
        int start, int count, int projectedHandCount, bool currentTurn,
        int weakAttackBonusPercent = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(weakAttackBonusPercent);
        ArgumentOutOfRangeException.ThrowIfNegative(projectedHandCount);
        if (count == 0)
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
        int shivTargets = CurrentShivTargets(view);
        List<PowerTurnCardOption> options = new(count);
        for (int index = 0; index < count; index++)
        {
            Card card = view.LiveCard(start + index);
            if (view.HasKeyword(card, Keywords.Unplayable))
                continue;
            int cost = currentTurn
                ? Math.Max(0, Kernel.EnergyCostWithModifiers(
                    view.Battle, view.Rules, card, Pile.Hand))
                : view.LocalEnergyCost(card);
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
