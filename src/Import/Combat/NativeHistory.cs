// Reads the native CombatManager.History arrays used by SimulatedCombatState's root getters.
// Reference semantics (not code) follow CombatSolver src/Search/RootCombatHistorySnapshot.cs and
// SimulatedCombatState.CardEventHistory.cs / CardLifecycle.cs / CardPowerHistory.cs at a62f1850.
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;

using Creature = MegaCrit.Sts2.Core.Entities.Creatures.Creature;

namespace Sts2Solver.Import;

/// <summary>Immutable snapshot of the native combat history plus the root turn window.</summary>
internal sealed class NativeHistory
{
    private readonly CombatState _state;
    private readonly CardPlayStartedEntry[] _cardPlaysStarted;
    private readonly CardPlayFinishedEntry[] _cardPlaysFinished;
    private readonly DamageReceivedEntry[] _damageReceived;
    private readonly PowerReceivedEntry[] _powerReceived;
    private readonly CardDiscardedEntry[] _cardsDiscarded;
    private readonly CreatureAttackedEntry[] _creatureAttacked;
    private readonly EnergySpentEntry[] _energySpent;
    private readonly StarsModifiedEntry[] _starsModified;
    private readonly CardDrawnEntry[] _cardsDrawn;
    private readonly CardExhaustedEntry[] _cardsExhausted;
    private readonly BlockGainedEntry[] _blockGained;
    private readonly CardGeneratedEntry[] _cardsGenerated;
    private readonly OrbChanneledEntry[] _orbsChanneled;

    private NativeHistory(CombatState state)
    {
        _state = state;
        CombatHistory history = CombatManager.Instance.History;
        _cardPlaysStarted = [.. history.CardPlaysStarted];
        _cardPlaysFinished = [.. history.CardPlaysFinished];
        _damageReceived = [.. history.Entries.OfType<DamageReceivedEntry>()];
        _powerReceived = [.. history.Entries.OfType<PowerReceivedEntry>()];
        _cardsDiscarded = [.. history.Entries.OfType<CardDiscardedEntry>()];
        _creatureAttacked = [.. history.Entries.OfType<CreatureAttackedEntry>()];
        _energySpent = [.. history.Entries.OfType<EnergySpentEntry>()];
        _starsModified = [.. history.Entries.OfType<StarsModifiedEntry>()];
        _cardsDrawn = [.. history.Entries.OfType<CardDrawnEntry>()];
        _cardsExhausted = [.. history.Entries.OfType<CardExhaustedEntry>()];
        _blockGained = [.. history.Entries.OfType<BlockGainedEntry>()];
        _cardsGenerated = [.. history.Entries.OfType<CardGeneratedEntry>()];
        _orbsChanneled = [.. history.Entries.OfType<OrbChanneledEntry>()];
    }

    public static NativeHistory Capture(CombatState state) => new(state);

    /// <summary>
    /// <c>SimulatedCombatState.BrightestFlameMaxHpSpent</c>: the native
    /// <c>CombatManager.History.CardPlaysStarted</c> entries whose card is BrightestFlame, summing the
    /// card's dynamic <c>MaxHp</c> exactly as the existing root snapshot does.
    /// </summary>
    public static int BrightestFlameMaxHpSpent()
        => CombatManager.Instance.History.CardPlaysStarted
            .Where(entry => entry.CardPlay.Card is BrightestFlame)
            .Sum(entry => entry.CardPlay.Card.DynamicVars.MaxHp.IntValue);

    private bool ThisTurn(CombatHistoryEntry entry) => entry.HappenedThisTurn(_state);

    public int CardsPlayedThisTurn(Creature owner)
        => _cardPlaysStarted.Count(entry => ThisTurn(entry) && entry.CardPlay.IsFirstInSeries
            && ReferenceEquals(entry.CardPlay.Player.Creature, owner));

    public int CardsFinishedThisTurn(Creature owner)
        => _cardPlaysFinished.Count(entry => ThisTurn(entry)
            && ReferenceEquals(entry.CardPlay.Player.Creature, owner));

    public int ManualCardsPlayedThisTurn(Creature owner)
        => _cardPlaysStarted.Count(entry => ThisTurn(entry) && entry.CardPlay.IsFirstInSeries
            && !entry.CardPlay.IsAutoPlay && ReferenceEquals(entry.CardPlay.Player.Creature, owner));

    public int CardPlayStartsThisTurn(Creature owner)
        => _cardPlaysStarted.Count(entry => ThisTurn(entry)
            && ReferenceEquals(entry.CardPlay.Player.Creature, owner));

    public int AttackPlayStartsThisTurn(Creature owner)
        => _cardPlaysStarted.Count(entry => ThisTurn(entry)
            && ReferenceEquals(entry.CardPlay.Player.Creature, owner)
            && entry.CardPlay.Card.Type == CardType.Attack);

