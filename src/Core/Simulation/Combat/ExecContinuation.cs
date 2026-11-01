namespace Sts2Solver.Simulation.Combat;

/// <summary>
/// Snapshot of every <see cref="Exec"/> field that the player-turn-start half can read after an
/// <see cref="Kernel.TryPrepareEndTurn"/> prefix. The prefix is an image of the execution
/// context at the point where the full step would enter <c>AdvanceRoundPlayerStart</c>; restoring it
/// into a fresh <see cref="Exec"/> is equivalent to continuing the original stack frame.
///
/// The fields that cannot be carried by value are asserted at capture time instead:
/// <see cref="Exec.NeedsChoice"/>, <see cref="Exec.Request"/>, the pending choice count, the effect
/// depth and the nested Sly mark range. <see cref="Exec.ChoiceTiming"/> and
/// <see cref="Exec.PendingBlockBreakOwners"/> are overwritten by the continuation entry, and
/// <see cref="Exec.TakingExtraTurn"/> is false on any shared prefix (extra turns are not shared).
/// </summary>
internal readonly struct ExecContinuation
{
    internal readonly bool HistoryRisk;
    internal readonly int AttackUnblockedHits;
    internal readonly int CardDamageTotal;
    internal readonly bool CardKilledTarget;
    internal readonly int LastDamageKills;
    internal readonly int LastDamageTotal;
    internal readonly int LastDamageOverkill;
    internal readonly Card Card;
    internal readonly CardStats? Stats;
    internal readonly byte Target;
    internal readonly int XValue;
    internal readonly int StarValue;
    internal readonly int PlayIndex;
    internal readonly int PlayPosition;
    internal readonly int DrawnCardPosition;
    internal readonly bool IntrinsicDrawHandled;
    internal readonly GeneratedChoiceOptions GeneratedOptions;
    internal readonly int GeneratedOptionCount;
    internal readonly PowerAmountChanges PendingPowerChanges;
    internal readonly int PendingPowerChangeCount;
    internal readonly CardPlayPairs CardPlayPairs;
    internal readonly int CardPlayPairCount;
    internal readonly MonologuePendingArray MonologuePending;
    internal readonly int MonologuePendingCount;
    internal readonly int CurrentMonologueBase;
    internal readonly bool MonologuesResolved;
    internal readonly bool CardPowerSourceActive;
    internal readonly int CardPowerSourceSerial;
    internal readonly int CardPowerSourceSerialCounter;
    internal readonly int LampTriggerSerial;
    internal readonly int LampInternalTypeCount;
    internal readonly LampInternalPowerTypes LampInternalTypes;
    internal readonly bool VambraceTriggerActive;
    internal readonly int VambraceTriggerPosition;
    internal readonly int VambraceTriggerSerial;
    internal readonly int LastCardPowerSourceSerial;
    internal readonly int PlayIdentity;
    internal readonly int LastPlayIdentity;
    internal readonly int PlayIdentityCounter;
    internal readonly int MusicBoxPlayIdentity;
    internal readonly bool CurrentPlayIsAutoPlay;
    internal readonly bool CurrentPlayIsHistoryCourseDupe;
    internal readonly int CurrentPlayEnergyValue;

    internal ExecContinuation(
        bool historyRisk, int attackUnblockedHits, int cardDamageTotal, bool cardKilledTarget,
        int lastDamageKills, int lastDamageTotal, int lastDamageOverkill, in Card card,
        CardStats? stats, byte target, int xValue, int starValue, int playIndex, int playPosition,
        int drawnCardPosition, bool intrinsicDrawHandled, in GeneratedChoiceOptions generatedOptions,
        int generatedOptionCount, in PowerAmountChanges pendingPowerChanges, int pendingPowerChangeCount,
        in CardPlayPairs cardPlayPairs, int cardPlayPairCount, in MonologuePendingArray monologuePending,
        int monologuePendingCount, int currentMonologueBase, bool monologuesResolved,
        bool cardPowerSourceActive, int cardPowerSourceSerial, int cardPowerSourceSerialCounter,
        int lampTriggerSerial, int lampInternalTypeCount, in LampInternalPowerTypes lampInternalTypes,
        bool vambraceTriggerActive, int vambraceTriggerPosition, int vambraceTriggerSerial,
        int lastCardPowerSourceSerial, int playIdentity, int lastPlayIdentity, int playIdentityCounter,
        int musicBoxPlayIdentity, bool currentPlayIsAutoPlay, bool currentPlayIsHistoryCourseDupe,
        int currentPlayEnergyValue)
    {
        HistoryRisk = historyRisk;
        AttackUnblockedHits = attackUnblockedHits;
        CardDamageTotal = cardDamageTotal;
        CardKilledTarget = cardKilledTarget;
        LastDamageKills = lastDamageKills;
        LastDamageTotal = lastDamageTotal;
        LastDamageOverkill = lastDamageOverkill;
        Card = card;
        Stats = stats;
        Target = target;
        XValue = xValue;
        StarValue = starValue;
        PlayIndex = playIndex;
        PlayPosition = playPosition;
        DrawnCardPosition = drawnCardPosition;
        IntrinsicDrawHandled = intrinsicDrawHandled;
        GeneratedOptions = generatedOptions;
        GeneratedOptionCount = generatedOptionCount;
        PendingPowerChanges = pendingPowerChanges;
        PendingPowerChangeCount = pendingPowerChangeCount;
        CardPlayPairs = cardPlayPairs;
        CardPlayPairCount = cardPlayPairCount;
        MonologuePending = monologuePending;
        MonologuePendingCount = monologuePendingCount;
        CurrentMonologueBase = currentMonologueBase;
        MonologuesResolved = monologuesResolved;
        CardPowerSourceActive = cardPowerSourceActive;
        CardPowerSourceSerial = cardPowerSourceSerial;
        CardPowerSourceSerialCounter = cardPowerSourceSerialCounter;
        LampTriggerSerial = lampTriggerSerial;
        LampInternalTypeCount = lampInternalTypeCount;
        LampInternalTypes = lampInternalTypes;
        VambraceTriggerActive = vambraceTriggerActive;
        VambraceTriggerPosition = vambraceTriggerPosition;
        VambraceTriggerSerial = vambraceTriggerSerial;
        LastCardPowerSourceSerial = lastCardPowerSourceSerial;
        PlayIdentity = playIdentity;
        LastPlayIdentity = lastPlayIdentity;
        PlayIdentityCounter = playIdentityCounter;
        MusicBoxPlayIdentity = musicBoxPlayIdentity;
        CurrentPlayIsAutoPlay = currentPlayIsAutoPlay;
        CurrentPlayIsHistoryCourseDupe = currentPlayIsHistoryCourseDupe;
        CurrentPlayEnergyValue = currentPlayEnergyValue;
    }
}
