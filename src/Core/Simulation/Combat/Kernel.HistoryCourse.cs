namespace Sts2Solver.Simulation.Combat;

// Semantics: game v0.111.0 CardModel.Play and Relics.HistoryCourse.AfterAutoPrePlayPhaseEntered.
// CardPlaysFinished holds the original object, including later mutations and nested finished plays.
public static partial class Kernel
{
    internal static Card HistoryCourseValue(in Card source)
    {
        Card value = source;
        value.HistoryCourseRole = 0;
        value.TransientMark = 0;
        return value;
    }

    private static void RecordHistoryCourseAttack(ref Exec x, int playPosition)
    {
        if (!x.R.HasHistoryCourse || x.CurrentPlayIsHistoryCourseDupe)
            return;
        for (int i = 0; i < x.B.CardCount; i++)
            x.B.Cards[i].HistoryCourseRole &= 2;
        ref Card source = ref Piles.At(ref x.B, Pile.Play, playPosition);
        source.HistoryCourseRole |= 1;
        x.B.HistoryCourseCard = HistoryCourseValue(source);
        x.B.Flags |= BattleFlags.HistoryCourseCardPresent;
    }

    private static void RefreshHistoryCourse(ref Exec x)
    {
        if (!x.R.HasHistoryCourse || (x.B.Flags & BattleFlags.HistoryCourseCardPresent) == 0)
            return;
        for (int i = 0; i < x.B.CardCount; i++)
        {
            if ((x.B.Cards[i].HistoryCourseRole & 1) == 0)
                continue;
            x.B.HistoryCourseCard = HistoryCourseValue(x.B.Cards[i]);
            return;
        }
    }

    internal static void BeginHistoryCourseTurn(ref Exec x)
    {
        if (!x.R.HasHistoryCourse)
            return;
        RefreshHistoryCourse(ref x);
        x.HasPreviousHistoryCourseCard = (x.B.Flags & BattleFlags.HistoryCourseCardPresent) != 0;
        x.PreviousHistoryCourseCard = x.B.HistoryCourseCard;
        for (int i = 0; i < x.B.CardCount; i++)
            x.B.Cards[i].HistoryCourseRole = (byte)((x.B.Cards[i].HistoryCourseRole & 1) << 1);
        x.B.HistoryCourseCard = default;
        x.B.Flags &= ~BattleFlags.HistoryCourseCardPresent;
    }

    private static Card PreviousHistoryCourse(in Battle battle, in Card fallback)
    {
        for (int i = 0; i < battle.CardCount; i++)
            if ((battle.Cards[i].HistoryCourseRole & 2) != 0)
                return HistoryCourseValue(battle.Cards[i]);
        return fallback;
    }

    internal static void PreserveHistoryCourseSource(ref Battle battle, in Card source)
    {
        if ((source.HistoryCourseRole & 1) != 0)
            battle.HistoryCourseCard = HistoryCourseValue(source);
    }
}