    public int AttackSkillStartsThisTurn(Creature owner)
        => _cardPlaysStarted.Count(entry => ThisTurn(entry)
            && ReferenceEquals(entry.CardPlay.Player.Creature, owner)
            && entry.CardPlay.Card.Type is CardType.Attack or CardType.Skill);

    public int ZeroCostAttackStartsThisTurn(Creature owner)
        => _cardPlaysStarted.Count(entry => ThisTurn(entry)
            && ReferenceEquals(entry.CardPlay.Player.Creature, owner)
            && entry.CardPlay.Card.Type == CardType.Attack
            && entry.CardPlay.Resources.EnergyValue == 0);

    public int CardPlaySeriesStartedThisTurn(Creature owner)
        => _cardPlaysStarted.Count(entry => ThisTurn(entry) && entry.CardPlay.IsFirstInSeries
            && ReferenceEquals(entry.CardPlay.Player.Creature, owner));

    public int SkillCardsPlayedThisTurn(Creature owner)
        => _cardPlaysStarted.Count(entry => ThisTurn(entry)
            && ReferenceEquals(entry.CardPlay.Player.Creature, owner)
            && entry.CardPlay.Card.Type == CardType.Skill);

    public int SkillCardsFinishedThisTurn(Creature owner)
        => _cardPlaysFinished.Count(entry => ThisTurn(entry)
            && ReferenceEquals(entry.CardPlay.Player.Creature, owner)
            && entry.CardPlay.Card.Type == CardType.Skill);

    public int AttacksPlayedThisTurn(Creature owner)
        => _cardPlaysFinished.Count(entry => ThisTurn(entry)
            && entry.CardPlay.Card.Type == CardType.Attack
            && ReferenceEquals(entry.CardPlay.Player.Creature, owner));

    public int ShivsPlayedThisTurn(Creature owner)
        => _cardPlaysFinished.Count(entry => ThisTurn(entry)
            && entry.CardPlay.Card.Tags.Contains(CardTag.Shiv)
            && ReferenceEquals(entry.CardPlay.Player.Creature, owner));

    public int BlockCardsPlayedThisTurn(Creature owner)
        => _blockGained.Count(entry => ThisTurn(entry)
            && entry.CardPlay != null
            && ReferenceEquals(entry.CardPlay.Player.Creature, owner)
            && entry.Props.IsCardOrMonsterMove());

    public int CardsExhaustedThisTurn(Creature owner)
        => _cardsExhausted.Count(entry => ThisTurn(entry) && ReferenceEquals(entry.Actor, owner));

    public int CardsDiscardedThisTurn(Creature owner)
        => _cardsDiscarded.Count(entry => ThisTurn(entry) && ReferenceEquals(entry.Actor, owner));

    public int CreatureAttacksThisTurn(Creature owner)
        => _creatureAttacked.Count(entry => ThisTurn(entry) && ReferenceEquals(entry.Actor, owner));

    public int NonHandDrawsThisTurn(Player player)
        => _cardsDrawn.Count(entry => ThisTurn(entry)
            && ReferenceEquals(entry.Actor.Player, player) && !entry.FromHandDraw);

    public int StatusCardsDrawnThisTurn(Player player)
        => _cardsDrawn.Count(entry => ThisTurn(entry)
            && ReferenceEquals(entry.Actor.Player, player) && entry.Card.Type == CardType.Status);

    public int EnergySpentThisTurn(Player player)
        => _energySpent.Where(entry => ThisTurn(entry) && ReferenceEquals(entry.Actor.Player, player))
            .Sum(entry => entry.Amount);

    public int StarsGainedThisTurn(Player player)
        => _starsModified.Where(entry => ThisTurn(entry)
                && ReferenceEquals(entry.Actor.Player, player) && entry.Amount > 0)
            .Sum(entry => entry.Amount);

    public bool HasLostHpThisTurn(Creature receiver)
        => _damageReceived.Any(entry => ThisTurn(entry)
            && ReferenceEquals(entry.Receiver, receiver) && entry.Result.UnblockedDamage > 0);

    /// <summary><c>SimulatedCombatState.PlayerUnblockedDamageEvents</c>: combat-cumulative events.</summary>
    public int UnblockedDamageEvents(Creature receiver)
        => _damageReceived.Count(entry =>
            ReferenceEquals(entry.Receiver, receiver) && entry.Result.UnblockedDamage > 0);

    public int UnblockedDamageEventsThisTurn(Creature receiver)
        => _damageReceived.Count(entry => ThisTurn(entry)
            && ReferenceEquals(entry.Receiver, receiver) && entry.Result.UnblockedDamage > 0);

    /// <summary><c>SimulatedCombatState._cumulativeHpLost</c>: player unblocked damage over the combat.</summary>
    public int CumulativeHpLost(Creature receiver) => SumHpLost(_damageReceived, receiver);

