using System.Runtime.CompilerServices;

namespace Sts2Solver.Simulation.Combat;

public static partial class Kernel
{
    /// <summary>
    /// Mirrors <c>SimulatedCombatState.CanPlayCard</c> → <c>CombatPredictionSimulator.CanPlay</c>
    /// for a card in hand.
    /// </summary>
    public static bool CanPlayCard(in Battle battle, Rules rules, int handIndex)
        => CanPlayCard(battle, rules, handIndex, out _, out _);

    /// <summary>
    /// Same check, also returning the effective hand costs it computed on the way. The costs are only
    /// meaningful when the result is true; a false result may return before computing them (the
    /// Enthralled lock and Unplayable checks run first). Callers that feed the costs to
    /// <c>ReachableHandValue</c> must not run the energy/star cost hooks a second time.
    /// </summary>
    public static bool CanPlayCard(in Battle battle, Rules rules, int handIndex,
        out int energyCost, out int starCost)
    {
        energyCost = 0;
        starCost = 0;
        ref readonly Card card = ref battle.Cards[handIndex];
        CardDef def = Registry.Card(card.Id);
        // SimulatedCombatState.IsCardPlayPrevented: the Enthralled lock. Smog is rejected at admission.
        if (!def.LocksOtherHandCards)
        {
            for (int index = 0; index < battle.HandCount; index++)
            {
                if (Registry.Card(battle.Cards[index].Id).LocksOtherHandCards)
                    return false;
            }
        }
        if (HasKeyword(battle, card, Keywords.Unplayable))
            return false;
        energyCost = EnergyCostWithModifiers(battle, rules, card, Pile.Hand);
        starCost = StarCostWithModifiers(battle, rules, card, Pile.Hand);
        // ShouldPayExcessEnergyCostWithStars has no vanilla listener (Hook.ShouldPayExcessEnergyCostWithStars
        // only observes models, and no model overrides it), so the excess-energy conversion cannot trigger.
        if (energyCost > battle.Energy || starCost > battle.Stars)
            return false;
        if (!ShouldPlay(battle, rules, card))
            return false;
        return def.IsPlayable(battle, rules, card);
    }

    /// <summary>
    /// The effective keyword set at a stable boundary: the card's local keywords, plus the Ethereal
    /// that <c>HexPower.TryModifyKeywordsInCombat</c> grants to a Hexed card while the player holds
    /// HexPower (the existing engine calls the native hook lazily, so the value is not stored).
    /// </summary>
    public static bool HasKeyword(in Battle battle, in Card card, Keywords keyword)
    {
        if (card.HasKeyword(keyword))
            return true;
        if (keyword == Keywords.Retain && Registry.Card(card.Id).NativeId == "SHIV"
            && Registry.TryPower("PHANTOM_BLADES_POWER", out ushort phantom))
            return GetAmount(battle, Limits.PlayerOwner, phantom) > 0;
        if (keyword != Keywords.Ethereal
            || !Registry.TryAffliction("HEXED", out ushort hexed)
            || !Registry.TryPower("HEX_POWER", out ushort hex)
            || card.AfflictionId != hexed + 1)
        {
            return false;
        }
        return GetAmount(battle, Limits.PlayerOwner, hex) > 0;
    }

