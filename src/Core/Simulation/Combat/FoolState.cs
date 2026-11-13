// Rules checked against game 0.113.0 (16ac0fe3), and CombatSolver at
// f48b69c590cb8b689ec7fdf033c2f62e625e0f1e, src/Search/SimulatedCombatState.Melodies.cs
// (MIT, Copyright (c) 2026 Torch1230 and contributors).
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sts2Solver.Simulation.Combat;

[StructLayout(LayoutKind.Sequential)]
public struct FoolArrow
{
    // Native ArrowStatus: Retrieved=0, Away=1, Destroyed=2; bit 2 is DestroyAfterImpaled.
    public byte Flags;
    public byte Next;
    public readonly bool Available => (Flags & 3) == 0;
    public bool DestroyAfterImpaled
    {
        readonly get => (Flags & 4) != 0;
        set => Flags = (byte)((Flags & ~4) | (value ? 4 : 0));
    }
    public void SetStatus(int status) => Flags = (byte)((Flags & ~3) | status);
}

[InlineArray(FoolState.ArrowCapacity)]
public struct FoolArrowArray { private FoolArrow _first; }

[InlineArray(3)]
public struct MelodyCounts { private int _first; }

[StructLayout(LayoutKind.Sequential)]
public struct FoolState
{
    public const int ArrowCapacity = 32;
    public FoolArrowArray Arrows;
    public byte ArrowCount;
    private byte _padding;
    public ushort NextCardIdentity;
    public MelodyCounts Started;
    public MelodyCounts Finished;
    public MelodyCounts FirstPlay;
    public int MelodyCount;
    public int SadnessGenerated;
    // Per-action command identities. Zero at stable boundaries.
    public int AttackSerial;
    public int ActiveAttack;
}

public static partial class Kernel
{
    internal static void RecordSadnessGenerated(ref Battle battle, in Card card)
    {
        if (Registry.Card(card.Id).NativeId == "SADNESS") battle.Fool.SadnessGenerated++;
    }

    public static int MelodiesThisTurn(in Battle battle)
        => (battle.Fool.Finished[0] > 0 ? 1 : 0) + (battle.Fool.Finished[1] > 0 ? 1 : 0)
            + (battle.Fool.Finished[2] > 0 ? 1 : 0);

    public static bool PlayedMelody(in Battle battle, CardTag melody)
        => battle.Fool.Finished[MelodyIndex(melody)] > 0;

    public static bool FirstMelodyPlay(ref Exec x, CardTag melody)
        => x.PlayIndex == 0 && x.B.Fool.FirstPlay[MelodyIndex(melody)] == x.PlayIdentity
            && x.PlayIdentity != 0;

    private static int MelodyIndex(CardTag melody) => melody switch
    {
        CardTag.Wah => 0, CardTag.Cho => 1, CardTag.Ki => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(melody)),
    };

    private static void RecordMelodyStarted(ref Exec x, CardTag tags)
    {
        for (int i = 0; i < 3; i++)
        {
            if ((tags & (CardTag)((int)CardTag.Wah << i)) == 0) continue;
            if (x.B.Fool.Started[i]++ == 0) x.B.Fool.FirstPlay[i] = x.PlayIdentity;
        }
    }

    private static void RecordMelodyFinished(ref Battle battle, CardTag tags)
    {
        if ((tags & (CardTag.Wah | CardTag.Cho | CardTag.Ki)) == 0) return;
        battle.Fool.MelodyCount++;
        for (int i = 0; i < 3; i++)
            if ((tags & (CardTag)((int)CardTag.Wah << i)) != 0) battle.Fool.Finished[i]++;
    }

    public static ushort EnsureFoolCardIdentity(ref Exec x)
    {
        ref Card card = ref CurrentPlayCard(ref x);
        if (card.FoolIdentity == 0)
            card.FoolIdentity = checked((ushort)++x.B.Fool.NextCardIdentity);
        x.Card.FoolIdentity = card.FoolIdentity;
        return card.FoolIdentity;
    }

    // Only powers that retain a native CardModel reference keep its identity in the byte image.
    // Canonicalizing by pile order lets equivalent branches merge without losing the reference.
    public static void NormalizeFoolState(ref Battle battle)
    {
        Span<int> old = stackalloc int[Limits.Powers];
        Span<bool> linked = stackalloc bool[Limits.Powers];
        for (int p = 0; p < battle.PowerCount; p++)
        {
            string id = Registry.Power(battle.Powers[p].Id).NativeId;
            linked[p] = id is "AIM_POWER" or "SHOW_OFF_POWER";
            old[p] = linked[p] ? battle.Powers[p].Aux0 : 0;
            if (linked[p])
            {
                if (old[p] > 0) battle.Powers[p].Aux0 = -1;
                if (id == "AIM_POWER")
                {
                    battle.Powers[p].Aux1 = 0;
                    battle.Powers[p].AmountOnTurnStart = 0;
                }
            }
        }
        int next = 0;
        for (int c = 0; c < battle.CardCount; c++)
        {
            int previous = battle.Cards[c].FoolIdentity;
            battle.Cards[c].FoolIdentity = 0;
            if (previous == 0) continue;
            for (int p = 0; p < battle.PowerCount; p++)
            {
                if (!linked[p] || old[p] != previous) continue;
                if (battle.Cards[c].FoolIdentity == 0)
                    battle.Cards[c].FoolIdentity = checked((ushort)++next);
                battle.Powers[p].Aux0 = battle.Cards[c].FoolIdentity;
            }
        }
        battle.Fool.NextCardIdentity = checked((ushort)next);
        battle.Fool.FirstPlay = default;
        battle.Fool.AttackSerial = 0;
        battle.Fool.ActiveAttack = 0;
        battle.HistoryCourseCard.FoolIdentity = 0;
        for (int i = 0; i < Limits.DetachedCards; i++) battle.DetachedCards[i].FoolIdentity = 0;
    }
}
