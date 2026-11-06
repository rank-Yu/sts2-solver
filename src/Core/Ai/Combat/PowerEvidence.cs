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

        /// <summary>
        /// HP that still has to be removed before the combat can end: current HP plus the HP of
        /// Test Subject's later forms and Decimillipede's reattach heal, excluding secondary enemies
        /// (MinionPower, including EyeWithTeeth and Parafright) because the last primary enemy's death
        /// ends the combat and kills them. Enemy block and future summons are not included; healing
        /// other than the Decimillipede reattach amount that a dead segment must be dealt again is
        /// not projected.
        /// </summary>
        public int RemainingEnemyHp() => _scorer.RemainingPrimaryEnemyHp(_battle);

        /// <summary>Signed parent-to-child delta of the specific effect, not unrelated powers.</summary>
        public int PowerAmountGain(in Battle parent, ushort powerId)
        {
            int before = Kernel.GetAmount(parent, Limits.PlayerOwner, powerId);
            int after = Kernel.GetAmount(_battle, Limits.PlayerOwner, powerId);
            return (int)Math.Clamp((long)after - before, 0, int.MaxValue);
        }

        public int RemainingTurns() => RemainingTurns(out _, out _);

        /// <summary>
        /// Remaining-turn estimate plus its damage model: the next turn's damage and its linear
        /// growth per later turn. Per-enemy estimates (the poison kill order in <c>Scorer</c>) reuse
        /// both through <see cref="TurnsToDeal"/>, so every caller shares one model.
        /// </summary>
        public int RemainingTurns(out long damagePerTurn, out long damageGrowth)
        {
            int drawPerTurn = Math.Max(1, _scorer.ModifiedHandDrawForEvidence(_battle));
            int liveDeckSize = Math.Max(1, _features.LiveDeckSize);
            int aliveEnemies = Math.Max(1, _features.AliveEnemyCount);

            // Damage per deck cycle from the live cards' actual attack damage (per-hit damage plus
            // current Strength, times hits and targets), not the card-value heuristic, plus the
            // other damage sources below. Energy is not limited: the estimate errs short.
            long strength = Math.Max(0, Kernel.GetAmount(
                _battle, Limits.PlayerOwner, PowerEvidenceIds.Strength));
            long attackCycleDamage = 0;
            long ongoingPerTurn = 0;
            long oneTimeDamage = 0;

            // Existing poison is valued at its next trigger (the larger end of its decay); using
            // that as a per-turn lower bound can only make the remaining-turn estimate shorter.
            long poisonTriggers = 1L
                + Math.Max(0, Kernel.GetAmount(
                    _battle, Limits.PlayerOwner, PowerEvidenceIds.Accelerant));
            if ((_battle.Flags & BattleFlags.OstyPresent) != 0 && _battle.Osty.IsAlive)
            {
                poisonTriggers += Math.Max(0, Kernel.GetAmount(
                    _battle, Limits.OstyOwner, PowerEvidenceIds.Accelerant));
            }
            poisonTriggers = Math.Clamp(poisonTriggers, 0L, int.MaxValue);
            int poisonedEnemies = 0;
            for (int index = 0; index < _battle.EnemyCount; index++)
            {
                ref readonly Enemy enemy = ref _battle.Enemies[index];
                if ((enemy.Flags & EnemyFlags.InCombat) == 0 || !enemy.Creature.IsAlive)
                    continue;
                int currentHp = Math.Max(0, enemy.Creature.CurrentHp);
                int poison = Math.Max(0, EnemyPowerAmount(index, PowerEvidenceIds.Poison));
                if (poison > 0)
                {
                    poisonedEnemies++;
                    ongoingPerTurn += PoisonStackProjection.DelayedStackDamage(
                        poison, (int)poisonTriggers, 1, currentHp);
                }
                oneTimeDamage += OneTimeDamage(_battle, index, currentHp);
            }

            // Poison and Doom powers already in play. Noxious Fumes adds poison to every enemy at
            // each player turn start; Envenom adds it per attack hit; Countdown adds Doom to a random
            // enemy at each player turn start. Corrosive Wave removes itself at the end of the
            // player's turn (CorrosiveWavePower.AfterSideTurnEnd), so it adds nothing to later turns.
            long noxious = Math.Max(0, Kernel.GetAmount(
                _battle, Limits.PlayerOwner, PowerEvidenceIds.NoxiousFumes));
            ongoingPerTurn += noxious * aliveEnemies;
            ongoingPerTurn += Math.Max(0, Kernel.GetAmount(
                _battle, Limits.PlayerOwner, PowerEvidenceIds.Countdown));
            // Channeled orbs: Lightning hits one enemy and Glass every enemy each turn; Dark's
            // passive adds the same amount to its eventual evoke.
            for (int index = 0; index < _battle.OrbCount; index++)
            {
                string orbId = Registry.Orb(_battle.Orbs[index].Id).NativeId;
                if (orbId is not ("LIGHTNING_ORB" or "DARK_ORB" or "GLASS_ORB"))
                    continue;
                long passive = (long)Math.Max(0m, Math.Ceiling(
                    Kernel.OrbPassiveValue(_battle, _rules, index)));
                ongoingPerTurn += orbId == "GLASS_ORB" ? passive * aliveEnemies : passive;
            }
            long envenom = Math.Max(0, Kernel.GetAmount(
                _battle, Limits.PlayerOwner, PowerEvidenceIds.Envenom));
            // Reaper Form applies Doom equal to the unblocked attack damage times its amount.
            long reaper = Math.Max(0, Kernel.GetAmount(
                _battle, Limits.PlayerOwner, PowerEvidenceIds.ReaperForm));

            long otherCycleDamage = 0;
            // A channeled damaging orb is worth about its evoke plus one passive (Focus applies to
            // both); Glass hits every enemy.
            long focus = Math.Max(0, Kernel.GetAmount(
                _battle, Limits.PlayerOwner, PowerEvidenceIds.Focus));
            long lightningChannelValue = 8 + 3 + 2 * focus;
            long darkChannelValue = 6 + 6 + 2 * focus;
            long glassChannelValue = 8L * aliveEnemies;
            long vulnerablePerCycle = 0;
            long strengthPowerPerCycle = 0;
            long extraDrawPerCycle = 0;
            long poisonPerCycle = 0;
            long doomPerCycle = 0;
            bool allEnemyPoison = noxious > 0;
            long attackHitsPerCycle = 0;
            long envenomPerCycle = 0;
            int liveCount = HandCount + _battle.DrawCount + _battle.DiscardCount;
            for (int index = 0; index < liveCount; index++)
            {
                Card card = LiveCard(index);
                CardStats stats = Stats(card);
                ref readonly CardContentFacts facts = ref CardContentFacts.Get(card.Id);
                int targets = CardContentFacts.IsAllEnemies(card.Id, stats) ? aliveEnemies : 1;
                CardScore score = Score(card);
                CardType type = Type(card);
                if (type == CardType.Attack)
                {
                    int perHit = score.Calculated != CalculatedKind.None
                        ? Math.Max(0, (int)Math.Round(_scorer.BranchCardValueForEvidence(_battle, card)))
                        : Damage(card);
                    int hits = Math.Max(1, AttackHits(card));
                    if (perHit > 0)
                        attackCycleDamage += (perHit + strength) * hits * targets;
                    attackHitsPerCycle += (long)hits * targets;
                }
                else if (facts.DamageVar >= 0)
                {
                    int damage = Math.Max(0, Var(card, facts.DamageVar));
                    int repeat = facts.RepeatVar >= 0 ? Math.Max(1, Var(card, facts.RepeatVar)) : 1;
                    otherCycleDamage += (long)damage * repeat;
                }

                for (int variable = 0; variable < facts.PoisonVars.Length; variable++)
                {
                    poisonPerCycle += (long)Math.Max(0, Var(card, facts.PoisonVars[variable])) * targets;
                    allEnemyPoison |= targets > 1;
                }
                for (int variable = 0; variable < facts.DoomVars.Length; variable++)
                    doomPerCycle += (long)Math.Max(0, Var(card, facts.DoomVars[variable])) * targets;
                if (stats.Target is TargetType.AnyEnemy or TargetType.AllEnemies or TargetType.RandomEnemy)
                {
                    for (int variable = 0; variable < facts.VulnerableVars.Length; variable++)
                        vulnerablePerCycle += Math.Max(0, Var(card, facts.VulnerableVars[variable]));
                }
                if (type == CardType.Power)
                {
                    for (int variable = 0; variable < facts.StrengthPowerVars.Length; variable++)
                        strengthPowerPerCycle += Math.Max(0, Var(card, facts.StrengthPowerVars[variable]));
                }
                if (score.CardsVar >= 0)
                    extraDrawPerCycle += Math.Max(0, Var(card, score.CardsVar));
                for (int variable = 0; variable < facts.OstyDamageVars.Length; variable++)
                    otherCycleDamage += Math.Max(0, Var(card, facts.OstyDamageVars[variable]));
                otherCycleDamage += facts.LightningChannels * lightningChannelValue
                    + facts.DarkChannels * darkChannelValue
                    + facts.GlassChannels * glassChannelValue;
                for (int variable = 0; variable < facts.EnvenomVars.Length; variable++)
                    envenomPerCycle += Math.Max(0, Var(card, facts.EnvenomVars[variable]));
            }
            // Cards seen per turn: the hand draw plus what the drawn draw cards add (one level).
            // Energy is not limited: measured against the quality fights' actual lengths this keeps
            // the estimate short more often than an energy cap (which also drops energy gain,
            // 0-cost and generated cards the estimate does not model).
            int cardsPerTurn = (int)Math.Clamp(
                drawPerTurn + PerTurn(extraDrawPerCycle, drawPerTurn, liveDeckSize),
                drawPerTurn, Math.Max(drawPerTurn, liveDeckSize));
            // Vulnerable (+50% attack damage) for the share of turns the deck keeps it applied.
            long vulnerableTurnsPerTurn = PerTurn(vulnerablePerCycle, cardsPerTurn, liveDeckSize);
            long vulnerablePercent = 100 + 50 * Math.Min(1L, vulnerableTurnsPerTurn);
            attackCycleDamage = attackCycleDamage * vulnerablePercent / 100;
            long cardPoisonPerCycle = poisonPerCycle + envenomPerCycle * attackHitsPerCycle;
            long cycleDamage = attackCycleDamage * (1 + reaper) + otherCycleDamage
                + cardPoisonPerCycle + doomPerCycle;
            drawPerTurn = cardsPerTurn;
            long attackHitsPerTurn = PerTurn(attackHitsPerCycle, drawPerTurn, liveDeckSize);
            ongoingPerTurn += envenom * attackHitsPerTurn;

            long deckDamagePerTurn = PerTurn(cycleDamage, drawPerTurn, liveDeckSize);
            damagePerTurn = Math.Max(1, deckDamagePerTurn) + ongoingPerTurn;

            // Linear growth. Poison stacks applied each turn keep triggering on later turns while
            // every poisoned enemy loses one stack per trigger, so the per-turn poison damage rises
            // by (applied - decay) * triggers. Doom does not grow: it never triggers again, each
            // turn's new Doom lowers the kill line once, like direct damage. Demon Form's Strength
            // raises every later attack hit (and Reaper Form's Doom from it).
            long poisonAppliedPerTurn = PerTurn(cardPoisonPerCycle, drawPerTurn, liveDeckSize)
                + noxious * aliveEnemies + envenom * attackHitsPerTurn;
            long decayingEnemies = allEnemyPoison ? aliveEnemies : Math.Max(1, poisonedEnemies);
            long poisonGrowth = poisonAppliedPerTurn <= 0
                ? 0
                : Math.Max(0, poisonAppliedPerTurn - decayingEnemies * poisonTriggers) * poisonTriggers;
            // Strength per turn: Demon Form in play, plus power cards (Inflame and the like) played
            // as they are drawn.
            long strengthPerTurn = Math.Max(0, Kernel.GetAmount(
                    _battle, Limits.PlayerOwner, PowerEvidenceIds.DemonForm))
                + PerTurn(strengthPowerPerCycle, drawPerTurn, liveDeckSize);
            damageGrowth = poisonGrowth + strengthPerTurn * attackHitsPerTurn * (1 + reaper);

            long enemyHp = Math.Max(0, (long)_features.EnemyHp - oneTimeDamage);
            return TurnsToDeal(enemyHp, damagePerTurn, damageGrowth,
                ScoreWeights.SetupValueHorizonTurns);
        }

        /// <summary>
        /// Turns each enemy lives under the shared damage model (<see cref="RemainingTurns(out long, out long)"/>)
        /// when enemies die in ascending effective-HP order, ties by slot: the turns needed to deal
        /// the HP of the enemies killed before it plus its own (effective HP minus Demise and Doom),
        /// at most R, so the last enemy lives R turns and a single enemy is unchanged. A living
        /// enemy without effective HP keeps R; enemies outside the combat or dead without a pending
        /// revive get 0. <paramref name="life"/> is indexed by enemy slot and fully written.
        /// Returns R.
        /// </summary>
        public int FillEnemyLifeTurns(Span<int> life)
        {
            int remaining = RemainingTurns(out long perTurn, out long growth);
            int count = Math.Min(life.Length, (int)_battle.EnemyCount);
            life.Clear();
            Span<int> hp = stackalloc int[count];
            for (int index = 0; index < count; index++)
            {
                ref readonly Enemy enemy = ref _battle.Enemies[index];
                int effective = (enemy.Flags & EnemyFlags.InCombat) == 0
                    ? 0
                    : _scorer.EffectiveEnemyHpForEvidence(_battle, index);
                if (effective <= 0)
                {
                    hp[index] = -1;
                    life[index] = (enemy.Flags & EnemyFlags.InCombat) != 0 && enemy.Creature.IsAlive
                        ? remaining
                        : 0;
                    continue;
                }
                if (enemy.Creature.IsAlive)
                    effective -= OneTimeDamage(_battle, index, Math.Max(0, enemy.Creature.CurrentHp));
                hp[index] = Math.Max(0, effective);
            }
            // The last enemy in the kill order lives exactly R turns (the whole-fight estimate), so
            // a single enemy is unchanged.
            int last = -1;
            for (int index = 0; index < count; index++)
            {
                if (hp[index] >= 0 && (last < 0 || hp[index] >= hp[last]))
                    last = index;
            }
            for (int index = 0; index < count; index++)
            {
                int own = hp[index];
                if (own < 0)
                    continue;
                if (index == last)
                {
                    life[index] = remaining;
                    continue;
                }
                long cumulative = 0;
                for (int other = 0; other < count; other++)
                {
                    int value = hp[other];
                    if (value >= 0 && (value < own || (value == own && other <= index)))
                        cumulative += value;
                }
                life[index] = TurnsToDeal(cumulative, perTurn, growth, remaining);
            }
            return remaining;
        }

        /// <summary>
        /// Enemy-turns left: the sum over enemies of their kill-order life minus
        /// <paramref name="offset"/> turns (1 excludes the current turn). Recurring all-enemy
        /// effects reach each enemy only while it lives, so they scale with this sum instead of
        /// living enemies times R; a single enemy gives R - offset.
        /// </summary>
        public int EnemyTurns(int offset)
        {
            Span<int> life = stackalloc int[_battle.EnemyCount];
            FillEnemyLifeTurns(life);
            long total = 0;
            for (int index = 0; index < life.Length; index++)
                total += Math.Max(0, life[index] - offset);
            return (int)Math.Min(int.MaxValue, total);
        }

        private static long PerTurn(long perCycle, int drawPerTurn, int liveDeckSize)
            => (perCycle * drawPerTurn + liveDeckSize - 1) / liveDeckSize;

        /// <summary>
        /// One-time damage an enemy is already committed to: Demise, and Doom up to its current HP
        /// (the enemy dies when its HP is at most its Doom at its turn end, DoomPower.BeforeSideTurnEnd).
        /// </summary>
        public static int OneTimeDamage(in Battle battle, int index, int currentHp)
        {
            byte owner = Kernel.EnemyOwner((byte)index);
            int demise = Math.Max(0, Kernel.GetAmount(battle, owner, PowerEvidenceIds.Demise));
            int doom = Math.Max(0, Kernel.GetAmount(battle, owner, PowerEvidenceIds.Doom));
            return (int)Math.Min(Math.Max(0, currentHp), (long)demise + doom);
        }

        /// <summary>
        /// Fewest turns, at least one and at most <paramref name="maxTurns"/>, whose cumulative
        /// damage reaches <paramref name="hp"/> when turn k deals
        /// <paramref name="firstTurn"/> + <paramref name="growth"/> * (k - 1).
        /// </summary>
        public static int TurnsToDeal(long hp, long firstTurn, long growth, int maxTurns)
        {
            long perTurn = Math.Max(1, firstTurn);
            long dealt = 0;
            for (int turn = 1; turn < maxTurns; turn++)
            {
                dealt += perTurn + Math.Max(0, growth) * (turn - 1);
                if (dealt >= hp)
                    return turn;
            }
            return Math.Max(1, maxTurns);
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
        /// Raw damage of the current enemy attack intents before player block and before the
        /// DieForYou redirect. Used to estimate Osty HP loss for Necro Mastery.
        /// </summary>
        public int CurrentIncomingAttackDamage()
        {
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
                if (move == rules.StunnedNode || move == rules.ReviveNode
                    || (uint)move >= (uint)rules.Nodes.Length)
                {
                    continue;
                }
                MonsterNode target = rules.Nodes[move];
                MonsterDef def = Registry.Monster(enemy.MonsterId);
                byte owner = Kernel.EnemyOwner(index);
                for (int attack = 0; attack < target.Attacks.Length; attack++)
                {
                    MonsterAttack intent = target.Attacks[attack];
                    int damage = def.ModifyAttackDamage(
                        _battle, _rules, owner, move, attack, intent.BaseDamage);
                    if (damage <= 0)
                        continue;
                    int repeats = Math.Max(1, def.ModifyAttackRepeats(
                        _battle, _rules, owner, move, attack, intent.Repeats));
                    total += (long)damage * repeats;
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

        /// <summary>
        /// Distinct orb types the player will hold: the current orbs' types plus the types the live
        /// cards channel (Chaos's random orb counts as one more type), at most the orb slots.
        /// </summary>
        public int ReachableOrbTypes()
        {
            int mask = 0;
            int otherTypes = 0;
            ulong otherMask = 0;
            for (int index = 0; index < _battle.OrbCount; index++)
            {
                int bit = CardContentFacts.OrbTypeBit(Registry.Orb(_battle.Orbs[index].Id).NativeId);
                if (bit != 0)
                {
                    mask |= bit;
                }
                else if (_battle.Orbs[index].Id < 64 && (otherMask & (1UL << _battle.Orbs[index].Id)) == 0)
                {
                    otherMask |= 1UL << _battle.Orbs[index].Id;
                    otherTypes++;
                }
            }
            for (int index = 0; index < LiveCount; index++)
                mask |= CardContentFacts.Get(LiveCard(index).Id).ChanneledOrbTypes;
            int types = System.Numerics.BitOperations.PopCount((uint)(mask & CardContentFacts.KnownOrbTypes))
                + otherTypes;
            if ((mask & CardContentFacts.RandomOrbType) != 0 && types < CardContentFacts.KnownOrbTypeCount)
                types++;
            return Math.Min(types, (int)_battle.OrbCapacity);
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

        public int CountCardsCostAtLeast(int minimum)
        {
            int count = 0;
            for (int index = 0; index < LiveCount; index++)
            {
                if (Kernel.EnergyCostWithModifiers(_battle, _rules, LiveCard(index), Pile.Hand)
                    >= minimum)
                {
                    count++;
                }
            }
            return count;
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

        /// <summary>An actual Osty or a live card that can summon one.</summary>
        public bool HasOstyOrSummonSource()
        {
            if (OstyPresent)
                return true;
            for (int index = 0; index < LiveCount; index++)
            {
                Card card = LiveCard(index);
                if (NativeId(card).Contains("OSTY", StringComparison.OrdinalIgnoreCase)
                    || HasVarFragment(card, "Summon"))
                {
                    return true;
                }
            }
            return false;
        }

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
            "BIASED_COGNITION" => view.Battle.OrbCount > 0 || DefectHasOrbChannelSource(view),
            "BUFFER" => view.PowerAmountGain(parent, PowerEvidenceIds.Buffer) > 0
                && (view.IncomingDamage() > 0 || view.IncomingAttackHits(nextTurn: false) > 0
                    || view.IncomingAttackHits(nextTurn: true) > 0),
            "BULK_UP" => hasAttack && hasBlockSkill,
            "CAPACITOR" => view.Battle.OrbCount > 0 || DefectHasOrbChannelSource(view),
            "CONSUMING_SHADOW" => view.Battle.OrbCount > 0,
            "COOLANT" => view.ReachableOrbTypes() > 0,
            "CREATIVE_AI" => remainingTurns > 1,
            "DEFRAGMENT" => view.Battle.OrbCount > 0 || DefectHasOrbChannelSource(view),
            "ECHO_FORM" => remainingTurns > 1 && (hasAttack || hasBlockSkill),
            "FERAL" => view.CountZeroCostAttack() > 0,
            "HAILSTORM" => view.CountOrb("FROST") > 0,
            "ITERATION" => view.StatusOrCurseInHand() || DefectStatusSources(view) > 0,
            "LOOP" => view.Battle.OrbCount > 0,
            "MACHINE_LEARNING" => remainingTurns > 1,
            "SMOKESTACK" => DefectStatusSources(view) > 0,
            "SPINNER" => remainingTurns > 1,
            "STORM" => view.CountType(CardType.Power) > 0,
            "SUBROUTINE" => view.PowerAmountGain(parent, PowerEvidenceIds.Subroutine) > 0
                && view.PlayablePowerSources() > 0,
            "THUNDER" => view.CountOrb("LIGHTNING") > 0,
            "TRASH_TO_TREASURE" => DefectStatusSources(view) > 0,

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
            "DEVOUR_LIFE" => view.CountSouls() > 0 || view.HasOstyOrSummonSource(),
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
            "CRUELTY" => PerTriggerDamage(view, gain, view.TotalAttackDamage() / 100),
            "DARK_EMBRACE" => PerTriggerResource(gain * Math.Max(1, attacks), view.CountExhaustSources()),
            "DEMON_FORM" => GrowthFrontier(view,
                damagePerAttack: gain,
                remainingTurns: TriangularTurns(view.RemainingTurns())),
            "FEEL_NO_PAIN" => PerTriggerBlock(gain, view.CountExhaustSources(), incoming),
            "HELLRAISER" => PerTurnDamage(view, Math.Max(1, view.MaxDamage()), turns, 1),
            "INFERNO" => PerTurnDamage(view, gain, view.EnemyTurns(1), 1),
            "INFLAME" => GrowthFrontier(view, damagePerAttack: gain,
                remainingTurns: view.RemainingTurns()),
            "JUGGERNAUT" => PerTriggerDamage(view, gain, Math.Max(1, view.CountVar("Block"))),
            "JUGGLING" => PerTurnResource(Math.Max(1, view.MaxDamage()), turns),
            "PYRE" => PerTurnResource(gain, turns),
            "RUPTURE" => GrowthFrontier(view, damagePerAttack: SaturatingMul(gain,
                Math.Max(1, view.CountVar("SelfDamage") + view.CountVar("HpLoss")
                    + view.CountVar("LoseHp"))),
                remainingTurns: view.RemainingTurns()),
            "STAMPEDE" => PerTurnDamage(view, Math.Max(1, view.MaxDamage()), turns, 1),
            "STONE_ARMOR" => PerTurnBlock(gain, turns, incoming),
            "UNMOVABLE" => PerTurnBlock(Math.Max(1, view.MaxBlock()), 1, incoming),
            "VICIOUS" => PerTriggerResource(gain * Math.Max(1, view.EnergyUnit()),
                view.CountVar("Vulnerable")),

            // Colorless. AUTOMATION counts one payout per ten future draws; CALAMITY and PANACHE
            // use their real trigger counts; FASTEN/PREP_TIME/PROWESS use the current-hand growth
            // frontier; ROLLING_BOULDER is a per-turn damage source. All damage-valued results are
            // bounded by the HP still required to end the combat.
            "AUTOMATION" => AutomationProjectionPotential(view, parent, turns),
            "CALAMITY" => CalamityProjectionPotential(view, parent),
            "ENTROPY" => PerTurnResource(view.EnergyUnit(), turns),
            "ETERNAL_ARMOR" => PerTurnBlock(gain, turns, incoming),
            "FASTEN" => GrowthFrontier(view, blockPerSkillBonus: gain,
                remainingTurns: view.RemainingTurns()),
            "MAYHEM" => PerTurnResource(view.EnergyUnit(), turns),
            "NOSTALGIA" => PerTurnResource(view.EnergyUnit(), turns),
            "PANACHE" => PanacheProjectionPotential(view, parent),
            "PREP_TIME" => GrowthFrontier(view, damagePerAttack: gain,
                remainingTurns: view.RemainingTurns()),
            "PROWESS" => GrowthFrontier(view,
                blockPerSkillBonus: view.PowerAmountGain(parent, PowerEvidenceIds.Dexterity),
                damagePerAttack: view.PowerAmountGain(parent, PowerEvidenceIds.Strength),
                remainingTurns: view.RemainingTurns()),
            "ROLLING_BOULDER" => RollingBoulderProjectionPotential(view, parent),
            "STRATAGEM" => PerTurnResource(view.EnergyUnit(), turns),
            "ACCELERANT" => AccelerantProjectionPotential(view, parent),
            "ENVENOM" => EnvenomProjectionPotential(view, parent),
            "NOXIOUS_FUMES" => NoxiousFumesProjectionPotential(view, parent),
            "AFTERIMAGE" => AfterimageProjectionPotential(view, parent),
            "ABRASIVE" => FootworkProjectionPotential(view, parent),
            "ACCURACY" => AccuracyProjectionPotential(view, parent),
            "FAN_OF_KNIVES" => FanOfKnivesProjectionPotential(view, parent),
            "FOOTWORK" => FootworkProjectionPotential(view, parent),
            "INFINITE_BLADES" => InfiniteBladesProjectionPotential(view, parent),
            "MASTER_PLANNER" => MasterPlannerProjectionPotential(view, parent),
            "PHANTOM_BLADES" => PhantomBladesProjectionPotential(view, parent),
            "SERPENT_FORM" => SerpentFormProjectionPotential(view, parent),
            "SPEEDSTER" => SpeedsterProjectionPotential(view, parent),
            "TOOLS_OF_THE_TRADE" => ToolsOfTheTradeProjectionPotential(view, parent),
            "TRACKING" => TrackingProjectionPotential(view, parent),
            "WELL_LAID_PLANS" => WellLaidPlansProjectionPotential(view, parent),
            "WRAITH_FORM" => WraithFormProjectionPotential(
                view, parent, parentProjectedPlayerHp),

            // Necrobinder: the upstream opening projections with the remaining-to-kill HP cap on
            // every damage term. The power amount is part of the trigger count for stack-scaling
            // generators, and Lethality is applied on every remaining turn instead of once.
            "CALCIFY" => PerTurnDamage(view, gain, turns, 1),
            "CALL_OF_THE_VOID" => PerTurnResource(
                SaturatingMul(gain, Math.Max(1, view.EnergyUnit())), turns),
            "COUNTDOWN" => PerTurnDamage(view, gain, turns, 1),
            "DANSE_MACABRE" => PerTriggerBlock(gain,
                view.CountCardsCostAtLeast(2), incoming),
            "DEMESNE" => PerTurnResource(
                SaturatingMul(gain, Math.Max(1, view.EnergyUnit())), turns),
            "DEVOUR_LIFE" => PerTriggerResource(gain, view.CountSouls()),
            "FRIENDSHIP" => FriendshipProjectionPotential(view, parent, gain),
            "HAUNT" => PerTriggerDamage(view, gain, view.CountSouls()),
            "LETHALITY" => LethalityProjectionPotential(view, gain, turns),
            "NECRO_MASTERY" => NecroMasteryProjectionPotential(view, parent),
            "NEUROSURGE" => NeurosurgeProjectionPotential(view, gain),
            "PAGESTORM" => PerTriggerResource(
                SaturatingMul(gain, Math.Max(1, view.EnergyUnit())),
                view.CountKeyword(Keywords.Ethereal)),
            "REAPER_FORM" => PerTriggerDamage(view, 1, view.TotalAttackDamage()),
            "SENTRY_MODE" => PerTurnResource(
                SaturatingMul(gain, Math.Max(1, view.EnergyUnit())), turns),
            "SHROUD" => PerTriggerBlock(gain,
                Math.Max(1, view.CountVar("Doom")), incoming),
            "SLEIGHT_OF_FLESH" => SleightOfFleshProjectionPotential(view, gain),
            "SPIRIT_OF_ASH" => PerTriggerBlock(gain,
                view.CountKeyword(Keywords.Ethereal), incoming),
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
            "CALCIFY" => view.PowerAmountGain(parent, PowerEvidenceIds.Calcify),
            "CALL_OF_THE_VOID" => view.PowerAmountGain(parent, PowerEvidenceIds.CallOfTheVoid),
            "COUNTDOWN" => view.PowerAmountGain(parent, PowerEvidenceIds.Countdown),
            "DANSE_MACABRE" => view.PowerAmountGain(parent, PowerEvidenceIds.DanseMacabre),
            "DEMESNE" => view.PowerAmountGain(parent, PowerEvidenceIds.Demesne),
            "DEVOUR_LIFE" => view.PowerAmountGain(parent, PowerEvidenceIds.DevourLife),
            "FRIENDSHIP" => view.PowerAmountGain(parent, PowerEvidenceIds.Friendship),
            "HAUNT" => view.PowerAmountGain(parent, PowerEvidenceIds.Haunt),
            "NECRO_MASTERY" => view.PowerAmountGain(parent, PowerEvidenceIds.NecroMastery),
            "LETHALITY" => view.PowerAmountGain(parent, PowerEvidenceIds.Lethality),
            "NEUROSURGE" => view.PowerAmountGain(parent, PowerEvidenceIds.Neurosurge),
            "PAGESTORM" => view.PowerAmountGain(parent, PowerEvidenceIds.Pagestorm),
            "SENTRY_MODE" => view.PowerAmountGain(parent, PowerEvidenceIds.SentryMode),
            "SHROUD" => view.PowerAmountGain(parent, PowerEvidenceIds.Shroud),
            "SLEIGHT_OF_FLESH" => view.PowerAmountGain(parent, PowerEvidenceIds.SleightOfFlesh),
            "SPIRIT_OF_ASH" => view.PowerAmountGain(parent, PowerEvidenceIds.SpiritOfAsh),
            "ETERNAL_ARMOR" => view.PowerAmountGain(parent, PowerEvidenceIds.Plating),
            "ROLLING_BOULDER" => view.PowerAmountGain(parent, PowerEvidenceIds.RollingBoulder),
            "AUTOMATION" => view.PowerAmountGain(parent, PowerEvidenceIds.Automation),
            "FASTEN" => view.PowerAmountGain(parent, PowerEvidenceIds.Fasten),
            "PANACHE" => view.PowerAmountGain(parent, PowerEvidenceIds.Panache),
            "PREP_TIME" => view.PowerAmountGain(parent, PowerEvidenceIds.Vigor),
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
    /// Lethality's first attack each turn gains <c>gain</c> percent of its damage. The upstream
    /// opening projection credited one such attack; the power is active every remaining turn, so the
    /// same per-turn bonus is multiplied by the remaining-turn estimate and capped by enemy HP.
    /// </summary>
    private static int LethalityProjectionPotential(scoped in View view, int gain, int turns)
    {
        if (gain <= 0)
            return 0;
        int bonusPerTurn = (int)Math.Min(int.MaxValue,
            Math.Max(1L, (long)Math.Max(1, view.MaxDamage()) * gain / 100));
        return PerTriggerDamage(view, bonusPerTurn, Math.Max(1, turns));
    }

    /// <summary>
    /// Sleight of Flesh deals its amount whenever a debuff is applied. The upstream projection
    /// counted any number of live debuff sources as one; count each source card so multiple
    /// Vulnerable/Weak/Doom/Poison sources are not collapsed to a single trigger.
    /// </summary>
    private static int SleightOfFleshProjectionPotential(scoped in View view, int gain)
    {
        int sources = 0;
        for (int index = 0; index < view.LiveCount; index++)
        {
            if (view.Score(view.LiveCard(index)).HasDebuffDynamicVar)
                sources++;
        }
        return PerTriggerDamage(view, gain, sources);
    }

    /// <summary>
    /// <c>AutomationProjectionPotential</c>: every ten cards drawn return the newly gained energy,
    /// so payouts are <c>drawPerTurn * turns / 10</c>, each worth one energy unit.
    /// </summary>
    private static int AutomationProjectionPotential(scoped in View view, in Battle parent,
        int turns)
    {
        int amount = view.PowerAmountGain(parent, PowerEvidenceIds.Automation);
        if (amount == 0)
            return 0;
        int payouts = AutomationPayouts(view.ModifiedHandDraw(), Math.Max(0, turns));
        return payouts <= 0
            ? 0
            : PerTriggerResource(SaturatingMul(amount, view.EnergyUnit()), payouts);
    }

    /// <summary>Every ten cards drawn pay one trigger; leftover draws do not carry a payout.</summary>
    private static int AutomationPayouts(int drawsPerTurn, int turns)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(drawsPerTurn);
        ArgumentOutOfRangeException.ThrowIfNegative(turns);
        return drawsPerTurn * turns / 10;
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
        // Each enemy receives the recurring poison only while it lives (kill-order life).
        Span<int> life = stackalloc int[view.Battle.EnemyCount];
        view.FillEnemyLifeTurns(life);
        long total = 0;
        for (int position = 0; position < view.Battle.RosterCount; position++)
        {
            int index = view.Battle.Roster[position];
            ref readonly Enemy enemy = ref view.Battle.Enemies[index];
            if (!enemy.Creature.IsAlive || (enemy.Flags & EnemyFlags.InCombat) == 0)
                continue;
            total += PoisonStackProjection.RecurringApplicationDamage(
                poisonPerTurn,
                Math.Max(0, life[index] - 1),
                Math.Max(0, enemy.Creature.CurrentHp));
        }
        return (int)Math.Min(int.MaxValue, total);
    }

    /// <summary>
    /// <c>FanOfKnivesProjectionPotential</c>: the frontier uplift when Shivs hit every living enemy,
    /// plus the single-target damage of the Shivs this copy generates. Bounded by remaining enemy HP.
    /// </summary>
    private static int FanOfKnivesProjectionPotential(scoped in View view, in Battle parent)
    {
        int targets = Math.Max(1, view.Features.AliveEnemyCount);
        PowerTurnCardOption[] baselineOptions = BuildHandOptions(view, shivTargetsOverride: 1);
        PowerTurnCardOption[] poweredOptions = BuildHandOptions(
            view, shivTargetsOverride: targets);
        int incoming = view.IncomingDamage();
        int aoeUplift = ContinuousFrontierValue(
            view,
            PowerTurnFrontier.Build(view.Battle.Energy, incoming, baselineOptions),
            PowerTurnFrontier.Build(view.Battle.Energy, incoming, poweredOptions),
            ExtraTargetTurns(view, targets));

        int generatedShivs = Math.Max(0, CountHandShivs(view) - CountHandShivs(view, parent));
        int generatedDamage = 0;
        if (generatedShivs > 0)
        {
            List<int> damages = new(view.HandCount);
            for (int index = 0; index < view.HandCount; index++)
            {
                Card card = view.HandCard(index);
                if ((view.Stats(card).Tags & CardTag.Shiv) != 0)
                    damages.Add(Math.Max(0, view.Damage(card)));
            }
            damages.Sort();
            for (int index = 0; index < Math.Min(generatedShivs, damages.Count); index++)
                generatedDamage = SaturatingAdd(generatedDamage, damages[index]);
        }
        return Math.Min(view.RemainingEnemyHp(),
            SaturatingAdd(generatedDamage, aoeUplift));
    }

    private static int CountHandShivs(scoped in View view)
    {
        int count = 0;
        for (int index = 0; index < view.HandCount; index++)
        {
            if ((view.Stats(view.HandCard(index)).Tags & CardTag.Shiv) != 0)
                count++;
        }
        return count;
    }

    private static int CountHandShivs(scoped in View view, in Battle battle)
    {
        int count = 0;
        for (int index = 0; index < battle.HandCount; index++)
        {
            if ((view.Stats(battle.Cards[index]).Tags & CardTag.Shiv) != 0)
                count++;
        }
        return count;
    }

    /// <summary>
    /// <c>InfiniteBladesProjectionPotential</c>: one generated Shiv per remaining turn, each copy
    /// dealing the base Shiv damage to the current Shiv target count. Bounded by remaining enemy HP.
    /// </summary>
    private static int InfiniteBladesProjectionPotential(scoped in View view, in Battle parent)
    {
        int amount = view.PowerAmountGain(parent, PowerEvidenceIds.InfiniteBlades);
        if (amount == 0)
            return 0;
        if (!Registry.TryCard("SHIV", out ushort shivId))
            throw new UnsupportedException("无限刀刃投影缺少 SHIV 的 Sts2Solver 定义。");
        CardStats shivStats = view.Rules.Card(shivId, 0);
        if (shivStats.Vars.Length == 0)
            throw new UnsupportedException("无限刀刃投影缺少 SHIV 的 Damage 变量。");
        int shivDamage = Math.Max(0, (int)shivStats.Vars[0]);
        int futureTurns = Math.Max(0, view.RemainingTurns() - 1);
        long raw = (long)futureTurns * amount * shivDamage * CurrentShivTargets(view);
        return Math.Min(view.RemainingEnemyHp(), (int)Math.Min(int.MaxValue, raw));
    }

    /// <summary>
    /// <c>SerpentFormProjectionPotential</c>: every played card gains the Serpent Form increment as
    /// damage on the current-hand frontier. Bounded by remaining enemy HP.
    /// </summary>
    private static int SerpentFormProjectionPotential(scoped in View view, in Battle parent)
    {
        int damagePerCard = view.PowerAmountGain(parent, PowerEvidenceIds.SerpentForm);
        if (damagePerCard == 0)
            return 0;
        PowerTurnCardOption[] options = BuildHandOptions(view);
        int incoming = view.IncomingDamage();
        return ContinuousFrontierValue(
            view,
            PowerTurnFrontier.Build(view.Battle.Energy, incoming, options),
            PowerTurnFrontier.Build(view.Battle.Energy, incoming, options,
                damagePerCard: damagePerCard),
            view.RemainingTurns());
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
        return ContinuousFrontierValue(
            view,
            PowerTurnFrontier.Build(view.Battle.Energy, incoming, options),
            PowerTurnFrontier.Build(view.Battle.Energy, incoming, options,
                blockPerCardBonus: blockPerCard),
            view.RemainingTurns());
    }

    /// <summary>
    /// <c>FootworkProjectionPotential</c> (shared by Footwork and Abrasive's Dexterity half):
    /// each Dexterity point adds one block to every block card actually played this turn. Use the
    /// live deck's expected block-card plays per turn, not the current hand repeated R times; the
    /// per-turn block is bounded by that turn's incoming damage.
    /// </summary>
    private static int FootworkProjectionPotential(scoped in View view, in Battle parent)
    {
        int dexterityGain = view.PowerAmountGain(parent, PowerEvidenceIds.Dexterity);
        if (dexterityGain == 0)
            return 0;
        int blockCardsPerTurn = BlockCardsPerTurn(view);
        if (blockCardsPerTurn == 0)
            return 0;
        return PerTurnBlock(
            SaturatingMul(dexterityGain, blockCardsPerTurn),
            view.RemainingTurns(),
            view.IncomingDamage());
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
        return ContinuousFrontierValue(
            view,
            PowerTurnFrontier.Build(view.Battle.Energy, incoming, options),
            PowerTurnFrontier.Build(view.Battle.Energy, incoming, options,
                damagePerShiv: damagePerShiv),
            view.RemainingTurns());
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
        int damageUplift = options.Length == 0 ? 0 : ContinuousFrontierValue(
            view,
            PowerTurnFrontier.Build(view.Battle.Energy, view.IncomingDamage(), options),
            PowerTurnFrontier.Build(view.Battle.Energy, view.IncomingDamage(), options,
                firstShivDamageBonus: SaturatingMul(amount, CurrentShivTargets(view))),
            view.RemainingTurns());
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
        int enemies = Math.Max(1, view.Features.AliveEnemyCount);
        // Card-effect draws in the current hand happen once this turn; the natural hand draw is
        // the only draw source that repeats every remaining turn.
        long burstDraws = 0;
        for (int index = 0; index < view.HandCount; index++)
            burstDraws += Math.Max(0, view.DrawCount(view.HandCard(index)));
        // This turn's draws hit every living enemy; each later hand draw hits the enemies still
        // alive, i.e. one trigger per enemy-turn.
        long futureEnemyDraws = (long)view.ModifiedHandDraw() * view.EnemyTurns(0);
        long triggers = Math.Min(int.MaxValue, burstDraws * enemies + futureEnemyDraws);
        return PerTriggerDamage(view, damagePerDraw, (int)triggers);
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
        return Math.Max(0, DrawDiscardTransition.Evaluate(
            view.Battle.MaxHandSize, drawPerTurn,
            retainedCards.ToArray(), nextDrawCards.ToArray()).NetValue);
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
        return ContinuousFrontierValue(
            view,
            PowerTurnFrontier.Build(view.Battle.Energy, incoming, options),
            PowerTurnFrontier.Build(view.Battle.Energy, incoming, powered),
            view.RemainingTurns());
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
        long value = prevented * 8;
        // Wraith Form loses Dexterity every turn. Each lost point removes one block per block
        // card played; value that lost prevention on the same 8-per-HP scale.
        int dexterityLoss = view.PowerAmountGain(parent, PowerEvidenceIds.WraithForm);
        int blockCardsPerTurn = BlockCardsPerTurn(view);
        long costHp = (long)dexterityLoss * blockCardsPerTurn;
        long cost = costHp * 8;
        return (int)Math.Min(int.MaxValue, Math.Max(0, value - cost));
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

    /// Regent opening projections. These stay together in one character-owned block so the shared
    /// primitive helpers above are not changed by character batches.
    /// </summary>
    private static int RegentArsenalProjection(scoped in View view, in Battle parent)
    {
        int gain = view.PowerAmountGain(parent, PowerEvidenceIds.Arsenal);
        if (gain == 0)
            return 0;
        int generatedSources = Math.Max(1, view.CountCardGeneration());
        return GrowthFrontier(view,
            damagePerAttack: SaturatingMul(gain, generatedSources),
            remainingTurns: view.RemainingTurns());
    }

    private static int RegentBlackHoleProjection(scoped in View view, in Battle parent)
    {
        int gain = view.PowerAmountGain(parent, PowerEvidenceIds.BlackHole);
        if (gain == 0)
            return 0;
        int triggers = SaturatingAdd(
            Math.Max(0, view.Battle.Stars), RegentStarCostCardCount(view));
        return PerTriggerDamage(
            view, gain, SaturatingMul(triggers, Math.Max(1, view.Features.AliveEnemyCount)));
    }

    private static int RegentStarCostCardCount(scoped in View view)
    {
        int count = 0;
        for (int index = 0; index < view.LiveCount; index++)
        {
            Card card = view.LiveCard(index);
            Pile pile = Piles.PileOf(view.Battle, index, out _);
            if ((card.Flags & CardFlags.StarCostsX) != 0
                || Kernel.StarCostWithModifiers(view.Battle, view.Rules, card, pile) > 0)
                count++;
        }
        return count;
    }

    private static int RegentMonarchsGazeProjection(scoped in View view, in Battle parent)
    {
        int gain = view.PowerAmountGain(parent, PowerEvidenceIds.MonarchsGaze);
        if (gain == 0)
            return 0;
        int hits = view.IncomingAttackHits(nextTurn: false);
        return hits == 0
            ? 0
            : PerTurnBlock(
                SaturatingMul(gain, hits), view.RemainingTurns(), view.IncomingDamage());
    }

    private static int RegentOrbitProjection(scoped in View view, in Battle parent, int turns)
    {
        int gain = view.PowerAmountGain(parent, PowerEvidenceIds.Orbit);
        if (gain == 0)
            return 0;
        int payouts = (int)Math.Min(int.MaxValue,
            (long)view.ModifiedMaxEnergy() * Math.Max(0, turns) / 4);
        return PerTriggerResource(SaturatingMul(gain, view.EnergyUnit()), payouts);
    }

    private static int RegentIncrementalEnergyUnitProjection(scoped in View view,
        in Battle parent, ushort powerId, int turns)
    {
        int gain = view.PowerAmountGain(parent, powerId);
        return gain == 0
            ? 0
            : PerTurnResource(SaturatingMul(gain, view.EnergyUnit()), turns);
    }

    private static int RegentParryProjection(scoped in View view, int turns, int incoming)
        => PerTurnBlock(view.PlayerPowerAmount(PowerEvidenceIds.Parry), turns, incoming);

    private static int RegentPillarProjection(scoped in View view, in Battle parent, int incoming)
    {
        int gain = view.PowerAmountGain(parent, PowerEvidenceIds.PillarOfCreation);
        if (gain == 0)
            return 0;
        return PerTriggerBlock(gain, Math.Max(1, view.CountCardGeneration()), incoming);
    }

    private static int RegentSealedThroneProjection(scoped in View view, in Battle parent)
    {
        int gain = view.PowerAmountGain(parent, PowerEvidenceIds.TheSealedThrone);
        return gain == 0
            ? 0
            : PerTriggerResource(SaturatingMul(gain, view.EnergyUnit()), view.LiveCount);
    }

    /// <summary>Expected attack-hit instances per turn, for Strength/Dexterity cost estimates.</summary>
    private static int AttackHitsPerTurn(scoped in View view)
    {
        long hits = 0;
        for (int index = 0; index < view.LiveCount; index++)
        {
            Card card = view.LiveCard(index);
            if (view.Type(card) == CardType.Attack)
                hits += Math.Max(1, view.AttackHits(card));
        }
        if (hits == 0)
            return 0;
        int live = Math.Max(1, view.LiveCount);
        int draws = Math.Min(Math.Max(1, view.ModifiedHandDraw()), live);
        long drawnHits = hits * draws;
        return (int)Math.Max(1, Math.Min(int.MaxValue,
            (drawnHits + live - 1) / live));
    }

    /// <summary>
    /// <c>Calamity</c>: each played Attack adds one random Attack card to hand. Value the added
    /// cards at the live deck's average attack value and the expected attack plays per turn.
    /// </summary>
    /// <summary>
    /// <c>Calamity</c>: each played Attack adds one random Attack card to hand. Value one generated
    /// card at the live attack average. Do not multiply by attack plays or remaining turns: that
    /// assumes the generated-card chain can be played every turn and makes the beam chase a growing
    /// hand, which is the state explosion seen in the Regent D2 request.
    /// </summary>
    private static int CalamityProjectionPotential(scoped in View view, in Battle parent)
    {
        int gain = view.PowerAmountGain(parent, PowerEvidenceIds.Calamity);
        if (gain == 0)
            return 0;
        int attacks = view.CountType(CardType.Attack);
        if (attacks == 0)
            return 0;
        long totalValue = 0;
        for (int index = 0; index < view.LiveCount; index++)
        {
            Card card = view.LiveCard(index);
            if (view.Type(card) == CardType.Attack)
                totalValue += Math.Max(0, view.CardValue(card));
        }
        long averageValue = Math.Max(1, totalValue / attacks);
        long raw = gain * averageValue;
        return Math.Min(view.RemainingEnemyHp(), (int)Math.Min(int.MaxValue, raw));
    }

    /// <summary>
    /// <c>Panache</c>: every five cards played deals the power amount to all enemies. Use the
    /// per-turn draw count as the number of playable cards; fewer than five draws never trigger.
    /// </summary>
    private static int PanacheProjectionPotential(scoped in View view, in Battle parent)
    {
        int gain = view.PowerAmountGain(parent, PowerEvidenceIds.Panache);
        if (gain == 0)
            return 0;
        int draws = view.RawModifiedHandDraw();
        if (draws == 0)
            return 0;
        long totalCost = 0;
        int playable = 0;
        for (int index = 0; index < view.LiveCount; index++)
        {
            Card card = view.LiveCard(index);
            if (view.HasKeyword(card, Keywords.Unplayable))
                continue;
            Pile pile = Piles.PileOf(view.Battle, index, out _);
            int cost = Math.Max(0, Kernel.EnergyCostWithModifiers(
                view.Battle, view.Rules, card, pile));
            totalCost += cost;
            playable++;
        }
        if (playable == 0)
            return 0;
        double averageCost = (double)totalCost / playable;
        int byEnergy = averageCost <= 0d
            ? draws
            : (int)Math.Min(int.MaxValue, Math.Floor(
                view.ModifiedMaxEnergy() / averageCost));
        int playsPerTurn = Math.Min(draws, byEnergy);
        if (playsPerTurn < 5)
            return 0;
        int triggersPerTurn = playsPerTurn / 5;
        // Each trigger hits the enemies alive at the time: triggers per turn times enemy-turns.
        return PerTriggerDamage(view, gain,
            SaturatingMul(triggersPerTurn, view.EnemyTurns(0)));
    }

    /// <summary>
    /// <c>Rolling Boulder</c>: each instance deals its current amount each turn, then grows by the
    /// native canonical increment (5). Count the new instance's linear growth over R turns.
    /// </summary>
    private static int RollingBoulderProjectionPotential(scoped in View view, in Battle parent)
    {
        int gain = view.PowerAmountGain(parent, PowerEvidenceIds.RollingBoulder);
        if (gain == 0)
            return 0;
        // Turn k deals gain + 5(k-1) to every living enemy; each enemy takes it for its own life.
        Span<int> life = stackalloc int[view.Battle.EnemyCount];
        view.FillEnemyLifeTurns(life);
        long raw = 0;
        for (int index = 0; index < life.Length; index++)
        {
            long turns = life[index];
            if (turns > 0)
                raw += (long)gain * turns + 5L * turns * (turns - 1) / 2;
        }
        return Math.Min(view.RemainingEnemyHp(), (int)Math.Min(int.MaxValue, raw));
    }

    /// <summary>Expected block-card plays per turn, for Dexterity-loss cost estimates.</summary>
    private static int BlockCardsPerTurn(scoped in View view)
    {
        int blockCards = 0;
        for (int index = 0; index < view.LiveCount; index++)
        {
            Card card = view.LiveCard(index);
            if (view.Type(card) == CardType.Skill && view.Block(card) > 0)
                blockCards++;
        }
        if (blockCards == 0)
            return 0;
        int live = Math.Max(1, view.LiveCount);
        int draws = Math.Min(Math.Max(1, view.ModifiedHandDraw()), live);
        long drawn = (long)blockCards * draws;
        return (int)Math.Max(1, Math.Min(int.MaxValue, (drawn + live - 1) / live));
    }

    /// <summary>
    /// <c>NecroMastery</c>: Osty only loses HP when it redirects unblocked powered attack damage
    /// (DieForYouPower). The per-turn loss is therefore the incoming unblocked damage, bounded by
    /// Osty's current HP; each lost HP deals <c>gain</c> damage to every living enemy.
    /// </summary>
    private static int NecroMasteryProjectionPotential(scoped in View view, in Battle parent)
    {
        int gain = view.PowerAmountGain(parent, PowerEvidenceIds.NecroMastery);
        if (gain == 0)
            return 0;
        int ostyHp = Math.Max(0, view.Features.OstyHp);
        int block = Math.Max(0, view.Features.PlayerBlock);
        int incoming = Math.Max(
            Math.Max(0, view.CurrentIncomingAttackDamage() - block),
            Math.Max(0, view.IncomingDamage()));
        if (ostyHp == 0 || incoming == 0)
            return 0;
        // Osty loses the incoming damage each turn until its HP runs out; each lost HP hits the
        // enemies alive that turn (kill-order life).
        Span<int> life = stackalloc int[view.Battle.EnemyCount];
        int remaining = view.FillEnemyLifeTurns(life);
        long ostyLeft = ostyHp;
        long raw = 0;
        for (int turn = 1; turn <= remaining && ostyLeft > 0; turn++)
        {
            long loss = Math.Min(ostyLeft, (long)incoming);
            ostyLeft -= loss;
            int alive = 0;
            for (int index = 0; index < life.Length; index++)
            {
                if (life[index] >= turn)
                    alive++;
            }
            raw += loss * gain * alive;
        }
        return Math.Min(view.RemainingEnemyHp(), (int)Math.Min(int.MaxValue, raw));
    }

    /// <summary>
    /// <c>Friendship</c>: each stack adds one maximum energy per turn, but the card also removes
    /// Strength. Subtract the lost attack damage over the same remaining turns.
    /// </summary>
    private static int FriendshipProjectionPotential(
        scoped in View view, in Battle parent, int gain)
    {
        int turns = view.RemainingTurns();
        int benefit = PerTurnResource(
            SaturatingMul(gain, Math.Max(1, view.EnergyUnit())), turns);
        int strengthBefore = Kernel.GetAmount(
            parent, Limits.PlayerOwner, PowerEvidenceIds.Strength);
        int strengthAfter = view.PlayerPowerAmount(PowerEvidenceIds.Strength);
        int strengthLoss = Math.Max(0, strengthBefore - strengthAfter);
        int hitsPerTurn = AttackHitsPerTurn(view);
        int cost = SaturatingMul(strengthLoss, hitsPerTurn, turns);
        return Math.Max(0, benefit - cost);
    }

    /// <summary>
    /// <c>Neurosurge</c>: the immediate energy/draw value stays; each turn the power adds its
    /// amount of Doom to the player, so subtract the accumulated Doom threat (capped by player HP).
    /// </summary>
    private static int NeurosurgeProjectionPotential(scoped in View view, int gain)
    {
        int benefit = PerTriggerResource(
            SaturatingMul(gain, Math.Max(1, view.EnergyUnit())), 1);
        long doom = (long)gain * view.RemainingTurns();
        int playerHp = Math.Max(0, view.Features.PlayerHp);
        int cost = playerHp == 0
            ? (int)Math.Min(int.MaxValue, doom)
            : (int)Math.Min(playerHp, doom * doom / playerHp);
        return Math.Max(0, benefit - cost);
    }

    private static int SimpleProjection(scoped in View view, in Battle parent,
        string id, int turns, int incoming, int enemies, int gain)
    {
        return id switch
        {
            "BIASED_COGNITION" => DefectBiasedCognitionDamagePotential(view, parent, gain),
            "DEFRAGMENT" => DefectFocusDamagePotential(view, gain),
            "BUFFER" => BufferPotential(gain, incoming, view.IncomingAttackHits(nextTurn: true)),
            "BULK_UP" => GrowthFrontier(view,
                blockPerSkillBonus: view.PowerAmountGain(parent, PowerEvidenceIds.Dexterity),
                damagePerAttack: view.PowerAmountGain(parent, PowerEvidenceIds.Strength),
                remainingTurns: view.RemainingTurns()),
            "CAPACITOR" => DefectCapacitorPotential(view, parent, turns),
            "CONSUMING_SHADOW" => DefectConsumingShadowPotential(view, parent),
            "COOLANT" => DefectCoolantPotential(view, parent, turns, incoming),
            "CREATIVE_AI" => DefectEnergyUnitPerTurnPotential(view, parent,
                DefectPowerIds.CreativeAi, turns),
            "ECHO_FORM" => DefectEchoFormPotential(view, parent, turns),
            "FERAL" => DefectFeralPotential(view, parent),
            "HAILSTORM" => DefectHailstormPotential(view, parent, turns, enemies),
            "ITERATION" => DefectIterationPotential(view, parent),
            "LOOP" => DefectLoopPotential(view, parent),
            "MACHINE_LEARNING" => DefectEnergyUnitPerTurnPotential(view, parent,
                DefectPowerIds.MachineLearning, turns),
            "SMOKESTACK" => DefectSmokestackPotential(view, parent, enemies),
            "SPINNER" => DefectSpinnerPotential(view, parent, turns),
            "STORM" => DefectStormPotential(view, parent),
            "SUBROUTINE" => DefectSubroutinePotential(view, parent),
            "THUNDER" => DefectThunderPotential(view, parent),
            "TRASH_TO_TREASURE" => DefectTrashToTreasurePotential(view, parent),
            "ARSENAL" => RegentArsenalProjection(view, parent),
            "BLACK_HOLE" => RegentBlackHoleProjection(view, parent),
            "CHILD_OF_THE_STARS" => PerTriggerBlock(
                gain, view.StarSpendCapacity(), incoming),
            "FURNACE" => PerTurnResource(gain, turns),
            "GENESIS" => PerTurnResource(gain, turns),
            "MONARCHS_GAZE" => RegentMonarchsGazeProjection(view, parent),
            "NEUTRON_AEGIS" => PerTurnBlock(gain, turns, incoming),
            "ORBIT" => RegentOrbitProjection(view, parent, turns),
            "PALE_BLUE_DOT" => RegentIncrementalEnergyUnitProjection(
                view, parent, PowerEvidenceIds.PaleBlueDot, turns),
            "PARRY" => RegentParryProjection(view, turns, incoming),
            "PILLAR_OF_CREATION" => RegentPillarProjection(view, parent, incoming),
            "SEEKING_EDGE" => PerTurnResource(view.EnergyUnit(), turns),
            "SPECTRUM_SHIFT" => RegentIncrementalEnergyUnitProjection(
                view, parent, PowerEvidenceIds.SpectrumShift, turns),
            "SWORD_SAGE" => PerTurnDamage(view, Math.Max(1, view.MaxDamage()), turns, 1),
            "THE_SEALED_THRONE" => RegentSealedThroneProjection(view, parent),
            "TYRANNY" => RegentIncrementalEnergyUnitProjection(
                view, parent, PowerEvidenceIds.Tyranny, turns),
            "VOID_FORM" => RegentIncrementalEnergyUnitProjection(
                view, parent, PowerEvidenceIds.VoidForm, turns),
            _ => 0,
        };
    }

    /// <summary>
    /// <c>Defragment</c>: permanent Focus is worth one damage per current orb every remaining turn.
    /// </summary>
    private static int DefectFocusDamagePotential(scoped in View view, int gain)
    {
        int orbs = view.Battle.OrbCount;
        if (gain == 0 || orbs == 0)
            return 0;
        return PerTurnDamage(
            view, gain, SaturatingMul(orbs, view.RemainingTurns()), 1);
    }

    /// <summary>
    /// <c>Biased Cognition</c>: the Focus gain decays by one at each turn start, so sum
    /// <c>max(0, gain - i * decay)</c> over the remaining turns before applying the orb count.
    /// </summary>
    private static int DefectBiasedCognitionDamagePotential(
        scoped in View view, in Battle parent, int gain)
    {
        int orbs = view.Battle.OrbCount;
        if (gain == 0 || orbs == 0)
            return 0;
        int decay = Math.Max(1,
            view.PowerAmountGain(parent, DefectPowerIds.BiasedCognition));
        long focusTurns = 0;
        for (int turn = 0; turn < view.RemainingTurns(); turn++)
        {
            int focus = Math.Max(0, gain - turn * decay);
            focusTurns += focus;
            if (focus == 0)
                break;
        }
        long raw = focusTurns * orbs;
        return Math.Min(view.RemainingEnemyHp(), (int)Math.Min(int.MaxValue, raw));
    }

    /// <summary>Capacitor's new orb slots are resource over the remaining turns.</summary>
    private static int DefectCapacitorPotential(scoped in View view, in Battle parent, int turns)
    {
        int slots = Math.Max(0, view.Battle.OrbCapacity - parent.OrbCapacity);
        return slots == 0 ? 0 : PerTurnResource(slots, turns);
    }

    /// <summary>
    /// <c>Consuming Shadow</c>: at each turn end EvokeLast removes up to <c>gain</c> orbs. Dark
    /// orbs also gain one passive tick before that turn's evocation. Only actual damage evocations
    /// (Dark/Lightning/Glass) contribute; Frost/Plasma are left to their own categories.
    /// </summary>
    private static int DefectConsumingShadowPotential(scoped in View view, in Battle parent)
    {
        int gain = view.PowerAmountGain(parent, DefectPowerIds.ConsumingShadow);
        if (gain == 0)
            return 0;
        int orbCount = view.Battle.OrbCount;
        if (orbCount == 0)
            return 0;
        int evokes = Math.Min(orbCount,
            SaturatingMul(gain, view.RemainingTurns()));
        long damage = 0;
        for (int evoke = 0; evoke < evokes; evoke++)
        {
            int index = orbCount - 1 - evoke; // EvokeLast
            string orbId = Registry.Orb(view.Battle.Orbs[index].Id).NativeId;
            decimal value = Kernel.OrbEvokeValue(view.Battle, view.Rules, index);
            switch (orbId)
            {
                case "DARK_ORB":
                {
                    int passiveTicks = evoke / gain + 1;
                    decimal passive = Math.Max(0m,
                        Kernel.OrbPassiveValue(view.Battle, view.Rules, index));
                    value += passive * passiveTicks;
                    damage += (long)Math.Ceiling(Math.Max(0m, value));
                    break;
                }
                case "LIGHTNING_ORB":
                    damage += (long)Math.Ceiling(Math.Max(0m, value));
                    break;
                case "GLASS_ORB":
                    damage += (long)Math.Ceiling(Math.Max(0m, value * 2m))
                        * Math.Max(1, view.Features.AliveEnemyCount);
                    break;
                // Frost and Plasma evocations have their own block/energy categories.
            }
        }
        return Math.Min(view.RemainingEnemyHp(), (int)Math.Min(int.MaxValue, damage));
    }

    /// <summary>
    /// Coolant grants block per distinct orb type each turn, bounded by incoming damage. The types
    /// are the ones the player will hold (current plus channeled by the live cards, at most the orb
    /// slots), not only the opening slots.
    /// </summary>
    private static int DefectCoolantPotential(scoped in View view, in Battle parent,
        int turns, int incoming)
    {
        int gain = view.PowerAmountGain(parent, DefectPowerIds.Coolant);
        if (gain == 0)
            return 0;
        return PerTurnBlock(SaturatingMul(gain, view.ReachableOrbTypes()), turns, incoming);
    }

    /// <summary>Card generation or extra card plays valued at the live deck's average card value.</summary>
    private static int DefectEnergyUnitPerTurnPotential(scoped in View view, in Battle parent,
        ushort gainPower, int turns)
    {
        int gain = view.PowerAmountGain(parent, gainPower);
        return gain == 0
            ? 0
            : PerTurnResource(SaturatingMul(gain, view.EnergyUnit()), turns);
    }

    /// <summary>
    /// <c>Feral</c>: each turn the first <c>X</c> zero-cost attacks return to hand. The new stack
    /// only adds returns above what the parent power already covered.
    /// </summary>
    private static int DefectFeralPotential(scoped in View view, in Battle parent)
    {
        int gain = view.PowerAmountGain(parent, DefectPowerIds.Feral);
        if (gain == 0)
            return 0;
        int zeroCostAttacks = view.CountZeroCostAttack();
        if (zeroCostAttacks == 0)
            return 0;
        int before = Math.Min(Kernel.GetAmount(parent, Limits.PlayerOwner, DefectPowerIds.Feral),
            zeroCostAttacks);
        int after = Math.Min(view.PlayerPowerAmount(DefectPowerIds.Feral), zeroCostAttacks);
        int addedReturns = Math.Max(0, after - before);
        return addedReturns == 0
            ? 0
            : PerTurnResource(SaturatingMul(addedReturns, view.EnergyUnit()),
                view.RemainingTurns());
    }

    /// <summary>
    /// Echo Form repeats the first card played each turn. The incremental stack is one extra
    /// repeat, estimated as the best current attack damage on a single target, as upstream does.
    /// </summary>
    private static int DefectEchoFormPotential(scoped in View view, in Battle parent, int turns)
    {
        int gain = view.PowerAmountGain(parent, DefectPowerIds.EchoForm);
        if (gain == 0)
            return 0;
        return PerTurnDamage(
            view, SaturatingMul(gain, Math.Max(1, view.MaxDamage())), turns, 1);
    }

    /// <summary>Hailstorm deals its incremental amount to every living enemy each turn.</summary>
    private static int DefectHailstormPotential(scoped in View view, in Battle parent,
        int turns, int enemies)
    {
        int gain = view.PowerAmountGain(parent, DefectPowerIds.Hailstorm);
        return gain == 0 ? 0 : PerTurnDamage(view, gain, turns, enemies);
    }

    /// <summary>Iteration draws its incremental amount when a generated Status is drawn.</summary>
    private static int DefectIterationPotential(scoped in View view, in Battle parent)
    {
        int gain = view.PowerAmountGain(parent, DefectPowerIds.Iteration);
        if (gain == 0)
            return 0;
        int sources = DefectStatusSources(view);
        return sources == 0
            ? 0
            : PerTriggerResource(SaturatingMul(gain, view.EnergyUnit()), sources);
    }

    /// <summary>
    /// <c>Loop</c>: at turn start trigger the rightmost orb's passive (queue index 0) once per
    /// stack, every remaining turn. The category follows that orb type.
    /// </summary>
    private static int DefectLoopPotential(scoped in View view, in Battle parent)
    {
        int gain = view.PowerAmountGain(parent, DefectPowerIds.Loop);
        if (gain == 0 || view.Battle.OrbCount == 0)
            return 0;
        string orbId = Registry.Orb(view.Battle.Orbs[0].Id).NativeId;
        int passive = (int)Math.Ceiling(Math.Max(0m,
            Kernel.OrbPassiveValue(view.Battle, view.Rules, 0)));
        if (passive == 0)
            return 0;
        int turns = view.RemainingTurns();
        return orbId switch
        {
            "FROST_ORB" => PerTurnBlock(
                SaturatingMul(gain, passive), turns, view.IncomingDamage()),
            "PLASMA_ORB" => PerTurnResource(SaturatingMul(gain, passive), turns),
            "GLASS_ORB" => PerTurnDamage(view, gain,
                SaturatingMul(passive, view.EnemyTurns(0)), 1),
            "LIGHTNING_ORB" or "DARK_ORB" =>
                PerTurnDamage(view, gain, SaturatingMul(passive, turns), 1),
            _ => throw new UnsupportedException($"Loop 投影不认识的球 {orbId}。"),
        };
    }

    /// <summary>Smokestack deals its incremental amount to all enemies per generated Status.</summary>
    private static int DefectSmokestackPotential(scoped in View view, in Battle parent, int enemies)
    {
        int gain = view.PowerAmountGain(parent, DefectPowerIds.Smokestack);
        if (gain == 0)
            return 0;
        int sources = DefectStatusSources(view);
        return sources == 0
            ? 0
            : PerTriggerDamage(view, gain, SaturatingMul(sources, enemies));
    }

    /// <summary>Spinner channels one Glass orb per stack each turn; value it as one orb per turn.</summary>
    private static int DefectSpinnerPotential(scoped in View view, in Battle parent, int turns)
    {
        int gain = view.PowerAmountGain(parent, DefectPowerIds.Spinner);
        return gain == 0 ? 0 : PerTurnResource(gain, turns);
    }

    /// <summary>Storm channels its incremental amount of Lightning per playable Power.</summary>
    private static int DefectStormPotential(scoped in View view, in Battle parent)
    {
        int gain = view.PowerAmountGain(parent, DefectPowerIds.Storm);
        if (gain == 0)
            return 0;
        int sources = view.PlayablePowerSources();
        return sources == 0
            ? 0
            : PerTriggerDamage(view, SaturatingMul(gain, view.EnergyUnit()), sources);
    }

    /// <summary>Subroutine grants its incremental energy per playable Power.</summary>
    private static int DefectSubroutinePotential(scoped in View view, in Battle parent)
    {
        int gain = view.PowerAmountGain(parent, PowerEvidenceIds.Subroutine);
        if (gain == 0)
            return 0;
        int sources = view.PlayablePowerSources();
        return sources == 0
            ? 0
            : PerTriggerResource(SaturatingMul(gain, view.EnergyUnit()), sources);
    }

    /// <summary>Thunder deals its incremental amount for each current Lightning orb.</summary>
    private static int DefectThunderPotential(scoped in View view, in Battle parent)
    {
        int gain = view.PowerAmountGain(parent, DefectPowerIds.Thunder);
        if (gain == 0)
            return 0;
        int triggers = view.CountOrb("LIGHTNING");
        return triggers == 0 ? 0 : PerTriggerDamage(view, gain, triggers);
    }

    /// <summary>Trash to Treasure channels its incremental amount of orbs per generated Status.</summary>
    private static int DefectTrashToTreasurePotential(scoped in View view, in Battle parent)
    {
        int gain = view.PowerAmountGain(parent, DefectPowerIds.TrashToTreasure);
        if (gain == 0)
            return 0;
        int sources = DefectStatusSources(view);
        return sources == 0
            ? 0
            : PerTriggerResource(SaturatingMul(gain, view.EnergyUnit()), sources);
    }

    /// <summary>
    /// Status-generation sources in the live piles. The upstream <c>PowerCountWithStatusVar</c>
    /// reads a Status dynamic variable; in this content that variable does not exist, so the
    /// equivalent fact is the card's own "Status" variable or a declared generated Status card.
    /// </summary>
    private static int DefectStatusSources(scoped in View view)
    {
        int count = 0;
        for (int index = 0; index < view.LiveCount; index++)
        {
            Card card = view.LiveCard(index);
            if (view.HasVarFragment(card, "Status") || DefectGeneratesStatus(view, card))
                count++;
        }
        return count;
    }

    private static bool DefectGeneratesStatus(scoped in View view, in Card card)
    {
        IReadOnlyList<(string CardId, int Upgrade)> generated =
            Registry.Card(card.Id).GeneratedCards;
        for (int index = 0; index < generated.Count; index++)
        {
            (string cardId, int upgrade) = generated[index];
            if (!Registry.TryCard(cardId, out ushort id))
                throw new UnsupportedException($"能力投影的生成牌 {cardId} 没有 Sts2Solver 定义。");
            if (view.Rules.Card(id, upgrade).Type == CardType.Status)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Live cards that can channel an orb. Used only by the Defect opening-trigger evidence so a
    /// Focus/Capacitor route with an empty current orb queue still sees its channel sources.
    /// </summary>
    private static bool DefectHasOrbChannelSource(scoped in View view)
    {
        for (int index = 0; index < view.LiveCount; index++)
        {
            string id = view.NativeId(view.LiveCard(index));
            if (id is "ICE_LANCE" or "METEOR_STRIKE" or "COLD_SNAP" or "BALL_LIGHTNING"
                or "REFRACT" or "NULL" or "CONSUMING_SHADOW" or "SPINNER"
                or "GLACIER" or "GLASSWORK" or "FUSION" or "CHILL" or "CHAOS"
                or "DARKNESS" or "TEMPEST" or "SHADOW_SHIELD" or "ZAP" or "RAINBOW"
                or "VOLTAIC" or "COOLHEADED" or "LIGHTNING_ROD" or "STORM")
            {
                return true;
            }
        }
        return false;
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
        int damagePerAttack = 0, int remainingTurns = 1)
    {
        if (remainingTurns <= 0)
            return 0;
        int incoming = view.IncomingDamage();
        PowerTurnCardOption[] baseline = BuildHandOptions(view);
        if (baseline.Length == 0)
            return 0;
        PowerTurnCardOption[] powered = weakAttackBonusPercent > 0
            ? BuildHandOptions(view, weakAttackBonusPercent)
            : baseline;
        IReadOnlyList<PowerTurnFrontierState> baselineStates =
            PowerTurnFrontier.Build(view.Battle.Energy, incoming, baseline);
        IReadOnlyList<PowerTurnFrontierState> poweredStates = PowerTurnFrontier.Build(
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
            damagePerAttack);
        // A permanent stat change keeps paying every remaining turn. Damage is capped by the
        // current remaining enemy HP. The frontier's HP uplift is already bounded by this turn's
        // incoming damage; summing it over R is total prevented HP. Do not multiply the one-turn
        // 8-per-HP frontier weight by R: that would count the same HP weight in every turn.
        int damage = SaturatingMul(
            PowerTurnFrontier.DefensiveDamageUplift(baselineStates, poweredStates),
            remainingTurns);
        int block = SaturatingMul(
            PowerTurnFrontier.DefensiveHpUplift(baselineStates, poweredStates),
            remainingTurns);
        return SaturatingAdd(Math.Min(view.RemainingEnemyHp(), damage), block);
    }

    /// <summary>Triangular number of turns for a stat that grows by a fixed amount each turn.</summary>
    private static int TriangularTurns(int turns)
    {
        if (turns <= 0)
            return 0;
        return (int)Math.Min(int.MaxValue, (long)turns * (turns + 1) / 2);
    }

    /// <summary>
    /// Value of a current-hand frontier that repeats every remaining turn. Damage is capped by
    /// remaining enemy HP; the defensive part keeps the frontier's per-turn bounded prevention.
    /// </summary>
    private static int ContinuousFrontierValue(scoped in View view,
        IReadOnlyList<PowerTurnFrontierState> baseline,
        IReadOnlyList<PowerTurnFrontierState> powered, int remainingTurns)
    {
        if (remainingTurns <= 0)
            return 0;
        int damage = SaturatingMul(
            PowerTurnFrontier.DefensiveDamageUplift(baseline, powered), remainingTurns);
        // Defensive HP uplift is already capped by the current incoming damage. Summing it over
        // the horizon gives total prevented HP; the 8-per-HP one-turn frontier weight must not be
        // repeated R times.
        int block = SaturatingMul(
            PowerTurnFrontier.DefensiveHpUplift(baseline, powered), remainingTurns);
        return SaturatingAdd(Math.Min(view.RemainingEnemyHp(), damage), block);
    }

    private static int MarginalFrontierValue(IReadOnlyList<PowerTurnFrontierState> baseline,
        IReadOnlyList<PowerTurnFrontierState> powered)
        => SaturatingAdd(
            PowerTurnFrontier.DefensiveDamageUplift(baseline, powered),
            SaturatingMul(PowerTurnFrontier.DefensiveHpUplift(baseline, powered), 8));

    private static PowerTurnCardOption[] BuildHandOptions(scoped in View view,
        int weakAttackBonusPercent = 0, int shivTargetsOverride = 0)
        => BuildOptions(view, 0, view.HandCount, view.HandCount, currentTurn: true,
            weakAttackBonusPercent, shivTargetsOverride);

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
        int weakAttackBonusPercent = 0, int shivTargetsOverride = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(weakAttackBonusPercent);
        ArgumentOutOfRangeException.ThrowIfNegative(shivTargetsOverride);
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
        int shivTargets = shivTargetsOverride > 0
            ? shivTargetsOverride
            : CurrentShivTargets(view);
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

    /// <summary>
    /// Turns an all-enemy uplift over a single target lasts, per extra target: the enemy-turns
    /// beyond the one target that is always alive, divided by the extra targets now. Equals R when
    /// every enemy lives the whole fight.
    /// </summary>
    private static int ExtraTargetTurns(scoped in View view, int targets)
    {
        Span<int> life = stackalloc int[view.Battle.EnemyCount];
        int remaining = view.FillEnemyLifeTurns(life);
        int living = 0;
        long enemyTurns = 0;
        for (int index = 0; index < life.Length; index++)
        {
            if (life[index] <= 0)
                continue;
            living++;
            enemyTurns += life[index];
        }
        int extraTargets = Math.Min(targets, living) - 1;
        if (extraTargets <= 0)
            return remaining;
        return (int)Math.Max(0, (enemyTurns - remaining) / extraTargets);
    }

    private static int PerTurnDamage(scoped in View view, int damagePerTurn, int turns, int targets)
        => Math.Min(view.RemainingEnemyHp(),
            SaturatingMul(damagePerTurn, Math.Max(0, turns), Math.Max(1, targets)));

    /// <summary>
    /// Block gained every turn, bounded per turn: each turn's block counts up to that turn's incoming
    /// damage (at least half the block), then the turns are summed. Bounding the summed block once
    /// would credit only half of a long horizon.
    /// </summary>
    private static int PerTurnBlock(int blockPerTurn, int turns, int incoming)
        => SaturatingMul(BoundedPrevention(blockPerTurn, incoming), Math.Max(0, turns));

    private static int PerTriggerDamage(scoped in View view, int perTrigger, int triggers)
        => Math.Min(view.RemainingEnemyHp(), SaturatingMul(perTrigger, Math.Max(0, triggers)));

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

    private static class DefectPowerIds
    {
        private static ushort Required(string name)
            => Registry.TryPower(name, out ushort id) ? id
                : throw new UnsupportedException($"缺陷能力投影缺少状态 {name}。");

        internal static readonly ushort ConsumingShadow = Required("CONSUMING_SHADOW_POWER");
        internal static readonly ushort Coolant = Required("COOLANT_POWER");
        internal static readonly ushort CreativeAi = Required("CREATIVE_AI_POWER");
        internal static readonly ushort EchoForm = Required("ECHO_FORM_POWER");
        internal static readonly ushort Feral = Required("FERAL_POWER");
        internal static readonly ushort Hailstorm = Required("HAILSTORM_POWER");
        internal static readonly ushort Iteration = Required("ITERATION_POWER");
        internal static readonly ushort Loop = Required("LOOP_POWER");
        internal static readonly ushort MachineLearning = Required("MACHINE_LEARNING_POWER");
        internal static readonly ushort Smokestack = Required("SMOKESTACK_POWER");
        internal static readonly ushort Spinner = Required("SPINNER_POWER");
        internal static readonly ushort Storm = Required("STORM_POWER");
        internal static readonly ushort Thunder = Required("THUNDER_POWER");
        internal static readonly ushort TrashToTreasure = Required("TRASH_TO_TREASURE_POWER");
        internal static readonly ushort BiasedCognition = Required("BIASED_COGNITION_POWER");
    }
}