    /// <summary>
    /// The same combat HP loss read from the live <c>CombatManager.History</c> (cleared at every combat
    /// end), for a kept route the Mod shows again after its search root.
    /// </summary>
    public static int HpLostSoFar(Creature receiver)
        => SumHpLost(CombatManager.Instance.History.Entries.OfType<DamageReceivedEntry>(), receiver);

    private static int SumHpLost(IEnumerable<DamageReceivedEntry> entries, Creature receiver)
        => entries.Where(entry => ReferenceEquals(entry.Receiver, receiver))
            .Sum(entry => Math.Max(0, entry.Result.UnblockedDamage));

    public bool WasDoomAppliedThisTurn(Creature applier)
        => _powerReceived.Any(entry => ThisTurn(entry)
            && entry.Power is MegaCrit.Sts2.Core.Models.Powers.DoomPower
            && ReferenceEquals(entry.Applier, applier));

    public int PoweredAttackHitsThisTurn(Creature dealer, Creature receiver)
        => _damageReceived.Count(entry => ThisTurn(entry)
            && ReferenceEquals(entry.Dealer, dealer)
            && ReferenceEquals(entry.Receiver, receiver)
            && entry.Result.Props.IsPoweredAttack());

    public int FinishedCardPlays => _cardPlaysFinished.Length;

    public int CardsDrawn(Player player)
        => _cardsDrawn.Count(entry => ReferenceEquals(entry.Actor.Player, player));

    public int LightningChannels(Player player)
        => _orbsChanneled.Count(entry => ReferenceEquals(entry.Actor.Player, player)
            && entry.Orb is MegaCrit.Sts2.Core.Models.Orbs.LightningOrb);

    /// <summary><c>SimulatedCombatState._orbChannels</c>: every orb the player channeled this combat.</summary>
    public int OrbChannels(Player player)
        => _orbsChanneled.Count(entry => ReferenceEquals(entry.Actor.Player, player));

    public int EtherealPlays(Player player)
        => _cardPlaysFinished.Count(entry => ReferenceEquals(entry.CardPlay.Player, player) && entry.WasEthereal);

    public int CardsGenerated(Player player)
        => _cardsGenerated.Count(entry => ReferenceEquals(entry.Creator, player));

    /// <summary>
    /// <c>SimulatedCombatState._returnToHandNextTurn</c> root seeding: the card returns at the next
    /// <c>BeforeHandDraw</c>, where the native Bolas/ThrummingHatchet test is "played in the owner's
    /// last player turn" [GAME ThrummingHatchet.cs BeforeHandDraw, CombatHistoryEntry.HappenedLastPlayerTurn].
    /// At a play-phase root that is a play of the current turn. At a <c>SetupPlayerTurn</c> entry
    /// root (<paramref name="beforeHandDraw"/>) the turn number has already advanced and this turn's
    /// <c>BeforeHandDraw</c> has not run yet, so it is a play of the last player turn.
    /// </summary>
    public bool ReturnsToHand(CardModel card, bool beforeHandDraw)
        => card is Bolas or ThrummingHatchet
            && _cardPlaysFinished.Any(entry => ReferenceEquals(entry.CardPlay.Card, card)
                && (beforeHandDraw ? entry.HappenedLastPlayerTurn(card.Owner) : ThisTurn(entry)));

    public bool WasCardPlayFinished(CardPlay play)
        => _cardPlaysFinished.Any(entry => ReferenceEquals(entry.CardPlay, play));

    public bool FetchPlayedThisTurn(CardModel card)
        => card is Fetch && _cardPlaysFinished.Any(entry => ThisTurn(entry)
            && ReferenceEquals(entry.CardPlay.Card, card));

    /// <summary><c>CaptureEmotionChipState</c>: unblocked damage in the previous/current player turn.</summary>
    public (bool LastPlayerTurn, bool ThisTurn) EmotionChipDamage(EmotionChip relic)
    {
        Creature receiver = relic.Owner.Creature;
        bool last = CombatManager.Instance.History.Entries.Any(entry =>
            entry is DamageReceivedEntry damage
            && ReferenceEquals(damage.Receiver, receiver)
            && !damage.Result.WasFullyBlocked
            && damage.HappenedLastPlayerTurn(relic.Owner));
        bool current = CombatManager.Instance.History.Entries.Any(entry =>
            entry is DamageReceivedEntry damage
            && ReferenceEquals(damage.Receiver, receiver)
            && !damage.Result.WasFullyBlocked
            && damage.HappenedThisTurn(receiver.CombatState));
        return (last, current);
    }

    /// <summary><c>ChainsOfBindingPredictionState.CaptureRoot</c>.</summary>
    public int BoundCardsAfflictedThisTurn(ChainsOfBindingPower power)
        => CombatManager.Instance.History.Entries.OfType<CardAfflictedEntry>()
            .Count(entry => ThisTurn(entry)
                && ReferenceEquals(entry.Actor, power.Owner)
                && entry.Affliction is MegaCrit.Sts2.Core.Models.Afflictions.Bound);
}