    /// <summary>Native <c>ShouldPlay</c> through <c>HookMirrors.ShouldPlay</c>: any combat listener may forbid the play.</summary>
    private static bool ShouldPlay(in Battle battle, Rules rules, in Card card)
    {
        if ((Registry.AnyHooks & HookMask.ShouldPlay) == 0 || IsOverOrEnding(battle))
            return true;
        Span<Listener> listeners = stackalloc Listener[HookDispatch.MaxListenerCount(battle, rules)];
        int count = HookDispatch.CollectListeners(battle, rules, HookMask.ShouldPlay, listeners);
        for (int index = 0; index < count; index++)
        {
            if (!HookDispatch.Definition(battle, rules, listeners[index]).ShouldPlay(battle, rules, listeners[index], card))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Mirrors <c>GetEnergyCostWithModifiers</c> (X cost pays all current energy). <paramref name="pile"/>
    /// is the card's current pile, which <c>FreeAttackPower</c> reads (only Hand and Play are free).
    /// </summary>
    public static int EnergyCostWithModifiers(in Battle battle, Rules rules, in Card card, Pile pile)
    {
        if ((card.Flags & CardFlags.CostsX) != 0)
            return battle.Energy;
        int cost = EnergyCostWithLocalModifiers(battle, card);
        if (cost < 0)
            return Math.Max(0, cost);
        // HookMirrors.ModifyEnergyCostInCombat: normal listeners then late listeners; the existing
        // engine truncates the decimal and clamps at zero afterwards.
        cost = (int)HookDispatch.ModifyEnergyCost(battle, rules, pile, card, cost);
        return Math.Max(0, cost);
    }

    /// <summary>
    /// Mirrors <c>CombatPredictedCardExtensions.GetEnergyCostValueWithModifiers</c>: a negative base
    /// costs (Unplayable cards) and X costs are returned unchanged; otherwise local modifiers and the
    /// combat cost hooks apply and the result is clamped at zero. Scrape uses it to decide which drawn
    /// cards are not free, so a -1 Unplayable cost must stay distinguishable from 0.
    /// </summary>
    public static int EnergyCostValueWithModifiers(in Battle battle, Rules rules, in Card card,
        Pile pile)
    {
        int cost = card.BaseCost;
        if (cost < 0 || (card.Flags & CardFlags.CostsX) != 0)
            return cost;
        for (int index = 0; index < card.EnergyModifierCount; index++)
        {
            CostModifier modifier = card.EnergyModifiers[index];
            cost = modifier.Type switch
            {
                CostType.Absolute => modifier.IsReduceOnly ? Math.Min(cost, modifier.Amount) : modifier.Amount,
                CostType.Relative => modifier.IsReduceOnly ? Math.Min(cost, cost + modifier.Amount) : cost + modifier.Amount,
                _ => throw new UnsupportedException($"未知的费用修饰类型 {modifier.Type}。"),
            };
        }
        cost = (int)HookDispatch.ModifyEnergyCost(battle, rules, pile, card, cost);
        return Math.Max(0, cost);
    }

    /// <summary>
    /// Mirrors <c>CardEnergyCost.GetWithModifiers(CostModifiers.Local)</c>: the base cost plus the
    /// card's local modifiers, without the combat cost hooks. The existing engine's evaluation uses
    /// this value (never the hook-modified one) for free-card opportunity and skill energy.
    /// </summary>
    public static int EnergyCostWithLocalModifiers(in Battle battle, in Card card)
    {
        int cost = card.BaseCost;
        if (cost < 0)
            return cost;
        for (int index = 0; index < card.EnergyModifierCount; index++)
        {
            CostModifier modifier = card.EnergyModifiers[index];
            cost = modifier.Type switch
            {
                CostType.Absolute => modifier.IsReduceOnly ? Math.Min(cost, modifier.Amount) : modifier.Amount,
                CostType.Relative => modifier.IsReduceOnly ? Math.Min(cost, cost + modifier.Amount) : cost + modifier.Amount,
                _ => throw new UnsupportedException($"未知的费用修饰类型 {modifier.Type}。"),
            };
        }
        return Math.Max(0, cost);
    }

    /// <summary>
    /// Mirrors <c>CardModel.ResolveEnergyXValue</c>: the captured X value with <c>Hook.ModifyXValue</c>
    /// applied (Chemical X). Every effect reads X through here, at the point native code calls
    /// <c>ResolveEnergyXValue</c>, not when the resources are spent. Only X-cost cards may resolve it.
    /// </summary>
    public static int ResolveEnergyXValue(ref Exec x)
    {
        if ((x.Card.Flags & CardFlags.CostsX) == 0)
            throw new InvalidOperationException("这张卡不是 X 费卡，不能解析 X 值。");
        return HookDispatch.ModifyXValue(x.B, x.R, x.Card, x.XValue);
    }

    /// <summary>
    /// Mirrors a manual play through <c>CombatPredictionSimulator.ManualPlay</c>: spend resources,
    /// move to the play pile, run every play index (effect then tail) and settle the result pile.
    /// The caller has already checked playability and the target.
    /// </summary>
    public static void ManualPlay(ref Exec x, int handIndex, byte target)
    {
        ref Battle battle = ref x.B;
        Card card = battle.Cards[handIndex];
        CardStats stats = x.R.Card(card.Id, card.Upgrade);

        // SpendResources (the card is still in hand).
        int energyValue = EnergyCostWithModifiers(battle, x.R, card, Pile.Hand);
        int starValue = StarCostWithModifiers(battle, x.R, card, Pile.Hand);
        x.XValue = energyValue;
        if ((card.Flags & CardFlags.CostsX) != 0)
        {
            card.CapturedEnergy = checked((short)energyValue);
            battle.Cards[handIndex].CapturedEnergy = card.CapturedEnergy;
        }
        // LastStarsSpent: captured even when the play is free (auto-play captures it too).
        x.StarValue = starValue;
        if (energyValue > 0)
            battle.AddCounter(Counter.EnergySpentThisTurn, energyValue);
        if (energyValue > 0)
            battle.Energy = (int)Math.Clamp((long)battle.Energy - energyValue, 0, Limits.ValueClamp);
        // CardModel.SpendResources: AfterEnergySpent runs before the star cost (OrbitPower).
        if (x.R.HasIdentityContent)
        {
            HookDispatch.AfterEnergySpent(ref x, card, energyValue);
            if (x.NeedsChoice)
                return;
        }
        if (starValue > 0)
        {
            battle.Stars = (int)Math.Clamp((long)battle.Stars - starValue, 0, Limits.ValueClamp);
            // PowerLifecycleSupport.AfterStarsSpent: the engine returns from ManualPlay while a pending
            // choice is outstanding, before the card leaves the hand.
            HookDispatch.AfterStarsSpent(ref x, starValue);
            if (x.NeedsChoice)
                return;
        }

        // OnPlayWrapper: AddDuringManualCardPlay.
        if (IsOverOrEnding(battle))
            return;
        int playPosition = Piles.MoveToEnd(ref battle, Pile.Hand, handIndex, Pile.Play);

        x.Card = card;
        x.Stats = stats;
        x.Target = target;
        x.CurrentPlayEnergyValue = energyValue;
        x.CurrentPlayIsAutoPlay = false;
        x.CurrentPlayIsHistoryCourseDupe = card.IsDupe;
        ChoiceSource savedOrigin = x.ChoiceOrigin;
        x.ChoiceOrigin = new ChoiceSource(ChoiceSourceKind.Card, card.Id, card.Upgrade);
        try
        {
            PlayFromPlay(ref x, card, target, energyValue, playPosition, isAutoPlay: false);
        }
        finally
        {
            x.ChoiceOrigin = savedOrigin;
        }
    }

    /// <summary>
    /// The shared body of <c>OnPlayWrapper</c> once the card is in the play pile: result location,
    /// play count, every play index, and the result-pile tail. Manual and auto-play differ only in
    /// how the card reached the play pile and how resources were handled.
    /// </summary>
    private static void PlayFromPlay(ref Exec x, Card card, byte target, int energyValue,
        int playPosition, bool isAutoPlay)
        => PlayFromPlay(ref x, card, target, energyValue, playPosition, isAutoPlay, out _, out _);

    private static void PlayFromPlay(ref Exec x, Card card, byte target, int energyValue,
        int playPosition, bool isAutoPlay, out Pile resultPile, out int resultIndex)
    {
        // A nested auto-play inside this play's effects runs its own PlayFromPlay and moves
        // PlayPosition to its slot; put this play's slot back afterwards so CurrentPlayCard keeps
        // addressing this card (nested cards are appended after it and leave the play pile first).
        int outerPlayPosition = x.PlayPosition;
        // A nested auto-play inside this play's effects (OwnChoice discarding a Sly card) completes its
        // own power-application scope, which overwrites LastCardPowerSourceSerial. Restore the outer
        // play's completed scope so Vambrace's AfterCardPlayed still matches its triggering card.
        int outerLastSourceSerial = x.LastCardPowerSourceSerial;
        int outerPlayIdentity = x.PlayIdentity;
        int outerLastPlayIdentity = x.LastPlayIdentity;
        PlayFromPlayCore(ref x, card, target, energyValue, playPosition, isAutoPlay, out resultPile, out resultIndex);
        x.PlayPosition = outerPlayPosition;
        x.LastCardPowerSourceSerial = outerLastSourceSerial;
        x.PlayIdentity = outerPlayIdentity;
        x.LastPlayIdentity = outerLastPlayIdentity;
    }

    private static void PlayFromPlayCore(ref Exec x, Card card, byte target, int energyValue,
        int playPosition, bool isAutoPlay, out Pile resultPile, out int resultIndex)
    {
        resultPile = Pile.Play;
        resultIndex = -1;
        // The play-pile slot is fixed before the first effect runs (nested auto-plays append after
        // it), so OnPlay code can address its own live card with CurrentPlayCard.
        x.PlayPosition = playPosition;
        x.PlayIdentity = ++x.PlayIdentityCounter;
        CardStats stats = x.R.Card(card.Id, card.Upgrade);
        x.CurrentPlayEnergyValue = energyValue;
        Pile result = ResultLocation(x.B, x.R, card, stats, x.CurrentPlayIsHistoryCourseDupe,
            out bool removeFromCombat, out PilePosition basePosition);
        // CardModel.GetResultLocationForCardPlay clears ExhaustOnNextPlay when the result is Exhaust
        // (Havoc/Cascade force-exhaust the auto-played card).
        if (result == Pile.Exhaust)
        {
            card.Flags &= ~CardFlags.ExhaustOnNextPlay;
            if (playPosition < x.B.PlayCount)
                Piles.At(ref x.B, Pile.Play, playPosition).Flags &= ~CardFlags.ExhaustOnNextPlay;
        }
        // HookMirrors.ModifyCardPlayResultLocation, then CardModel.OnPlayWrapper's direct after dispatch
        // over the modifiers that changed the location.
        Span<Listener> locationModifiers = stackalloc Listener[HookDispatch.MaxListenerCount(x.B, x.R)];
        Pile hooked = HookDispatch.ModifyCardPlayResultLocation(ref x, card, isAutoPlay, energyValue, result,
            basePosition, locationModifiers, out int locationModifierCount, out PilePosition resultPosition);
        if (x.NeedsChoice)
            return;
        if (hooked != result)
        {
            if (removeFromCombat)
                throw new UnsupportedException("出牌去向修改把移出战斗的卡改成了牌堆。");
            if (hooked == Pile.Play)
                throw new UnsupportedException($"出牌去向修改返回了无效牌堆 {hooked}。");
            result = hooked;
        }
        if (resultPosition != PilePosition.Bottom && result is not (Pile.Draw or Pile.Discard or Pile.Hand))
            throw new UnsupportedException($"出牌去向位置对 {result} 无定义。");
        HookDispatch.AfterModifyingCardPlayResultLocation(ref x, card, result, locationModifiers[..locationModifierCount]);
        if (x.NeedsChoice)
            return;
        // HookMirrors.ModifyCardPlayCount then Hook.AfterModifyingCardPlayCount. The base count is
        // CardModel.GetEnchantedReplayCount() (the enchantment modifies the replay count first).
        int basePlayCount = EnchantedReplayCount(card);
        Span<Listener> playCountModifiers = stackalloc Listener[HookDispatch.MaxListenerCount(x.B, x.R)];
        int playCount = HookDispatch.ModifyCardPlayCount(ref x, card, target, basePlayCount + 1,
            playCountModifiers, out int playCountModifierCount);
        HookDispatch.AfterModifyingCardPlayCount(ref x, card, playCountModifiers[..playCountModifierCount]);
        if (x.NeedsChoice)
            return;
        if (x.B.Player.IsDead)
            return;

        // Per-play damage history used by Fisticuffs/Omnislice/HandOfGreed; never part of the state.
        // The existing engine captures a fresh history start for every CardPlay of a replay/Echo Form
        // series, so a compensation on a later play must not see an earlier play's damage.
        x.EffectDepth++;
        // Entries at and after the count are never read, so only an outer play with live options
        // needs the whole table saved and restored around nested plays.
        int savedGeneratedOptions = x.GeneratedOptionCount;
        GeneratedChoiceOptions savedOptionCards;
        Unsafe.SkipInit(out savedOptionCards);
        if (savedGeneratedOptions != 0)
            savedOptionCards = x.GeneratedOptions;
        bool completed = true;
        for (int index = 0; index < playCount; index++)
        {
            if (IsOverOrEnding(x.B))
                break;
            if (index > 0)
                SyncCurrentPlayCard(ref x);
            x.CardDamageTotal = 0;
            x.CardKilledTarget = false;
            x.PlayIndex = index;
            // Native CardPlay.Card is the live CardModel: a replay's BeforeCardPlayed sees what the
            // previous play index changed on it (Glam disables itself in AfterCardPlayed, so a Juggling
            // clone made at the replay's start copies the disabled enchantment).
            CardPlayInfo play = new((uint)playPosition < (uint)x.B.PlayCount
                    ? Piles.At(ref x.B, Pile.Play, playPosition)
                    : card,
                target, index, playCount, removeFromCombat ? Pile.Play : result,
                Piles.Start(x.B, Pile.Play) + playPosition);
            BeforeCardPlayedHook before = new() { Play = play };
            HookDispatch.Dispatch(ref x, ref before, gatedByEnding: true);
            if (x.NeedsChoice)
            {
                completed = false;
                break;
            }
            RecordCardPlayStarted(ref x.B, stats, Registry.Card(card.Id).ResolveCardType(x.B, x.R, card),
                index, energyValue, isAutoPlay);
            if (x.R.HasFoolMechanics)
                RecordMelodyStarted(ref x, stats.Tags);
            bool identityScope = x.R.HasIdentityContent;
            int monologueBase = 0;
            int outerSourceSerial = 0;
            bool outerSourceActive = false;
            int outerMonologueBase = 0;
            if (identityScope)
            {
                monologueBase = x.MonologuePendingCount;
                CaptureMonologuePending(ref x);
                outerSourceSerial = x.CardPowerSourceSerial;
                outerSourceActive = x.CardPowerSourceActive;
                outerMonologueBase = x.CurrentMonologueBase;
                x.CurrentMonologueBase = monologueBase;
                x.CardPowerSourceSerial = ++x.CardPowerSourceSerialCounter;
                x.CardPowerSourceActive = true;
            }
            Registry.Card(card.Id).OnPlay(ref x);
            if (x.NeedsChoice)
            {
                if (identityScope)
                {
                    CompleteCardPowerSource(ref x);
                    x.CardPowerSourceSerial = outerSourceSerial;
                    x.CardPowerSourceActive = outerSourceActive;
                    x.CurrentMonologueBase = outerMonologueBase;
                }
                completed = false;
                break;
            }
            // CorePowerSupport.ApplyCardPowers resolves queued power amount changes after the card's
            // compensation effects (Vicious draws here, not inside the effect that applied Vulnerable).
            ResolvePowerAmountChanges(ref x);
            if (x.NeedsChoice)
            {
                if (identityScope)
                {
                    CompleteCardPowerSource(ref x);
                    x.CardPowerSourceSerial = outerSourceSerial;
                    x.CardPowerSourceActive = outerSourceActive;
                    x.CurrentMonologueBase = outerMonologueBase;
                }
                completed = false;
                break;
            }
            // ICombatPredictionCardExecutionSink.ApplyCardPlayEffects settles newly dead enemies right
            // after the card's Power compensation (before the card's own choice and tail). An auto-play
            // that kills the Phrog Parasite must spawn its Wrigglers before the next queued auto-play
            // picks a target.
            ApplyEnemyDeathPowers(ref x);
            if (x.NeedsChoice)
            {
                if (identityScope)
                {
                    CompleteCardPowerSource(ref x);
                    x.CardPowerSourceSerial = outerSourceSerial;
                    x.CardPowerSourceActive = outerSourceActive;
                    x.CurrentMonologueBase = outerMonologueBase;
                }
                completed = false;
                break;
            }
            // The existing engine normalizes card afflictions at the end of the compensated card
            // effects, before the card's own choice.
            NormalizeCardAfflictions(ref x);
            // The card power-application scope ends after ApplyCardPlayEffects, before OwnChoice
            // (CardExecutionContinuation's using block).
            if (identityScope)
            {
                CompleteCardPowerSource(ref x);
                x.CardPowerSourceSerial = outerSourceSerial;
                x.CardPowerSourceActive = outerSourceActive;
            }
            if (x.NeedsChoice)
            {
                completed = false;
                break;
            }
            Registry.Card(card.Id).OwnChoice(ref x);
            if (x.NeedsChoice)
            {
                completed = false;
                break;
            }
            // Tail: abort if the owner died, then EnchantmentModel.OnPlay, then AfflictionModel.OnPlay
            // (CombatPredictionSimulator.CardTailContinuation's stages).
            if (x.B.Player.IsDead)
            {
                completed = false;
                break;
            }
            if (!RunCardAttachmentOnPlay(ref x, playPosition, target))
            {
                completed = false;
                break;
            }
            // CardModel is mutable: the played card value as seen by AfterCardPlayed includes the
            // effects and attachment OnPlay updates of this play index.
            if ((uint)playPosition < (uint)x.B.PlayCount)
            {
                play = new CardPlayInfo(Piles.At(ref x.B, Pile.Play, playPosition), target, index,
                    playCount, removeFromCombat ? Pile.Play : result,
                    Piles.Start(x.B, Pile.Play) + playPosition);
            }
            // CardPlayFinished: no tracked counter yet. AfterCardPlayed runs its normal pass, then
            // re-reads the listeners for the late pass, without the ending gate. The native history
            // records the finished play before AfterCardPlayed, so effects triggered there (Calamity
            // generating a card, Stomp.AfterEnteredCombat counting attacks) must see this play.
            RecordCardPlayFinished(ref x, stats, Registry.Card(card.Id).ResolveCardType(x.B, x.R, card));
            if (x.R.HasFoolMechanics)
                RecordMelodyFinished(ref x.B, stats.Tags);
            if (Registry.Card(card.Id).ResolveCardType(x.B, x.R, card) == CardType.Attack)
                RecordHistoryCourseAttack(ref x, playPosition);
            // CardPlayFinishedEntry.WasEthereal is `card.Keywords.Contains(Ethereal)`, taken when the
            // entry is created, before AfterCardPlayed. Keywords runs Hook.ModifyKeywords: a Hexed card
            // counts as Ethereal while HexPower is up, so the battle-aware helper (local keyword plus
            // Hexed+HexPower) is the source. Reading it after AfterCardPlayed misses a play whose
            // listeners kill the Hex applier (Storm channels Lightning, the evoke kills Spectral Knight).
            if ((uint)playPosition < (uint)x.B.PlayCount
                && HasKeyword(x.B, Piles.At(ref x.B, Pile.Play, playPosition), Keywords.Ethereal))
            {
                x.B.AddCounter(Counter.EtherealPlays, 1);
            }
            x.LastPlayIdentity = x.PlayIdentity;
            AfterCardPlayedHook after = new() { Play = play };
            HookDispatch.Dispatch(ref x, ref after, gatedByEnding: false);
            if (x.NeedsChoice)
            {
                completed = false;
                break;
            }
            AfterCardPlayedLateHook afterLate = new() { Play = play };
            HookDispatch.Dispatch(ref x, ref afterLate, gatedByEnding: false);
            if (x.NeedsChoice)
            {
                completed = false;
                break;
            }
            if (identityScope)
            {
                x.MonologuePendingCount = monologueBase;
                x.CurrentMonologueBase = outerMonologueBase;
            }
            // SimulatedCombatState.CompleteCardPlayEffects resolves the remaining power amount changes
            // before recording the play and its lifecycle counters.
            ResolvePowerAmountChanges(ref x);
            if (x.NeedsChoice)
            {
                completed = false;
                break;
            }
            RecordCardPlayed(ref x, Registry.Card(card.Id).ResolveCardType(x.B, x.R, card));
            // SimulatedCombatState.RecordCardLifecycle: the completed play records the exact Fetch
            // object in _fetchCardsPlayedThisTurn. The value still sits in the play pile here.
            if ((uint)playPosition < (uint)x.B.PlayCount
                && Registry.Card(card.Id).TracksFetchPlay)
            {
                Piles.At(ref x.B, Pile.Play, playPosition).Private |= FetchPlayedBit;
            }
            Registry.Card(card.Id).AfterPlayCompleted(ref x, index == playCount - 1);
            if (x.NeedsChoice) { completed = false; break; }
            // The tail fails when the owner died during the completion effects.
            if (x.B.Player.IsDead)
            {
                completed = false;
                break;
            }
        }
        x.EffectDepth--;
        if (savedGeneratedOptions != 0)
            x.GeneratedOptions = savedOptionCards;
        x.GeneratedOptionCount = savedGeneratedOptions;
        if (x.NeedsChoice || !completed)
            return;

        // ContinueCardResultExecution: the card is still in the play pile at its position. The
        // native cost cleanup touches only this card, so it runs before the move here. Content that
        // removes an earlier play-pile card (SummonForth moving a queued Sovereign Blade) shifts the
        // live slot; PlayPosition is the adjusted identity, while the captured local can be stale.
        int resultSlot = playPosition;
        if ((uint)x.PlayPosition < (uint)x.B.PlayCount
            && Fingerprint.Equal(Piles.At(ref x.B, Pile.Play, x.PlayPosition), x.Card))
        {
            resultSlot = x.PlayPosition;
        }
        if (resultSlot < x.B.PlayCount)
        {
            if (removeFromCombat)
            {
                Piles.RemoveAt(ref x.B, Pile.Play, resultSlot);
            }
            else
            {
                AfterCardPlayedCleanup(ref Piles.At(ref x.B, Pile.Play, resultSlot));
                if (result == Pile.Exhaust)
                {
                    int exhaustCount = x.B.ExhaustCount;
                    Exhaust(ref x, Pile.Play, resultSlot);
                    resultPile = Pile.Exhaust;
                    resultIndex = exhaustCount;
                }
                else if (MoveCard(ref x.B, Pile.Play, resultSlot, result, resultPosition,
                             out Pile movedPile, out int movedIndex))
                {
                    resultPile = movedPile;
                    resultIndex = movedIndex;
                }
            }
        }
        // CombatManager.CheckForEmptyHand runs after the result-pile move. The existing engine
        // special-cases UnceasingTop in SimulatedCombatState.TriggerRelicsAfterHandEmptied (there is no
        // generic AfterHandEmptied listener), so the kernel does the same after the outermost effect.
        AfterHandEmptied(ref x);
    }

    /// <summary>
    /// <c>SimulatedCombatState.TriggerRelicsAfterHandEmptied</c>: with an empty hand, a live
    /// <c>UnceasingTop</c> and a non-empty draw or discard pile, draw one card. The existing engine
    /// invokes this from the card/potion completion path only when no card or potion effect is running.
    /// </summary>
    private static void AfterHandEmptied(ref Exec x)
    {
        if (!IsInProgress(x.B) || x.EffectDepth != 0)
            return;
        if (x.B.Phase is not (PlayerPhase.AutoPrePlay or PlayerPhase.Play or PlayerPhase.AutoPostPlay))
            return;
        if (x.B.HandCount != 0 || (x.B.DrawCount == 0 && x.B.DiscardCount == 0))
            return;
        if (!Registry.TryRelic("UNCEASING_TOP", out ushort top))
            return;
        bool hasTop = false;
        for (int index = 0; index < x.R.Relics.Count; index++)
        {
            Relic relic = x.B.GetRelic(x.R, index);
            if (relic.Id == top && (relic.Flags & RelicFlags.Melted) == 0)
            {
                hasTop = true;
                break;
            }
        }
        if (!hasTop)
            return;
        Draw(ref x, 1, fromHandDraw: false);
    }

    /// <summary>Mirrors <c>SimulatedCombatState.RecordCardPlayStarted</c> for a manual or auto play.</summary>
    private static void RecordCardPlayStarted(ref Battle battle, CardStats stats,
        CardType cardType, int playIndex, int energyValue, bool isAutoPlay)
    {
        battle.AddCounter(Counter.CardPlayStartsThisTurn, 1);
        if (cardType == CardType.Attack)
            battle.AddCounter(Counter.AttackPlayStartsThisTurn, 1);
        if (cardType is CardType.Attack or CardType.Skill)
            battle.AddCounter(Counter.AttackSkillStartsThisTurn, 1);
        if (cardType == CardType.Attack && energyValue == 0)
            battle.AddCounter(Counter.ZeroCostAttackStartsThisTurn, 1);
        if (playIndex != 0)
            return;
        battle.AddCounter(Counter.CardPlaySeriesStartedThisTurn, 1);
        if (!isAutoPlay)
            battle.AddCounter(Counter.ManualCardsPlayedThisTurn, 1);
    }

    /// <summary>
    /// <c>CardModel.GetEnchantedReplayCount</c>: the base replay count after the enchantment's
    /// <c>EnchantPlayCount</c>. Pure; the evaluation and search read it without mutating the card.
    /// </summary>
    public static int EnchantedReplayCount(in Card card)
    {
        int replayCount = card.ReplayCount;
        if (card.EnchantmentId != 0)
            replayCount = Registry.Enchantment((ushort)(card.EnchantmentId - 1))
                .EnchantPlayCount(card, replayCount);
        return replayCount;
    }

    /// <summary>
    /// Mirrors <c>RecordCardPlayed</c> then <c>RecordCardLifecycle</c> in
    /// <c>SimulatedCombatState.CompleteCardPlayEffects</c>. Slow, Fetch, relic card counters and
    /// returning cards are not supported yet.
    /// </summary>
    private static void RecordCardPlayFinished(ref Exec x, CardStats stats, CardType cardType)
    {
        ref Battle battle = ref x.B;
        if (cardType != CardType.Attack)
            return;
        battle.AddCounter(Counter.AttacksPlayedThisTurn, 1);
        if ((stats.Tags & CardTag.Shiv) != 0)
            battle.AddCounter(Counter.ShivsPlayedThisTurn, 1);
    }

    private static void RecordCardPlayed(ref Exec x, CardType cardType)
    {
        ref Battle battle = ref x.B;
        battle.AddCounter(Counter.CardsPlayedThisTurn, 1);
        // CardPlayFinished entry: CombatPredictionHistory records one per completed card play.
        battle.AddCounter(Counter.FinishedCardPlays, 1);
        if (cardType == CardType.Skill)
        {
            battle.AddCounter(Counter.SkillCardsPlayedThisTurn, 1);
            // SimulatedCombatState.RecordSkillPlayed's _skillsPlayedThisTurn membership.
            battle.SetCounter(Counter.SkillPlayedThisSideTurn, 1);
        }
    }

    /// <summary>
    /// The current card in the play pile while its tail runs. The play pile segment is located from
    /// the live counts because effects may have added or removed other cards.
    /// </summary>
    public static ref Card CurrentPlayCard(ref Exec x)
    {
        if ((uint)x.PlayPosition >= (uint)x.B.PlayCount)
            throw new InvalidOperationException("当前没有正在打出的卡。");
        return ref Piles.At(ref x.B, Pile.Play, x.PlayPosition);
    }

    /// <summary>Copies the live play-pile card back into <see cref="Exec.Card"/> after a tail hook changed it.</summary>
    public static void SyncCurrentPlayCard(ref Exec x)
    {
        x.Card = CurrentPlayCard(ref x);
        x.Stats = x.R.Card(x.Card.Id, x.Card.Upgrade);
    }

    /// <summary>
    /// <c>EnchantmentModel.OnPlay</c> then <c>AfflictionModel.OnPlay</c> for the card at
    /// <paramref name="playPosition"/>. Returns false when a choice interrupted the tail or the owner
    /// died, matching <c>CardTailContinuation</c>.
    /// </summary>
    private static bool RunCardAttachmentOnPlay(ref Exec x, int playPosition, byte target)
    {
        if ((uint)playPosition >= (uint)x.B.PlayCount)
            return true;
        // Nested auto-plays overwrite Exec.Card/Stats/Target; restore the outer play context so
        // the attachment hooks see this card and this target (CardModel/CardPlay are per-play objects
        // in the existing engine).
        ref Card played = ref Piles.At(ref x.B, Pile.Play, playPosition);
        x.Card = played;
        x.Stats = x.R.Card(played.Id, played.Upgrade);
        x.Target = target;
        x.PlayPosition = playPosition;
        if (played.EnchantmentId != 0)
        {
            Registry.Enchantment((ushort)(played.EnchantmentId - 1)).OnPlay(ref x, played, target);
            if (x.NeedsChoice || x.B.Player.IsDead)
                return false;
            played = ref Piles.At(ref x.B, Pile.Play, playPosition);
        }
        if (played.AfflictionId != 0)
        {
            Registry.Affliction((ushort)(played.AfflictionId - 1)).OnPlay(ref x, played, target);
            if (x.NeedsChoice || x.B.Player.IsDead)
                return false;
        }
        return true;
    }

    /// <summary>Mirrors <c>CardResultLocationMirrors.GetResultLocation</c> for the supported cards.</summary>
    private static Pile ResultLocation(in Battle battle, Rules rules, in Card card,
        CardStats stats, bool historyCourseDupe, out bool removeFromCombat,
        out PilePosition position)
    {
        removeFromCombat = false;
        position = PilePosition.Bottom;
        // CardModel.GetResultLocationForCardPlay: IsDupe returns PileType.None before the Power and
        // result-location-override branches. The card leaves combat after its play.
        if (historyCourseDupe)
        {
            removeFromCombat = true;
            return Pile.Play;
        }
        if (Registry.Card(card.Id).ResolveCardType(battle, rules, card) == CardType.Power)
        {
            removeFromCombat = true;
            return Pile.Play;
        }
        Pile baseLocation = (card.Flags & CardFlags.ExhaustOnNextPlay) != 0
            || HasKeyword(battle, card, Keywords.Exhaust)
                ? Pile.Exhaust
                : Pile.Discard;
        // CardModel.GetResultLocationForCardPlay override (ShiningStrike, ParticleWall).
        return Registry.Card(card.Id).ResultLocation(battle, rules, card, baseLocation, out position);
    }

    /// <summary>
    /// Appends one native <c>LocalCostModifier</c> to a card value
    /// (<c>CardEnergyCost.AddThisCombat</c>/<c>AddThisTurn</c>/<c>SetThisCombat</c>).
    /// </summary>
    public static void AddEnergyCostModifier(ref Card card, int amount, CostType type,
        CostExpiration expiration, bool reduceOnly)
    {
        // CardEnergyCost.SetThisCombat adds an absolute 0 modifier for a non-negative canonical cost;
        // AddThisCombat/AddUntilPlayed ignore zero.
        if (amount == 0 && type != CostType.Absolute)
            return;
        CardCostModifiers.Append(ref card, CostModifier.Create(amount, type, expiration, reduceOnly));
    }

    /// <summary>Mirrors <c>CardEnergyCost.AfterCardPlayedCleanup</c> and the temporary star cost cleanup.</summary>
    private static void AfterCardPlayedCleanup(ref Card card)
    {
        int kept = 0;
        for (int index = 0; index < card.EnergyModifierCount; index++)
        {
            CostModifier modifier = card.EnergyModifiers[index];
            // CardEnergyCost.AfterCardPlayedCleanup: Expiration.HasFlag(WhenPlayed), so EndOfTurn | WhenPlayed goes too.
            if (((byte)modifier.Expiration & (byte)CostExpiration.WhenPlayed) != 0)
                continue;
            card.EnergyModifiers[kept++] = modifier;
        }
        for (int index = kept; index < Limits.CardEnergyCostModifiers; index++)
            card.EnergyModifiers[index] = default;
        card.EnergyModifierCount = kept;
        CardCostModifiers.Compact(ref card);
        if (card.HasTemporaryStarCost)
            card.RemoveTemporaryStarCosts(atTurnEnd: false);
    }

    /// <summary>
    /// <c>CorePowerSupport.CapturePendingMonologues</c>: every positive MonologuePower instance on the
    /// player, in table (listener) order. Captured before the card's effects; resolved afterwards.
    /// </summary>
    private static void CaptureMonologuePending(ref Exec x)
    {
        if (!x.R.HasIdentityContent)
            return;
        if (!Registry.TryPower("MONOLOGUE_POWER", out ushort id))
            return;
        byte occurrence = 0;
        for (int index = 0; index < x.B.PowerCount; index++)
        {
            ref readonly Power power = ref x.B.Powers[index];
            if (power.Id != id || power.Owner != Limits.PlayerOwner)
                continue;
            if (power.Amount > 0)
                x.PushMonologuePending(occurrence, power.Aux0);
            occurrence++;
        }
    }

    /// <summary>Native Monologue.AfterCardPlayed runs after card choices and attachments, in listener order.</summary>
    public static void ResolveMonologueAfterPlay(ref Exec x, Listener self, ushort strength)
    {
        ushort monologue = x.B.Powers[self.Index].Id;
        int occurrence = 0;
        for (int index = 0; index < self.Index; index++)
        {
            if (x.B.Powers[index].Id == monologue && x.B.Powers[index].Owner == self.Owner)
                occurrence++;
        }
        for (int index = x.CurrentMonologueBase; index < x.MonologuePendingCount; index++)
        {
            MonologuePending entry = x.MonologuePending[index];
            if (entry.Occurrence != occurrence)
                continue;
            int applied = x.B.Powers[self.Index].Aux0;
            ApplyPower(ref x, self.Owner, strength,
                entry.Strength, self.Owner);
            int row = FindPowerOccurrence(x.B, self.Owner, monologue, occurrence);
            if (row >= 0)
                x.B.Powers[row].Aux1 += applied;
            return;
        }
    }

    /// <summary>Marks the end of the current card power-application scope (UnsettlingLamp latch).</summary>
    internal static void CompleteCardPowerSource(ref Exec x)
    {
        x.LastCardPowerSourceSerial = x.CardPowerSourceSerial;
        if (x.LampTriggerSerial == 0 || x.LampTriggerSerial != x.CardPowerSourceSerial)
            return;
        if (Registry.TryRelic("UNSETTLING_LAMP", out ushort lamp))
        {
            for (int index = 0; index < x.R.Relics.Count; index++)
            {
                if (x.R.Relics.Id(index) == lamp && x.B.GetRelic(x.R, index).Counter == 0)
                    x.B.RelicState(x.R, index).Counter = 1;
            }
        }
        x.LampTriggerSerial = 0;
        x.LampInternalTypeCount = 0;
    }

    /// <summary>
    /// <c>SimulatedCombatState.ApplyPowerFromSource</c> with a non-null card source outside the normal
    /// OnPlay scope (Inky's enchantment OnPlay). Opens and completes a one-application source scope.
    /// </summary>
    public static int ApplyPowerFromCard(ref Exec x, byte target, ushort powerId, int amount, byte applier,
        in Card card)
    {
        _ = card;
        int outerSerial = x.CardPowerSourceSerial;
        bool outerActive = x.CardPowerSourceActive;
        x.CardPowerSourceSerial = ++x.CardPowerSourceSerialCounter;
        x.CardPowerSourceActive = true;
        int applied = ApplyPower(ref x, target, powerId, amount, applier);
        CompleteCardPowerSource(ref x);
        x.CardPowerSourceSerial = outerSerial;
        x.CardPowerSourceActive = outerActive;
        return applied;
    }
}
