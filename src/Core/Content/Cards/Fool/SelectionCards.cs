// 对照游戏 0.113.0 (16ac0fe3) KiChant.OnPlay；临时标记跟踪洗牌、选牌和弃牌期间的原对象。
using Sts2Solver.Simulation.Combat;
namespace Sts2Solver.Content.Cards.Fool;

[ContentId("KI_CHANT")]
public sealed class KiChant : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Cards"];
    public override void OnPlay(ref Exec x)
    {
        int limit = FoolEffects.Int(ref x, 0);
        if (x.B.DrawCount < limit && x.B.DiscardCount > 0)
        {
            int original = x.B.DrawCount;
            Span<byte> oldMarks = stackalloc byte[original];
            Span<byte> oldOuter = stackalloc byte[original];
            Span<int> originalIndices = stackalloc int[original];
            for (int i = 0; i < original; i++) originalIndices[i] = i;
            int saved = Kernel.ReserveTransientMarks(ref x, original, out int first);
            Kernel.MarkPileCards(ref x.B, Pile.Draw, originalIndices, oldMarks, oldOuter, first);
            try
            {
                Kernel.Shuffle(ref x);
                if (x.NeedsChoice) return;
                for (int i = original - 1; i >= 0; i--)
                    if (Kernel.TryFindUniqueTransientMark(x.B, oldMarks[i], out Pile pile, out int index)
                        && pile == Pile.Draw)
                        Kernel.MoveCard(ref x.B, pile, index, Pile.Draw, PilePosition.Top);
            }
            finally
            {
                Kernel.RestoreMarkedCards(ref x.B, oldMarks, oldOuter);
                Kernel.ReleaseTransientMarks(ref x, saved, first, original);
            }
        }
        int count = Math.Min(limit, x.B.DrawCount);
        if (count == 0) return;
        Span<int> options = stackalloc int[count];
        for (int i = 0; i < count; i++) options[i] = i;
        Span<int> chosen = stackalloc int[1];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.MoveToHand, Pile.Draw, options, 1, chosen, out int take)) return;
        Span<byte> marks = stackalloc byte[count];
        Span<byte> outer = stackalloc byte[count];
        int markBase = Kernel.ReserveTransientMarks(ref x, count, out int markFirst);
        Kernel.MarkPileCards(ref x.B, Pile.Draw, options, marks, outer, markFirst);
        try
        {
            if (take > 0) Kernel.MoveCard(ref x.B, Pile.Draw, chosen[0], Pile.Hand);
            // The native selection remains excluded even if a full hand prevents the move.
            for (int i = 0; i < count; i++)
            {
                if (take > 0 && i == chosen[0]) continue;
                if (Kernel.TryFindUniqueTransientMark(x.B, marks[i], out Pile pile, out int index)
                    && pile == Pile.Draw) Kernel.Discard(ref x, pile, index);
                if (x.NeedsChoice) return;
            }
        }
        finally
        {
            Kernel.RestoreMarkedCards(ref x.B, marks, outer);
            Kernel.ReleaseTransientMarks(ref x, markBase, markFirst, count);
        }
    }
}
